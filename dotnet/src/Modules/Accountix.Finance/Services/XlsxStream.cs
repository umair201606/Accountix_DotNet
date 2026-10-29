using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Accountix.Finance.Services;

/// <summary>
/// A streaming .xlsx writer for flat report tables (build_excel / add_list_sheet):
/// the cells are written once, straight into the package, with a small table of
/// cell styles shared by every cell that looks alike. No object per cell, so a
/// sheet of 10,000 rows × 15 columns takes a fraction of a second (ClosedXML
/// needed 1.3 s at best). The layout is exactly BuildExcel's: heading block,
/// slate header row, row bands, money / quantity / date formats, column widths
/// from the text, frozen and print-repeated header, AutoFilter, A4 fit-to-width,
/// footer "company · title" and "Page X of Y", gridlines off.
/// </summary>
internal sealed class XlsxStream
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ── style tables ────────────────────────────────────────────────────

    public sealed record Font(double Size, bool Bold = false, bool Italic = false, string? Color = null);
    public sealed record Border(string? Left, string? Right, string? Top, string? Bottom, string LeftColor = "CBD5E1",
                                string RightColor = "CBD5E1", string TopColor = "CBD5E1", string BottomColor = "CBD5E1");
    public sealed record Xf(Font Font, string? Fill, Border? Border, string? NumFmt, string? HAlign, string? VAlign, bool Wrap = false);

    private readonly List<Font> _fonts = [new(11)];
    private readonly List<string?> _fills = [null, "gray125"];
    private readonly List<Border?> _borders = [null];
    private readonly List<string> _numFmts = [];
    private readonly List<Xf> _xfs = [new(new Font(11), null, null, null, null, null)];
    private readonly Dictionary<Xf, int> _xfIds = [];

    private static int IndexOf<T>(List<T> list, T item)
    {
        int i = list.IndexOf(item);
        if (i >= 0) return i;
        list.Add(item);
        return list.Count - 1;
    }

    public int Style(Xf xf)
    {
        if (_xfIds.TryGetValue(xf, out int id)) return id;
        IndexOf(_fonts, xf.Font);
        if (xf.Fill is not null) IndexOf(_fills, xf.Fill);
        if (xf.Border is not null) IndexOf(_borders, xf.Border);
        if (xf.NumFmt is not null) IndexOf(_numFmts, xf.NumFmt);
        _xfs.Add(xf);
        return _xfIds[xf] = _xfs.Count - 1;
    }

    // ── sheets ──────────────────────────────────────────────────────────

    public sealed class Sheet
    {
        public required string Name { get; init; }
        public int Columns { get; init; }
        public double[] Widths { get; init; } = [];
        public int HeaderRow { get; init; }
        public int? FilterLastRow { get; init; }
        public bool Landscape { get; init; }
        public string FooterLeft { get; init; } = "";
        public List<(int Row, int FromCol, int ToCol)> Merges { get; } = [];
        public Dictionary<int, double> RowHeights { get; } = [];
        /// <summary>Writes the &lt;row&gt; elements; given the writer and the cell helper.</summary>
        public required Action<Rows> WriteRows { get; init; }
    }

    private readonly List<Sheet> _sheets = [];
    public void Add(Sheet s) => _sheets.Add(s);

    /// <summary>Writes rows and cells in order (rows ascending, cells left to right).</summary>
    public sealed class Rows(StreamWriter w, Dictionary<int, double> heights)
    {
        private int _row;
        private int _col;

        public void Row(int row)
        {
            if (_row > 0) w.Write("</row>");
            _row = row;
            _col = 0;
            w.Write("<row r=\"");
            w.Write(row);
            if (heights.TryGetValue(row, out double h))
            {
                w.Write("\" ht=\"");
                w.Write(h.ToString(Inv));
                w.Write("\" customHeight=\"1");
            }
            w.Write("\">");
        }

        public void End()
        {
            if (_row > 0) w.Write("</row>");
            _row = 0;
        }

        private void Ref(int col, int style)
        {
            _col = col;
            w.Write("<c r=\"");
            WriteColumn(w, col);
            w.Write(_row);
            if (style > 0)
            {
                w.Write("\" s=\"");
                w.Write(style);
            }
            w.Write('"');
        }

        public void Blank(int col, int style)
        {
            Ref(col, style);
            w.Write("/>");
        }

        public void Text(int col, string text, int style)
        {
            Ref(col, style);
            w.Write(" t=\"inlineStr\"><is><t");
            if (text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1]))) w.Write(" xml:space=\"preserve\"");
            w.Write('>');
            Escape(w, text);
            w.Write("</t></is></c>");
        }

        public void Number(int col, string number, int style)
        {
            Ref(col, style);
            w.Write("><v>");
            w.Write(number);
            w.Write("</v></c>");
        }

        public void Bool(int col, bool value, int style)
        {
            Ref(col, style);
            w.Write(value ? " t=\"b\"><v>1</v></c>" : " t=\"b\"><v>0</v></c>");
        }
    }

    public static void WriteColumn(TextWriter w, int col)
    {
        Span<char> buf = stackalloc char[4];
        int i = 4;
        while (col > 0)
        {
            int rem = (col - 1) % 26;
            buf[--i] = (char)('A' + rem);
            col = (col - 1) / 26;
        }
        w.Write(buf[i..]);
    }

    public static string Column(int col)
    {
        var sw = new StringWriter();
        WriteColumn(sw, col);
        return sw.ToString();
    }

    private static void Escape(TextWriter w, string s)
    {
        foreach (char ch in s)
        {
            switch (ch)
            {
                case '&': w.Write("&amp;"); break;
                case '<': w.Write("&lt;"); break;
                case '>': w.Write("&gt;"); break;
                case '"': w.Write("&quot;"); break;
                case '\t' or '\n' or '\r': w.Write(ch); break;
                default:
                    if (ch >= ' ' && ch != '￾' && ch != '￿') w.Write(ch);
                    break;
            }
        }
    }

    private static string Esc(string s)
    {
        var sw = new StringWriter();
        Escape(sw, s);
        return sw.ToString();
    }

    // ── package ─────────────────────────────────────────────────────────

    public byte[] Save(string title, string author, string company)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Part(string path, string xml)
            {
                using var w = new StreamWriter(zip.CreateEntry(path, CompressionLevel.Fastest).Open(), new UTF8Encoding(false));
                w.Write(xml);
            }

            var types = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>" +
                "<Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/>");
            for (int i = 1; i <= _sheets.Count; i++)
                types.Append($"<Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            types.Append("</Types>");
            Part("[Content_Types].xml", types.ToString());

            Part("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
                "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties\" Target=\"docProps/app.xml\"/>" +
                "</Relationships>");

            string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", Inv);
            Part("docProps/core.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" " +
                "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" " +
                "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
                $"<dc:title>{Esc(title)}</dc:title><dc:subject>{Esc(title)}</dc:subject><dc:creator>{Esc(author)}</dc:creator>" +
                $"<dcterms:created xsi:type=\"dcterms:W3CDTF\">{now}</dcterms:created><dcterms:modified xsi:type=\"dcterms:W3CDTF\">{now}</dcterms:modified>" +
                "</cp:coreProperties>");
            Part("docProps/app.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\">" +
                $"<Application>Accountix ERP</Application><Company>{Esc(company)}</Company></Properties>");

            var wbXml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><bookViews><workbookView activeTab=\"0\"/></bookViews><sheets>");
            for (int i = 0; i < _sheets.Count; i++)
                wbXml.Append($"<sheet name=\"{Esc(_sheets[i].Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
            wbXml.Append("</sheets><definedNames>");
            for (int i = 0; i < _sheets.Count; i++)
            {
                var s = _sheets[i];
                string q = "'" + s.Name.Replace("'", "''") + "'";
                if (s.FilterLastRow is int fl)
                    wbXml.Append($"<definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"{i}\" hidden=\"1\">{Esc(q)}!$A${s.HeaderRow}:${Column(Math.Max(s.Columns, 1))}${fl}</definedName>");
                wbXml.Append($"<definedName name=\"_xlnm.Print_Titles\" localSheetId=\"{i}\">{Esc(q)}!${s.HeaderRow}:${s.HeaderRow}</definedName>");
            }
            wbXml.Append("</definedNames></workbook>");
            Part("xl/workbook.xml", wbXml.ToString());

            var rels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (int i = 1; i <= _sheets.Count; i++)
                rels.Append($"<Relationship Id=\"rId{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i}.xml\"/>");
            rels.Append($"<Relationship Id=\"rId{_sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
            rels.Append("</Relationships>");
            Part("xl/_rels/workbook.xml.rels", rels.ToString());

            for (int i = 0; i < _sheets.Count; i++)
            {
                var s = _sheets[i];
                using var w = new StreamWriter(zip.CreateEntry($"xl/worksheets/sheet{i + 1}.xml", CompressionLevel.Fastest).Open(),
                                               new UTF8Encoding(false), 1 << 16);
                w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                        "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                        "<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr>");
                w.Write($"<sheetViews><sheetView showGridLines=\"0\"{(i == 0 ? " tabSelected=\"1\"" : "")} workbookViewId=\"0\">" +
                        $"<pane ySplit=\"{s.HeaderRow}\" topLeftCell=\"A{s.HeaderRow + 1}\" activePane=\"bottomLeft\" state=\"frozen\"/>" +
                        "<selection pane=\"bottomLeft\"/></sheetView></sheetViews><sheetFormatPr defaultRowHeight=\"15\"/>");
                if (s.Widths.Length > 0)
                {
                    w.Write("<cols>");
                    for (int c = 0; c < s.Widths.Length; c++)
                        w.Write($"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{s.Widths[c].ToString(Inv)}\" customWidth=\"1\"/>");
                    w.Write("</cols>");
                }
                w.Write("<sheetData>");
                var rows = new Rows(w, s.RowHeights);
                s.WriteRows(rows);
                rows.End();
                w.Write("</sheetData>");
                if (s.FilterLastRow is int last)
                    w.Write($"<autoFilter ref=\"A{s.HeaderRow}:{Column(Math.Max(s.Columns, 1))}{last}\"/>");
                if (s.Merges.Count > 0)
                {
                    w.Write($"<mergeCells count=\"{s.Merges.Count}\">");
                    foreach (var (r, a, b) in s.Merges)
                        w.Write($"<mergeCell ref=\"{Column(a)}{r}:{Column(b)}{r}\"/>");
                    w.Write("</mergeCells>");
                }
                w.Write("<printOptions horizontalCentered=\"1\"/>" +
                        "<pageMargins left=\"0.4\" right=\"0.4\" top=\"0.6\" bottom=\"0.6\" header=\"0.3\" footer=\"0.3\"/>" +
                        $"<pageSetup paperSize=\"9\" fitToWidth=\"1\" fitToHeight=\"0\" orientation=\"{(s.Landscape ? "landscape" : "portrait")}\"/>" +
                        $"<headerFooter><oddFooter>&amp;L&amp;8{Esc(s.FooterLeft)}&amp;R&amp;8Page &amp;P of &amp;N</oddFooter></headerFooter>");
                w.Write("</worksheet>");
            }

            Part("xl/styles.xml", StylesXml());
        }
        return ms.ToArray();
    }

    private string StylesXml()
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        if (_numFmts.Count > 0)
        {
            sb.Append($"<numFmts count=\"{_numFmts.Count}\">");
            for (int i = 0; i < _numFmts.Count; i++)
                sb.Append($"<numFmt numFmtId=\"{164 + i}\" formatCode=\"{Esc(_numFmts[i])}\"/>");
            sb.Append("</numFmts>");
        }
        sb.Append($"<fonts count=\"{_fonts.Count}\">");
        foreach (var f in _fonts)
        {
            sb.Append("<font>");
            if (f.Bold) sb.Append("<b/>");
            if (f.Italic) sb.Append("<i/>");
            sb.Append($"<sz val=\"{f.Size.ToString(Inv)}\"/>");
            sb.Append(f.Color is null ? "<color theme=\"1\"/>" : $"<color rgb=\"FF{f.Color}\"/>");
            sb.Append("<name val=\"Calibri\"/><family val=\"2\"/><scheme val=\"minor\"/></font>");
        }
        sb.Append($"</fonts><fills count=\"{_fills.Count}\">");
        foreach (var fill in _fills)
            sb.Append(fill switch
            {
                null => "<fill><patternFill patternType=\"none\"/></fill>",
                "gray125" => "<fill><patternFill patternType=\"gray125\"/></fill>",
                _ => $"<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF{fill}\"/><bgColor indexed=\"64\"/></patternFill></fill>",
            });
        sb.Append($"</fills><borders count=\"{_borders.Count}\">");
        foreach (var b in _borders)
        {
            if (b is null) { sb.Append("<border><left/><right/><top/><bottom/><diagonal/></border>"); continue; }
            string Side(string tag, string? style, string color) =>
                style is null ? $"<{tag}/>" : $"<{tag} style=\"{style}\"><color rgb=\"FF{color}\"/></{tag}>";
            sb.Append("<border>").Append(Side("left", b.Left, b.LeftColor)).Append(Side("right", b.Right, b.RightColor))
              .Append(Side("top", b.Top, b.TopColor)).Append(Side("bottom", b.Bottom, b.BottomColor)).Append("<diagonal/></border>");
        }
        sb.Append("</borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>");
        sb.Append($"<cellXfs count=\"{_xfs.Count}\">");
        foreach (var x in _xfs)
        {
            int font = _fonts.IndexOf(x.Font);
            int fill = x.Fill is null ? 0 : _fills.IndexOf(x.Fill);
            int border = x.Border is null ? 0 : _borders.IndexOf(x.Border);
            int numFmt = x.NumFmt is null ? 0 : 164 + _numFmts.IndexOf(x.NumFmt);
            sb.Append($"<xf numFmtId=\"{numFmt}\" fontId=\"{font}\" fillId=\"{fill}\" borderId=\"{border}\" xfId=\"0\"");
            if (numFmt > 0) sb.Append(" applyNumberFormat=\"1\"");
            if (font > 0) sb.Append(" applyFont=\"1\"");
            if (fill > 0) sb.Append(" applyFill=\"1\"");
            if (border > 0) sb.Append(" applyBorder=\"1\"");
            if (x.HAlign is null && x.VAlign is null && !x.Wrap) { sb.Append("/>"); continue; }
            sb.Append(" applyAlignment=\"1\"><alignment");
            if (x.HAlign is not null) sb.Append($" horizontal=\"{x.HAlign}\"");
            if (x.VAlign is not null) sb.Append($" vertical=\"{x.VAlign}\"");
            if (x.Wrap) sb.Append(" wrapText=\"1\"");
            sb.Append("/></xf>");
        }
        sb.Append("</cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");
        return sb.ToString();
    }
}
