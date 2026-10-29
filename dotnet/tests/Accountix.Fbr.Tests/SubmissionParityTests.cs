using System.Text.RegularExpressions;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;
using static Accountix.Fbr.Tests.Scenario;

namespace Accountix.Fbr.Tests;

/// <summary>
/// Send, Retry, Validate, Status and the record's JSON give the answers the Python app gave, send FBR
/// the requests it sent and keep the rows it kept, for success, every kind of failure, and retries.
/// The Python app played the same scenarios on the same invoices on 29 Oct 2026, with requests.post/get
/// replaced by a stand-in answering exactly what <see cref="FakeFbr"/> answers here
/// (Golden/capture/SubmissionScenario.py); its normalised results are Golden/SubmissionScenario.json and
/// Golden/SettingsScenario.json.
/// </summary>
public partial class SubmissionParityTests
{
    private const string HsError = """{"error": "Provide proper HS Code", "statusCode": "01"}""";
    private const string Gateway = "<html><body><h1>502 Bad Gateway</h1>nginx</body></html>";
    private const string NoErrorKey = """{"validationResponse": {"statusCode": "01", "status": "Invalid", "invoiceStatuses": [{"itemSNo": "1", "errorCode": "0052", "error": "HS “x”"}]}, "dated": "2026-10-29 10:00:01"}""";
    private const string Reported = """{"fbrInvoiceNumber": "7000007DI1747119701593", "submissionId": "S-77", "dated": "2026-10-29 10:00:02", "validationResponse": {"statusCode": "00", "status": "Valid"}}""";

    /// <summary>Roles of the scenario's invoices, in the order of <see cref="InvoicesAsync"/>.</summary>
    internal static readonly string[] Roles = ["x", "y", "z", "w", "v"];

    /// <summary>The scenario's five invoices (x: reported after three failures, y: no payment status, z: a draft,
    /// w: a debit note, v: per-line tax), numbered with a fresh tag.</summary>
    internal static async Task<(string Tag, int[] Ids)> InvoicesAsync()
    {
        string tag = Guid.NewGuid().ToString("N")[..5];
        int x = await FbrData.InvoiceAsync($"SI-PAR-{tag}-X",
            [new(2m, 1500m, TaxPct: 18m, Description: "Inverter — 5kW"), new(1m, 2.675m, DiscAmount: 0.005m, TaxPct: 18m)],
            [new(250m), new(10m, "absorb", false)], furtherTax: true);
        int y = await FbrData.InvoiceAsync($"SI-PAR-{tag}-Y", [new(1m, 10m)], paymentStatus: null);
        int z = await FbrData.InvoiceAsync($"SI-PAR-{tag}-Z", [new(1m, 10m)], status: "unapproved");
        int w = await FbrData.InvoiceAsync($"DN-PAR-{tag}-W", [new(3m, 0.1m, TaxPct: 18m)]);
        int v = await FbrData.InvoiceAsync($"SI-PAR-{tag}-V", [new(1m, 99.99m)], taxMode: "individual", totalTax: 18m);
        return (tag, [x, y, z, w, v]);
    }

    internal static List<Step> Steps(int[] ids) => Steps(ids[0], ids[1], ids[2], ids[3], ids[4]);

    private static List<Step> Steps(int x, int y, int z, int w, int v) =>
    [
        new("POST", $"/fbr/submit/{x}", x, [new(400, HsError)]),
        new("POST", "/fbr/retry/{rec}", x, [new(502, Gateway, "text/html")]),
        new("POST", "/fbr/retry/{rec}", x, [new(400, NoErrorKey)]),
        new("POST", "/fbr/retry/{rec}", x, [new(200, Reported)]),
        new("POST", $"/fbr/submit/{x}", x),                                   // already reported: FBR is not called
        new("POST", "/fbr/retry/{rec}", x),                                   // nor here
        new("GET", $"/fbr/status/{x}", x, [new(200, """{"status": "Valid", "amount": 1.50, "n": 10}""")]),
        new("GET", "/fbr/record/{rec}", x),
        new("POST", $"/fbr/validate/{x}", x, [new(400, """{"error": {"code": "0052", "detail": "x"}, "z": 1}""")]),
        new("POST", $"/fbr/validate/{x}", x, [new(400, """[1, "two"]""")]),
        new("POST", $"/fbr/validate/{x}", x, [new(200, """{"validationResponse": {"status": "Valid", "statusCode": "00"}}""")]),
        new("POST", $"/fbr/submit/{y}", y),                                   // no payment status: "Failed to build payload"
        new("POST", $"/fbr/submit/{z}", z),                                   // a draft
        new("POST", $"/fbr/submit/{w}", w, [new(200, """{"fbrInvoiceNumber": 123, "submissionId": null}""")]),
        new("POST", $"/fbr/submit/{v}", v, [new(200, "")]),                    // an empty answer is {}
        new("GET", $"/fbr/status/{v}", v),                                     // reported without a number: no call
        new("GET", $"/fbr/status/{z}", z),                                     // no record: 404 JSON
    ];

    /// <summary>
    /// What a run of the scenario left, comparable between runs: the tag → "TAG", invoice ids → their
    /// role, record ids → "rec_&lt;role&gt;", the submitter's id/name → "owner_b", times → their format.
    /// </summary>
    internal static object Normalise(Result result, IReadOnlyList<List<FbrInvoiceRecord>> rows, int[] ids, string tag, string ownerName)
    {
        var invoiceRole = ids.Select((id, i) => (id, Roles[i])).ToDictionary(p => p.id, p => p.Item2);
        var recordRole = new Dictionary<int, string>();
        for (int i = 0; i < rows.Count; i++)
            for (int k = 0; k < rows[i].Count; k++)
                recordRole[rows[i][k].Id] = "rec_" + Roles[i] + (k == 0 ? "" : "_" + k);
        string Text(string? s) => s is null ? null! : s.Replace(tag, "TAG");
        string Body(string body)
        {
            body = Text(body);
            body = RecordIds().Replace(body, m => $"\"{m.Groups[1].Value}\":\"{(recordRole.TryGetValue(int.Parse(m.Groups[2].Value), out var r) ? r : "?" + m.Groups[2].Value)}\"");
            body = InvoiceIds().Replace(body, m => $"\"inv_invoice_id\":\"{(invoiceRole.TryGetValue(int.Parse(m.Groups[1].Value), out var r) ? r : "?")}\"");
            body = body.Replace($"\"submitted_by\":\"{ownerName}\"", "\"submitted_by\":\"owner_b\"");
            return SubmittedAt().Replace(body, m => "\"submitted_at\":\"" + Regex.Replace(m.Groups[1].Value, "[0-9]", "9") + "\"");
        }
        return new
        {
            responses = result.Responses.Select((r, i) => new
            {
                step = i,
                status = r.Status,
                body = r.Status == 404 && !r.Body.StartsWith('{') ? null : Body(r.Body),
                location = r.Location,
            }).ToList(),
            sent = result.Sent.Select(s => new { method = s.Method, url = s.Url, body = Text(s.Body), auth = s.Auth }).ToList(),
            rows = rows.Select((list, i) => new
            {
                invoice = Roles[i],
                records = list.Select(r => new
                {
                    company = r.CompanyId == FbrData.CompanyB ? "B" : r.CompanyId?.ToString(),
                    invoice = r.InvInvoiceId is int iid && invoiceRole.TryGetValue(iid, out var role) ? role : "?",
                    fbr_invoice_number = r.FbrInvoiceNumber, fbr_submission_id = r.FbrSubmissionId, status = r.Status,
                    request_json = Text(r.RequestJson), response_json = Text(r.ResponseJson), error_message = Text(r.ErrorMessage),
                    invoice_type = r.InvoiceType, attempt_count = r.AttemptCount,
                    submitted_by = r.SubmittedBy == FbrData.OwnerB ? "owner_b" : r.SubmittedBy?.ToString(),
                    submitted_at = r.SubmittedAt is not null, created_at = r.CreatedAt is not null,
                }).ToList(),
            }).ToList(),
        };
    }

    [GeneratedRegex("\"(record_id|id)\":(\\d+)")]
    private static partial Regex RecordIds();

    [GeneratedRegex("\"inv_invoice_id\":(\\d+)")]
    private static partial Regex InvoiceIds();

    [GeneratedRegex("\"submitted_at\":\"([^\"]*)\"")]
    private static partial Regex SubmittedAt();

    internal static async Task<string> OwnerNameAsync()
    {
        await using var db = FbrData.Db();
        return await db.Users.Where(u => u.Id == FbrData.OwnerB).Select(u => u.FullName).SingleAsync() ?? "";
    }

    internal static async Task<List<List<FbrInvoiceRecord>>> RowsAsync(int[] ids)
    {
        var rows = new List<List<FbrInvoiceRecord>>();
        foreach (int id in ids)
            rows.Add(await FbrData.RecordsAsync(id));
        return rows;
    }

    [ReferenceFact]
    public async Task Send_retry_validate_and_status_match_python()
    {
        await FbrData.ConfigureAsync();
        var (tag, ids) = await InvoicesAsync();
        await using var host = await ModuleHost.StartAsync(typeof(FbrModule).Assembly, typeof(FakeFbrModule).Assembly);
        var dotnet = await DotnetAsync(Steps(ids), host.ClientFor(FbrData.OwnerB, FbrData.CompanyB));
        var rows = await RowsAsync(ids);

        // The scenario reported X once, after three failed attempts on one record.
        Assert.Single(rows[0]);
        Assert.Equal(("success", 4), (rows[0][0].Status, rows[0][0].AttemptCount));
        Golden.Match("SubmissionScenario", Normalise(dotnet, rows, ids, tag, await OwnerNameAsync()));
    }

    /// <summary>The settings form (saved twice) and the connection test against a working and a broken FBR.</summary>
    internal static List<Step> SettingsSteps() =>
    [
        new("POST", "/fbr/settings/", 0, Form: new()
        {
            ["environment"] = "live", ["access_token"] = "tok-123\r\nsecond", ["seller_ntn"] = "N-1", ["seller_strn"] = "S-1",
            ["seller_cnic"] = "C-1", ["seller_name"] = "Näme", ["seller_business_name"] = "Biz “B”", ["seller_address"] = "Addr\r\nLine 2",
            ["seller_phone"] = "0300", ["seller_email"] = "a@b.test", ["is_active"] = "on",
        }),
        new("POST", "/fbr/settings/", 0, Form: new() { ["environment"] = "sandbox", ["access_token"] = "tok" }),
        new("POST", "/fbr/settings/test-connection", 0, [new(200, "OK", "text/plain")]),
        new("POST", "/fbr/settings/test-connection", 0, [new(503, "down", "text/plain")]),
    ];

    /// <summary>Statuses and redirects of every step, bodies of the 200s, the requests sent, and the settings row kept.</summary>
    internal static object NormaliseSettings(Result result, FbrConfig row) => new
    {
        responses = result.Responses.Select((r, i) => new { step = i, status = r.Status, location = r.Location, body = r.Status == 200 ? r.Body : null }).ToList(),
        sent = result.Sent.Select(s => new { method = s.Method, url = s.Url, body = s.Body, auth = s.Auth }).ToList(),
        row = new
        {
            company = row.CompanyId == FbrData.CompanyB ? "B" : row.CompanyId?.ToString(),
            environment = row.Environment, access_token = row.AccessToken, seller_ntn = row.SellerNtn, seller_strn = row.SellerStrn,
            seller_cnic = row.SellerCnic, seller_name = row.SellerName, seller_business_name = row.SellerBusinessName,
            seller_address = row.SellerAddress, seller_phone = row.SellerPhone, seller_email = row.SellerEmail,
            is_active = row.IsActive, token_expiry = row.TokenExpiry,
            created = row.CreatedAt is not null, updated_not_before_created = row.UpdatedAt >= row.CreatedAt,
        },
    };

    internal static async Task<FbrConfig> ConfigRowAsync()
    {
        await using var db = FbrData.Db();
        var rows = await db.Set<FbrConfig>().AsNoTracking().OrderBy(c => c.Id).ToListAsync();
        Assert.Single(rows);
        return rows[0];
    }

    [ReferenceFact]
    public async Task Settings_and_test_connection_match_python()
    {
        await FbrData.RemoveConfigAsync();
        try
        {
            await using var host = await ModuleHost.StartAsync(typeof(FbrModule).Assembly, typeof(FakeFbrModule).Assembly);
            var dotnet = await DotnetAsync(SettingsSteps(), host.ClientFor(FbrData.OwnerB, FbrData.CompanyB));
            Golden.Match("SettingsScenario", NormaliseSettings(dotnet, await ConfigRowAsync()));
        }
        finally
        {
            await FbrData.ConfigureAsync();
        }
    }
}
