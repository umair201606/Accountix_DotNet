using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace Accountix.Payroll.Services;

/// <summary>One line of the bank sheet: what the bank pays to whom.</summary>
public sealed record BankSheetRow(string Code, string Name, string BankName, string AccountTitle, string AccountNumber, double Net);

/// <summary>What the salary slip prints (view_slip / download_slip_pdf).</summary>
public sealed class SlipPrint
{
    public required string CompanyName { get; init; }
    public required int Month { get; init; }
    public required int Year { get; init; }
    public required string EmployeeName { get; init; }
    public string? Designation { get; init; }
    public string? Department { get; init; }
    public required string Code { get; init; }
    public string? Cnic { get; init; }
    public required double BasicSalary { get; init; }
    public required double GrossPay { get; init; }
    public required double TotalDeductions { get; init; }
    public required double NetPay { get; init; }
    public required SlipComponents Components { get; init; }
}

/// <summary>
/// The payroll files, in the layouts the Python app writes them (openpyxl and
/// reportlab, not the report pipeline): banks import the sheet as it is.
/// </summary>
public sealed class PayrollExports(CompanyClock clock)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static void Header(IXLCell c, string text, bool border)
    {
        c.Value = text;
        c.Style.Font.Bold = true;
        c.Style.Font.FontColor = XLColor.White;
        c.Style.Font.FontSize = 11;
        c.Style.Fill.BackgroundColor = XLColor.FromHtml("#1A237E");
        c.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        if (border)
        {
            c.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            c.Style.Alignment.WrapText = true;
            Thin(c);
        }
    }

    private static void Thin(IXLCell c)
    {
        c.Style.Border.LeftBorder = XLBorderStyleValues.Thin;
        c.Style.Border.RightBorder = XLBorderStyleValues.Thin;
        c.Style.Border.TopBorder = XLBorderStyleValues.Thin;
        c.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
    }

    private static byte[] Save(XLWorkbook wb)
    {
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>export_bank_ledger: one row per slip with a net to pay, then the total (= Salary Payable).</summary>
    public byte[] BankSheet(int month, int year, IEnumerable<BankSheetRow> rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet($"Payroll_{month}_{year}");
        string[] headers = ["Employee Code", "Employee Name", "Bank Name", "Account Title", "Account Number", "Net Pay"];
        for (int i = 0; i < headers.Length; i++)
            Header(ws.Cell(1, i + 1), headers[i], border: false);
        int ri = 1;
        double total = 0.0;
        foreach (var r in rows)
        {
            double net = Py.Round(r.Net, 2);
            if (net <= 0) continue;
            ri++;
            total += net;
            object[] values = [r.Code, r.Name, r.BankName, r.AccountTitle, r.AccountNumber, net];
            for (int ci = 1; ci <= 6; ci++)
            {
                var cell = ws.Cell(ri, ci);
                if (values[ci - 1] is double d) cell.Value = d;
                else if (values[ci - 1] is string s && s.Length > 0) cell.Value = s;
                cell.Style.Alignment.Horizontal = ci < 6 ? XLAlignmentHorizontalValues.Left : XLAlignmentHorizontalValues.Right;
            }
            ws.Cell(ri, 6).Style.NumberFormat.Format = "#,##0.00";
        }
        var label = ws.Cell(ri + 1, 5);
        label.Value = "Total";
        label.Style.Font.Bold = true;
        var tc = ws.Cell(ri + 1, 6);
        tc.Value = Py.Round(total, 2);
        tc.Style.Font.Bold = true;
        tc.Style.NumberFormat.Format = "#,##0.00";
        for (int ci = 1; ci <= headers.Length; ci++)
            ws.Column(ci).Width = 22;
        return Save(wb);
    }

    public static readonly string[] TemplateHeaders =
    [
        "Employee Code", "Full Name", "Bonus", "Income Tax (blank = automatic)",
        "Custom Deduction", "Allowances (blank = salary package)",
    ];

    /// <summary>download_payroll_template: this company's salaried staff, and only the columns the upload reads.</summary>
    public byte[] Template(IEnumerable<(string Code, string Name)> staff)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Payroll Data");
        for (int i = 0; i < TemplateHeaders.Length; i++)
            Header(ws.Cell(1, i + 1), TemplateHeaders[i], border: true);
        int row = 1;
        foreach (var (code, name) in staff)
        {
            row++;
            object?[] values = [code, name, null, null, null, null];
            for (int ci = 1; ci <= values.Length; ci++)
            {
                var c = ws.Cell(row, ci);
                if (values[ci - 1] is string s && s.Length > 0) c.Value = s;
                Thin(c);
            }
        }
        for (int ci = 1; ci <= TemplateHeaders.Length; ci++)
            ws.Column(ci).Width = 20;
        return Save(wb);
    }

    /// <summary>The uploaded sheet's rows as header → text (csv.DictReader / openpyxl, every value stripped).</summary>
    public static List<Dictionary<string, string>> ReadUpload(byte[] data, string ext)
    {
        var rows = new List<Dictionary<string, string>>();
        if (ext == ".csv")
        {
            string text = new UTF8Encoding(false).GetString(data);
            if (text.Length > 0 && text[0] == '﻿') text = text[1..];
            var records = Csv(text);
            if (records.Count == 0) return rows;
            var headers = records[0];
            foreach (var rec in records.Skip(1))
            {
                if (rec.Count == 0) continue;
                var row = new Dictionary<string, string>();
                for (int i = 0; i < headers.Count; i++)
                    row[headers[i]] = i < rec.Count ? rec[i] : "";
                rows.Add(row);
            }
            return rows;
        }
        using var wb = new XLWorkbook(new MemoryStream(data));
        var ws = wb.Worksheets.FirstOrDefault(w => w.TabActive) ?? wb.Worksheet(1);
        var used = ws.RangeUsed();
        if (used is null) return rows;
        int lastCol = used.LastColumn().ColumnNumber(), lastRow = used.LastRow().RowNumber();
        var head = Enumerable.Range(1, lastCol).Select(c => CellText(ws.Cell(1, c)).Trim()).ToList();
        for (int r = 2; r <= lastRow; r++)
        {
            var values = Enumerable.Range(1, lastCol).Select(c => CellText(ws.Cell(r, c)).Trim()).ToList();
            if (values.All(v => v.Length == 0)) continue;
            var row = new Dictionary<string, string>();
            for (int i = 0; i < head.Count; i++)
                row[head[i]] = values[i];
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>str(cell.value) as openpyxl reads it: whole numbers without ".0".</summary>
    private static string CellText(IXLCell c)
    {
        var v = c.Value;
        if (v.IsBlank) return "";
        if (v.IsNumber)
        {
            double d = v.GetNumber();
            return d == Math.Floor(d) && Math.Abs(d) < 1e15 ? ((long)d).ToString(Inv) : Py.Repr(d);
        }
        if (v.IsBoolean) return v.GetBoolean() ? "True" : "False";
        if (v.IsDateTime) return v.GetDateTime().ToString("yyyy-MM-dd HH:mm:ss", Inv);
        return v.ToString(Inv);
    }

    private static List<List<string>> Csv(string text)
    {
        var records = new List<List<string>>();
        var field = new StringBuilder();
        var record = new List<string>();
        bool quoted = false, any = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else field.Append(ch);
                continue;
            }
            switch (ch)
            {
                case '"': quoted = true; any = true; break;
                case ',': record.Add(field.ToString()); field.Clear(); any = true; break;
                case '\r': break;
                case '\n':
                    if (any || field.Length > 0) record.Add(field.ToString());
                    records.Add(record);
                    record = [];
                    field.Clear();
                    any = false;
                    break;
                default: field.Append(ch); any = true; break;
            }
        }
        if (any || field.Length > 0)
        {
            record.Add(field.ToString());
            records.Add(record);
        }
        return records;
    }

    // ── Salary slip PDF (reportlab canvas, A4, coordinates from the bottom as Python draws them) ──

    private static readonly XColor Navy = XColor.FromArgb(0x1a, 0x23, 0x7e);

    /// <summary>download_slip_pdf.</summary>
    public byte[] SlipPdf(SlipPrint s, Func<double, int, string> money)
    {
        PayrollFonts.EnsureRegistered();
        var doc = new PdfDocument();
        doc.Info.Title = $"Salary Slip - {s.Month}/{s.Year}";
        const double pw = 595.2755905511812, ph = 841.8897637795277, margin = 50, colMid = pw / 2;
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(pw);
        page.Height = XUnit.FromPoint(ph);
        var g = XGraphics.FromPdfPage(page);
        XFont F(double size, bool bold = false) => new(PayrollFonts.Sans, size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);
        void Text(string t, XFont f, XColor col, double x, double y, XStringFormat? fmt = null) =>
            g.DrawString(t, f, new XSolidBrush(col), x, ph - y, fmt ?? XStringFormats.BaseLineLeft);
        void NewPage()
        {
            g.Dispose();
            page = doc.AddPage();
            page.Width = XUnit.FromPoint(pw);
            page.Height = XUnit.FromPoint(ph);
            g = XGraphics.FromPdfPage(page);
        }

        // Header
        Text(s.CompanyName, F(20, true), Navy, pw / 2, ph - 40, XStringFormats.BaseLineCenter);
        Text($"Salary Slip - {s.Month}/{s.Year}", F(11), XColors.Black, pw / 2, ph - 58, XStringFormats.BaseLineCenter);
        g.DrawLine(new XPen(Navy, 2), margin, 65, pw - margin, 65);

        // Employee
        double y = ph - 88;
        var body = F(9.5);
        Text($"Employee: {s.EmployeeName}", body, XColors.Black, margin, y);
        Text($"Designation: {(string.IsNullOrEmpty(s.Designation) ? "-" : s.Designation)}", body, XColors.Black, colMid + 10, y);
        y -= 15;
        Text($"Department: {(string.IsNullOrEmpty(s.Department) ? "-" : s.Department)}", body, XColors.Black, margin, y);
        Text($"Code: {s.Code}", body, XColors.Black, colMid + 10, y);
        y -= 15;
        Text($"CNIC: {(string.IsNullOrEmpty(s.Cnic) ? "-" : s.Cnic)}", body, XColors.Black, margin, y);
        y -= 22;

        Text("Earnings", F(11, true), Navy, margin, y);
        Text("Deductions", F(11, true), Navy, colMid + 10, y);
        y -= 16;

        var earnings = new List<(string, double)> { ("Basic Salary", s.BasicSalary) };
        earnings.AddRange(s.Components.Allowances.Where(kv => kv.Value != 0).Select(kv => (kv.Key, kv.Value)));
        var deductions = s.Components.Deductions.Where(kv => kv.Value != 0).Select(kv => (kv.Key, kv.Value)).ToList();
        int maxRows = Math.Max(Math.Max(earnings.Count, deductions.Count), 0);
        var shade = new XSolidBrush(XColor.FromArgb(0xf0, 0xf4, 0xff));
        for (int i = 0; i < maxRows; i++)
        {
            if (y < 150)
            {
                NewPage();
                y = ph - 60;
                Text("Earnings (contd.)", F(11, true), Navy, margin, y);
                Text("Deductions (contd.)", F(11, true), Navy, colMid + 10, y);
                y -= 16;
            }
            var fill = i % 2 == 1 ? shade : XBrushes.White;
            g.DrawRectangle(fill, margin, ph - (y - 2) - 14, colMid - margin - 5, 14);
            g.DrawRectangle(fill, colMid + 5, ph - (y - 2) - 14, pw - margin - colMid - 5, 14);
            if (i < earnings.Count)
            {
                Text(earnings[i].Item1, body, XColors.Black, margin + 3, y);
                Text($"Rs. {money(earnings[i].Item2, 2)}", body, XColors.Black, colMid - 10, y, XStringFormats.BaseLineRight);
            }
            if (i < deductions.Count)
            {
                Text(deductions[i].Item1, body, XColors.Black, colMid + 10, y);
                Text($"Rs. {money(deductions[i].Item2, 2)}", body, XColors.Black, pw - margin, y, XStringFormats.BaseLineRight);
            }
            y -= 14;
        }
        y -= 6;

        // Totals
        if (y < 170)
        {
            NewPage();
            y = ph - 60;
        }
        g.DrawLine(new XPen(Navy, 1), margin, ph - y, pw - margin, ph - y);
        var bold10 = F(10, true);
        Text("Total Earnings", bold10, XColors.Black, margin, y - 14);
        Text($"Rs. {money(s.GrossPay, 2)}", bold10, XColors.Black, colMid - 10, y - 14, XStringFormats.BaseLineRight);
        Text("Total Deductions", bold10, XColors.Black, colMid + 10, y - 14);
        Text($"Rs. {money(s.TotalDeductions, 2)}", bold10, XColors.Black, pw - margin, y - 14, XStringFormats.BaseLineRight);
        y -= 24;

        // Net pay box
        double boxY = y - 50;
        var green = XColor.FromArgb(0x1b, 0x5e, 0x20);
        g.DrawRoundedRectangle(new XPen(green, 2), new XSolidBrush(XColor.FromArgb(0xe8, 0xf5, 0xe9)),
                               margin, ph - boxY - 48, pw - 2 * margin, 48, 12, 12);
        Text($"Net Payable: Rs. {money(s.NetPay, 0)}", F(14, true), green, margin + 12, boxY + 16);
        string words = NumToWords((long)Py.Round(s.NetPay, 0));
        Text($"(Rupees {words})", F(9), green, pw - margin - 12, boxY + 18, XStringFormats.BaseLineRight);
        y = boxY - 20;

        // Footer
        if (s.Components.Info is { Count: > 0 } info)
            Text(string.Join("  ", info.Select(kv => $"{kv.Key}: {(kv.Value.IsInt ? kv.Value.ToString() : G(kv.Value.Value))}")),
                 F(8.5), XColor.FromArgb(0x37, 0x41, 0x51), margin, y);
        Text($"Generated on {clock.Now.ToString("dd-MMM-yyyy HH:mm", Inv)} | This is a computer-generated slip",
             F(7.5), XColor.FromArgb(0x9c, 0xa3, 0xaf), pw / 2, 30, XStringFormats.BaseLineCenter);
        g.Dispose();
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    /// <summary>f"{v:g}".</summary>
    private static string G(double v) => v.ToString("G6", Inv).Replace("E", "e");

    /// <summary>_num_to_words: Indian system (Lakh, Crore), "... Only".</summary>
    public static string NumToWords(long n)
    {
        if (n == 0) return "Zero";
        string[] ones = ["", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine",
            "Ten", "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen"];
        string[] tens = ["", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety"];
        string Convert(long num)
        {
            if (num < 0) return "";
            if (num < 20) return ones[num];
            if (num < 100) return tens[num / 10] + (num % 10 != 0 ? " " + ones[num % 10] : "");
            if (num < 1000) return ones[num / 100] + " Hundred" + (num % 100 != 0 ? " " + Convert(num % 100) : "");
            if (num < 100000) return Convert(num / 1000) + " Thousand" + (num % 1000 != 0 ? " " + Convert(num % 1000) : "");
            if (num < 10000000) return Convert(num / 100000) + " Lakh" + (num % 100000 != 0 ? " " + Convert(num % 100000) : "");
            return Convert(num / 10000000) + " Crore" + (num % 10000000 != 0 ? " " + Convert(num % 10000000) : "");
        }
        return Convert(n) + " Only";
    }
}

/// <summary>The components_json of a slip, normalised (_normalize_comps).</summary>
public sealed class SlipComponents
{
    public OrderedDictionary<string, double> Allowances { get; } = new();
    public OrderedDictionary<string, double> Deductions { get; } = new();
    public OrderedDictionary<string, Py.Num>? Info { get; private set; }

    public static SlipComponents Parse(string? json)
    {
        var c = new SlipComponents();
        if (string.IsNullOrEmpty(json)) return c;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                bool structured = root.TryGetProperty("allowances", out var a) & root.TryGetProperty("deductions", out var d);
                if (structured)
                {
                    Fill(c.Allowances, a);
                    Fill(c.Deductions, d);
                    if (root.TryGetProperty("info", out var info) && info.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        c.Info = new();
                        foreach (var p in info.EnumerateObject())
                            c.Info[p.Name] = p.Value.ValueKind == System.Text.Json.JsonValueKind.Number && !p.Value.GetRawText().Contains('.')
                                             && !p.Value.GetRawText().Contains('e')
                                ? Py.Num.Int(p.Value.GetInt64())
                                : new Py.Num(Py.JsonNumber(p.Value) ?? 0);
                    }
                }
                else
                    foreach (var p in root.EnumerateObject())
                        c.Allowances[p.Name] = Truthy(p.Value) ? Py.JsonNumber(p.Value) ?? 0 : 0;
            }
            else if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var item in root.EnumerateArray().Where(i => i.ValueKind == System.Text.Json.JsonValueKind.Object))
                {
                    string name = item.TryGetProperty("name", out var n) ? n.ToString() : "Item";
                    double val = item.TryGetProperty("value", out var v) ? Py.JsonNumber(v) ?? 0 : 0;
                    string type = item.TryGetProperty("type", out var t) ? t.ToString() : "allowance";
                    (type is "deduction" or "tax" or "loan" ? c.Deductions : c.Allowances)[name] = val;
                }
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return c;
    }

    private static bool Truthy(System.Text.Json.JsonElement e) => e.ValueKind switch
    {
        System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.False => false,
        System.Text.Json.JsonValueKind.Number => e.GetDouble() != 0,
        System.Text.Json.JsonValueKind.String => e.GetString()!.Length > 0,
        _ => true,
    };

    private static void Fill(OrderedDictionary<string, double> into, System.Text.Json.JsonElement e)
    {
        if (e.ValueKind != System.Text.Json.JsonValueKind.Object) return;
        foreach (var p in e.EnumerateObject())
            into[p.Name] = Py.JsonNumber(p.Value) ?? 0;
    }
}

/// <summary>Liberation Sans for the slip PDF; the same face names as Finance's resolver, so either serves both.</summary>
public static class PayrollFonts
{
    public const string Sans = "Liberation Sans";
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
            var asm = typeof(PayrollFonts).Assembly;
            string? res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(faceName + ".ttf", StringComparison.Ordinal));
            if (res is null) return null;
            using var s = asm.GetManifestResourceStream(res)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
