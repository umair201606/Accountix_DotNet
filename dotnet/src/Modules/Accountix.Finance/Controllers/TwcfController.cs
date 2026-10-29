using System.Globalization;
using Accountix.Finance.Services;
using ClosedXML.Excel;

namespace Accountix.Finance.Controllers;

public sealed class TwcfPageModel
{
    public TwcfMatrix? Matrix { get; init; }
    public DateOnly Start { get; init; }
    public bool Loaded { get; init; }
    public IReadOnlyList<TwcfLine> Lines { get; init; } = [];
    public TwcfLine? Editing { get; init; }
}

/// <summary>
/// /finance/twcf (finance_app/routes/reports.py twcf): the 13-week cash flow
/// matrix (GET, computed only once a start is chosen) and its forecast lines
/// (POST add / edit / delete). No snapshot decorator, as in Python.
/// </summary>
[Area("Finance")]
[Module(Modules.Finance)]
[Route("finance")]
public sealed class TwcfController(AccountixDbContext db, TwcfReports twcf, ReportExport export, CompanyClock clock) : Controller
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const string Section = "financial_reports";

    [HttpGet("twcf")]
    [AnyMember]   // rights_guard _twcf: GET needs financial_reports view
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        if (this.Deny(Section, Rights.View) is { } refused) return refused;
        return await RenderAsync(ct);
    }

    [HttpPost("twcf")]
    [AnyMember]   // rights_guard _twcf: add → create, delete → delete, anything else → edit
    public async Task<IActionResult> Post(CancellationToken ct)
    {
        var form = Request.Form;
        string action = form["action"].FirstOrDefault() ?? "";
        string right = action switch { "add" => Rights.Create, "delete" => Rights.Delete, _ => Rights.Edit };
        if (this.Deny(Section, right) is { } refused) return refused;

        string startArg = (form["start"].FirstOrDefault() is { Length: > 0 } fs ? fs : Request.Query["start"].FirstOrDefault() ?? "").Trim();
        string back = "/finance/twcf?start=" + Uri.EscapeDataString(startArg);

        if (action is "add" or "edit")
        {
            int? lineId = FormInt(form["line_id"].FirstOrDefault());
            TwcfLine? line = lineId is int lid ? await db.Set<TwcfLine>().FirstOrDefaultAsync(l => l.Id == lid, ct) : null;
            if (action == "edit" && line is null)
            {
                this.Flash("Forecast line not found.", "danger");
                return Redirect(back);
            }

            string direction = form["direction"].FirstOrDefault() ?? "";
            string category = form["category"].FirstOrDefault() ?? "";
            string description = (form["description"].FirstOrDefault() ?? "").Trim();
            string amount = (form["amount"].FirstOrDefault() ?? "").Trim();
            string startRaw = (form["start_date"].FirstOrDefault() ?? "").Trim();
            string frequency = form["frequency"].FirstOrDefault() is { Length: > 0 } fq ? fq : "oneoff";
            decimal? amountVal = null;
            DateOnly? startVal = null;
            if (decimal.TryParse(amount, NumberStyles.Float, Inv, out decimal parsed))
            {
                amountVal = parsed;
                startVal = ReportPeriod.ParseDate(startRaw);
            }

            var allowed = direction == Twcf.In ? Twcf.UserInCategories : Twcf.UserOutCategories;
            if (direction is not (Twcf.In or Twcf.Out))
                this.Flash("Choose whether this line is a receipt or a payment.", "danger");
            else if (!allowed.Contains(category))
                this.Flash("Choose a valid category for that direction.", "danger");
            else if (description.Length == 0)
                this.Flash("Description is required.", "danger");
            else if (amountVal is null || amountVal <= 0)
                this.Flash("Amount must be a positive number.", "danger");
            else if (startVal is null)
                this.Flash("Start date is required.", "danger");
            else if (!Twcf.Frequencies.Contains(frequency))
                this.Flash("Unknown frequency.", "danger");
            else
            {
                int dayOfWeek = 0, dayOfMonth = 1, month = 1;
                if (PyInt(form["day_of_week"].FirstOrDefault(), 0) is int dw
                    && PyInt(form["day_of_month"].FirstOrDefault(), 1) is int dm
                    && PyInt(form["month"].FirstOrDefault(), 1) is int mo)
                {
                    dayOfWeek = Math.Max(0, Math.Min(6, dw));
                    dayOfMonth = Math.Max(1, Math.Min(31, dm));
                    month = Math.Max(1, Math.Min(12, mo));
                }

                if (action == "add")
                {
                    db.Add(new TwcfLine
                    {
                        Direction = direction, Category = category, Description = description, Amount = amountVal.Value,
                        StartDate = startVal.Value, Frequency = frequency, DayOfWeek = dayOfWeek, DayOfMonth = dayOfMonth,
                        Month = month, CreatedAt = clock.UtcNow,
                    });
                    this.Flash($"Forecast line added: {description}", "success");
                }
                else
                {
                    line!.Direction = direction;
                    line.Category = category;
                    line.Description = description;
                    line.Amount = amountVal.Value;
                    line.StartDate = startVal.Value;
                    line.Frequency = frequency;
                    line.DayOfWeek = dayOfWeek;
                    line.DayOfMonth = dayOfMonth;
                    line.Month = month;
                    this.Flash($"Forecast line updated: {description}", "success");
                }
                await db.SaveChangesAsync(ct);
            }
            return Redirect(back);
        }

        if (action == "delete")
        {
            int? id = FormInt(form["line_id"].FirstOrDefault());
            var line = id is int i ? await db.Set<TwcfLine>().FirstOrDefaultAsync(l => l.Id == i, ct) : null;
            if (line is not null)
            {
                db.Remove(line);
                await db.SaveChangesAsync(ct);
                this.Flash($"Forecast line deleted: {line.Description}", "success");
            }
            else
                this.Flash("Forecast line not found.", "error");
            return Redirect(back);
        }

        // Any other action falls through to the page, as Python's view does.
        return await RenderAsync(ct);
    }

    /// <summary>request.form.get(..., type=int): an int, or null when missing / not a whole number.</summary>
    private static int? FormInt(string? s) =>
        int.TryParse(s, NumberStyles.Integer, Inv, out int v) ? v : null;

    /// <summary>int(value or default): blank takes the default; text that is not an int fails (null).</summary>
    private static int? PyInt(string? s, int fallback) =>
        string.IsNullOrEmpty(s) ? fallback : int.TryParse(s, NumberStyles.Integer, Inv, out int v) ? v : null;

    private async Task<IActionResult> RenderAsync(CancellationToken ct)
    {
        string startStr = (Request.Query["start"].FirstOrDefault() ?? "").Trim();
        bool loaded = startStr.Length > 0;
        // Python 500s on an unreadable start; this opens the current week instead.
        DateOnly start = (loaded ? ReportPeriod.ParseDate(startStr) : null) ?? TwcfReports.DefaultStart(clock.Today);
        TwcfMatrix? matrix = null;
        List<TwcfLine> lines = [];
        TwcfLine? editing = null;
        if (loaded)
        {
            // Finished weeks turn over at the company's midnight (build_matrix's local_today).
            matrix = await twcf.BuildMatrixAsync(start, clock.Today, ct);
            lines = matrix is null ? [] : await db.Set<TwcfLine>().AsNoTracking().OrderBy(l => l.StartDate).ThenBy(l => l.Id).ToListAsync(ct);
            if (FormInt(Request.Query["edit"].FirstOrDefault()) is int editId)
                editing = lines.FirstOrDefault(l => l.Id == editId);
        }

        string? fmt = Request.Query["format"].FirstOrDefault();
        if (loaded && matrix is not null && fmt == "excel")
        {
            var file = export.File("excel", ExcelBytes(matrix, start), "13 Week Cash Flow", ReportPeriod.FilePeriod(asOf: start));
            return File(file.Content, file.ContentType, file.FileName);
        }
        if (loaded && matrix is not null && fmt == "pdf")
        {
            var table = PdfTable(matrix, start, HttpContext.Access()?.NumberFormat);
            var file = export.File("pdf", export.BuildPdf(table), "13 Week Cash Flow", ReportPeriod.FilePeriod(asOf: start));
            return File(file.Content, file.ContentType, file.FileName);
        }

        return View("~/Areas/Finance/Views/Twcf/Index.cshtml", new TwcfPageModel
        {
            Matrix = matrix, Start = start, Loaded = loaded, Lines = lines, Editing = editing,
        });
    }

    private static string WindowLine(TwcfMatrix m, DateOnly start) =>
        $"Forecast window {ReportPeriod.ColLabel(start)} to {ReportPeriod.ColLabel(m.Weeks[^1].End)}";

    /// <summary>
    /// The Excel sheet exactly as Python builds it by hand. (ClosedXML writes a number with
    /// 15 significant digits, openpyxl with 16: an unrounded float such as -3834.6894999999995
    /// is stored -3834.6895 here and -3834.689499999999 by Python — the same to Excel's display.)
    /// </summary>
    internal byte[] ExcelBytes(TwcfMatrix matrix, DateOnly start)
    {
        var weeks = matrix.Weeks;
        int ncols = weeks.Count + 2;   // Category + 13 weeks + Total
        using var wb = export.NewWorkbook("13 Week Cash Flow (TWCF)");
        var ws = ReportExport.AddSheet(wb, "13 Week Cash Flow");
        int firstRow = export.WriteHeading(ws, ncols, "13 Week Cash Flow (TWCF)", WindowLine(matrix, start), []);

        var thin = XLColor.FromHtml("#CBD5E1");
        void Thin(IXLCell c)
        {
            var b = c.Style.Border;
            b.LeftBorder = b.RightBorder = b.TopBorder = b.BottomBorder = XLBorderStyleValues.Thin;
            b.LeftBorderColor = b.RightBorderColor = b.TopBorderColor = b.BottomBorderColor = thin;
        }

        ws.Cell(firstRow, 1).Value = "Category";
        for (int i = 0; i < weeks.Count; i++)
            ws.Cell(firstRow, 2 + i).Value = $"Wk {i + 1}\n{TwcfReports.WeekLabel(weeks[i].Start, weeks[i].End)}";
        ws.Cell(firstRow, ncols).Value = "Total";
        for (int ci = 1; ci <= ncols; ci++)
        {
            var c = ws.Cell(firstRow, ci);
            c.Style.Font.FontColor = XLColor.White;
            c.Style.Font.Bold = true;
            c.Style.Font.FontSize = 10;
            c.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E293B");
            c.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            c.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            c.Style.Alignment.WrapText = true;
            Thin(c);
        }

        int r = firstRow + 1;
        foreach (var row in matrix.Rows)
        {
            var range = ws.Range(r, 1, r, ncols);
            if (row.Kind == "section")
            {
                range.Merge();
                var c = ws.Cell(r, 1);
                c.Value = row.Label;
                c.Style.Font.Bold = true;
                c.Style.Font.FontSize = 11;
                c.Style.Font.FontColor = XLColor.FromHtml("#334155");
                c.Style.Fill.BackgroundColor = XLColor.FromHtml("#F1F5F9");
                for (int ci = 1; ci <= ncols; ci++) Thin(ws.Cell(r, ci));
                r++;
                continue;
            }
            var label = ws.Cell(r, 1);
            label.Value = row.Label;
            var values = row.Values ?? [];
            for (int i = 0; i < weeks.Count; i++)
                if (i < values.Length && values[i] is double v)
                {
                    var cc = ws.Cell(r, 2 + i);
                    cc.Value = v;
                    cc.Style.NumberFormat.Format = Money.ExcelWestern;
                }
            if (row.Total is double total)
            {
                var tc = ws.Cell(r, ncols);
                tc.Value = total;
                tc.Style.NumberFormat.Format = Money.ExcelWestern;
            }
            var st = range.Style;
            switch (row.Kind)
            {
                case "grand":
                    st.Font.Bold = true;
                    st.Font.FontSize = 11;
                    st.Font.FontColor = XLColor.White;
                    st.Fill.BackgroundColor = XLColor.FromHtml("#1E293B");
                    break;
                case "total" or "net" or "headroom":
                    st.Font.Bold = true;
                    st.Font.FontSize = 10;
                    st.Fill.BackgroundColor = XLColor.FromHtml("#FAFAFA");
                    if (row.Kind == "net")
                        ws.Range(r, 2, r, ncols).Style.Fill.BackgroundColor = XLColor.FromHtml("#EFF6FF");
                    break;
                case "variance":
                    st.Font.Italic = true;
                    st.Font.FontSize = 10;
                    break;
                case "opening":
                    label.Style.Font.Italic = true;
                    label.Style.Font.FontSize = 10;
                    label.Style.Font.Bold = true;
                    break;
                default:
                    st.Font.FontSize = 10;
                    break;
            }
            for (int ci = 1; ci <= ncols; ci++)
            {
                var c = ws.Cell(r, ci);
                Thin(c);
                c.Style.Alignment.Horizontal = ci > 1 ? XLAlignmentHorizontalValues.Right : XLAlignmentHorizontalValues.Left;
                c.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            }
            r++;
        }

        ws.Column(1).Width = 36;
        for (int i = 0; i < weeks.Count; i++) ws.Column(2 + i).Width = 13;
        ws.Column(ncols).Width = 14;
        export.SetupSheet(ws, ncols, firstRow, "13 Week Cash Flow", landscape: true);
        ws.SheetView.Freeze(firstRow, 1);   // freeze_panes = B{first_row + 1}
        return ReportExport.Save(wb);
    }

    /// <summary>The PDF table: Python's row kinds mapped (opening → account, net / headroom → subtotal, variance → plain).</summary>
    internal static ReportTable PdfTable(TwcfMatrix matrix, DateOnly start, string? numberFormat)
    {
        var weeks = matrix.Weeks;
        var columns = new List<ReportColumn> { new("Category") };
        columns.AddRange(weeks.Select((wk, i) => new ReportColumn($"Wk {i + 1}\n{TwcfReports.WeekLabel(wk.Start, wk.End)}", ReportColumnKind.Money)));
        columns.Add(new ReportColumn("Total", ReportColumnKind.Money));
        var rows = new List<ReportRow>();
        foreach (var row in matrix.Rows)
        {
            if (row.Kind == "section")
            {
                rows.Add(new ReportRow([row.Label, .. Enumerable.Repeat<object?>("", weeks.Count + 1)], "section"));
                continue;
            }
            var cells = new List<object?> { row.Label };
            // Pre-formatted, as Python passes format_amount() strings.
            cells.AddRange((row.Values ?? []).Select(v => v is double d ? (object?)TwcfReports.Display(d, numberFormat) : ""));
            cells.Add(row.Total is double t ? TwcfReports.Display(t, numberFormat) : "");
            string kind = row.Kind switch
            {
                "opening" => "account", "net" => "subtotal", "variance" => "plain", "headroom" => "subtotal", _ => row.Kind,
            };
            rows.Add(new ReportRow(cells, kind));
        }
        return new ReportTable
        {
            Title = "13 Week Cash Flow (TWCF)",
            Period = WindowLine(matrix, start),
            Columns = columns,
            Rows = rows,
            IndentColumn = 0,
        };
    }
}
