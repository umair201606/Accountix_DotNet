using System.Net;
using System.Text.RegularExpressions;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;
using ClosedXML.Excel;
using Xunit.Abstractions;

namespace Accountix.Finance.Tests;

/// <summary>
/// The statement pages through the .NET app for the golden books (StatementSeed), against the
/// Python app's pages for the same books, captured once (Golden/capture/statement_pages.py →
/// Golden/StatementPages.json): every figure cell, the empty states, the export files' names,
/// the refusals without the view right; plus the workbooks' layout and a fixed query budget.
/// </summary>
public partial class StatementPageTests(ITestOutputHelper output)
{
    private static int Company => StatementSeed.Company;
    private static int OwnerB => StatementSeed.OwnerB;
    private static Task<ModuleHost> HostAsync() => ModuleHost.StartAsync(typeof(FinanceModule).Assembly);

    [GeneratedRegex("<td class=\"num[^\"]*\"[^>]*>(.*?)</td>", RegexOptions.Singleline)]
    private static partial Regex NumCell();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();

    /// <summary>The figure cells of a page, in order, tags and spacing stripped (as the capture read Python's).</summary>
    private static List<string> Figures(string html) =>
        NumCell().Matches(html).Select(m => Regex.Replace(WebUtility.HtmlDecode(Tag().Replace(m.Groups[1].Value, "")), @"\s+", " ").Trim()).ToList();

    private static System.Text.Json.Nodes.JsonNode Part(string key) => Golden.Load("StatementPages")[key]!;

    [ReferenceFact]
    public async Task Every_figure_on_every_statement_page_is_the_python_figure()
    {
        await StatementSeed.EnsureAsync();
        var golden = Part("figures").AsObject();
        Assert.Equal(20, golden.Count);
        await using var host = await HostAsync();
        var c = host.ClientFor(OwnerB, Company);
        var net = new System.Text.Json.Nodes.JsonObject();
        foreach (var (url, _) in golden)
        {
            var r = await c.GetAsync(await StatementSeed.RealAsync(url));
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var mine = Figures(await r.Content.ReadAsStringAsync());
            net[url] = Golden.Node(mine);
            output.WriteLine($"{url}: {mine.Count} figures");
        }
        Golden.Equal(golden, net, "StatementPages.figures");
    }

    [ReferenceFact]
    public async Task Bare_opens_wait_for_view_as_python_did()
    {
        await StatementSeed.EnsureAsync();
        var golden = Part("bare").AsObject();
        await using var host = await HostAsync();
        var c = host.ClientFor(OwnerB, Company);
        var net = new System.Text.Json.Nodes.JsonObject();
        foreach (var (url, expected) in golden)
        {
            string html = await (await c.GetAsync(url)).Content.ReadAsStringAsync();
            string says = expected!["says"]!.GetValue<string>();
            net[url] = Golden.Node(new { figures = Figures(html), says = html.Contains(says, StringComparison.Ordinal) ? says : "(missing) " + says });
        }
        Golden.Equal(golden, net, "StatementPages.bare");
        // The ledger lists no account until asked.
        string ledger = await (await c.GetAsync("/finance/ledger")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("class=\"ledger-section\"", ledger);
    }

    [ReferenceFact]
    public async Task Exports_are_files_named_as_python_named_them()
    {
        await StatementSeed.EnsureAsync();
        var golden = Part("exports").AsObject();
        // An export asked for without a period is named by the company's today and the period holding it:
        // the capture wrote those dates as tokens.
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi")).DateTime);
        AccountingPeriod holding;
        await using (var db = StatementSeed.Db())
            holding = await db.Set<AccountingPeriod>().Where(p => p.StartDate <= today && p.EndDate >= today)
                .OrderByDescending(p => p.IsActive).ThenByDescending(p => p.StartDate).FirstAsync();
        string Tokens(string url, string name) => url.Contains("period_id") || url.Contains("from=") ? name
            : name.Replace(holding.StartDate.ToString("yyyy-MM-dd"), "{period_start}").Replace(holding.EndDate.ToString("yyyy-MM-dd"), "{period_end}")
                  .Replace(today.ToString("yyyy-MM-dd"), "{today}");
        await using var host = await HostAsync();
        var c = host.ClientFor(OwnerB, Company);
        var net = new System.Text.Json.Nodes.JsonObject();
        foreach (var (url, _) in golden)
        {
            var r = await c.GetAsync(await StatementSeed.RealAsync(url));
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            string name = r.Content.Headers.ContentDisposition!.FileNameStar ?? r.Content.Headers.ContentDisposition.FileName!.Trim('"');
            byte[] bytes = await r.Content.ReadAsByteArrayAsync();
            if (name.EndsWith(".xlsx")) using (new XLWorkbook(new MemoryStream(bytes))) { }
            else if (name.EndsWith(".pdf")) Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
            net[url] = Tokens(url, name);
            output.WriteLine($"{url} → {name} ({bytes.Length} bytes)");
        }
        Golden.Equal(golden, net, "StatementPages.exports");
        // Other formats are not exports on the statements: the page answers, as in Python.
        var csv = await c.GetAsync(await StatementSeed.RealAsync("/finance/trial-balance?filter_mode=period&period_id={FY 2026}&format=csv"));
        Assert.Equal("text/html", csv.Content.Headers.ContentType!.MediaType);
    }

    [ReferenceFact]
    public async Task The_workbooks_carry_python_layout()
    {
        await StatementSeed.EnsureAsync();
        int fy25 = await StatementSeed.PeriodIdAsync("FY 2025"), fy26 = await StatementSeed.PeriodIdAsync("FY 2026");
        string comp = $"filter_mode=comparative&period_id={fy26}&comp_mode=1&comp_period_ids={fy25}";
        await using var host = await HostAsync();
        var c = host.ClientFor(OwnerB, Company);
        async Task<XLWorkbook> Book(string url) => new(new MemoryStream(await (await c.GetAsync(url)).Content.ReadAsByteArrayAsync()));

        using (var tb = await Book($"/finance/trial-balance?{comp}&format=excel"))
        {
            var ws = tb.Worksheet(1);
            Assert.Equal("Trial Balance", ws.Name);
            Assert.Equal("Dr Clsg (31 December, 2025)", ws.Cell(5, 10).GetString());
            Assert.Equal("TOTAL", ws.Cell(ws.LastRowUsed()!.RowNumber(), 2).GetString());
        }
        using (var bs = await Book($"/finance/balance-sheet?{comp}&format=excel"))
        {
            var ws = bs.Worksheet("Balance Sheet");
            Assert.Equal("ASSETS", ws.Cell(5, 1).GetString());
            Assert.Equal("31 December, 2026", ws.Cell(6, 3).GetString());
            Assert.Equal(6, ws.SheetView.SplitRow);   // header row found by its fill, as _find_header_row
            Assert.Equal("LIABILITIES + EQUITY", ws.Cell(ws.LastRowUsed()!.RowNumber(), 1).GetString());
        }
        using (var pl = await Book($"/finance/profit-loss?{comp}&format=excel"))
        {
            var ws = pl.Worksheet("P&L");
            Assert.Equal(new[] { "Code", "Account", "31 December, 2026", "31 December, 2025" }, Enumerable.Range(1, 4).Select(i => ws.Cell(5, i).GetString()));
            Assert.Equal("Sales", ws.Cell(6, 1).GetString());
        }
        using (var gl = await Book($"/finance/ledger?filter_mode=period&period_id={fy25}&mode=all&format=excel"))
        {
            Assert.Equal("Summary", gl.Worksheet(1).Name);
            Assert.True(gl.Worksheets.Count > 5);
            var link = gl.Worksheet(1).Cell(6, 1);
            Assert.True(link.HasHyperlink);
            Assert.Contains(link.GetString(), gl.Worksheets.Select(w => w.Name));
        }
        using (var so = await Book($"/finance/socie?filter_mode=period&period_id={fy26}&format=excel"))
            Assert.Equal("SOCIE", so.Worksheet(1).Name);
    }

    [ReferenceFact]
    public async Task Pages_run_a_fixed_number_of_queries()
    {
        await StatementSeed.EnsureAsync();
        int fy25 = await StatementSeed.PeriodIdAsync("FY 2025"), fy26 = await StatementSeed.PeriodIdAsync("FY 2026"),
            fy24 = await StatementSeed.PeriodIdAsync("FY 2024");
        string comp = $"filter_mode=comparative&period_id={fy26}&comp_mode=1&comp_period_ids={fy25},{fy24}";
        await using var host = await HostAsync();
        var c = host.ClientFor(OwnerB, Company);
        foreach (string page in new[] { "trial-balance", "profit-loss", "balance-sheet", "socie", "cash-flow" })
        {
            (await host.MeasureAsync(c, $"/finance/{page}?{comp}", runs: 1)).Within(5000, 40);
            (await host.MeasureAsync(c, $"/finance/{page}?{comp}&format=excel", runs: 1)).Within(5000, 40);
        }
        (await host.MeasureAsync(c, "/finance/ledger?filter_mode=custom&from=2025-01-01&to=2026-12-31&mode=all", runs: 1)).Within(5000, 40);
        (await host.MeasureAsync(c, "/finance/ledger?filter_mode=custom&from=2025-01-01&to=2026-12-31&mode=all&format=excel", runs: 1)).Within(5000, 40);
        (await host.MeasureAsync(c, "/finance/label-pl?from=2025-01-01&to=2026-12-31", runs: 1)).Within(5000, 40);
        (await host.MeasureAsync(c, "/finance/", runs: 1)).Within(5000, 40);
    }

    [ReferenceFact]
    public async Task Without_the_view_right_every_statement_is_refused_as_python_refused()
    {
        await StatementSeed.EnsureAsync();
        var golden = Part("no_view").AsObject();
        await using var host = await HostAsync();
        var c = host.ClientFor(StatementSeed.NoViewUser, Company);
        var net = new System.Text.Json.Nodes.JsonObject();
        foreach (var (url, _) in golden)
        {
            var r = await c.GetAsync(url);
            Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
            net[url] = (int)r.StatusCode;
        }
        Golden.Equal(golden, net, "StatementPages.no_view");
        // The sidebar hides what the person may not open.
        var owner = host.ClientFor(OwnerB, Company);
        string html = await (await owner.GetAsync("/finance/")).Content.ReadAsStringAsync();
        Assert.Contains("href=\"/finance/twcf\"", html);
        Assert.Contains("href=\"/finance/label-pl\"", html);
    }
}
