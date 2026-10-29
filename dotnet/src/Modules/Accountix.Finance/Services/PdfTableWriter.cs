using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Accountix.Finance.Services;

/// <summary>
/// Draws a report table straight onto PDF pages (PdfSharp), the layout build_pdf
/// gives every export: centred heading block, a slate header row repeated on every
/// page, row bands by kind (section / total / subtotal / grand / spacer / plain),
/// and a footer rule with "company · title", the generated line and "Page X of Y".
/// Linear in the number of rows: no layout engine, one pass, then the footers.
/// </summary>
internal sealed class PdfTableWriter : IDisposable
{
    private const double Mm = 72 / 25.4;
    private readonly PdfDocument _doc = new();
    private readonly double _pageW, _pageH, _left, _top, _bottom;
    private readonly Dictionary<(double, bool, bool), XFont> _fonts = [];
    private readonly Dictionary<(string, double, bool, bool), double> _widths = [];
    private XGraphics? _g;
    private double _y;

    public PdfTableWriter(bool landscape, string title, string author)
    {
        PdfFonts.EnsureRegistered();
        _pageW = (landscape ? 297 : 210) * Mm;
        _pageH = (landscape ? 210 : 297) * Mm;
        _left = 10 * Mm;
        _top = 12 * Mm;
        _bottom = 17 * Mm;
        _doc.Info.Title = title;
        _doc.Info.Author = author;
        NewPage();
    }

    public double ContentWidth => _pageW - 2 * _left;

    private XFont Font(double size, bool bold, bool mono = false)
    {
        if (!_fonts.TryGetValue((size, bold, mono), out var f))
            _fonts[(size, bold, mono)] = f = new XFont(mono ? PdfFonts.Mono : PdfFonts.Sans, size,
                                                       bold && !mono ? XFontStyleEx.Bold : XFontStyleEx.Regular);
        return f;
    }

    private double Width(string text, double size, bool bold, bool mono = false)
    {
        if (text.Length == 0) return 0;
        if (!_widths.TryGetValue((text, size, bold, mono), out var w))
            _widths[(text, size, bold, mono)] = w = _g!.MeasureString(text, Font(size, bold, mono)).Width;
        return w;
    }

    private static double LineHeight(double size) => size * 1.2;

    private static XColor Color(string hex) =>
        XColor.FromArgb(Convert.ToInt32(hex[..2], 16), Convert.ToInt32(hex[2..4], 16), Convert.ToInt32(hex[4..6], 16));

    private void NewPage()
    {
        _g?.Dispose();
        var page = _doc.AddPage();
        page.Width = XUnit.FromPoint(_pageW);
        page.Height = XUnit.FromPoint(_pageH);
        _g = XGraphics.FromPdfPage(page);
        _y = _top;
    }

    /// <summary>A centred heading line (company, title, period, filters).</summary>
    public void Heading(string text, double size, bool bold, string color, double after = 0)
    {
        foreach (string line in Wrap(text, ContentWidth, size, bold, false))
        {
            double w = Width(line, size, bold);
            _g!.DrawString(line, Font(size, bold), new XSolidBrush(Color(color)), _left + (ContentWidth - w) / 2, _y, XStringFormats.TopLeft);
            _y += LineHeight(size);
        }
        _y += after;
    }

    public void Space(double points) => _y += points;

    /// <summary>Greedy word wrap within a width; explicit newlines kept; a word wider than the cell stays whole.</summary>
    private List<string> Wrap(string text, double width, double size, bool bold, bool mono)
    {
        var lines = new List<string>();
        foreach (string para in text.Split('\n'))
        {
            if (Width(para, size, bold, mono) <= width) { lines.Add(para); continue; }
            string cur = "";
            foreach (string word in para.Split(' '))
            {
                string next = cur.Length == 0 ? word : cur + " " + word;
                if (cur.Length > 0 && Width(next, size, bold, mono) > width)
                {
                    lines.Add(cur);
                    cur = word;
                }
                else cur = next;
            }
            lines.Add(cur);
        }
        return lines;
    }

    public sealed record Style(string? Fill, bool Bold, string Color, (double Width, string Color)? Top, (double Width, string Color)? Bottom);

    /// <summary>The table: header row (repeated on every page) and the body rows with their bands.</summary>
    public void Table(IReadOnlyList<string> headers, IReadOnlyList<List<string>> body, IReadOnlyList<string> kinds, double[] widths,
                      double baseFont, double headFont, double padX, ISet<int> numericCols, int? monoCol, int? indentCol,
                      IReadOnlySet<int> boldRows, Func<string, int, List<string>, Style> styleFor)
    {
        int ncols = widths.Length;
        double tableW = widths.Sum();
        var xs = new double[ncols + 1];
        xs[0] = _left;
        for (int i = 0; i < ncols; i++) xs[i + 1] = xs[i] + widths[i];
        double pad = baseFont >= 7 ? 4 : 2;
        var white = new XSolidBrush(XColors.White);

        // Header lines are the same on every page.
        var headLines = headers.Select((h, ci) => Wrap(h, widths[ci] - 2 * padX, headFont, true, false)).ToList();
        double headH = 2 * pad + headLines.Max(l => l.Count) * LineHeight(headFont);
        void Header()
        {
            _g!.DrawRectangle(new XSolidBrush(Color("1E293B")), _left, _y, tableW, headH);
            for (int ci = 0; ci < ncols; ci++)
            {
                var lines = headLines[ci];
                double ty = _y + (headH - lines.Count * LineHeight(headFont)) / 2;
                foreach (string line in lines)
                {
                    double w = Width(line, headFont, true);
                    _g.DrawString(line, Font(headFont, true), white, xs[ci] + (widths[ci] - w) / 2, ty, XStringFormats.TopLeft);
                    ty += LineHeight(headFont);
                }
            }
            _y += headH;
        }
        Header();

        for (int ri = 0; ri < body.Count; ri++)
        {
            string kind = kinds[ri];
            var cells = body[ri];
            var st = styleFor(kind, ri, cells);
            bool bold = st.Bold || boldRows.Contains(ri);
            string? fill = boldRows.Contains(ri) ? "E8EEF5" : st.Fill;
            bool section = kind == "section";
            var cellLines = new List<string>[ncols];
            int maxLines = 1;
            for (int ci = 0; ci < ncols; ci++)
            {
                if (section && ci > 0) { cellLines[ci] = []; continue; }
                bool mono = kind == "account" && monoCol == ci;
                double indent = kind == "account" && indentCol == ci ? 10 : 0;
                double avail = (section ? tableW : widths[ci]) - 2 * padX - indent;
                cellLines[ci] = cells[ci].Length == 0 ? [] : Wrap(cells[ci], avail, baseFont, bold, mono);
                maxLines = Math.Max(maxLines, cellLines[ci].Count);
            }
            double rowH = 2 * pad + maxLines * LineHeight(baseFont);
            if (_y + rowH > _pageH - _bottom)
            {
                NewPage();
                Header();
            }
            if (fill is not null)
                _g!.DrawRectangle(new XSolidBrush(Color(fill)), _left, _y, tableW, rowH);
            var brush = new XSolidBrush(Color(st.Color));
            for (int ci = 0; ci < ncols; ci++)
            {
                var lines = cellLines[ci];
                if (lines.Count == 0) continue;
                bool mono = kind == "account" && monoCol == ci;
                double indent = kind == "account" && indentCol == ci ? 10 : 0;
                double cellLeft = xs[ci] + padX + indent;
                double cellRight = (section ? xs[ncols] : xs[ci + 1]) - padX;
                double ty = _y + (rowH - lines.Count * LineHeight(baseFont)) / 2;
                var font = Font(baseFont, bold, mono);
                foreach (string line in lines)
                {
                    double x = numericCols.Contains(ci) && !section ? cellRight - Width(line, baseFont, bold, mono) : cellLeft;
                    _g!.DrawString(line, font, brush, x, ty, XStringFormats.TopLeft);
                    ty += LineHeight(baseFont);
                }
            }
            if (st.Top is var (tw, tc))
                _g!.DrawLine(new XPen(Color(tc), tw), _left, _y, _left + tableW, _y);
            if (st.Bottom is var (bw, bc))
                _g!.DrawLine(new XPen(Color(bc), bw), _left, _y + rowH, _left + tableW, _y + rowH);
            _y += rowH;
        }
    }

    /// <summary>Footer on every page: rule, left text, centred generated line, "Page X of Y".</summary>
    public byte[] Finish(string footerLeft, string generated, string muted, string rule)
    {
        _g?.Dispose();
        _g = null;
        int n = _doc.PageCount;
        for (int i = 0; i < n; i++)
        {
            using var g = XGraphics.FromPdfPage(_doc.Pages[i], XGraphicsPdfPageOptions.Append);
            _g = g;
            double y = _pageH - 6 * Mm - 4 - LineHeight(7);
            g.DrawLine(new XPen(Color(rule), 0.5), _left, y, _left + ContentWidth, y);
            var font = Font(7, false);
            var brush = new XSolidBrush(Color(muted));
            double third = ContentWidth / 3;
            g.DrawString(Clip(footerLeft, third - 4), font, brush, _left + 2, y + 2, XStringFormats.TopLeft);
            string mid = Clip(generated, third - 4);
            g.DrawString(mid, font, brush, _left + third + (third - Width(mid, 7, false)) / 2, y + 2, XStringFormats.TopLeft);
            string page = $"Page {i + 1} of {n}";
            g.DrawString(page, font, brush, _left + ContentWidth - 2 - Width(page, 7, false), y + 2, XStringFormats.TopLeft);
            _g = null;
        }
        using var ms = new MemoryStream();
        _doc.Save(ms, false);
        return ms.ToArray();
    }

    private string Clip(string text, double width)
    {
        if (Width(text, 7, false) <= width) return text;
        while (text.Length > 1 && Width(text + "…", 7, false) > width) text = text[..^1];
        return text + "…";
    }

    public void Dispose()
    {
        _g?.Dispose();
        _doc.Dispose();
    }
}
