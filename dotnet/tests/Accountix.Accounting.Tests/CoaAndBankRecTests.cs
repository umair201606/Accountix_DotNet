using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Accounting.Tests;

public class CoaAndBankRecTests
{
    private static int OwnerB => Reference.UserId(Reference.Email("owner_b"));
    private static int CompanyB => Reference.Company("b");

    private static AccountixDbContext Db()
    {
        var company = new CompanyContext();
        company.Set(CompanyB);
        return Reference.NewDb(company);
    }

    [ReferenceFact]
    public async Task Accounts_are_added_under_their_parent_edited_and_deleted_or_retired()
    {
        await using var host = await ModuleHost.StartAsync(typeof(AccountingModule).Assembly);
        var c = host.ClientFor(OwnerB, CompanyB);
        int parent;
        await using (var db = Db())
            parent = db.Set<ChartOfAccount>().Single(a => a.Code == "5-04-01-01").Id;
        string name = "Test expense " + Guid.NewGuid().ToString("N")[..6];
        var r = await c.PostFormAsync("/accounting/coa/add",
            [new("name", name), new("parent_id", parent.ToString()), new("description", ""), new("cash_flow_activity", ""), new("pl_section", "admin")],
            "/accounting/coa/add");
        Assert.Equal("/accounting/coa/", r.Headers.Location!.OriginalString);
        ChartOfAccount acct;
        await using (var db = Db())
            acct = db.Set<ChartOfAccount>().Single(a => a.Name == name);
        Assert.Equal(5, acct.Level);
        Assert.StartsWith("5-04-01-01-", acct.Code);
        Assert.Equal("admin", acct.PlSection);

        string list = await (await c.GetAsync("/accounting/coa/")).Content.ReadAsStringAsync();
        Assert.Contains(acct.Code, list);

        await c.PostFormAsync($"/accounting/coa/{acct.Id}/edit",
            [new("name", name + " x"), new("description", "d"), new("is_active", "1"), new("cash_flow_activity", "operating"), new("pl_section", "")],
            $"/accounting/coa/{acct.Id}/edit");
        await using (var db = Db())
        {
            var a = db.Set<ChartOfAccount>().Single(x => x.Id == acct.Id);
            Assert.Equal(name + " x", a.Name);
            Assert.Equal("operating", a.CashFlowActivity);
            Assert.Null(a.PlSection);
        }

        await c.PostFormAsync($"/accounting/coa/{acct.Id}/delete", [], "/accounting/coa/");
        await using (var db = Db())
            Assert.False(db.Set<ChartOfAccount>().Any(x => x.Id == acct.Id));

        int fixedId;
        await using (var db = Db())
            fixedId = db.Set<ChartOfAccount>().Single(a => a.Code == "1-01-01").Id;
        await c.PostFormAsync($"/accounting/coa/{fixedId}/delete", [], "/accounting/coa/");
        await using (var db = Db())
            Assert.True(db.Set<ChartOfAccount>().Any(x => x.Id == fixedId));
    }

    [ReferenceFact]
    public async Task The_next_code_api_skips_used_segments()
    {
        await using var host = await ModuleHost.StartAsync(typeof(AccountingModule).Assembly);
        var c = host.ClientFor(OwnerB, CompanyB);
        int parent;
        await using (var db = Db())
            parent = db.Set<ChartOfAccount>().Single(a => a.Code == "1-01-01-02").Id;
        using var json = JsonDocument.Parse(await (await c.GetAsync($"/accounting/coa/api/next-code?parent_id={parent}")).Content.ReadAsStringAsync());
        Assert.StartsWith("1-01-01-02-", json.RootElement.GetProperty("code").GetString());
        Assert.Equal(5, json.RootElement.GetProperty("level").GetInt32());
    }

    // ── bank reconciliation, against Python's pages (Golden/BankRec.json, capture/bank_rec.py) ──

    private const string GoldenBank = "Golden Rec Bank";

    private static readonly Regex Flash = new(@"class=""app-flash app-flash-(\w+)""[^>]*>.*?<span class=""app-flash-msg"">(.*?)</span>", RegexOptions.Singleline);

    private static string Text(string html) =>
        Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")), @"\s+", " ").Trim();

    /// <summary>The page's content (header to the end of the statement), as capture/bank_rec.py cut it.</summary>
    private static string Content(string html)
    {
        int start = html.IndexOf("<div class=\"page-header\">", StringComparison.Ordinal);
        int stmt = html.IndexOf("br-stmt", start, StringComparison.Ordinal);
        int end = html.IndexOf("</table>", stmt, StringComparison.Ordinal) + "</table>".Length;
        return html[start..end];
    }

    /// <summary>
    /// What a reconciliation page says, read the same way from both apps' pages: heading, status, the four
    /// figures (and whether the difference shows as settled), the statement input, every open line (ticked,
    /// its net, date, voucher, details, receipt, payment), the buttons and the reconciliation statement, plus
    /// the flashes. The account and the vouchers are written as tokens ({code}, {bank}, receipt, payment).
    /// </summary>
    private static object Read(string content, IEnumerable<string[]> flashes, string code, string name, IReadOnlyDictionary<string, string> vouchers)
    {
        string T(string s)
        {
            s = s.Replace(code, "{code}").Replace(name, "{bank}");
            foreach (var (number, role) in vouchers) s = s.Replace(number, "{" + role + "}");
            return s;
        }
        string Between(string from, string to)
        {
            int i = content.IndexOf(from, StringComparison.Ordinal);
            return i < 0 ? "" : content[i..content.IndexOf(to, i + from.Length, StringComparison.Ordinal)];
        }
        var rows = Regex.Matches(Between("<table class=\"table br-table\">", "</table>"), "<tr>(.*?)</tr>", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value).Where(r => r.Contains("<td", StringComparison.Ordinal)).Select(r => new
            {
                ticked = Regex.IsMatch(r, @"\schecked[\s>=]"),
                disabled = Regex.IsMatch(r, @"\sdisabled[\s>=]"),
                net = Regex.Match(r, "data-net=\"([^\"]*)\"").Groups[1].Value,
                cells = Regex.Matches(r, "<td[^>]*>(.*?)</td>", RegexOptions.Singleline).Select(c => T(Text(c.Groups[1].Value))).ToList(),
            }).ToList();
        return new
        {
            heading = T(Text(Regex.Match(content, "<h1>(.*?)</h1>", RegexOptions.Singleline).Groups[1].Value)),
            status = Text(Regex.Match(content, "<span class=\"badge[^\"]*\">(.*?)</span>").Groups[1].Value),
            figures = Regex.Matches(content, "<div class=\"stat-label\">([^<]*)</div><div class=\"stat-value([^\"]*)\"[^>]*>([^<]*)</div>")
                .Select(m => new[] { m.Groups[1].Value, m.Groups[2].Value.Trim(), m.Groups[3].Value }).ToList(),
            lines_header = T(Text(Regex.Match(content, "<div class=\"card-header\">(.*?)</div>").Groups[1].Value)),
            statement_input = Regex.Match(content, "id=\"br-statement\"[^>]*value=\"([^\"]*)\"").Groups[1].Value,
            rows,
            buttons = Regex.Matches(content, "<button[^>]*>(.*?)</button>", RegexOptions.Singleline).Select(m => Text(m.Groups[1].Value))
                .Where(b => b is "Save" or "Complete").ToList(),
            statement = Regex.Matches(Between("<table class=\"table br-stmt\">", "</table>"), "<tr[^>]*><td>(.*?)</td><td class=\"r\">(.*?)</td></tr>")
                .Select(m => new[] { m.Groups[1].Value, m.Groups[2].Value }).ToList(),
            flashes = flashes.Select(f => new[] { f[0], T(f[1]) }).ToList(),
        };
    }

    private static object ReadPage(string html, string code, string name, IReadOnlyDictionary<string, string> vouchers) =>
        Read(Content(html), Flash.Matches(html).Select(m => new[] { m.Groups[1].Value, WebUtility.HtmlDecode(m.Groups[2].Value) }), code, name, vouchers);

    /// <summary>
    /// Golden: a bank account of the test's own (company B, under 1-01-01-02 Bank Accounts) takes a bank
    /// receipt of 500 and a bank payment of 75 through the voucher form; two statements are then reconciled
    /// as Python's pages showed them, step by step: figures, open lines, ticks, refusal, completion, the
    /// cleared-before carry into the next statement and the reconciliation statement. Python's pages for the
    /// same steps are in the fixture and are read by the same code.
    /// </summary>
    [ReferenceFact]
    public async Task A_reconciliation_reads_and_completes_as_python_does()
    {
        var golden = Golden.Load("BankRec");
        await using var host = await ModuleHost.StartAsync(typeof(AccountingModule).Assembly);
        var c = host.ClientFor(OwnerB, CompanyB);
        int revenue, expense;
        await using (var db = Db())
        {
            revenue = db.Set<ChartOfAccount>().Single(a => a.Code == "4-01-01-01-0001").Id;
            expense = db.Set<ChartOfAccount>().Single(a => a.Code == "5-04-01-01-0001").Id;
        }
        var bank = await GoldenBankAsync(c);
        await ClearGoldenBankAsync(c, bank.Id);

        async Task<int> VoucherAsync(string type, string when, int account, string desc, string amount)
        {
            bool receipt = type == "BRV";
            var r = await c.PostFormAsync("/accounting/vouchers",
            [
                new("action", "approve"), new("voucher_type", type), new("voucher_date", when),
                new("cash_bank_account_id", bank.Id.ToString()), new("label_id", ""), new("notes", desc),
                new("account_id[]", account.ToString()), new("description[]", desc),
                new("debit[]", receipt ? "" : amount), new("credit[]", receipt ? amount : ""), new("label_id[]", ""),
            ], $"/accounting/vouchers?type={type}");
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            return int.Parse(r.Headers.Location!.OriginalString.Split('/').Last());
        }
        int receiptId = await VoucherAsync("BRV", "2026-06-10T10:00", revenue, "Golden receipt", "500");
        int paymentId = await VoucherAsync("BPV", "2026-06-20T10:00", expense, "Golden payment", "75");
        await c.GetAsync("/accounting/vouchers/list");   // shows (and so clears) the vouchers' own flashes
        Dictionary<string, string> vouchers;
        Dictionary<string, int> lineOf;
        await using (var db = Db())
        {
            vouchers = new()
            {
                [db.Set<AccountingVoucher>().Single(v => v.Id == receiptId).VoucherNumber] = "receipt",
                [db.Set<AccountingVoucher>().Single(v => v.Id == paymentId).VoucherNumber] = "payment",
            };
            lineOf = (await db.Set<JournalLine>().Where(l => l.AccountId == bank.Id && l.JournalEntry.IsPosted == true)
                .Select(l => new { l.JournalEntry.VoucherType, l.Id }).ToListAsync()).ToDictionary(l => l.VoucherType, l => l.Id);
        }

        var pyVouchers = golden["vouchers"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>());
        string pyCode = golden["account"]!["code"]!.GetValue<string>(), pyName = golden["account"]!["name"]!.GetValue<string>();
        async Task StepAsync(string step, int recId)
        {
            string html = await (await c.GetAsync($"/accounting/bank-reconciliation/{recId}")).Content.ReadAsStringAsync();
            var py = golden["steps"]![step]!;
            var expected = Read(py["content"]!.GetValue<string>(),
                                py["flashes"]!.AsArray().Select(f => new[] { f![0]!.GetValue<string>(), f[1]!.GetValue<string>() }),
                                pyCode, pyName, pyVouchers);
            Golden.Equal(Golden.Node(expected), ReadPage(html, bank.Code, bank.Name, vouchers), $"BankRec {step}");
        }
        async Task<int> NewAsync(string date, string balance)
        {
            var r = await c.PostFormAsync("/accounting/bank-reconciliation/new",
                [new("account_id", bank.Id.ToString()), new("statement_date", date), new("statement_balance", balance)], "/accounting/bank-reconciliation");
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            return int.Parse(r.Headers.Location!.OriginalString.Split('/').Last());
        }
        Task SaveAsync(int recId, string balance, string action, params int[] lines) =>
            c.PostFormAsync($"/accounting/bank-reconciliation/{recId}/save",
                [new("statement_balance", balance), new("action", action), .. lines.Select(l => new KeyValuePair<string, string>("line_ids[]", l.ToString()))],
                $"/accounting/bank-reconciliation/{recId}");

        int r1 = await NewAsync("2026-06-30", "500");
        await StepAsync("rec1_new", r1);
        await SaveAsync(r1, "500", "save", lineOf["BRV"]);
        await StepAsync("rec1_saved", r1);
        await SaveAsync(r1, "500", "complete", lineOf["BRV"]);
        await StepAsync("rec1_done", r1);
        int r2 = await NewAsync("2026-07-31", "425");
        await StepAsync("rec2_new", r2);
        await SaveAsync(r2, "425", "complete");
        await StepAsync("rec2_refused", r2);
        await SaveAsync(r2, "425", "complete", lineOf["BPV"]);
        await StepAsync("rec2_done", r2);
        await using (var db = Db())
            Assert.Equal(["completed", "completed"], db.Set<BankReconciliation>().Where(r => r.Id == r1 || r.Id == r2).Select(r => r.Status).ToList());

        await ClearGoldenBankAsync(c, bank.Id);
        await using (var db = Db())
            Assert.False(db.Set<BankReconciliation>().Any(r => r.AccountId == bank.Id));
    }

    /// <summary>The test's bank account, added through the chart form on the first run and reused after.</summary>
    private static async Task<ChartOfAccount> GoldenBankAsync(PageClient c)
    {
        await using (var db = Db())
            if (await db.Set<ChartOfAccount>().AsNoTracking().SingleOrDefaultAsync(a => a.Name == GoldenBank) is { } found)
                return found;
        int parent;
        await using (var db = Db())
            parent = db.Set<ChartOfAccount>().Single(a => a.Code == "1-01-01-02").Id;
        var r = await c.PostFormAsync("/accounting/coa/add",
            [new("name", GoldenBank), new("parent_id", parent.ToString()), new("description", ""), new("cash_flow_activity", ""), new("pl_section", "")],
            "/accounting/coa/add");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        await using (var db = Db())
            return await db.Set<ChartOfAccount>().AsNoTracking().SingleAsync(a => a.Name == GoldenBank);
    }

    /// <summary>
    /// Leave the account with no reconciliation and no posted voucher (what an earlier, interrupted run
    /// left too): reconciliations reopened newest first and deleted, vouchers unapproved and deleted, all
    /// through the app's routes.
    /// </summary>
    private static async Task ClearGoldenBankAsync(PageClient c, int bankId)
    {
        List<(int Id, string Status)> recs;
        List<(int Id, string Status)> vouchers;
        await using (var db = Db())
        {
            recs = (await db.Set<BankReconciliation>().Where(r => r.AccountId == bankId).OrderByDescending(r => r.StatementDate)
                .Select(r => new { r.Id, r.Status }).ToListAsync()).Select(r => (r.Id, r.Status ?? "")).ToList();
            vouchers = (await db.Set<AccountingVoucher>().Where(v => v.CashBankAccountId == bankId)
                .Select(v => new { v.Id, v.Status }).ToListAsync()).Select(v => (v.Id, v.Status!)).ToList();
        }
        foreach (var (id, status) in recs)
        {
            if (status == "completed")
                await c.PostFormAsync($"/accounting/bank-reconciliation/{id}/reopen", [], $"/accounting/bank-reconciliation/{id}");
            await c.PostFormAsync($"/accounting/bank-reconciliation/{id}/delete", [], $"/accounting/bank-reconciliation/{id}");
        }
        foreach (var (id, status) in vouchers)
        {
            if (status == "approved")
                await c.PostFormAsync($"/accounting/vouchers/{id}/unapprove", [], $"/accounting/vouchers/{id}");
            await c.PostFormAsync($"/accounting/vouchers/{id}/delete", [], $"/accounting/vouchers/{id}");
        }
        await c.GetAsync("/accounting/vouchers/list");   // clear the flashes these left
    }
}
