using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using PdfSharp.Drawing;
using PdfSharp.Fonts;

namespace Accountix.Finance.Services;

/// <summary>
/// One export pipeline for every report (shared/report_export.py): Excel,
/// PDF and CSV. Every file carries the company / title / period heading, the
/// filters and who generated it when; the header row stays put (frozen,
/// repeated on every printed page); money in the company's number format;
/// section / total / grand rows styled alike in Excel and PDF; fit-to-width
/// print setup with "Page X of Y"; and a file name Company_Report_Period.ext.
/// </summary>
public sealed partial class ReportExport(IHttpContextAccessor http, CompanyClock clock) : IReportExport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Palette: the screen's statement styling.
    private const string Slate = "1E293B", SectionBg = "F1F5F9", SectionFg = "334155", TotalBg = "FAFAFA",
        TotalRule = "94A3B8", ProfitBg = "EFF6FF", NegBg = "FEF2F2", NegFg = "B91C1C", Muted = "64748B", Rule = "E2E8F0",
        CellRule = "CBD5E1";

    private const int SheetFirstRow = 5;
    private const string DateFmt = "dd-mmm-yyyy";

    public const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private EffectiveAccess? Access => http.HttpContext?.Access();

    /// <summary>company_name(): the profile's name (CompanyInfo), else the company's.</summary>
    public string CompanyName => (Access?.Profile?.CompanyName ?? Access?.Company?.Name ?? "").Trim();
    private string UserName => Access?.User is { } u ? (string.IsNullOrEmpty(u.FullName) ? u.Email : u.FullName) : "";
    private string NumberFormat => Access?.NumberFormat ?? "en";

    /// <summary>"Generated 29 Oct 2026, 14:05 by Ali" on the company's clock.</summary>
    public string GeneratedLine()
    {
        string text = $"Generated {clock.Now.ToString("dd MMM yyyy, HH:mm", Inv)}";
        return UserName.Length > 0 ? $"{text} by {UserName}" : text;
    }

    private string NoteLine(IEnumerable<string> filters) =>
        string.Join("  ·  ", filters.Where(f => !string.IsNullOrEmpty(f)).Append(GeneratedLine()));

    private static IEnumerable<string> FilterLines(ReportTable t) =>
        t.Filters.Select(f => string.IsNullOrEmpty(f.Label) ? f.Value : $"{f.Label}: {f.Value}");

    [GeneratedRegex("&amp;|&")]
    private static partial Regex AmpRe();

    private static string Slug(string? text, int limit = 60, bool keepDash = false)
    {
        text = AmpRe().Replace(text ?? "", "and");
        text = Regex.Replace(text, keepDash ? "[^A-Za-z0-9-]+" : "[^A-Za-z0-9]+", "_");
        text = text.Trim('_', '-');
        if (text.Length > limit) text = text[..limit];
        return text.Trim('_', '-');
    }

    /// <summary>export_filename: Company_Report_Period.ext.</summary>
    public string FileName(string title, string ext, string? period)
    {
        var parts = new List<string>();
        string c = Slug(CompanyName, 30);
        if (c.Length > 0) parts.Add(c);
        string t = Slug(title);
        parts.Add(t.Length > 0 ? t : "report");
        if (!string.IsNullOrEmpty(period))
        {
            string p = Slug(period, 40, keepDash: true);
            if (p.Length > 0) parts.Add(p);
        }
        return string.Join("_", parts) + "." + ext;
    }

    public Task<ExportFile> ExportTableAsync(string format, ReportTable table, CancellationToken ct = default)
    {
        string fmt = (format ?? "").ToLowerInvariant();
        string period = table.FilePeriod ?? table.Period ?? "";
        ExportFile file = fmt switch
        {
            "excel" or "xlsx" => new(FileName(table.Title, "xlsx", period), XlsxType, BuildExcel(table)),
            "pdf" => new(FileName(table.Title, "pdf", period), "application/pdf", BuildPdf(table)),
            "csv" => new(FileName(table.Title, "csv", period), "text/csv", BuildCsv(table)),
            _ => throw new UnknownExportFormatException(format ?? ""),
        };
        return Task.FromResult(file);
    }

    // ── cells ───────────────────────────────────────────────────────────

    private static bool IsNumber(object? v) => v is decimal or double or float or int or long or short;

    private static decimal ToDecimal(object v) => v switch
    {
        decimal d => d,
        double d => (decimal)d,
        float f => (decimal)f,
        int i => i,
        long l => l,
        short s => s,
        _ => 0m,
    };

    private static bool IsDate(object? v) => v is DateTime or DateOnly;

    /// <summary>looks_numeric: a figure, whoever formatted it ("1,234.50", "(9.5)", "12%").</summary>
    public static bool LooksNumeric(object? v)
    {
        if (v is bool) return false;
        if (IsNumber(v)) return true;
        if (v is not string s) return false;
        s = s.Trim().Replace(",", "");
        if (s.StartsWith('(') && s.EndsWith(')')) s = s[1..^1];
        if (s.EndsWith('%')) s = s[..^1];
        return s.Length > 0 && double.TryParse(s, NumberStyles.Float, Inv, out _);
    }

    private string CellText(object? v) => v switch
    {
        null => "",
        bool b => b ? "True" : "False",
        DateTime or DateOnly => "00-Mmm-0000",
        _ when IsNumber(v) => Money.Format(ToDecimal(v!), 2, NumberFormat),
        _ => v.ToString() ?? "",
    };

    /// <summary>Excel has no "decimals only when needed": a quantity column is whole when every value is.</summary>
    private static string ColumnFormat(ReportTable t, int ci, string money)
    {
        var kind = ci < t.Columns.Count ? t.Columns[ci].Kind : ReportColumnKind.Text;
        switch (kind)
        {
            case ReportColumnKind.Integer:
                return "0";
            case ReportColumnKind.Quantity:
                bool whole = t.Rows.Where(r => ci < r.Cells.Count && IsNumber(r.Cells[ci]))
                    .All(r => decimal.Truncate(ToDecimal(r.Cells[ci]!)) == ToDecimal(r.Cells[ci]!));
                return whole ? "#,##0" : "#,##0.00";
            case ReportColumnKind.Percent:
                return "0.00\\%";
            default:
                return money;
        }
    }

    // ── Excel ───────────────────────────────────────────────────────────

    /// <summary>
    /// build_excel: one sheet of the table (plus any extra list sheets), written by the
    /// streaming writer — linear in the cells, no per-cell objects (10,000 × 15 well under a second).
    /// </summary>
    public byte[] BuildExcel(ReportTable t)
    {
        var x = new XlsxStream();
        string money = Money.ExcelFormat(NumberFormat);
        int ncols = t.Columns.Count;
        int hdr = SheetFirstRow;
        var kinds = t.Rows.Select(r => r.Style ?? "plain").ToList();
        var formats = Enumerable.Range(0, ncols).Select(ci => ColumnFormat(t, ci, money)).ToArray();
        var headers = t.Columns.Select(c => c.Header).ToList();
        double[] widths = t.ColumnWidths is { } cw ? cw.ToArray() : AutoWidths(headers, t.Rows.Select(r => r.Cells), ncols);
        bool autofilter = t.Autofilter ?? !kinds.Any(k => k is "section" or "subtotal");
        int lastRow = hdr + t.Rows.Count;
        int filterLast = lastRow;   // the filter stops above a closing total row, so sorting never moves it
        while (filterLast > hdr && kinds[filterLast - hdr - 1] is "total" or "grand")
            filterLast--;

        var dataFont = new XlsxStream.Font(10);
        var boldFont = new XlsxStream.Font(10, Bold: true);
        var grid = new XlsxStream.Border("thin", "thin", "thin", "thin");
        // (row band, bold, value type, column) → style id, so each distinct look is resolved once.
        var cache = new Dictionary<(string Kind, bool Neg, bool Bold, int Type, int Col), int>();
        int StyleFor(string kind, bool negative, bool bold, int type, int col)
        {
            var key = (kind, negative, bold, type, type == 1 ? col : -1);
            if (cache.TryGetValue(key, out int id)) return id;
            var (font, fill, border) = Band(kind, negative, bold ? boldFont : dataFont, grid);
            string? numFmt = type == 1 ? formats[col] : type == 2 ? DateFmt : null;
            return cache[key] = x.Style(new XlsxStream.Xf(font, fill, border, numFmt, type == 1 ? "right" : "left", "center"));
        }

        var sheet = HeadedSheet(x, SheetName(t.SheetTitle ?? t.Title), ncols, widths, hdr, t.Title, t.Period ?? "", FilterLines(t),
                                autofilter && filterLast > hdr ? filterLast : null, ncols > 5, t.Title, headers, rows =>
        {
            for (int i = 0; i < t.Rows.Count; i++)
            {
                var row = t.Rows[i];
                bool bold = t.BoldRows.Contains(i);
                string kind = kinds[i];
                if (bold && kind == "plain") kind = "total";
                bool negative = kind == "subtotal" && row.Cells.Any(v => IsNumber(v) && ToDecimal(v!) < 0);
                rows.Row(hdr + 1 + i);
                for (int ci = 0; ci < ncols; ci++)
                {
                    object? val = ci < row.Cells.Count ? row.Cells[ci] : null;
                    int type = IsNumber(val) ? 1 : IsDate(val) ? 2 : 0;
                    WriteValue(rows, ci + 1, val, StyleFor(kind, negative, bold, type, ci));
                }
            }
        });
        x.Add(sheet);

        foreach (var (name, extraHeaders, extraRows) in t.ExtraSheets)
            x.Add(ListSheet(x, name, extraHeaders, extraRows, t.Title, t.Period ?? "", money));

        return x.Save(t.Title, UserName.Length > 0 ? UserName : "Accountix ERP", CompanyName);
    }

    /// <summary>style_row's band for a data row: font, fill and border by kind.</summary>
    private static (XlsxStream.Font Font, string? Fill, XlsxStream.Border Border) Band(string kind, bool negative, XlsxStream.Font font,
                                                                                       XlsxStream.Border grid) => kind switch
    {
        "section" => (new XlsxStream.Font(10, Bold: true, Color: SectionFg), SectionBg, grid),
        "total" => (new XlsxStream.Font(10, Bold: true, Color: "000000"), TotalBg, grid with { TopColor = TotalRule }),
        "subtotal" => (new XlsxStream.Font(10, Bold: true, Color: negative ? NegFg : "000000"), negative ? NegBg : ProfitBg,
                       grid with { Top = "medium", Bottom = "medium", TopColor = Slate, BottomColor = Slate }),
        "grand" => (new XlsxStream.Font(10, Bold: true, Color: "FFFFFF"), Slate,
                    new XlsxStream.Border(null, null, "medium", "double", TopColor: Slate, BottomColor: Slate)),
        _ => (font, null, grid),
    };

    private static void WriteValue(XlsxStream.Rows rows, int col, object? v, int style)
    {
        switch (v)
        {
            case null: rows.Blank(col, style); break;
            case decimal d: rows.Number(col, d.ToString(Inv), style); break;
            case double d when double.IsFinite(d): rows.Number(col, d.ToString("R", Inv), style); break;
            case float f when float.IsFinite(f): rows.Number(col, ((double)f).ToString("R", Inv), style); break;
            case int or long or short: rows.Number(col, Convert.ToString(v, Inv)!, style); break;
            case DateTime dt: rows.Number(col, dt.ToOADate().ToString("R", Inv), style); break;
            case DateOnly d: rows.Number(col, d.ToDateTime(TimeOnly.MinValue).ToOADate().ToString("R", Inv), style); break;
            case bool b: rows.Bool(col, b, style); break;
            default: rows.Text(col, v.ToString() ?? "", style); break;
        }
    }

    /// <summary>autosize from the values themselves: what Excel will show, a wrapped header's longest word, 8–60.</summary>
    private double[] AutoWidths(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> rows, int ncols)
    {
        var max = new int[ncols];
        for (int ci = 0; ci < ncols && ci < headers.Count; ci++)
            max[ci] = (headers[ci] ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(w => w.Length).DefaultIfEmpty(0).Max();
        foreach (var cells in rows)
            for (int ci = 0; ci < ncols && ci < cells.Count; ci++)
            {
                int len = cells[ci] switch
                {
                    null => 0,
                    string str => str.Length,
                    DateTime or DateOnly => 11,
                    var v => CellText(v).Length,
                };
                if (len > max[ci]) max[ci] = len;
            }
        return max.Select(m => (double)Math.Min(Math.Max(m + 2, 8), 60)).ToArray();
    }

    /// <summary>A sheet with the heading block (company / title / period / note) and the slate header row.</summary>
    private XlsxStream.Sheet HeadedSheet(XlsxStream x, string name, int ncols, double[] widths, int hdr, string title, string period,
                                         IEnumerable<string> filters, int? filterLast, bool landscape, string footerTitle,
                                         IReadOnlyList<string> headers, Action<XlsxStream.Rows> body)
    {
        int span = Math.Max(ncols, 1);
        var heading = new (string Text, XlsxStream.Font Font)[]
        {
            (CompanyName, new(11, Bold: true, Color: SectionFg)),
            (title, new(15, Bold: true, Color: Slate)),
            (period, new(10, Color: SectionFg)),
            (NoteLine(filters), new(8, Italic: true, Color: Muted)),
        };
        var headingStyles = heading.Select(h => x.Style(new XlsxStream.Xf(h.Font, null, null, null, "center", "center"))).ToArray();
        int headStyle = x.Style(new XlsxStream.Xf(new XlsxStream.Font(10, Bold: true, Color: "FFFFFF"), Slate,
                                                  new XlsxStream.Border("thin", "thin", "thin", "thin"), null, "center", "center", Wrap: true));
        string footer = (CompanyName + (CompanyName.Length > 0 && footerTitle.Length > 0 ? " · " : "") + footerTitle).Replace("&", "&&");
        var sheet = new XlsxStream.Sheet
        {
            Name = name, Columns = ncols, Widths = widths, HeaderRow = hdr, FilterLastRow = filterLast, Landscape = landscape,
            FooterLeft = footer,
            WriteRows = rows =>
            {
                for (int r = 0; r < heading.Length; r++)
                {
                    rows.Row(r + 1);
                    rows.Text(1, heading[r].Text, headingStyles[r]);
                }
                rows.Row(hdr);
                for (int ci = 0; ci < headers.Count; ci++)
                    rows.Text(ci + 1, headers[ci], headStyle);
                body(rows);
            },
        };
        if (span > 1)
            for (int r = 1; r <= heading.Length; r++)
                sheet.Merges.Add((r, 1, span));
        sheet.RowHeights[2] = 22;
        sheet.RowHeights[hdr] = 30;
        return sheet;
    }

    /// <summary>add_list_sheet: a further plain list (gridded, money on figures, dates formatted) with its own heading.</summary>
    private XlsxStream.Sheet ListSheet(XlsxStream x, string name, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<object?>> rows,
                                       string title, string period, string money)
    {
        var grid = new XlsxStream.Border("thin", "thin", "thin", "thin");
        var font = new XlsxStream.Font(10);
        int text = x.Style(new XlsxStream.Xf(font, null, grid, null, null, null));
        int number = x.Style(new XlsxStream.Xf(font, null, grid, money, "right", null));
        int date = x.Style(new XlsxStream.Xf(font, null, grid, DateFmt, null, null));
        int hdr = SheetFirstRow;
        return HeadedSheet(x, SheetName(name), headers.Count, AutoWidths(headers, rows, headers.Count), hdr, $"{title} — {name}", period, [],
                           rows.Count > 0 ? hdr + rows.Count : null, headers.Count > 5, name, headers, w =>
        {
            for (int i = 0; i < rows.Count; i++)
            {
                w.Row(hdr + 1 + i);
                for (int ci = 0; ci < rows[i].Count; ci++)
                {
                    object? v = rows[i][ci];
                    WriteValue(w, ci + 1, v, IsNumber(v) ? number : IsDate(v) ? date : text);
                }
            }
        });
    }

    private static string SheetName(string? name)
    {
        name = Regex.Replace(name ?? "Report", @"[\[\]:*?/\\]", "-").Replace("&amp;", "&");
        return name.Length > 31 ? name[..31] : name.Length == 0 ? "Report" : name;
    }

    private static void SetValue(IXLCell c, object? v)
    {
        switch (v)
        {
            case null: c.Value = Blank.Value; break;
            case decimal d: c.Value = (double)d; break;
            case double d: c.Value = d; break;
            case float f: c.Value = f; break;
            case int i: c.Value = i; break;
            case long l: c.Value = l; break;
            case DateTime dt: c.Value = dt; break;
            case DateOnly d: c.Value = d.ToDateTime(TimeOnly.MinValue); break;
            case bool b: c.Value = b; break;
            default: c.Value = v.ToString(); break;
        }
    }

    private static void Border(IXLBorder b, string color)
    {
        var col = XLColor.FromHtml("#" + color);
        b.LeftBorder = b.RightBorder = b.TopBorder = b.BottomBorder = XLBorderStyleValues.Thin;
        b.LeftBorderColor = b.RightBorderColor = b.TopBorderColor = b.BottomBorderColor = col;
    }

    /// <summary>write_sheet_heading: company / title / period, then the filters + generated note. Returns the header row (5).</summary>
    public int WriteHeading(IXLWorksheet ws, int ncols, string title, string period, IEnumerable<string> filters)
    {
        int span = Math.Max(ncols, 1);
        var lines = new (string Text, bool Bold, double Size, string Color, bool Italic)[]
        {
            (CompanyName, true, 11, SectionFg, false),
            (title, true, 15, Slate, false),
            (period, false, 10, SectionFg, false),
            (NoteLine(filters), false, 8, Muted, true),
        };
        for (int r = 1; r <= lines.Length; r++)
        {
            var (text, bold, size, color, italic) = lines[r - 1];
            ws.Range(r, 1, r, span).Merge();
            var c = ws.Cell(r, 1);
            c.Value = text;
            c.Style.Font.Bold = bold;
            c.Style.Font.Italic = italic;
            c.Style.Font.FontSize = size;
            c.Style.Font.FontColor = XLColor.FromHtml("#" + color);
            c.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            c.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }
        ws.Row(2).Height = 22;
        return SheetFirstRow;
    }

    /// <summary>style_header_row: slate band, white bold, wrapped, 30pt high.</summary>
    public static void StyleHeader(IXLWorksheet ws, int row, IReadOnlyList<string> headers)
    {
        for (int ci = 0; ci < headers.Count; ci++)
        {
            var c = ws.Cell(row, ci + 1);
            c.Value = headers[ci];
            c.Style.Font.FontColor = XLColor.White;
            c.Style.Font.Bold = true;
            c.Style.Font.FontSize = 10;
            c.Style.Fill.BackgroundColor = XLColor.FromHtml("#" + Slate);
            c.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            c.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            c.Style.Alignment.WrapText = true;
            Border(c.Style.Border, CellRule);
        }
        ws.Row(row).Height = 30;
    }

    /// <summary>style_row: section band / total / subtotal / grand for one sheet row, as the PDF styles it.</summary>
    public static void StyleRow(IXLWorksheet ws, int row, int ncols, string kind, bool negative = false)
    {
        if (kind is not ("section" or "total" or "subtotal" or "grand") || ncols < 1) return;
        var s = ws.Range(row, 1, row, ncols).Style;
        var thin = XLColor.FromHtml("#" + CellRule);
        var slate = XLColor.FromHtml("#" + Slate);
        switch (kind)
        {
            case "section":
                s.Fill.BackgroundColor = XLColor.FromHtml("#" + SectionBg);
                s.Font.Bold = true;
                s.Font.FontSize = 10;
                s.Font.FontColor = XLColor.FromHtml("#" + SectionFg);
                break;
            case "total":
                s.Fill.BackgroundColor = XLColor.FromHtml("#" + TotalBg);
                s.Font.Bold = true;
                s.Font.FontSize = 10;
                s.Font.FontColor = XLColor.Black;
                s.Border.LeftBorder = s.Border.RightBorder = s.Border.BottomBorder = s.Border.TopBorder = XLBorderStyleValues.Thin;
                s.Border.LeftBorderColor = s.Border.RightBorderColor = s.Border.BottomBorderColor = thin;
                s.Border.TopBorderColor = XLColor.FromHtml("#" + TotalRule);
                break;
            case "subtotal":
                s.Fill.BackgroundColor = XLColor.FromHtml("#" + (negative ? NegBg : ProfitBg));
                s.Font.Bold = true;
                s.Font.FontSize = 10;
                s.Font.FontColor = XLColor.FromHtml("#" + (negative ? NegFg : "000000"));
                s.Border.LeftBorder = s.Border.RightBorder = XLBorderStyleValues.Thin;
                s.Border.LeftBorderColor = s.Border.RightBorderColor = thin;
                s.Border.TopBorder = s.Border.BottomBorder = XLBorderStyleValues.Medium;
                s.Border.TopBorderColor = s.Border.BottomBorderColor = slate;
                break;
            case "grand":
                s.Fill.BackgroundColor = slate;
                s.Font.Bold = true;
                s.Font.FontSize = 10;
                s.Font.FontColor = XLColor.White;
                s.Border.LeftBorder = s.Border.RightBorder = XLBorderStyleValues.None;
                s.Border.TopBorder = XLBorderStyleValues.Medium;
                s.Border.BottomBorder = XLBorderStyleValues.Double;
                s.Border.TopBorderColor = s.Border.BottomBorderColor = slate;
                break;
        }
    }

    // ── hand-built sheets (statements with their own layout) ───────────

    /// <summary>A workbook with the report's properties (set_properties).</summary>
    public XLWorkbook NewWorkbook(string title)
    {
        var wb = new XLWorkbook();
        wb.Properties.Title = title;
        wb.Properties.Author = UserName.Length > 0 ? UserName : "Accountix ERP";
        wb.Properties.Company = CompanyName;
        wb.Properties.Subject = title;
        return wb;
    }

    public static IXLWorksheet AddSheet(XLWorkbook wb, string name, int? position = null) =>
        position is int p ? wb.Worksheets.Add(SheetName(name), p) : wb.Worksheets.Add(SheetName(name));

    /// <summary>A data cell as the routes write one: DATA_FONT (or BOLD_FONT), THIN grid, right for figures.</summary>
    public static IXLCell Put(IXLWorksheet ws, int row, int col, object? value, bool bold = false, bool border = true)
    {
        var c = ws.Cell(row, col);
        SetValue(c, value);
        c.Style.Font.FontSize = 10;
        if (bold) c.Style.Font.Bold = true;
        if (border) Border(c.Style.Border, CellRule);
        c.Style.Alignment.Horizontal = IsNumber(value) ? XLAlignmentHorizontalValues.Right : XLAlignmentHorizontalValues.Left;
        c.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        return c;
    }

    /// <summary>finish_sheet: money / date formats on unformatted cells, widths, frozen header and print setup.</summary>
    public void FinishSheet(IXLWorksheet ws, int ncols, int firstRow = 3, string? title = null, bool autofilter = false)
    {
        string money = Money.ExcelFormat(NumberFormat);
        int last = ws.LastRowUsed()?.RowNumber() ?? firstRow;
        for (int r = firstRow; r <= last; r++)
            for (int ci = 1; ci <= ncols; ci++)
            {
                var c = ws.Cell(r, ci);
                if (c.Value.IsBlank || !string.IsNullOrEmpty(c.Style.NumberFormat.Format) || c.Style.NumberFormat.NumberFormatId != 0)
                    continue;
                if (c.Value.IsNumber) c.Style.NumberFormat.Format = money;
                else if (c.Value.IsDateTime) c.Style.NumberFormat.Format = DateFmt;
            }
        Autosize(ws, ncols, firstRow);
        SetupSheet(ws, ncols, FindHeaderRow(ws), title ?? ws.Name, autofilter, null, ncols > 5);
    }

    /// <summary>setup_sheet for a sheet whose header row is known (landscape: null = more than 5 columns).</summary>
    public void SetupSheet(IXLWorksheet ws, int ncols, int headerRow, string title, bool landscape) =>
        SetupSheet(ws, ncols, headerRow, title, false, null, landscape);

    /// <summary>_find_header_row: the first row from 3 down whose first cell carries the header fill.</summary>
    private static int? FindHeaderRow(IXLWorksheet ws)
    {
        int last = Math.Min(ws.LastRowUsed()?.RowNumber() ?? 0, 40);
        var slate = XLColor.FromHtml("#" + Slate);
        for (int r = SheetFirstRow - 2; r <= last; r++)
            if (ws.Cell(r, 1).Style.Fill.PatternType == XLFillPatternValues.Solid
                && ws.Cell(r, 1).Style.Fill.BackgroundColor == slate)
                return r;
        return null;
    }

    public static byte[] Save(XLWorkbook wb)
    {
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>send_export(buf, fmt, title, period): the file under Python's name for that title and period.</summary>
    public ExportFile File(string format, byte[] content, string title, string? period)
    {
        string fmt = (format ?? "").ToLowerInvariant();
        return fmt switch
        {
            "excel" or "xlsx" => new(FileName(title, "xlsx", period), XlsxType, content),
            "pdf" => new(FileName(title, "pdf", period), "application/pdf", content),
            "csv" => new(FileName(title, "csv", period), "text/csv", content),
            _ => throw new UnknownExportFormatException(format ?? ""),
        };
    }

    /// <summary>build_excel / build_pdf of a table, named by send_export (title and file period given separately).</summary>
    public ExportFile Send(string format, ReportTable table, string fileTitle, string? filePeriod)
    {
        string fmt = (format ?? "").ToLowerInvariant();
        byte[] bytes = fmt switch
        {
            "excel" or "xlsx" => BuildExcel(table),
            "pdf" => BuildPdf(table),
            "csv" => BuildCsv(table),
            _ => throw new UnknownExportFormatException(format ?? ""),
        };
        return File(fmt, bytes, fileTitle, filePeriod);
    }

    private void Autosize(IXLWorksheet ws, int ncols, int firstRow)
    {
        var merged = new HashSet<(int, int)>();
        foreach (var rng in ws.MergedRanges)
            if (rng.ColumnCount() > 1)
                foreach (var cell in rng.Cells())
                    merged.Add((cell.Address.RowNumber, cell.Address.ColumnNumber));
        int last = ws.LastRowUsed()?.RowNumber() ?? firstRow;
        for (int ci = 1; ci <= ncols; ci++)
        {
            int max = 0;
            for (int r = firstRow; r <= last; r++)
            {
                if (merged.Contains((r, ci))) continue;
                var cell = ws.Cell(r, ci);
                object? v = cell.Value.IsBlank ? null : cell.Value.IsNumber ? cell.Value.GetNumber()
                    : cell.Value.IsDateTime ? cell.Value.GetDateTime() : cell.Value.ToString(Inv);
                string text = CellText(v);
                // A wrapped header contributes its longest word.
                if (cell.Style.Alignment.WrapText)
                    text = (cell.Value.ToString(Inv) ?? "").Split(' ').OrderByDescending(w => w.Length).FirstOrDefault() ?? "";
                max = Math.Max(max, text.Length);
            }
            ws.Column(ci).Width = Math.Min(Math.Max(max + 2, 8), 60);
        }
    }

    private void SetupSheet(IXLWorksheet ws, int ncols, int? headerRowOrNull, string title, bool autofilter, int? lastRowOrNull, bool landscape)
    {
        if (headerRowOrNull is int headerRow)
        {
            ws.SheetView.FreezeRows(headerRow);
            ws.PageSetup.SetRowsToRepeatAtTop(headerRow, headerRow);
            int lastRow = lastRowOrNull ?? ws.LastRowUsed()?.RowNumber() ?? headerRow;
            if (autofilter && lastRow > headerRow)
                ws.Range(headerRow, 1, lastRow, Math.Max(ncols, 1)).SetAutoFilter();
        }
        ws.PageSetup.PageOrientation = landscape ? XLPageOrientation.Landscape : XLPageOrientation.Portrait;
        ws.PageSetup.PaperSize = XLPaperSize.A4Paper;
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.CenterHorizontally = true;
        ws.PageSetup.Margins.Left = ws.PageSetup.Margins.Right = 0.4;
        ws.PageSetup.Margins.Top = ws.PageSetup.Margins.Bottom = 0.6;
        string footer = (CompanyName + (CompanyName.Length > 0 && title.Length > 0 ? " · " : "") + title).Replace("&", "&&");
        ws.PageSetup.Footer.Left.AddText("&8" + footer);
        ws.PageSetup.Footer.Right.AddText("&8Page &P of &N");
        ws.ShowGridLines = false;
    }

    // ── PDF ─────────────────────────────────────────────────────────────

    private string PdfText(object? c, ReportTable t, int ci)
    {
        if (IsNumber(c))
        {
            var kind = ci < t.Columns.Count ? t.Columns[ci].Kind : ReportColumnKind.Text;
            decimal d = ToDecimal(c!);
            return kind switch
            {
                ReportColumnKind.Integer => Math.Round(d, 0).ToString("#,##0", Inv),
                ReportColumnKind.Quantity => d.ToString("#,##0.00", Inv).TrimEnd('0').TrimEnd('.'),
                _ => Money.Format(d, 2, NumberFormat),
            };
        }
        return c switch
        {
            DateTime dt => dt.ToString("dd MMM yyyy HH:mm", Inv),
            DateOnly d => d.ToString("dd MMM yyyy", Inv),
            null => "",
            _ => c.ToString() ?? "",
        };
    }

    public byte[] BuildPdf(ReportTable t)
    {
        PdfFonts.EnsureRegistered();
        int ncols = t.Columns.Count;
        bool landscape = t.Landscape || ncols > 5;
        string docTitle = AmpRe().Replace(t.Title, m => m.Value == "&amp;" ? "&" : m.Value);
        using var pdf = new PdfTableWriter(landscape, docTitle, UserName.Length > 0 ? UserName : "Accountix ERP");
        double pageWidthPt = pdf.ContentWidth;

        if (CompanyName.Length > 0)
            pdf.Heading(CompanyName, 11, bold: true, SectionFg);
        pdf.Heading(t.Title.Replace("&amp;", "&"), 16, bold: true, Slate, after: 2);
        if (!string.IsNullOrEmpty(t.Period))
            pdf.Heading(t.Period, 9.5, bold: false, SectionFg);
        var flt = FilterLines(t).Where(f => !string.IsNullOrEmpty(f)).ToList();
        if (flt.Count > 0)
            pdf.Heading(string.Join("  ·  ", flt), 7.5, bold: false, Muted);
        pdf.Space(5 * 72 / 25.4 + 9.6);

        var kinds = t.Rows.Select(r => r.Style ?? "plain").ToList();
        var body = t.Rows.Select(r => Enumerable.Range(0, ncols).Select(ci => PdfText(ci < r.Cells.Count ? r.Cells[ci] : null, t, ci)).ToList()).ToList();
        for (int i = 0; i < body.Count; i++)
            if (kinds[i] == "section")
            {
                string label = body[i].FirstOrDefault(c => c.Trim().Length > 0) ?? "";
                body[i] = [label, .. Enumerable.Repeat("", ncols - 1)];
            }

        // Numeric columns: every non-blank value looks like a figure.
        var numericCols = new HashSet<int>();
        for (int ci = 0; ci < ncols; ci++)
        {
            bool seen = false, ok = true;
            foreach (var r in t.Rows)
            {
                object? v = ci < r.Cells.Count ? r.Cells[ci] : null;
                if (v is null || v is string { Length: 0 }) continue;
                if (!LooksNumeric(v)) { ok = false; break; }
                seen = true;
            }
            if (ok && seen) numericCols.Add(ci);
        }

        // Column widths from the widest text, headings wrapping between words before the type shrinks.
        double baseFont = 8, headFont = 9, padX = 4;
        var widths = new double[ncols];
        using var measure = new PdfFonts.Measurer();
        // Each distinct text is measured once per column (a ledger repeats dates, vouchers and blanks).
        var bodyWidths = new double[ncols];
        for (int ci = 0; ci < ncols; ci++)
        {
            int col = ci;
            bodyWidths[ci] = body.Count == 0 ? 0 : body.Select(r => r[col]).Distinct().Max(x => measure.Width(x, baseFont, false));
        }
        for (int ci = 0; ci < ncols; ci++)
        {
            double bodyW = bodyWidths[ci];
            double headW = measure.Width(t.Columns[ci].Header.Split('\n').OrderByDescending(s => s.Length).First(), headFont, true);
            widths[ci] = Math.Max(bodyW, headW) + 2 * padX + 2;
        }
        if (widths.Sum() > pageWidthPt)
            for (int ci = 0; ci < ncols; ci++)
            {
                double bodyW = bodyWidths[ci];
                double headW = t.Columns[ci].Header.Split(' ', '\n').Max(w => measure.Width(w, headFont, true));
                widths[ci] = Math.Max(bodyW, headW) + 2 * padX + 2;
            }
        double total = widths.Sum();
        if (total > pageWidthPt)
        {
            double scale = pageWidthPt / total;
            baseFont = Math.Max(5.5, baseFont * scale);
            headFont = Math.Max(6.0, baseFont + 1);
            for (int ci = 0; ci < ncols; ci++) widths[ci] *= scale;
        }
        else if (total < pageWidthPt * 0.75 && ncols > 1)
        {
            var textCols = Enumerable.Range(0, ncols).Where(i => !numericCols.Contains(i)).ToList();
            if (textCols.Count == 0) textCols = Enumerable.Range(0, ncols).ToList();
            double extra = (pageWidthPt * 0.92 - total) / textCols.Count;
            foreach (int i in textCols) widths[i] += extra;
        }

        pdf.Table(t.Columns.Select(c => c.Header).ToList(), body, kinds, widths, baseFont, headFont, padX, numericCols,
                  t.MonoColumn, t.IndentColumn, t.BoldRows, (kind, ri, cells) => kind switch
                  {
                      "spacer" => new PdfTableWriter.Style(null, false, "000000", null, null),
                      "section" => new PdfTableWriter.Style(SectionBg, true, SectionFg, (0.5, CellRule), null),
                      "total" => new PdfTableWriter.Style(TotalBg, true, "000000", (0.6, TotalRule), null),
                      "subtotal" when cells.Any(c => c.StartsWith('(')) =>
                          new PdfTableWriter.Style(NegBg, true, NegFg, (1.0, Slate), (1.0, Slate)),
                      "subtotal" => new PdfTableWriter.Style(ProfitBg, true, "000000", (1.0, Slate), (1.0, Slate)),
                      "grand" => new PdfTableWriter.Style(Slate, true, "FFFFFF", null, null),
                      _ => new PdfTableWriter.Style(null, false, "000000", null, (0.4, Rule)),
                  });
        if (t.Rows.Count == 0)
        {
            pdf.Space(4 * 72 / 25.4);
            pdf.Heading("No entries for the selected filters.", 7.5, bold: false, Muted);
        }

        // Footer: rule, company · title, generated line, Page X of Y.
        string plainTitle = Regex.Replace(t.Title, "<[^>]+>", "").Replace("&amp;", "&");
        string footerLeft = string.Join(" · ", new[] { CompanyName, plainTitle }.Where(s => s.Length > 0));
        return pdf.Finish(footerLeft.Length > 110 ? footerLeft[..110] : footerLeft, GeneratedLine() is { Length: > 90 } g ? g[..90] : GeneratedLine(),
                          Muted, Rule);
    }

    // ── CSV ─────────────────────────────────────────────────────────────

    /// <summary>UTF-8 with BOM (Excel opens Urdu names and the rupee sign right), numbers raw, dates ISO.</summary>
    public static byte[] BuildCsv(ReportTable t)
    {
        using var ms = new MemoryStream();
        using (var w = new StreamWriter(ms, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 1 << 16, leaveOpen: true))
        {
            void Line(IEnumerable<object?> cells)
            {
                bool first = true;
                foreach (var cell in cells)
                {
                    if (!first) w.Write(',');
                    w.Write(Csv(cell));
                    first = false;
                }
                w.Write("\r\n");
            }
            Line(t.Columns.Select(c => (object?)c.Header));
            foreach (var r in t.Rows)
                Line(r.Cells);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Python's str(float) / repr: the shortest round-trip digits, fixed notation for
    /// exponents -4..15 (always with a ".0"), else "1e-05" / "1.5e+16" — what csv.writer prints.
    /// </summary>
    public static string PyFloat(double d)
    {
        if (double.IsNaN(d)) return "nan";
        if (double.IsInfinity(d)) return d > 0 ? "inf" : "-inf";
        if (d == 0) return double.IsNegative(d) ? "-0.0" : "0.0";
        string r = Math.Abs(d).ToString("R", Inv);
        int ePos = r.IndexOfAny(['E', 'e']);
        string mantissa = ePos < 0 ? r : r[..ePos];
        int exp = ePos < 0 ? 0 : int.Parse(r[(ePos + 1)..], NumberStyles.AllowLeadingSign, Inv);
        int dot = mantissa.IndexOf('.');
        string digits = dot < 0 ? mantissa : mantissa.Remove(dot, 1);
        int point = (dot < 0 ? mantissa.Length : dot) + exp;   // digits[..point] is the integer part
        int lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0') lead++;
        digits = digits[lead..];
        point -= lead;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) digits = "0";
        int sci = point - 1;
        string text;
        if (sci is >= -4 and < 16)
        {
            if (point <= 0) text = "0." + new string('0', -point) + digits;
            else if (point >= digits.Length) text = digits + new string('0', point - digits.Length) + ".0";
            else text = digits[..point] + "." + digits[point..];
        }
        else
            text = digits[..1] + (digits.Length > 1 ? "." + digits[1..] : "") + "e" + (sci < 0 ? "-" : "+") + Math.Abs(sci).ToString("00", Inv);
        return d < 0 ? "-" + text : text;
    }

    private static string Csv(object? v)
    {
        string s = v switch
        {
            null => "",
            DateTime dt => dt.TimeOfDay == TimeSpan.Zero && dt.Kind != DateTimeKind.Utc ? dt.ToString("yyyy-MM-ddTHH:mm:ss", Inv) : dt.ToString("yyyy-MM-ddTHH:mm:ss", Inv),
            DateOnly d => d.ToString("yyyy-MM-dd", Inv),
            decimal d => d.ToString(Inv),
            double d => PyFloat(d),
            float f => PyFloat(f),
            bool b => b ? "True" : "False",
            IFormattable f => f.ToString(null, Inv),
            _ => v.ToString() ?? "",
        };
        return s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}

/// <summary>Fonts for PDFs: Liberation Sans / Mono (SIL OFL), embedded, metric-compatible with Helvetica / Courier.</summary>
public static class PdfFonts
{
    public const string Sans = "Liberation Sans";
    public const string Mono = "Liberation Mono";
    private static readonly Lock Gate = new();
    private static bool _done;

    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (_done) return;
            GlobalFontSettings.FontResolver ??= new Resolver();
            _done = true;
        }
    }

    public static double Measure(string text, double size, bool bold)
    {
        EnsureRegistered();
        var font = new XFont(Sans, size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);
        using var g = XGraphics.CreateMeasureContext(new XSize(2000, 2000), XGraphicsUnit.Point, XPageDirection.Downwards);
        return g.MeasureString(text ?? "", font).Width;
    }

    /// <summary>One measuring context and one font per size for a whole document.</summary>
    public sealed class Measurer : IDisposable
    {
        private readonly XGraphics _g;
        private readonly Dictionary<(double, bool), XFont> _fonts = [];

        public Measurer()
        {
            EnsureRegistered();
            _g = XGraphics.CreateMeasureContext(new XSize(2000, 2000), XGraphicsUnit.Point, XPageDirection.Downwards);
        }

        public double Width(string? text, double size, bool bold)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            if (!_fonts.TryGetValue((size, bold), out var font))
                _fonts[(size, bold)] = font = new XFont(Sans, size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);
            return _g.MeasureString(text, font).Width;
        }

        public void Dispose() => _g.Dispose();
    }

    private sealed class Resolver : IFontResolver
    {
        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
        {
            if (familyName.Contains("Mono", StringComparison.OrdinalIgnoreCase) || familyName.Contains("Courier", StringComparison.OrdinalIgnoreCase))
                return new FontResolverInfo("LiberationMono-Regular");
            if (bold) return new FontResolverInfo("LiberationSans-Bold");
            if (italic) return new FontResolverInfo("LiberationSans-Italic");
            return new FontResolverInfo("LiberationSans-Regular");
        }

        public byte[]? GetFont(string faceName)
        {
            var asm = typeof(PdfFonts).Assembly;
            string? res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(faceName + ".ttf", StringComparison.Ordinal));
            if (res is null) return null;
            using var s = asm.GetManifestResourceStream(res)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
