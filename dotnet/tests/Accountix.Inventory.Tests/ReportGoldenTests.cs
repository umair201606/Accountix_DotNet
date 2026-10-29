using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Accountix.Core.Data;
using Accountix.Core.Shared;
using Accountix.Platform.Provisioning;
using Accountix.Platform.Services;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Inventory.Tests;

/// <summary>
/// The inventory reports of a company of their own (Golden/report_data.sql: products with dated receipts
/// and issues, a back-dated adjustment, a re-costing, movements, one document of each previewable type),
/// read through the .NET pages in a fixed order, and compared with what the Python app's own routes
/// answered for the same rows (Golden/reports.json, captured by Golden/capture/reports.py from the same
/// SQL file before Python was removed): every export's table, the stock-ledger JSON, each page's table
/// text, the print previews and the dashboard's figures.
/// </summary>
public class ReportGoldenTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The capture's steps, in its order (the dashboard last: it brings P2's drifted on-hand back to its ledger).</summary>
    private static readonly string[][] Steps =
    [
        ["csv", "valuation_feb_mar", "/inventory/reports/valuation?from=2026-02-01&as_of=2026-03-31&format=csv"],
        ["csv", "valuation_2026", "/inventory/reports/valuation?from=2026-01-01&as_of=2026-12-31&format=csv"],
        ["csv", "low_stock", "/inventory/reports/low-stock?format=csv"],
        ["csv", "sub_ledgers", "/inventory/vouchers/product-ledger/list?format=csv"],
        ["csv", "stock_ledger_p1", "/inventory/reports/stock-ledger?product_id=${p1}&format=csv"],
        ["csv", "stock_ledger_p2", "/inventory/reports/stock-ledger?product_id=${p2}&format=csv"],
        ["csv", "product_ledger_p1", "/inventory/vouchers/product-ledger?product_id=${p1}&format=csv"],
        ["csv", "product_ledger_p2", "/inventory/vouchers/product-ledger?product_id=${p2}&format=csv"],
        ["json", "stock_ledger_json_p1", "/inventory/reports/api/stock-ledger-json?product_id=${p1}"],
        ["json", "stock_ledger_json_p2", "/inventory/reports/api/stock-ledger-json?product_id=${p2}"],
        ["rows", "stock_list", "/inventory/stock/?q=RPT-${tag}"],
        ["rows", "stock_list_low", "/inventory/stock/?q=rpt-${tag_lower}&low_only=1"],
        ["rows", "movements_p1", "/inventory/stock/movements?product_id=${p1}"],
        ["rows", "stock_ledger_page_p1", "/inventory/reports/stock-ledger?product_id=${p1}"],
        ["rows", "product_ledger_page_p1", "/inventory/vouchers/product-ledger?product_id=${p1}"],
        ["rows", "product_ledger_page_p2", "/inventory/vouchers/product-ledger?product_id=${p2}"],
        ["rows", "sub_ledgers_page", "/inventory/vouchers/product-ledger/list"],
        ["rows", "low_stock_page", "/inventory/reports/low-stock"],
        ["rows", "valuation_page", "/inventory/reports/valuation?from=2026-02-01&as_of=2026-03-31"],
        ["preview", "preview_cons", "/inventory/vouchers/preview/CONS/${cons}"],
        ["preview", "preview_scrap", "/inventory/vouchers/preview/SCRAP/${scrap}"],
        ["preview", "preview_adj", "/inventory/vouchers/preview/ADJ/${adj}"],
        ["preview", "preview_pi", "/inventory/vouchers/preview/PI/${pi}"],
        ["preview", "preview_pr", "/inventory/vouchers/preview/PR/${pr}"],
        ["dashboard", "dashboard", "/inventory/"],
    ];

    [ReferenceFact]
    public async Task The_reports_show_what_python_showed_for_the_same_rows()
    {
        Golden.SameInputs("reports", Steps);
        string tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var values = new Dictionary<string, string>
        {
            ["user"] = ReportKit.OwnerB.ToString(Inv), ["tag"] = tag, ["tag_lower"] = tag.ToLowerInvariant(),
            ["slug"] = "inventory-reports-" + tag.ToLowerInvariant(),
        };
        await RunSqlFileAsync(values);

        await using var host = await ReportKit.HostAsync();
        var c = host.ClientFor(ReportKit.OwnerB, int.Parse(values["company"], Inv));
        foreach (var step in Steps)
        {
            string url = Regex.Replace(step[2], @"\$\{(\w+)\}", m => values[m.Groups[1].Value]);
            Golden.Match("reports", Norm(step[0], await StepLinesAsync(c, step[0], url), values), step[1]);
        }
    }

    /// <summary>Golden/report_data.sql block by block (the capture ran the same file the same way).</summary>
    private static async Task RunSqlFileAsync(Dictionary<string, string> values)
    {
        string script = await File.ReadAllTextAsync(Path.Combine(Golden.Dir, "report_data.sql"));
        await using var db = Reference.NewDb(new CompanyContext());
        DbConnection conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        foreach (Match m in Regex.Matches(script.Replace("\r\n", "\n"), @"^-- @(\w+)[ \t]*\n(.*?)(?=^-- @|\z)", RegexOptions.Multiline | RegexOptions.Singleline))
        {
            string name = m.Groups[1].Value, sql = m.Groups[2].Value.Trim();
            if (name == "provision")
            {
                await using var setup = Reference.NewDb(new CompanyContext());
                await new CompanySetup(setup, new FirstUse(setup, TimeProvider.System), TimeProvider.System)
                    .ProvisionAsync(int.Parse(values["company"], Inv));
                continue;
            }
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = Regex.Replace(sql, @"\$\{(\w+)\}", x => values[x.Groups[1].Value]);
            values[name] = Convert.ToString(await cmd.ExecuteScalarAsync(), Inv)!;
        }
    }

    private static async Task<List<string>> StepLinesAsync(PageClient c, string kind, string url)
    {
        if (kind == "csv")
        {
            CapturingReportExport.Last = null;
            var r = await c.GetAsync(url);
            Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url}: {(int)r.StatusCode}");
            var t = CapturingReportExport.Last ?? throw new InvalidOperationException($"{url} exported nothing");
            return [string.Join("|", t.Columns.Select(x => Canon(x.Header))), .. t.Rows.Select(row => string.Join("|", row.Cells.Select(x => Canon(Cell(x)))))];
        }
        string page = await ReportKit.PageAsync(c, url);
        switch (kind)
        {
            case "json":
                using (var doc = JsonDocument.Parse(page))
                    return doc.RootElement.EnumerateArray().Select(row => string.Join("|", row.EnumerateObject()
                        .OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => $"{p.Name}={JsonValue(p.Value)}"))).ToList();
            case "rows":
                return page.Contains("<tbody>")
                    ? ReportKit.Rows(page, "<tbody>", "</tbody>")
                    : ["empty " + ReportKit.Text(page, "<p class=\"text-muted\"", "</p>")];
            case "preview":
                return [ReportKit.Text(page, "<title>", "</title>"), ReportKit.Text(page, "<div class=\"voucher-header\">", "<div class=\"actions\">")];
            case "dashboard":
                return [ReportKit.Text(page, "<div class=\"stats-grid\">", "<!-- Alerts -->"), ReportKit.Text(page, "<!-- Voucher Stats -->", "<style>")];
            default:
                throw new ArgumentException(kind);
        }
    }

    /// <summary>An export cell as Python's CSV writer printed it.</summary>
    private static string Cell(object? v) => v switch
    {
        null => "",
        DateOnly d => d.ToString("yyyy-MM-dd", Inv),
        DateTime t => t.ToString("yyyy-MM-ddTHH:mm:ss", Inv),
        IFormattable f => f.ToString(null, Inv),
        _ => v.ToString() ?? "",
    };

    private static string JsonValue(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null => "null",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => Canon(e.GetRawText()),
        JsonValueKind.String => e.GetString()!,
        _ => e.GetRawText(),
    };

    private static readonly Regex Number = new(@"^-?\d+(\.\d+)?([eE][-+]?\d+)?$");

    /// <summary>A number by value: 10 decimals (Python's Decimal and .NET's decimal part in a quotient's 28th digit), no trailing zeros.</summary>
    private static string Canon(string s)
    {
        if (!Number.IsMatch(s)) return s;
        decimal d = Math.Round(decimal.Parse(s, NumberStyles.Float, Inv), 10, MidpointRounding.ToEven);
        return d == 0 ? "0" : d.ToString("0.##########", Inv);
    }

    /// <summary>The capture's roles, only where an id stands (a fresh database has small ids that equal figures).</summary>
    private static List<string> Norm(string kind, List<string> lines, Dictionary<string, string> values)
    {
        var rowsOf = values.Where(kv => kv.Key.StartsWith("row_")).ToDictionary(kv => kv.Value, kv => $"<{kv.Key}>");
        return lines.Select(line =>
        {
            if (kind == "json")
                line = Regex.Replace(line, @"(^|\|)id=(\d+)(?=\||$)", m => m.Groups[1].Value + "id=" + rowsOf.GetValueOrDefault(m.Groups[2].Value, m.Groups[2].Value));
            if (kind == "rows")
            {
                int sp = line.IndexOf(' ');
                string first = sp < 0 ? line : line[..sp];
                if (rowsOf.TryGetValue(first, out var role))
                    line = role + (sp < 0 ? "" : line[sp..]);
            }
            line = Regex.Replace(line, "#" + values["adj"] + @"\b", "#<adj>");
            line = line.Replace($"Approved by: {values["user"]} ", "Approved by: <owner_b> ");
            return line.Replace(values["tag"], "TAG");
        }).ToList();
    }
}
