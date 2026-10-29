using Accountix.Finance.Services;
using ClosedXML.Excel;

namespace Accountix.Finance.Controllers;

/// <summary>The statements' Excel / PDF files, laid out as the Python routes lay them out.</summary>
public sealed partial class ReportsController
{
    private static ReportColumn Money(string h) => new(h, ReportColumnKind.Money);
    private static ReportColumn Text(string h) => new(h);
    private string Nf => HttpContext.Access()?.NumberFormat ?? "en";
    private string Amt(double v) => TwcfReports.Display(v, Nf);

    // ── trial balance ───────────────────────────────────────────────────

    private ExportFile TrialBalanceExport(string fmt, ReportPeriod p, TrialBalanceResult r)
    {
        // Headers carry every comparative period in the query string; the figures only those in force (Python).
        var headers = new List<ReportColumn>
        {
            Text("Code"), Text("Account"), Text("Type"), Money("Dr Opening"), Money("Cr Opening"),
            Money("Dr Movement"), Money("Cr Movement"), Money("Dr Closing"), Money("Cr Closing"),
        };
        foreach (var cp in p.CompPeriods)
        {
            headers.Add(Money($"Dr Clsg ({ReportPeriod.ColLabel(cp.EndDate)})"));
            headers.Add(Money($"Cr Clsg ({ReportPeriod.ColLabel(cp.EndDate)})"));
        }
        int nComp = p.ActiveComp.Count;
        var rows = new List<ReportRow>();
        foreach (var row in r.Rows)
        {
            var cells = new List<object?> { row.Code, row.Name, row.Type, row.DrOpening, row.CrOpening, row.DrMovement, row.CrMovement, row.DrClosing, row.CrClosing };
            if (row.CompDrClosing is { Count: > 0 })
                for (int i = 0; i < row.CompDrClosing.Count; i++) { cells.Add(row.CompDrClosing[i]); cells.Add(row.CompCrClosing![i]); }
            else
                for (int i = 0; i < nComp; i++) { cells.Add(fmt == "excel" ? 0m : ""); cells.Add(fmt == "excel" ? 0m : ""); }
            rows.Add(new ReportRow(cells, fmt == "pdf" ? "account" : null));
        }
        var totals = new List<object?> { "", "TOTAL", "", r.TotalDrOpening, r.TotalCrOpening, r.TotalDrMovement, r.TotalCrMovement, r.TotalDrClosing, r.TotalCrClosing };
        foreach (var (dr, cr) in r.CompTotals) { totals.Add(dr); totals.Add(cr); }
        rows.Add(new ReportRow(totals, fmt == "pdf" ? "grand" : null));
        var table = new ReportTable
        {
            Title = "Trial Balance", SheetTitle = "Trial Balance", Columns = headers, Rows = rows,
            Period = ReportPeriod.PeriodLine(p.From, p.To, r.AsOf), MonoColumn = fmt == "pdf" ? 0 : null,
        };
        return export.Send(fmt, table, "Trial Balance", ReportPeriod.FilePeriod(p.From, p.To));
    }

    // ── profit & loss ───────────────────────────────────────────────────

    private ExportFile ProfitLossExcel(ReportPeriod p, List<PlRow> rows)
    {
        var comp = p.CompPeriods;
        int ncols = 3 + comp.Count;
        using var wb = export.NewWorkbook("Profit & Loss Statement");
        var ws = ReportExport.AddSheet(wb, "P&L");
        int hdr = export.WriteHeading(ws, ncols, "Profit & Loss Statement", ReportPeriod.PeriodLine(p.From, p.To), []);
        var heads = new List<string> { "Code", "Account", ReportPeriod.ColLabel(p.To) };
        heads.AddRange(comp.Select(cp => ReportPeriod.ColLabel(cp.EndDate)));
        ReportExport.StyleHeader(ws, hdr, heads);
        ws.Row(hdr).Height = ws.RowHeight;   // Python styled these cells by hand: no 30pt row
        int r = hdr + 1;
        object? CompAt(PlRow row, int i) => row.CompAmounts is { Count: > 0 } ca ? (i < ca.Count ? ca[i] : null) : 0m;
        foreach (var row in rows)
        {
            switch (row.Kind)
            {
                case "header":
                    ws.Range(r, 1, r, ncols).Merge();
                    ws.Cell(r, 1).Value = row.Label;
                    ReportExport.StyleRow(ws, r, ncols, "section");
                    break;
                case "account":
                    ReportExport.Put(ws, r, 1, row.Code, border: false);
                    ReportExport.Put(ws, r, 2, row.Name, border: false);
                    ReportExport.Put(ws, r, 3, row.Amount, border: false);
                    for (int i = 1; i <= comp.Count; i++) ReportExport.Put(ws, r, 3 + i, CompAt(row, i), border: false);
                    break;
                case "total":
                    ReportExport.Put(ws, r, 2, row.Label, bold: true, border: false);
                    ReportExport.Put(ws, r, 3, row.Amount, bold: true, border: false);
                    for (int i = 1; i <= comp.Count; i++) ReportExport.Put(ws, r, 3 + i, CompAt(row, i), bold: true, border: false);
                    ReportExport.StyleRow(ws, r, ncols, "total");
                    break;
                default:
                    ReportExport.Put(ws, r, 2, row.Label, bold: true, border: false);
                    ReportExport.Put(ws, r, 3, row.Amount, bold: true, border: false);
                    for (int i = 1; i <= comp.Count; i++) ReportExport.Put(ws, r, 3 + i, CompAt(row, i), bold: true, border: false);
                    ReportExport.StyleRow(ws, r, ncols, "subtotal", negative: row.Amount < 0);
                    r++;
                    break;
            }
            r++;
        }
        export.FinishSheet(ws, ncols, hdr + 1, "Profit & Loss");
        return export.File("excel", ReportExport.Save(wb), "Profit and Loss", ReportPeriod.FilePeriod(p.From, p.To));
    }

    private ExportFile ProfitLossPdf(ReportPeriod p, List<PlRow> rows)
    {
        var comp = p.CompPeriods;
        var headers = new List<ReportColumn> { Text("Code"), Text("Account / Section"), Money(ReportPeriod.ColLabel(p.To)) };
        headers.AddRange(comp.Select(cp => Money(ReportPeriod.ColLabel(cp.EndDate))));
        List<object?> Amounts(PlRow row) => row.CompAmounts is { Count: > 0 } ca
            ? ca.Select(a => (object?)a).ToList()
            : [row.Amount, .. Enumerable.Repeat<object?>("", comp.Count)];
        var lastSubtotal = rows.LastOrDefault(r => r.Kind == "subtotal");
        var data = new List<ReportRow>();
        foreach (var row in rows)
        {
            switch (row.Kind)
            {
                case "header":
                    data.Add(new ReportRow([row.Label.ToUpperInvariant(), .. Enumerable.Repeat<object?>("", 2 + comp.Count)], "section"));
                    break;
                case "account":
                    data.Add(new ReportRow([row.Code, row.Name, .. Amounts(row)], "account"));
                    break;
                case "total":
                    data.Add(new ReportRow(["", row.Label, .. Amounts(row)], "total"));
                    break;
                default:
                    data.Add(new ReportRow(["", row.Label, .. Amounts(row)], ReferenceEquals(row, lastSubtotal) ? "grand" : "subtotal"));
                    data.Add(new ReportRow(Enumerable.Repeat<object?>("", 3 + comp.Count).ToList(), "spacer"));
                    break;
            }
        }
        var table = new ReportTable
        {
            Title = "Profit & Loss Statement", Columns = headers, Rows = data, Period = ReportPeriod.PeriodLine(p.From, p.To),
            IndentColumn = 1, MonoColumn = 0,
        };
        return export.File("pdf", export.BuildPdf(table), "Profit and Loss", ReportPeriod.FilePeriod(p.From, p.To));
    }

    // ── P&L by label ────────────────────────────────────────────────────

    private async Task<ExportFile> LabelPlExportAsync(string fmt, DateOnly from, DateOnly to, List<PlRow> rows, decimal net,
                                                      List<ProjectLabel> labels, List<int> picked, List<decimal> nets,
                                                      decimal? unlabelledNet, CancellationToken ct)
    {
        bool all = picked.Count == 0;
        var headers = new List<ReportColumn> { Text("Code"), Text("Account") };
        headers.AddRange(labels.Select(l => Money(l.Name)));
        if (all) headers.Add(Money("Unlabelled"));
        headers.Add(Money("Total"));
        var data = new List<ReportRow>();
        foreach (var row in rows)
        {
            if (row.Kind == "header")
            {
                data.Add(new ReportRow([row.Label, .. Enumerable.Repeat<object?>("", headers.Count - 1)], "section"));
                continue;
            }
            if (row.Kind is not ("account" or "total" or "subtotal")) continue;
            List<object?> cells = row.Kind == "account" ? [row.Code, row.Name] : ["", row.Label];
            cells.AddRange((row.ByLabel ?? []).Select(v => (object?)v));
            if (all) cells.Add(row.Unlabelled);
            cells.Add(row.Amount);
            data.Add(new ReportRow(cells, row.Kind));
        }
        List<object?> grand = ["", "Net profit / (loss)", .. nets.Select(n => (object?)n)];
        if (all) grand.Add(unlabelledNet);
        grand.Add(net);
        data.Add(new ReportRow(grand, "grand"));
        string filter = "Labels: " + (!all ? string.Join(", ", labels.Select(l => l.Name)) : "all, plus Unlabelled");
        return await export.ExportTableAsync(fmt, new ReportTable
        {
            Title = "Profit & Loss by Project Label", Columns = headers, Rows = data, Period = ReportPeriod.PeriodLine(from, to),
            Filters = [("", filter)], MonoColumn = 0, FilePeriod = ReportPeriod.FilePeriod(from, to),
        }, ct);
    }

    // ── balance sheet ───────────────────────────────────────────────────

    private static List<(string Code, string Name, List<decimal> Amounts)> BsRows(List<BsItem> items, List<BsMerged> merged) =>
        merged.Count > 0
            ? merged.Select(m => (m.Code, m.Name, m.Amounts)).ToList()
            : items.Select(i => (i.Code, i.Name, new List<decimal> { i.Amount })).ToList();

    private static List<decimal> BsTotals(decimal baseTotal, IEnumerable<decimal> comp) => [baseTotal, .. comp];

    private ExportFile BalanceSheetExcel(ReportPeriod p, BalanceSheetResult r)
    {
        var comp = p.CompPeriods;
        int ncols = 3 + comp.Count;
        using var wb = export.NewWorkbook("Balance Sheet");
        var ws = ReportExport.AddSheet(wb, "Balance Sheet");
        int firstRow = export.WriteHeading(ws, ncols, "Balance Sheet", ReportPeriod.PeriodLine(asOf: r.AsOf), []);
        var hLabels = new List<string> { "Code", "Account", ReportPeriod.ColLabel(r.AsOf) };
        hLabels.AddRange(comp.Select(cp => ReportPeriod.ColLabel(cp.EndDate)));

        int Section(int sr, string title, List<(string Code, string Name, List<decimal> Amounts)> rows, string totalLabel, List<decimal> totals)
        {
            ws.Range(sr, 1, sr, ncols).Merge();
            ws.Cell(sr, 1).Value = title;
            ReportExport.StyleRow(ws, sr, ncols, "section");
            int hdr = sr + 1;
            ReportExport.StyleHeader(ws, hdr, hLabels.Take(ncols).ToList());
            ws.Range(hdr, 1, hdr, ncols).Style.Alignment.WrapText = false;
            ws.Row(hdr).Height = ws.RowHeight;
            int ri = hdr + 1;
            foreach (var (code, name, amounts) in rows)
            {
                ReportExport.Put(ws, ri, 1, code);
                ReportExport.Put(ws, ri, 2, name);
                for (int i = 0; i < Math.Min(amounts.Count, ncols - 2); i++) ReportExport.Put(ws, ri, 3 + i, amounts[i]);
                ri++;
            }
            int tr = hdr + rows.Count + 1;
            ReportExport.Put(ws, tr, 2, totalLabel, bold: true);
            for (int i = 0; i < Math.Min(totals.Count, ncols - 2); i++) ReportExport.Put(ws, tr, 3 + i, totals[i], bold: true);
            ReportExport.StyleRow(ws, tr, ncols, "total");
            return tr + 2;
        }

        var b = r.Base;
        int nr = Section(firstRow, "ASSETS", BsRows(b.Assets, r.MergedAssets), "Total Assets", BsTotals(b.TotalAssets, r.Comp.Select(c => c.TotalAssets)));
        nr = Section(nr, "LIABILITIES", BsRows(b.Liabilities, r.MergedLiabilities), "Total Liabilities",
                     BsTotals(b.TotalLiabilities, r.Comp.Select(c => c.TotalLiabilities)));
        nr = Section(nr, "EQUITY", BsRows(b.Equity, r.MergedEquity), "Total Equity", BsTotals(b.TotalEquity, r.Comp.Select(c => c.TotalEquity)));
        ws.Range(nr, 1, nr, 2).Merge();
        ws.Cell(nr, 1).Value = "LIABILITIES + EQUITY";
        var lte = BsTotals(b.TotalLiabilities + b.TotalEquity, r.Comp.Select(c => c.TotalLiabilities + c.TotalEquity));
        for (int i = 0; i < Math.Min(lte.Count, ncols - 2); i++) ws.Cell(nr, 3 + i).Value = (double)lte[i];
        ReportExport.StyleRow(ws, nr, ncols, "grand");
        export.FinishSheet(ws, ncols, firstRow, "Balance Sheet");
        return export.File("excel", ReportExport.Save(wb), "Balance Sheet", ReportPeriod.FilePeriod(asOf: r.AsOf));
    }

    private ExportFile BalanceSheetPdf(ReportPeriod p, BalanceSheetResult r)
    {
        var comp = p.CompPeriods;
        int ncols = 3 + comp.Count;
        var headers = new List<ReportColumn> { Text("Code"), Text("Account"), Money(ReportPeriod.ColLabel(r.AsOf)) };
        headers.AddRange(comp.Select(cp => Money(ReportPeriod.ColLabel(cp.EndDate))));
        var data = new List<ReportRow>();
        void Section(string title, List<(string Code, string Name, List<decimal> Amounts)> rows, string totalLabel, List<decimal> totals)
        {
            data.Add(new ReportRow([title.ToUpperInvariant(), .. Enumerable.Repeat<object?>("", ncols - 1)], "section"));
            foreach (var (code, name, amounts) in rows)
                data.Add(new ReportRow([code, name, .. amounts.Take(ncols - 2).Select(a => (object?)a)], "account"));
            data.Add(new ReportRow(["", totalLabel, .. totals.Take(ncols - 2).Select(a => (object?)a)], "total"));
            data.Add(new ReportRow(Enumerable.Repeat<object?>("", ncols).ToList(), "spacer"));
        }
        var b = r.Base;
        Section("Assets", BsRows(b.Assets, r.MergedAssets), "Total Assets", BsTotals(b.TotalAssets, r.Comp.Select(c => c.TotalAssets)));
        Section("Liabilities", BsRows(b.Liabilities, r.MergedLiabilities), "Total Liabilities",
                BsTotals(b.TotalLiabilities, r.Comp.Select(c => c.TotalLiabilities)));
        Section("Equity", BsRows(b.Equity, r.MergedEquity), "Total Equity", BsTotals(b.TotalEquity, r.Comp.Select(c => c.TotalEquity)));
        var lte = BsTotals(b.TotalLiabilities + b.TotalEquity, r.Comp.Select(c => c.TotalLiabilities + c.TotalEquity));
        data.Add(new ReportRow(["", "LIABILITIES + EQUITY", .. lte.Take(ncols - 2).Select(a => (object?)a)], "grand"));
        var table = new ReportTable
        {
            Title = "Balance Sheet", Columns = headers, Rows = data, Period = ReportPeriod.PeriodLine(asOf: r.AsOf),
            IndentColumn = 1, MonoColumn = 0,
        };
        return export.File("pdf", export.BuildPdf(table), "Balance Sheet", ReportPeriod.FilePeriod(asOf: r.AsOf));
    }

    // ── SOCIE ───────────────────────────────────────────────────────────

    private ExportFile SocieExport(string fmt, DateOnly from, DateOnly to, List<(DateOnly Start, DateOnly End)> specs,
                                   List<SocieColumn> columns, List<SocieRow> rows)
    {
        var headers = new List<ReportColumn> { Text("") };
        headers.AddRange(columns.Select(c => Money(c.Label)));
        var data = new List<ReportRow>();
        var bold = new HashSet<int>();
        foreach (var r in rows)
        {
            if (r.Kind is "opening" or "closing" or "grand" or "subtotal" or "section") bold.Add(data.Count);
            var line = new List<object?> { string.Concat(Enumerable.Repeat("    ", r.Indent)) + r.Label };
            foreach (var c in columns)
                line.Add(r.Values.TryGetValue(c.Key, out var v) ? v : null);
            data.Add(new ReportRow(line, fmt == "pdf" ? (bold.Contains(data.Count) ? "total" : "account") : null));
        }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string span = $"For the period {specs[0].Start.ToString("dd MMMM yyyy", inv)} to {specs[^1].End.ToString("dd MMMM yyyy", inv)}";
        var table = new ReportTable
        {
            Title = "Statement of Changes in Equity", SheetTitle = "SOCIE", Columns = headers, Rows = data, Period = span,
            BoldRows = fmt == "excel" ? bold : new HashSet<int>(), IndentColumn = fmt == "pdf" ? 0 : null,
        };
        return export.Send(fmt, table, "Statement of Changes in Equity", ReportPeriod.FilePeriod(from, to));
    }

    // ── cash flow ───────────────────────────────────────────────────────

    private ExportFile CashFlowExcel(ReportPeriod p, DateOnly from, DateOnly to, CashFlowResult r)
    {
        int nComp = r.CompMaps.Count;
        int cols = 2 + nComp;
        string title = $"Cash Flow Statement ({ViewHelpers.Title(r.Method)} Method)";
        using var wb = export.NewWorkbook(title);
        var ws = ReportExport.AddSheet(wb, "Cash Flow");
        int first = export.WriteHeading(ws, cols, title, ReportPeriod.PeriodLine(from, to), []);
        ReportExport.StyleHeader(ws, first, ["Item", ReportPeriod.ColLabel(to), .. p.CompPeriods.Take(nComp).Select(cp => ReportPeriod.ColLabel(cp.EndDate))]);
        first++;

        int Section(int sr, string sectionTitle, List<CashItem> items, string totalLabel, double total, List<double> compTotals)
        {
            ws.Range(sr, 1, sr, cols).Merge();
            ws.Cell(sr, 1).Value = sectionTitle;
            ReportExport.StyleRow(ws, sr, cols, "section");
            int row = sr + 1;
            foreach (var item in items)
            {
                ReportExport.Put(ws, row, 1, item.Name, border: false);
                ReportExport.Put(ws, row, 2, item.Value, border: false);
                var comps = item.Comps ?? Enumerable.Repeat<double?>(null, nComp).ToList();
                for (int ci = 0; ci < comps.Count; ci++) ReportExport.Put(ws, row, 3 + ci, comps[ci] ?? 0.0, border: false);
                row++;
            }
            ReportExport.Put(ws, row, 1, totalLabel, bold: true, border: false);
            ReportExport.Put(ws, row, 2, total, bold: true, border: false);
            for (int ci = 0; ci < compTotals.Count; ci++) ReportExport.Put(ws, row, 3 + ci, compTotals[ci], bold: true, border: false);
            ReportExport.StyleRow(ws, row, cols, "total");
            return row + 2;
        }

        List<double> Comp(string key) => r.CompMaps.Select(m => m[key]).ToList();
        int nr = Section(first, "OPERATING ACTIVITIES", r.Operating, "Net cash from operating activities", r.NetOperating, Comp("__net_op__"));
        nr = Section(nr, "INVESTING ACTIVITIES", r.Investing, "Net cash from investing activities", r.NetInvesting, Comp("__net_inv__"));
        nr = Section(nr, "FINANCING ACTIVITIES", r.Financing, "Net cash from financing activities", r.NetFinancing, Comp("__net_fin__"));
        foreach (var (label, val, comps) in new[]
                 {
                     ("NET CHANGE IN CASH", r.NetChange, Comp("__net_chg__")),
                     ("Opening cash & equivalents", r.OpeningCash, Comp("__open__")),
                     ("Closing cash & equivalents", r.ClosingCash, Comp("__close__")),
                 })
        {
            ReportExport.Put(ws, nr, 1, label, bold: true, border: false);
            ReportExport.Put(ws, nr, 2, val, bold: true, border: false);
            for (int ci = 0; ci < comps.Count; ci++) ReportExport.Put(ws, nr, 3 + ci, comps[ci], bold: true, border: false);
            ReportExport.StyleRow(ws, nr, cols, label.StartsWith("Closing", StringComparison.Ordinal) ? "grand" : "total");
            nr++;
        }
        export.FinishSheet(ws, cols, first, "Cash Flow Statement");
        return export.File("excel", ReportExport.Save(wb), "Cash Flow Statement", ReportPeriod.FilePeriod(from, to));
    }

    private ExportFile CashFlowPdf(ReportPeriod p, DateOnly from, DateOnly to, CashFlowResult r)
    {
        int nComp = r.CompMaps.Count;
        var headers = new List<ReportColumn> { Text("Item"), Money(ReportPeriod.ColLabel(to)) };
        headers.AddRange(p.CompPeriods.Select(cp => Money(ReportPeriod.ColLabel(cp.EndDate))));
        string C(double? v) => v is double d ? Amt(d) : "";
        List<object?> CompVals(string key) => r.CompMaps.Count > 0
            ? r.CompMaps.Select(m => (object?)Amt(m.TryGetValue(key, out var v) ? v : 0.0)).ToList()
            : Enumerable.Repeat<object?>("", nComp).ToList();
        var data = new List<ReportRow>();
        void Activity(string title, List<CashItem> items, string totalLabel, double total, string key)
        {
            data.Add(new ReportRow([title, "", .. Enumerable.Repeat<object?>("", nComp)], "section"));
            foreach (var item in items)
                data.Add(new ReportRow([item.Name, Amt(item.Value), .. (item.Comps ?? Enumerable.Repeat<double?>(null, nComp).ToList()).Select(c => (object?)C(c))], "account"));
            data.Add(new ReportRow([totalLabel, Amt(total), .. CompVals(key)], "total"));
            data.Add(new ReportRow(["", "", .. Enumerable.Repeat<object?>("", nComp)], "spacer"));
        }
        Activity("OPERATING ACTIVITIES", r.Operating, "Net cash from operating activities", r.NetOperating, "__net_op__");
        Activity("INVESTING ACTIVITIES", r.Investing, "Net cash from investing activities", r.NetInvesting, "__net_inv__");
        Activity("FINANCING ACTIVITIES", r.Financing, "Net cash from financing activities", r.NetFinancing, "__net_fin__");
        data.Add(new ReportRow(["NET CHANGE IN CASH", Amt(r.NetChange), .. CompVals("__net_chg__")], "grand"));
        data.Add(new ReportRow(["Opening cash & equivalents", Amt(r.OpeningCash), .. CompVals("__open__")], "plain"));
        data.Add(new ReportRow(["Closing cash & equivalents", Amt(r.ClosingCash), .. CompVals("__close__")], "total"));
        var table = new ReportTable
        {
            Title = $"Cash Flow Statement ({ViewHelpers.Title(r.Method)} Method)", Columns = headers, Rows = data,
            Period = ReportPeriod.PeriodLine(from, to), IndentColumn = 0,
        };
        return export.File("pdf", export.BuildPdf(table), "Cash Flow Statement", ReportPeriod.FilePeriod(from, to));
    }

    // ── general ledger ──────────────────────────────────────────────────

    private static readonly string[] LedgerHeaders = ["Date", "Voucher #", "Description", "Debit", "Credit", "Balance"];

    private ExportFile LedgerExcel(ReportPeriod p, List<LedgerSection> sections)
    {
        using var wb = export.NewWorkbook("General Ledger");
        string period = ReportPeriod.PeriodLine(p.From, p.To);
        if (sections.Count == 0)
        {
            var ws0 = ReportExport.AddSheet(wb, "General Ledger");
            export.WriteHeading(ws0, 6, "General Ledger", period, []);
            ws0.Cell(5, 1).Value = "No accounts selected.";
            ws0.Cell(5, 1).Style.Font.FontSize = 10;
        }
        var sheetNames = new List<string>();
        foreach (var sec in sections)
        {
            string name = sec.Account.Code.Length > 31 ? sec.Account.Code[..31] : sec.Account.Code;
            var ws = ReportExport.AddSheet(wb, name);
            sheetNames.Add(ws.Name);
            int hdr = export.WriteHeading(ws, 6, $"{sec.Account.Code} - {sec.Account.Name}", period, []);
            ReportExport.StyleHeader(ws, hdr, LedgerHeaders);
            ws.Row(hdr).Height = ws.RowHeight;
            int ri = hdr + 1;
            void Line(object?[] cells, bool bold)
            {
                for (int ci = 0; ci < cells.Length; ci++)
                    ReportExport.Put(ws, ri, ci + 1, cells[ci] is string { Length: 0 } ? null : cells[ci], bold);
                ri++;
            }
            Line(["", "", $"Opening Balance ({sec.OpeningSide})", "", "", sec.Opening], false);
            foreach (var l in sec.Rows)
                Line([l.Date, l.Voucher, l.Description, l.Debit != 0 ? l.Debit : 0m, l.Credit != 0 ? l.Credit : 0m, l.Balance], false);
            Line(["", "", "Period Movement", sec.TotalDebit, sec.TotalCredit, ""], true);
            ReportExport.StyleRow(ws, ri - 1, 6, "total");
            Line(["", "", $"Closing Balance ({sec.ClosingSide})", "", "", sec.Closing], true);
            ReportExport.StyleRow(ws, ri - 1, 6, "total");
            export.FinishSheet(ws, 6, hdr + 1, $"{sec.Account.Code} {sec.Account.Name}");
        }
        if (sections.Count > 1)
        {
            var ws = ReportExport.AddSheet(wb, "Summary", 1);
            int hdr = export.WriteHeading(ws, 8, "General Ledger — Summary", period, []);
            ReportExport.StyleHeader(ws, hdr, ["Code", "Account", "Opening", "Dr/Cr", "Debit", "Credit", "Closing", "Dr/Cr"]);
            int i = hdr + 1;
            for (int k = 0; k < sections.Count; k++, i++)
            {
                var sec = sections[k];
                object?[] row = [sec.Account.Code, sec.Account.Name, sec.Opening, sec.OpeningSide, sec.TotalDebit, sec.TotalCredit, sec.Closing, sec.ClosingSide];
                for (int ci = 0; ci < row.Length; ci++) ReportExport.Put(ws, i, ci + 1, row[ci]);
                var link = ws.Cell(i, 1);
                link.SetHyperlink(new XLHyperlink($"'{sheetNames[k]}'!A1"));
                link.Style.Font.FontColor = XLColor.FromHtml("#1D4ED8");
                link.Style.Font.Underline = XLFontUnderlineValues.Single;
            }
            ws.Cell(i, 2).Value = "Total";
            ws.Cell(i, 5).Value = (double)sections.Sum(s => s.TotalDebit);
            ws.Cell(i, 6).Value = (double)sections.Sum(s => s.TotalCredit);
            ReportExport.StyleRow(ws, i, 8, "grand");
            export.FinishSheet(ws, 8, hdr + 1, "General Ledger Summary");
            ws.SetTabActive();
            foreach (var other in wb.Worksheets.Where(w => w != ws)) other.TabSelected = false;
            ws.TabSelected = true;
        }
        return export.File("excel", ReportExport.Save(wb), "General Ledger", ReportPeriod.FilePeriod(p.From, p.To));
    }

    private ExportFile LedgerPdf(ReportPeriod p, List<LedgerSection> sections)
    {
        var data = new List<ReportRow>(sections.Sum(s => s.Rows.Count + 5));
        foreach (var sec in sections)
        {
            data.Add(new ReportRow([$"{sec.Account.Code}  {sec.Account.Name}", "", "", "", "", ""], "section"));
            data.Add(new ReportRow(["", "", $"Opening Balance ({sec.OpeningSide})", "", "", sec.Opening], "total"));
            foreach (var l in sec.Rows)
                data.Add(new ReportRow([l.Date, l.Voucher, l.Description, l.Debit, l.Credit, l.Balance], "account"));
            data.Add(new ReportRow(["", "", "Period Movement", sec.TotalDebit, sec.TotalCredit, ""], "total"));
            data.Add(new ReportRow(["", "", $"Closing Balance ({sec.ClosingSide})", "", "", sec.Closing], "grand"));
            data.Add(new ReportRow(["", "", "", "", "", ""], "spacer"));
        }
        var table = new ReportTable
        {
            Title = "General Ledger", Period = ReportPeriod.PeriodLine(p.From, p.To), Rows = data,
            Columns = [Text("Date"), Text("Voucher #"), Text("Description"), Money("Debit"), Money("Credit"), Money("Balance")],
        };
        return export.File("pdf", export.BuildPdf(table), "General Ledger", ReportPeriod.FilePeriod(p.From, p.To));
    }
}
