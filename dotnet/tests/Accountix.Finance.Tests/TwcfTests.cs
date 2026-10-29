using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.Finance.Services;
using Accountix.TestSupport;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PdfSharp.Pdf.IO;
using Xunit.Abstractions;

namespace Accountix.Finance.Tests;

/// <summary>
/// The 13-week cash flow (/finance/twcf): the matrix, the page, its Excel and PDF files and the
/// forecast-line form against fixed expectations the Python app gave for exactly this data
/// (Golden/Twcf*.json, Golden/Twcf.xlsx; capture scripts Golden/capture/twcf_*.py), plus the
/// refusals, rights, queries and many users, .NET only.
/// Data: the company "golden-twcf", built through .NET by <see cref="TwcfWorld"/>.
/// </summary>
public class TwcfTests(ITestOutputHelper output)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The page and files are compared on a window wholly in the past, so they read the same whatever today is.</summary>
    private const string PageStart = "2026-07-06";
    private const string PageUrl = "/finance/twcf?start=" + PageStart;
    /// <summary>The form's own window (a redirect target; not compared with Python).</summary>
    private const string FormStart = "2026-09-07";
    private const string FormUrl = "/finance/twcf?start=" + FormStart;

    private static int Owner => TwcfWorld.Owner;

    private static AccountixDbContext Db() => TwcfWorld.Db();

    private static Task<ModuleHost> HostAsync() => ModuleHost.StartAsync(typeof(FinanceModule).Assembly);

    private static async Task<PageClient> OwnerClientAsync(ModuleHost host)
    {
        var (cid, _) = await TwcfWorld.ReadyAsync();
        return host.ClientFor(Owner, cid);
    }

    // ── Python's round(x, 2), sum() and the line expansion ──────────────

    [Fact]
    public void Round2_rounds_the_exact_binary_value_half_to_even()
    {
        Assert.Equal(433.45, TwcfReports.Round2(433.455));       // 433.45499999… in binary
        Assert.Equal(2.67, TwcfReports.Round2(2.675));
        Assert.Equal(0.12, TwcfReports.Round2(0.125));           // an exact half: to even
        Assert.Equal(0.38, TwcfReports.Round2(0.375));
        Assert.Equal(-1.0, TwcfReports.Round2(-1.005));
        Assert.Equal(1234.57, TwcfReports.Round2(1234.5678));
        Assert.Equal(466.75, TwcfReports.Round2(466.75329999999997));
        Assert.Equal("433.45", TwcfReports.Display(433.455, "en"));
        Assert.Equal("(0.00)", TwcfReports.Display(-1e-12, "en"));   // Python prints a tiny negative in brackets
        Assert.Equal("1,00,000.13", TwcfReports.Display(100000.125000001, "hi"));
    }

    private static List<double> Doubles(JsonNode? array) => array!.AsArray().Select(x => x!.GetValue<double>()).ToList();

    /// <summary>Golden/TwcfNumbers.json: 1,610 floats and Python's round(x, 2) of each, compared exactly.</summary>
    [Fact]
    public void Round2_agrees_with_python_round_on_float_sums()
    {
        var g = Golden.Load("TwcfNumbers")["round2"]!;
        var xs = Doubles(g["inputs"]);
        Assert.True(xs.Count > 1600);
        Golden.Equal(g["outputs"], xs.Select(TwcfReports.Round2).ToList(), "TwcfNumbers.round2", tolerance: 0);
    }

    /// <summary>Golden/TwcfNumbers.json: 62 lists of floats and Python's built-in sum() of each, compared exactly.</summary>
    [Fact]
    public void PySum_agrees_with_python_builtin_sum()
    {
        var g = Golden.Load("TwcfNumbers")["sum"]!;
        var lists = g["inputs"]!.AsArray().Select(Doubles).ToList();
        Golden.Equal(g["outputs"], lists.Select(l => TwcfReports.PySum(l)).ToList(), "TwcfNumbers.sum", tolerance: 0);
        Assert.Equal(1.0, TwcfReports.PySum(Enumerable.Repeat(0.1, 10)));
    }

    [Fact]
    public void Recurring_lines_expand_as_python_does()
    {
        var end = new DateOnly(2026, 6, 30);
        // Monthly on the 31st: clamped to each month's last day, never before the start.
        var monthly = TwcfReports.Occurrences(new TwcfReports.LineSpec(new DateOnly(2026, 1, 31), "monthly", 0, 31, 1, 1), end);
        Assert.Equal(new DateOnly(2026, 2, 28), monthly[1]);
        Assert.Equal(6, monthly.Count);
        // Quarterly from mid-March on the 10th: the March occurrence is before the start.
        var q = TwcfReports.Occurrences(new TwcfReports.LineSpec(new DateOnly(2026, 3, 15), "quarterly", 0, 10, 1, 1), new DateOnly(2026, 12, 31));
        Assert.Equal([new DateOnly(2026, 6, 10), new DateOnly(2026, 9, 10), new DateOnly(2026, 12, 10)], q);
        // Weekly on Friday (4) from a Saturday: the first Friday after.
        var w = TwcfReports.Occurrences(new TwcfReports.LineSpec(new DateOnly(2026, 8, 1), "weekly", 4, 1, 1, 1), new DateOnly(2026, 8, 20));
        Assert.Equal([new DateOnly(2026, 8, 7), new DateOnly(2026, 8, 14)], w);
        // Yearly on 29 Feb: clamped to the 28th in a common year.
        var y = TwcfReports.Occurrences(new TwcfReports.LineSpec(new DateOnly(2024, 1, 1), "yearly", 0, 29, 2, 1), new DateOnly(2026, 12, 31));
        Assert.Equal([new DateOnly(2024, 2, 29), new DateOnly(2025, 2, 28), new DateOnly(2026, 2, 28)], y);
        Assert.Empty(TwcfReports.Occurrences(new TwcfReports.LineSpec(new DateOnly(2026, 1, 1), "daily", 0, 1, 1, 1), end));
        Assert.Equal(new DateOnly(2026, 10, 26), TwcfReports.DefaultStart(new DateOnly(2026, 10, 29)));   // Thursday → Monday
    }

    // ── the matrix against Python's build_matrix ────────────────────────

    /// <summary>The matrix in build_matrix's shape (dates ISO; absent keys as null / false), for the goldens.</summary>
    internal static object Shape(TwcfMatrix m) => new
    {
        weeks = m.Weeks.Select(w => new { start = w.Start.ToString("yyyy-MM-dd", Inv), end = w.End.ToString("yyyy-MM-dd", Inv), is_past = w.IsPast }),
        rows = m.Rows.Select(r => new { key = r.Key, kind = r.Kind, label = r.Label, auto = r.Auto, source = r.Source, total = r.Total, values = r.Values }),
        opening = m.Opening,
        total_in = m.TotalIn,
        total_out = m.TotalOut,
        net = m.Net,
        closing = m.Closing,
        has_past_weeks = m.HasPastWeeks,
        floor = m.Floor,
        has_scope = m.HasScope,
        has_payroll_auto = m.HasPayrollAuto,
    };

    private static async Task<TwcfMatrix> MatrixAsync(AccountixDbContext db, string start, string today) =>
        await new TwcfReports(new FinanceData(db)).BuildMatrixAsync(DateOnly.Parse(start, Inv), DateOnly.Parse(today, Inv));

    /// <summary>
    /// Golden/TwcfMatrices.json: build_matrix for four windows (past weeks with actuals and variance,
    /// all ahead, July, a Wednesday start), today given, every cell compared exactly.
    /// </summary>
    [ReferenceFact]
    public async Task The_matrix_equals_python_build_matrix_cell_for_cell()
    {
        await TwcfWorld.ReadyAsync();
        var g = Golden.Load("TwcfMatrices");
        await using var db = Db();
        foreach (var w in g["windows"]!.AsArray())
        {
            string start = (string)w!["start"]!, today = (string)w["today"]!;
            var m = await MatrixAsync(db, start, today);
            Golden.Equal(w["matrix"], Shape(m), $"TwcfMatrices {start}", tolerance: 0);
        }
        // The seeded window carries actuals, variance, payroll and a FIFO advance.
        var past = await MatrixAsync(db, "2026-09-07", "2026-10-29");
        Assert.True(past.HasPastWeeks);
        Assert.True(past.HasPayrollAuto);
        Assert.Contains(past.Rows.Single(r => r.Key == "variance").Values!, v => v is double d && d != 0);
    }

    /// <summary>Golden/TwcfMatrices.json "floor": with a cash floor of 2,000.50 the floor and headroom rows follow.</summary>
    [ReferenceFact]
    public async Task A_cash_floor_adds_the_floor_and_headroom_rows_as_python_does()
    {
        await TwcfWorld.ReadyAsync();
        var g = Golden.Load("TwcfMatrices")["floor"]!;
        decimal floor = (decimal)g["cash_floor"]!.GetValue<double>();
        await using (var db = Db())
            await db.Set<ReportSetting>().ExecuteUpdateAsync(s => s.SetProperty(x => x.TwcfCashFloor, floor));
        try
        {
            await using var db = Db();
            var m = await MatrixAsync(db, (string)g["start"]!, (string)g["today"]!);
            Golden.Equal(g["matrix"], Shape(m), "TwcfMatrices floor", tolerance: 0);
            Assert.Equal(["floor", "headroom"], m.Rows.TakeLast(2).Select(r => r.Key));
        }
        finally
        {
            await using var db = Db();
            await db.Set<ReportSetting>().ExecuteUpdateAsync(s => s.SetProperty(x => x.TwcfCashFloor, (decimal?)null));
        }
    }

    // ── the page ────────────────────────────────────────────────────────

    private static string MatrixTable(string html)
    {
        int s = html.IndexOf("<table class=\"report-table statement twcf\">", StringComparison.Ordinal);
        int e = html.IndexOf("</table>", s, StringComparison.Ordinal);
        return html[s..e];
    }

    /// <summary>Every cell's text, tags stripped and white space collapsed.</summary>
    private static List<string> Cells(string table) =>
        Regex.Matches(table, "<t[hd][^>]*>(.*?)</t[hd]>", RegexOptions.Singleline)
            .Select(m => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, "<[^>]+>", " ")), @"\s+", " ").Trim())
            .ToList();

    private static List<string> CellClasses(string table) =>
        Regex.Matches(table, "<td class=\"([^\"]*)\"").Select(m => Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim()).ToList();

    private static string Kpis(string h) => string.Join("|", Regex.Matches(h, "<span class=\"k-value[^\"]*\">([^<]*)</span>").Select(m => m.Groups[1].Value));

    private static string LinesTable(string h)
    {
        int s = h.IndexOf("Forecast Lines", StringComparison.Ordinal);
        int t = h.IndexOf("<table class=\"report-table\">", s, StringComparison.Ordinal);
        return t < 0 ? "" : string.Join("|", Cells(h[t..h.IndexOf("</table>", t, StringComparison.Ordinal)]));
    }

    /// <summary>
    /// Golden/TwcfPage.json holds the Python page for the same URL, person and company; both pages
    /// are read the same way: every matrix cell and its classes, the KPI strip, the lines table.
    /// </summary>
    [ReferenceFact]
    public async Task The_page_shows_the_same_matrix_as_the_python_page()
    {
        var g = Golden.Load("TwcfPage");
        Assert.Equal(PageUrl, (string)g["url"]!);
        string pyHtml = (string)g["html"]!;

        await using var host = await HostAsync();
        var c = await OwnerClientAsync(host);
        var r = await c.GetAsync(PageUrl);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        string html = await r.Content.ReadAsStringAsync();

        Golden.Equal(Golden.Node(new
            {
                cells = Cells(MatrixTable(pyHtml)), classes = CellClasses(MatrixTable(pyHtml)), kpis = Kpis(pyHtml), lines = LinesTable(pyHtml),
            }),
            new { cells = Cells(MatrixTable(html)), classes = CellClasses(MatrixTable(html)), kpis = Kpis(html), lines = LinesTable(html) },
            "TwcfPage");
        Assert.True(Cells(MatrixTable(html)).Count > 150, "the matrix has its 13 weeks of rows");
        foreach (string id in new[] { "twcf-kpis", "twcf-explain", "twcf-preview-hint", "twcf-frequency", "twcf-recur-wrap", "twcf-this-week" })
            Assert.Contains($"id=\"{id}\"", html);
        Assert.Contains("Additional payroll", html);
        Assert.Contains("from HR payroll", html);
        Assert.Contains($"window.TWCF_WEEKS = [{{s:'{PageStart}',e:'2026-07-12'}},", html);
    }

    [ReferenceFact]
    public async Task A_bare_open_shows_the_load_prompt_and_no_matrix()
    {
        await using var host = await HostAsync();
        var c = await OwnerClientAsync(host);
        var r = await c.GetAsync("/finance/twcf");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        string html = await r.Content.ReadAsStringAsync();
        Assert.Contains("id=\"twcf-load-prompt\"", html);
        Assert.Contains("Load 13-week forecast", html);
        foreach (string absent in new[] { "Opening cash &amp; equivalents", "Net cash flow", "Forecast Lines", "id=\"twcf-kpis\"", "TWCF_WEEKS", "format=excel" })
            Assert.DoesNotContain(absent, html);
        // An export asked for before a start is chosen renders the page, as Python.
        var x = await c.GetAsync("/finance/twcf?format=excel");
        Assert.Equal("text/html", x.Content.Headers.ContentType!.MediaType);
    }

    // ── exports ─────────────────────────────────────────────────────────

    private static string FileNameOf(HttpResponseMessage r) =>
        r.Content.Headers.ContentDisposition!.FileNameStar ?? r.Content.Headers.ContentDisposition.FileName!.Trim('"');

    /// <summary>Golden/Twcf.xlsx is the Python app's file for the same window; both are read by ClosedXML and compared cell by cell.</summary>
    [ReferenceFact]
    public async Task The_excel_sheet_matches_the_python_sheet()
    {
        var p = Golden.Load("TwcfExports")["excel"]!;
        using var pyWb = new XLWorkbook(new MemoryStream(Golden.Bytes("Twcf.xlsx")));
        var pws = pyWb.Worksheet(1);

        await using var host = await HostAsync();
        var c = await OwnerClientAsync(host);
        var r = await c.GetAsync(PageUrl + "&format=excel");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal((string)p["type"]!, r.Content.Headers.ContentType!.MediaType);
        Assert.Contains(FileNameOf(r), (string)p["name"]!);
        Assert.EndsWith($"13_Week_Cash_Flow_as_at_{PageStart}.xlsx", FileNameOf(r));
        byte[] xlsx = await r.Content.ReadAsByteArrayAsync();
        if (Environment.GetEnvironmentVariable("ACCOUNTIX_DUMP_DIR") is { Length: > 0 } dump)
            File.WriteAllBytes(Path.Combine(dump, "twcf.xlsx"), xlsx);
        using var wb = new XLWorkbook(new MemoryStream(xlsx));
        var ws = wb.Worksheet(1);

        // Both files read by the same reader: names, every cell (the "Generated …" line aside), formats, merges, freeze.
        Assert.Equal(pws.Name, ws.Name);
        int lastRow = pws.LastRowUsed()!.RowNumber();
        Assert.Equal(lastRow, ws.LastRowUsed()!.RowNumber());
        Assert.True(lastRow > 15, "the sheet has the matrix rows");
        for (int row = 1; row <= lastRow; row++)
        {
            if (row == 4) continue;
            for (int col = 1; col <= 15; col++)
            {
                var a = pws.Cell(row, col);
                var b = ws.Cell(row, col);
                // Numbers to 15 significant digits: ClosedXML stores no more (openpyxl stores 16).
                bool same = a.Value.IsNumber && b.Value.IsNumber
                    ? Math.Abs(a.Value.GetNumber() - b.Value.GetNumber()) <= 1e-12 * Math.Max(1, Math.Abs(a.Value.GetNumber()))
                    : a.Value.Equals(b.Value);
                Assert.True(same, $"{b.Address}: Python '{a.Value}' vs .NET '{b.Value}'");
                if (a.Value.IsNumber)
                    Assert.True(a.Style.NumberFormat.Format == b.Style.NumberFormat.Format, $"{b.Address} format");
                Assert.True(a.Style.Font.Bold == b.Style.Font.Bold, $"{b.Address} bold");
                Assert.True(a.Style.Font.Italic == b.Style.Font.Italic, $"{b.Address} italic");
                Assert.True(a.Style.Alignment.Horizontal == b.Style.Alignment.Horizontal || row <= 5, $"{b.Address} alignment");
            }
        }
        Assert.StartsWith("Generated ", ws.Cell(4, 1).GetString());
        Assert.Equal(pws.MergedRanges.Select(m => m.RangeAddress.ToString()).Order(StringComparer.Ordinal),
                     ws.MergedRanges.Select(m => m.RangeAddress.ToString()).Order(StringComparer.Ordinal));
        Assert.Equal((pws.SheetView.SplitRow, pws.SheetView.SplitColumn), (ws.SheetView.SplitRow, ws.SheetView.SplitColumn));
        Assert.Equal((5, 1), (ws.SheetView.SplitRow, ws.SheetView.SplitColumn));      // freeze_panes "B6"
        Assert.Equal(pws.PageSetup.PageOrientation, ws.PageSetup.PageOrientation);
        Assert.Equal(XLPageOrientation.Landscape, ws.PageSetup.PageOrientation);
        // Widths 36 / 13 / 14 as set; ClosedXML adds its cell padding to what it writes, so read back
        // through ClosedXML an openpyxl width comes out ~0.7 narrower than its own.
        Assert.Equal(36, ws.Column(1).Width);
        Assert.Equal(13, ws.Column(2).Width);
        Assert.Equal(14, ws.Column(15).Width);
        for (int col = 1; col <= 15; col++)
            Assert.True(Math.Abs(pws.Column(col).Width - ws.Column(col).Width) < 1, $"column {col} width");
        Assert.Contains("\n", ws.Cell(5, 2).GetString());
    }

    /// <summary>
    /// Golden/TwcfExports.json "pdf": the Python file's name and type, and the table Python handed its PDF
    /// builder (title, window line, headers, every cell as format_amount printed it, row kinds).
    /// </summary>
    [ReferenceFact]
    public async Task The_pdf_is_landscape_and_named_as_python_names_it()
    {
        var p = Golden.Load("TwcfExports")["pdf"]!;
        await using var host = await HostAsync();
        var c = await OwnerClientAsync(host);
        var r = await c.GetAsync(PageUrl + "&format=pdf");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal((string)p["type"]!, r.Content.Headers.ContentType!.MediaType);
        Assert.Contains(FileNameOf(r), (string)p["name"]!);
        byte[] bytes = await r.Content.ReadAsByteArrayAsync();
        using var pdf = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);
        Assert.True(pdf.Pages[0].Width.Point > pdf.Pages[0].Height.Point);

        // The cells are Python's format_amount strings, the rows Python's kinds.
        await using var db = Db();
        var m = await MatrixAsync(db, PageStart, "2026-10-29");
        var t = Accountix.Finance.Controllers.TwcfController.PdfTable(m, DateOnly.Parse(PageStart, Inv), "en");
        Golden.Equal(p["table"], new
        {
            title = t.Title,
            subtitle = t.Period,
            headers = t.Columns.Select(x => x.Header),
            rows = t.Rows.Select(x => x.Cells.Select(v => (string?)v)),
            kinds = t.Rows.Select(x => x.Style),
        }, "TwcfExports.pdf");
        Assert.Equal(15, t.Columns.Count);
        Assert.Equal("Wk 1\n06 Jul – 12 Jul", t.Columns[1].Header);
        var kinds = t.Rows.Select(x => x.Style).ToList();
        Assert.Equal("account", kinds[0]);                               // opening
        Assert.Equal("section", kinds[1]);
        Assert.Contains("subtotal", kinds);                              // net
        Assert.Equal("grand", kinds[^1]);
    }

    // ── forecast lines ──────────────────────────────────────────────────

    private static List<KeyValuePair<string, string>> LineForm(string action, string description, string? lineId = null,
        string direction = "out", string category = "payroll", string amount = "2500.5", string startDate = "2026-09-28",
        string frequency = "monthly", string dow = "2", string dom = "28", string month = "3")
    {
        var f = new List<KeyValuePair<string, string>>
        {
            new("start", FormStart), new("action", action), new("direction", direction), new("category", category),
            new("description", description), new("amount", amount), new("start_date", startDate), new("frequency", frequency),
            new("day_of_week", dow), new("day_of_month", dom), new("month", month),
        };
        if (lineId is not null) f.Add(new("line_id", lineId));
        return f;
    }

    private static async Task<TwcfLine?> LineAsync(string description)
    {
        await using var db = Db();
        return await db.Set<TwcfLine>().AsNoTracking().SingleOrDefaultAsync(l => l.Description == description);
    }

    /// <summary>What Golden/TwcfLines.json records of a stored line.</summary>
    private static object Shape(TwcfLine l) => new
    {
        direction = l.Direction, category = l.Category, amount = l.Amount, start_date = l.StartDate.ToString("yyyy-MM-dd", Inv),
        frequency = l.Frequency, day_of_week = l.DayOfWeek, day_of_month = l.DayOfMonth, month = l.Month,
    };

    /// <summary>
    /// Golden/TwcfLines.json: the Python route's answer and stored line for the same add (a
    /// non-integer day field resets all three to 0, 1, 1) and edit (out-of-range days clamp).
    /// </summary>
    [ReferenceFact]
    public async Task Adding_editing_and_deleting_a_line_writes_what_python_writes()
    {
        var g = Golden.Load("TwcfLines");
        var (cid, _) = await TwcfWorld.EnsureAsync();
        string tag = Guid.NewGuid().ToString("N")[..6];
        string netDesc = $"NET line {tag}";
        await using (var db = Db())   // what an interrupted earlier run may have left
            await db.Set<TwcfLine>().Where(l => l.Description.StartsWith("NET line ") || l.Description.StartsWith("RACE ")
                                                || l.Description.StartsWith("QCOUNT ")).ExecuteDeleteAsync();
        await using var host = await HostAsync();
        var c = await OwnerClientAsync(host);

        // add — with a non-integer day field, which resets all three to (0, 1, 1) in Python
        var r = await c.PostFormAsync("/finance/twcf", LineForm("add", netDesc, dom: "2.5"), FormUrl);
        string location = r.Headers.Location!.OriginalString;
        Golden.Equal(g["add"]!["answer"], new { status = (int)r.StatusCode, location }, "TwcfLines add answer");
        Assert.Contains($"Forecast line added: {netDesc}", await (await c.GetAsync(location)).Content.ReadAsStringAsync());
        var netLine = (await LineAsync(netDesc))!;
        Golden.Equal(g["add"]!["line"], Shape(netLine), "TwcfLines add line");
        Assert.Equal(cid, netLine.CompanyId);
        Assert.Equal((0, 1, 1), (netLine.DayOfWeek!.Value, netLine.DayOfMonth!.Value, netLine.Month!.Value));
        Assert.NotNull(netLine.CreatedAt);

        // edit — out-of-range days clamp
        var edit = LineForm("edit", netDesc + " v2", netLine.Id.ToString(Inv), direction: "in", category: "other_in", amount: "12.3456",
                            frequency: "yearly", dow: "9", dom: "40", month: "0");
        r = await c.PostFormAsync("/finance/twcf", edit, FormUrl);
        Golden.Equal(g["edit"]!["answer"], new { status = (int)r.StatusCode, location = r.Headers.Location?.OriginalString }, "TwcfLines edit answer");
        Assert.Contains($"Forecast line updated: {netDesc} v2", await (await c.GetAsync(location)).Content.ReadAsStringAsync());
        var netEdited = (await LineAsync(netDesc + " v2"))!;
        Golden.Equal(g["edit"]!["line"], Shape(netEdited), "TwcfLines edit line");
        Assert.Equal((6, 31, 1), (netEdited.DayOfWeek!.Value, netEdited.DayOfMonth!.Value, netEdited.Month!.Value));
        Assert.Equal(netLine.CreatedAt, netEdited.CreatedAt);

        // the edit link opens the form filled in
        string editPage = await (await c.GetAsync($"{FormUrl}&edit={netEdited.Id}")).Content.ReadAsStringAsync();
        Assert.Contains($"name=\"line_id\" value=\"{netEdited.Id}\"", editPage);
        Assert.Contains("value=\"12.35\"", editPage);
        Assert.Contains("Save changes", editPage);

        // delete
        r = await c.PostFormAsync("/finance/twcf", [new("start", FormStart), new("action", "delete"), new("line_id", netEdited.Id.ToString(Inv))], FormUrl);
        Golden.Equal(g["delete"]!["answer"], new { status = (int)r.StatusCode, location = r.Headers.Location?.OriginalString }, "TwcfLines delete answer");
        Assert.Contains($"Forecast line deleted: {netDesc} v2", await (await c.GetAsync(location)).Content.ReadAsStringAsync());
        Assert.Null(await LineAsync(netDesc + " v2"));
    }

    [ReferenceFact]
    public async Task Refused_lines_flash_python_words_and_store_nothing()
    {
        await using var host = await HostAsync();
        var c = await OwnerClientAsync(host);
        await using var db0 = Db();
        int before = await db0.Set<TwcfLine>().CountAsync();
        var cases = new (List<KeyValuePair<string, string>> Form, string Message)[]
        {
            (LineForm("add", "x", direction: ""), "Choose whether this line is a receipt or a payment."),
            (LineForm("add", "x", direction: "in", category: "rent"), "Choose a valid category for that direction."),
            (LineForm("add", "x", category: "collections"), "Choose a valid category for that direction."),
            (LineForm("add", "   "), "Description is required."),
            (LineForm("add", "x", amount: "0"), "Amount must be a positive number."),
            (LineForm("add", "x", amount: "-5"), "Amount must be a positive number."),
            (LineForm("add", "x", amount: "abc"), "Amount must be a positive number."),
            (LineForm("add", "x", amount: ""), "Amount must be a positive number."),
            (LineForm("add", "x", startDate: ""), "Start date is required."),
            (LineForm("add", "x", startDate: "31-02-2026"), "Start date is required."),
            (LineForm("add", "x", frequency: "daily"), "Unknown frequency."),
            (LineForm("edit", "x", "99999999"), "Forecast line not found."),
            ([new("start", FormStart), new("action", "delete"), new("line_id", "99999999")], "Forecast line not found."),
        };
        string token = await c.TokenFromAsync(FormUrl);
        foreach (var (form, message) in cases)
        {
            var r = await c.PostFormWithTokenAsync("/finance/twcf", form, token);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Equal(FormUrl, r.Headers.Location!.OriginalString);
            Assert.Contains(message, await (await c.GetAsync(r.Headers.Location.OriginalString)).Content.ReadAsStringAsync());
        }
        await using var db = Db();
        Assert.Equal(before, await db.Set<TwcfLine>().CountAsync());

        // The redirect keeps the start (blank → "?start="), and an unknown action shows the page.
        var blank = await c.PostFormWithTokenAsync("/finance/twcf", [new("action", "add")], token);
        Assert.Equal("/finance/twcf?start=", blank.Headers.Location!.OriginalString);
        var other = await c.PostFormWithTokenAsync(FormUrl, [new("action", "refresh")], token);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.Contains("Net cash flow", await other.Content.ReadAsStringAsync());
    }

    [ReferenceFact]
    public async Task A_viewer_sees_the_forecast_but_cannot_change_its_lines()
    {
        var (cid, viewer) = await TwcfWorld.EnsureAsync();
        await using var host = await HostAsync();
        var c = host.ClientFor(viewer, cid);
        var page = await c.GetAsync(FormUrl);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        await using var db = Db();
        int lineId = await db.Set<TwcfLine>().Where(l => l.Description == "TWCFSEED rent").Select(l => l.Id).SingleAsync();
        string token = await c.TokenFromAsync(FormUrl);
        foreach (var (form, right) in new (List<KeyValuePair<string, string>>, string)[]
                 {
                     (LineForm("add", "viewer add"), "create"),
                     (LineForm("edit", "viewer edit", lineId.ToString(Inv)), "edit"),
                     ([new("action", "delete"), new("line_id", lineId.ToString(Inv))], "delete"),
                 })
        {
            var r = await c.PostFormWithTokenAsync("/finance/twcf", form, token);
            Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
            if (r.StatusCode == HttpStatusCode.Redirect)
                Assert.Contains($"No &#x27;{right}&#x27; rights here", await (await c.GetAsync(FormUrl)).Content.ReadAsStringAsync());
        }
        Assert.Null(await LineAsync("viewer add"));
        Assert.Equal("TWCFSEED rent", (await db.Set<TwcfLine>().AsNoTracking().SingleAsync(l => l.Id == lineId)).Description);

        // A member without the finance module cannot open the page at all.
        var restricted = host.ClientFor(Reference.UserId(Reference.Email("restricted")), Reference.Company("b"));
        Assert.NotEqual(HttpStatusCode.OK, (await restricted.GetAsync(FormUrl)).StatusCode);
    }

    // ── queries, many users, speed ──────────────────────────────────────

    [ReferenceFact]
    public async Task The_page_runs_a_fixed_number_of_queries_however_many_lines()
    {
        await using var host = await HostAsync();
        var c = await OwnerClientAsync(host);
        const string url = FormUrl;
        var first = (await host.MeasureAsync(c, url, runs: 1)).Within(maxMilliseconds: 5000, maxQueries: 40);
        string tag = Guid.NewGuid().ToString("N")[..6];
        await using (var db = Db())
        {
            for (int i = 0; i < 8; i++)
                db.Add(new TwcfLine
                {
                    Direction = "out", Category = "other_out", Description = $"QCOUNT {tag} {i}", Amount = 10 + i,
                    StartDate = new DateOnly(2026, 9, 1), Frequency = i % 2 == 0 ? "weekly" : "monthly", DayOfWeek = i % 7,
                    DayOfMonth = 1 + i, Month = 1, CreatedAt = DateTime.UtcNow,
                });
            await db.SaveChangesAsync();
        }
        try
        {
            var more = (await host.MeasureAsync(c, url, runs: 1)).Within(maxMilliseconds: 5000, maxQueries: 40);
            Assert.Equal(first.Queries, more.Queries);
            (await host.MeasureAsync(c, url + "&format=excel", runs: 1)).Within(5000, 40);
            (await host.MeasureAsync(c, url + "&format=pdf", runs: 1)).Within(5000, 40);
            output.WriteLine($"twcf page: {first.Queries} queries, {first.Elapsed.TotalMilliseconds:F0} ms");
        }
        finally
        {
            await using var db = Db();
            await db.Set<TwcfLine>().Where(l => l.Description.StartsWith($"QCOUNT {tag}")).ExecuteDeleteAsync();
        }
    }

    [ReferenceFact]
    public async Task Six_people_changing_lines_at_once_get_answers_never_errors()
    {
        var (cid, _) = await TwcfWorld.EnsureAsync();
        await using var host = await HostAsync();
        var clients = host.ClientsFor(Owner, cid, 6);
        var tokens = await Task.WhenAll(clients.Select(x => x.TokenFromAsync(FormUrl)));
        string tag = Guid.NewGuid().ToString("N")[..6];

        // Six adds at once: six lines.
        var adds = await Race.RunAsync(6, i => clients[i].PostFormWithTokenAsync("/finance/twcf", LineForm("add", $"RACE {tag} {i}"), tokens[i]));
        Race.NoServerErrors(adds);
        await using (var db = Db())
            Assert.Equal(6, await db.Set<TwcfLine>().CountAsync(l => l.Description.StartsWith($"RACE {tag} ")));

        // Three edit and three delete one line at once: answered, and the line ends deleted or edited, never a 500.
        int id;
        await using (var db = Db())
            id = await db.Set<TwcfLine>().Where(l => l.Description == $"RACE {tag} 0").Select(l => l.Id).SingleAsync();
        var rs = await Race.RunAsync(6, i => clients[i].PostFormWithTokenAsync("/finance/twcf",
            i % 2 == 0 ? LineForm("edit", $"RACE {tag} 0 edited {i}", id.ToString(Inv))
                       : [new("start", FormStart), new("action", "delete"), new("line_id", id.ToString(Inv))], tokens[i]));
        Race.NoServerErrors(rs);
        await using (var db = Db())
        {
            Assert.Null(await db.Set<TwcfLine>().AsNoTracking().FirstOrDefaultAsync(l => l.Id == id));
            await db.Set<TwcfLine>().Where(l => l.Description.StartsWith($"RACE {tag} ")).ExecuteDeleteAsync();
        }
    }

    /// <summary>Golden/TwcfPerfMatrices.json: build_matrix on the full-year Samba books (read only) for three windows.</summary>
    [PerfFact]
    public async Task On_a_full_year_the_matrix_equals_python_build_matrix()
    {
        var g = Golden.Load("TwcfPerfMatrices");
        var today = Perf.YearEnd.AddDays(-20);
        Assert.Equal(today.ToString("yyyy-MM-dd", Inv), (string)g["today"]!);
        var starts = new[] { TwcfReports.DefaultStart(Perf.BusiestMonthStart), TwcfReports.DefaultStart(Perf.YearEnd.AddDays(-60)), Perf.YearStart };
        var windows = g["windows"]!.AsArray();
        Assert.Equal(starts.Select(d => d.ToString("yyyy-MM-dd", Inv)), windows.Select(w => (string)w!["start"]!));
        var company = new CompanyContext();
        company.Set(Perf.CompanyId);
        await using var db = Perf.NewDb(company);
        for (int i = 0; i < starts.Length; i++)
            Golden.Equal(windows[i]!["matrix"], Shape(await new TwcfReports(new FinanceData(db)).BuildMatrixAsync(starts[i], today)),
                         $"TwcfPerfMatrices {starts[i]:yyyy-MM-dd}", tolerance: 0);
    }

    /// <summary>
    /// Budgets on the full year. Python's own times for the same page were measured once (29 Oct) and are
    /// kept in docs/modules/finance.md; the Python app is no longer run here.
    /// </summary>
    [PerfFact]
    public async Task Thirteen_weeks_on_a_full_year_are_quick()
    {
        var start = TwcfReports.DefaultStart(Perf.BusiestMonthStart);
        string url = $"/finance/twcf?start={start:yyyy-MM-dd}";
        await using var host = await ModuleHost.StartOnAsync(Perf.ConnectionString, typeof(FinanceModule).Assembly);
        var c = host.ClientFor(Perf.AdminUserId, Perf.CompanyId);
        var page = (await host.MeasureAsync(c, url)).Within(1500, 40);
        var excel = (await host.MeasureAsync(c, url + "&format=excel")).Within(3000, 40);
        var pdf = (await host.MeasureAsync(c, url + "&format=pdf")).Within(3000, 40);
        output.WriteLine($"TWCF speed (best of 3): page {page.Elapsed.TotalMilliseconds:F0} ms / {page.Queries} q; " +
                         $"excel {excel.Elapsed.TotalMilliseconds:F0} ms; pdf {pdf.Elapsed.TotalMilliseconds:F0} ms");
    }
}
