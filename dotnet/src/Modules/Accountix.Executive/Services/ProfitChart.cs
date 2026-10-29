namespace Accountix.Executive.Services;

/// <summary>
/// profit_chart(): ready-to-print SVG geometry for the dashboard's 12-month
/// revenue / net profit chart, computed in doubles exactly as Python does
/// (shared/invoicing_performance.py layout constants and _nice_step).
/// </summary>
public sealed class ProfitChart
{
    public const double ViewW = 720.0, ViewH = 248.0;
    public const double PadL = 58.0, PadR = 14.0, PadT = 14.0, PadB = 26.0;
    public const double PlotW = ViewW - PadL - PadR;
    public const double PlotH = ViewH - PadT - PadB;

    public sealed record Tick(double V, double Y, int Dp);
    public sealed record Point(double X, double Y);
    public sealed record Series(string Key, string Label, string Var, string Dash, string Points, string Area, Point Last);
    public sealed record Col(double X, string Label, string FullLabel, int I);

    public double PlotR => PadL + PlotW;
    public double PlotB => PadT + PlotH;
    public double ZeroY { get; init; }
    public List<Tick> Ticks { get; init; } = [];
    public List<Series> SeriesList { get; init; } = [];
    public List<Col> Cols { get; init; } = [];
    public double Band { get; init; }

    private static readonly (string Key, string Label, string Var, string Dash)[] Specs =
    [
        ("revenue", "Revenue", "rev", "5 4"),
        ("net", "Net profit", "net", ""),
    ];

    /// <summary>_nice_step: a round gridline step covering span in roughly four steps.</summary>
    public static double NiceStep(double span, int targetTicks = 4)
    {
        if (span <= 0) return 1.0;
        double raw = span / Math.Max(targetTicks, 1);
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        foreach (double mult in new[] { 1, 2, 2.5, 5, 10 })
            if (raw <= mult * mag)
                return mult * mag;
        return 10 * mag;
    }

    /// <summary>null when there is nothing to draw (no month carries revenue or profit).</summary>
    public static ProfitChart? From(Profitability data)
    {
        var months = data.Months;
        if (months.Count == 0) return null;
        var rev = months.Select(m => Py.ToDouble(m.Revenue)).ToList();
        var net = months.Select(m => Py.ToDouble(m.Net)).ToList();
        if (!Enumerable.Range(0, months.Count).Any(i => rev[i] != 0 || net[i] != 0)) return null;

        double hi = rev.Concat(net).Append(0.0).Max();
        double lo = net.Append(0.0).Min();
        if (hi == lo) hi = lo + 1.0;
        double step = NiceStep(hi - lo);
        double top = Math.Ceiling(hi / step) * step;
        double bottom = Math.Floor(lo / step) * step;
        if (top == bottom) top = bottom + step;
        double span = months.Count - 1 is var s && s != 0 ? s : 1.0;

        double YOf(double value) => PadT + PlotH * (top - value) / (top - bottom);
        double XOf(int i) => PadL + (months.Count > 1 ? PlotW * i / span : PlotW / 2);

        int dp = step >= 1 ? 0 : Math.Max(0, -(int)Math.Floor(Math.Log10(step)));
        var ticks = new List<Tick>();
        double tick = bottom;
        while (tick <= top + step / 1000.0)
        {
            ticks.Add(new Tick(tick, Py.Round(YOf(tick), 2), dp));
            tick += step;
        }
        double zeroY = Py.Round(YOf(0), 2);
        var series = new List<Series>();
        foreach (var (key, label, var, dash) in Specs)
        {
            var values = key == "revenue" ? rev : net;
            var pts = values.Select((v, i) => new Point(Py.Round(XOf(i), 2), Py.Round(YOf(v), 2))).ToList();
            string points = string.Join(" ", pts.Select(p => $"{Py.Repr(p.X)},{Py.Repr(p.Y)}"));
            // The wash closes onto the zero line, so a loss month never reads as positive.
            string area = $"{Py.Repr(pts[0].X)},{Py.Repr(zeroY)} " + points + $" {Py.Repr(pts[^1].X)},{Py.Repr(zeroY)}";
            series.Add(new Series(key, label, var, dash, points, area, pts[^1]));
        }
        return new ProfitChart
        {
            ZeroY = zeroY, Ticks = ticks, SeriesList = series,
            Cols = months.Select((m, i) => new Col(Py.Round(XOf(i), 2), m.Label, m.FullLabel, i)).ToList(),
            Band = Py.Round(PlotW / span, 2),
        };
    }
}
