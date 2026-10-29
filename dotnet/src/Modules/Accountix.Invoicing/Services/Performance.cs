namespace Accountix.Invoicing.Services;

public sealed class MonthSlot
{
    public int M { get; init; }
    public string Name { get; init; } = "";
    public double Revenue { get; set; }
    public double Purchases { get; set; }
    public double Gross { get; set; }

    public double Of(string key) => key switch { "revenue" => Revenue, "purchases" => Purchases, _ => Gross };
}

public sealed class PerfTotals
{
    public double Revenue { get; set; }
    public double Purchases { get; set; }
    public double Gross { get; set; }
    public double? Margin { get; set; }

    public double Of(string key) => key switch { "revenue" => Revenue, "purchases" => Purchases, _ => Gross };
}

public sealed class Performance
{
    public int Year { get; init; }
    public required IReadOnlyList<MonthSlot> Months { get; init; }
    public required PerfTotals Totals { get; init; }
    public required PerfTotals Prev { get; init; }
    public int PrevYear => Year - 1;
    public required IReadOnlyDictionary<string, double?> Deltas { get; init; }
    public double Peak { get; init; }
    public double Trough { get; init; }
    public int? LabelId { get; init; }
}

public sealed record ChartTick(double V, double Y);
public sealed record ChartDot(double X, double Y, double V, string Month);
public sealed record ChartSeries(string Key, string Label, int Slot, string Var, string Points, string Area,
                                 IReadOnlyList<ChartDot> Dots, (double X, double Y) Last);
public sealed record ChartCol(double X, string Name, int I);

public sealed class ChartGeometry
{
    public double ViewW { get; init; }
    public double ViewH { get; init; }
    public double PadL { get; init; }
    public double PadT { get; init; }
    public double PadB { get; init; }
    public double PlotW { get; init; }
    public double PlotH { get; init; }
    public double PlotR { get; init; }
    public double PlotB { get; init; }
    public double ZeroY { get; init; }
    public required IReadOnlyList<ChartTick> Ticks { get; init; }
    public required IReadOnlyList<ChartSeries> Series { get; init; }
    public required IReadOnlyList<ChartCol> Cols { get; init; }
    public double Band { get; init; }
}

/// <summary>
/// Revenue / purchases / gross profit for the invoicing dashboard
/// (shared/invoicing_performance.py): approved documents only, net of tax and
/// of returns. Aggregated in SQL by month (Python adds the rows in Python).
/// </summary>
public sealed class PerformanceService(AccountixDbContext db, CompanyClock clock)
{
    public static readonly string[] MonthNames = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    /// <summary>_net: additions less deductions, NULLs as zero, in floats.</summary>
    private static double Net(decimal? add1, decimal? add2, decimal? sub)
    {
        double total = 0.0;
        total += Py.F(add1);
        total += Py.F(add2);
        total -= Py.F(sub);
        return total;
    }

    /// <summary>available_years: years carrying an approved document, newest first.</summary>
    public async Task<List<int>> AvailableYearsAsync(CancellationToken ct)
    {
        var years = new HashSet<int>();
        years.UnionWith(await db.Set<InvInvoice>().Where(x => x.VoucherStatus == "approved" && x.InvoiceDate != null)
            .Select(x => x.InvoiceDate!.Value.Year).Distinct().ToListAsync(ct));
        years.UnionWith(await db.Set<InvSalesReturn>().Where(x => x.Status == "approved" && x.ReturnDate != null)
            .Select(x => x.ReturnDate!.Value.Year).Distinct().ToListAsync(ct));
        years.UnionWith(await db.Set<InvPurchaseInvoice>().Where(x => x.Status == "approved" && x.InvoiceDate != null)
            .Select(x => x.InvoiceDate!.Value.Year).Distinct().ToListAsync(ct));
        years.UnionWith(await db.Set<InvPurchaseReturn>().Where(x => x.Status == "approved" && x.ReturnDate != null)
            .Select(x => x.ReturnDate!.Value.Year).Distinct().ToListAsync(ct));
        return years.OrderDescending().ToList();
    }

    private async Task<(List<MonthSlot> Months, PerfTotals Totals)> TotalsForAsync(int year, int? labelId, CancellationToken ct)
    {
        var months = Enumerable.Range(1, 12).Select(m => new MonthSlot { M = m, Name = MonthNames[m - 1] }).ToList();
        var start = new DateTime(year, 1, 1);
        var end = new DateTime(year + 1, 1, 1);

        // Python reads the rows (no ORDER BY: the scan's order) and adds them one by one in floats:
        // the same query here keeps the same order, so the sums agree to the last bit.
        var si = db.Set<InvInvoice>().AsNoTracking().Where(x => x.VoucherStatus == "approved" && x.InvoiceDate >= start && x.InvoiceDate < end);
        if (labelId is int l1) si = si.Where(x => x.LabelId == l1);
        foreach (var r in await si.Select(x => new { x.InvoiceDate, x.Subtotal, x.TotalCharges, x.TotalDiscount }).ToListAsync(ct))
            if (r.InvoiceDate is DateTime w) months[w.Month - 1].Revenue += Net(r.Subtotal, r.TotalCharges, r.TotalDiscount);

        var sr = db.Set<InvSalesReturn>().AsNoTracking().Where(x => x.Status == "approved" && x.ReturnDate >= start && x.ReturnDate < end);
        if (labelId is int l2) sr = sr.Where(x => db.Set<InvInvoice>().Where(i => i.LabelId == l2).Select(i => i.Id).Contains(x.OriginalInvoiceId));
        foreach (var r in await sr.Select(x => new { x.ReturnDate, x.GrossReturnValue, x.TotalCharges, x.TotalDiscount }).ToListAsync(ct))
            if (r.ReturnDate is DateTime w) months[w.Month - 1].Revenue += -1 * Net(r.GrossReturnValue, r.TotalCharges, r.TotalDiscount);

        var pi = db.Set<InvPurchaseInvoice>().AsNoTracking().Where(x => x.Status == "approved" && x.InvoiceDate >= start && x.InvoiceDate < end);
        if (labelId is int l3) pi = pi.Where(x => x.LabelId == l3);
        foreach (var r in await pi.Select(x => new { x.InvoiceDate, x.Subtotal, x.TotalExpenses, x.TotalDiscount }).ToListAsync(ct))
            if (r.InvoiceDate is DateTime w) months[w.Month - 1].Purchases += Net(r.Subtotal, r.TotalExpenses, r.TotalDiscount);

        var pr = db.Set<InvPurchaseReturn>().AsNoTracking().Where(x => x.Status == "approved" && x.ReturnDate >= start && x.ReturnDate < end);
        if (labelId is int l4) pr = pr.Where(x => db.Set<InvPurchaseInvoice>().Where(i => i.LabelId == l4).Select(i => i.Id).Contains(x.OriginalInvoiceId));
        foreach (var r in await pr.Select(x => new { x.ReturnDate, x.GrossReturnValue, x.TotalExpenses, x.TotalDiscount }).ToListAsync(ct))
            if (r.ReturnDate is DateTime w) months[w.Month - 1].Purchases += -1 * Net(r.GrossReturnValue, r.TotalExpenses, r.TotalDiscount);

        foreach (var m in months) m.Gross = m.Revenue - m.Purchases;
        var totals = new PerfTotals
        {
            Revenue = Py.Sum(months.Select(m => m.Revenue)),
            Purchases = Py.Sum(months.Select(m => m.Purchases)),
        };
        totals.Gross = totals.Revenue - totals.Purchases;
        totals.Margin = totals.Revenue > 0 ? totals.Gross / totals.Revenue * 100.0 : null;
        return (months, totals);
    }

    /// <summary>performance(year, label_id): the year's series, totals and year-on-year deltas.</summary>
    public async Task<Performance> PerformanceAsync(int? year, int? labelId, CancellationToken ct)
    {
        labelId = labelId is 0 ? null : labelId;
        if (year is null)
        {
            var years = await AvailableYearsAsync(ct);
            year = years.Count > 0 ? years[0] : clock.Now.Year;
        }
        var (months, totals) = await TotalsForAsync(year.Value, labelId, ct);
        var (_, prev) = await TotalsForAsync(year.Value - 1, labelId, ct);
        var deltas = new Dictionary<string, double?>();
        foreach (var key in new[] { "revenue", "purchases", "gross" })
        {
            double was = prev.Of(key), now = totals.Of(key);
            deltas[key] = was != 0 ? (now - was) / Math.Abs(was) * 100.0 : null;
        }
        double peak = months.Count > 0 ? months.Max(s => s.Revenue) : 0.0;
        double trough = months.Count > 0 ? months.Min(s => s.Gross) : 0.0;
        return new Performance
        {
            Year = year.Value, Months = months, Totals = totals, Prev = prev, Deltas = deltas,
            Peak = peak, Trough = Math.Min(trough, 0.0), LabelId = labelId,
        };
    }

    public const double ViewW = 720.0, ViewH = 248.0, PadL = 58.0, PadR = 14.0, PadT = 14.0, PadB = 26.0;
    public const double PlotW = ViewW - PadL - PadR, PlotH = ViewH - PadT - PadB;

    public static readonly (string Key, string Label, int Slot, string Var)[] SeriesSpec =
    [
        ("revenue", "Revenue", 1, "rev"), ("purchases", "Purchases", 2, "pur"), ("gross", "Gross profit", 3, "gp"),
    ];

    private static double NiceStep(double span, int targetTicks = 4)
    {
        if (span <= 0) return 1.0;
        double raw = span / Math.Max(targetTicks, 1);
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        foreach (double mult in new[] { 1, 2, 2.5, 5, 10 })
            if (raw <= mult * mag) return mult * mag;
        return 10 * mag;
    }

    /// <summary>chart(data): ready-to-print SVG geometry.</summary>
    public static ChartGeometry Chart(Performance data)
    {
        var months = data.Months;
        double hi = months.Select(s => s.Revenue).Concat(months.Select(s => s.Purchases)).Concat(months.Select(s => s.Gross)).Append(0.0).Max();
        double lo = months.Select(s => s.Gross).Append(0.0).Min();
        if (hi == lo) hi = lo + 1.0;
        double step = NiceStep(hi - lo);
        double top = Math.Ceiling(hi / step) * step;
        double bottom = Math.Floor(lo / step) * step;
        if (top == bottom) top = bottom + step;
        double YOf(double v) => PadT + PlotH * (top - v) / (top - bottom);
        double XOf(int i) => PadL + (months.Count > 1 ? PlotW * i / 11.0 : PlotW / 2);

        var ticks = new List<ChartTick>();
        double t = bottom;
        while (t <= top + step / 1000.0)
        {
            ticks.Add(new ChartTick(t, Py.Round(YOf(t), 2)));
            t += step;
        }
        var series = new List<ChartSeries>();
        foreach (var spec in SeriesSpec)
        {
            var pts = months.Select((s, i) => (X: Py.Round(XOf(i), 2), Y: Py.Round(YOf(s.Of(spec.Key)), 2))).ToList();
            string points = string.Join(" ", pts.Select(p => $"{Py.Repr(p.X)},{Py.Repr(p.Y)}"));
            string area = $"{Py.Repr(pts[0].X)},{Py.Fixed(YOf(0), 2)} " + points + $" {Py.Repr(pts[^1].X)},{Py.Fixed(YOf(0), 2)}";
            var dots = pts.Select((p, i) => new ChartDot(p.X, p.Y, months[i].Of(spec.Key), months[i].Name)).ToList();
            series.Add(new ChartSeries(spec.Key, spec.Label, spec.Slot, spec.Var, points, area, dots, (pts[^1].X, pts[^1].Y)));
        }
        return new ChartGeometry
        {
            ViewW = ViewW, ViewH = ViewH, PadL = PadL, PadT = PadT, PadB = PadB, PlotW = PlotW, PlotH = PlotH,
            PlotR = PadL + PlotW, PlotB = PadT + PlotH, ZeroY = Py.Round(YOf(0), 2), Ticks = ticks, Series = series,
            Cols = months.Select((s, i) => new ChartCol(Py.Round(XOf(i), 2), s.Name, i)).ToList(),
            Band = Py.Round(PlotW / 11.0, 2),
        };
    }

    /// <summary>chart_json: the hover layer's payload, values formatted by the app.</summary>
    public static Dictionary<string, object?> ChartJson(Performance data, ChartGeometry geo, string? numberFormat) => new()
    {
        ["year"] = data.Year,
        ["view_w"] = geo.ViewW,
        ["cols"] = geo.Cols.Select(c => new Dictionary<string, object?> { ["x"] = c.X, ["name"] = c.Name }).ToList(),
        ["series"] = geo.Series.Select(s => new Dictionary<string, object?>
        {
            ["key"] = s.Key, ["label"] = s.Label, ["var"] = s.Var,
            ["dots"] = s.Dots.Select(d => new Dictionary<string, object?>
            {
                ["x"] = d.X, ["y"] = d.Y, ["text"] = Py.Amount(d.V, 0, numberFormat),
            }).ToList(),
        }).ToList(),
    };
}
