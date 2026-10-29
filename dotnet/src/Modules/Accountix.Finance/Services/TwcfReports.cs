using System.Globalization;
using System.Numerics;

namespace Accountix.Finance.Services;

/// <summary>shared/models/twcf.py: directions, categories and frequencies of forecast lines.</summary>
public static class Twcf
{
    public const string In = "in";
    public const string Out = "out";
    public const string OneOff = "oneoff", Weekly = "weekly", Monthly = "monthly", Quarterly = "quarterly", Yearly = "yearly";

    public static readonly IReadOnlyDictionary<string, string> InCategories = new Dictionary<string, string>
    {
        ["collections"] = "Collections from customers",
        ["other_in"] = "Other receipts",
    };

    public static readonly IReadOnlyDictionary<string, string> OutCategories = new Dictionary<string, string>
    {
        ["suppliers"] = "Payments to suppliers",
        ["payroll"] = "Salaries & wages",
        ["rent"] = "Rent & utilities",
        ["taxes"] = "Taxes & duties",
        ["capex"] = "Capital expenditure",
        ["debt"] = "Loan repayments",
        ["other_out"] = "Other payments",
    };

    /// <summary>Categories a user line may claim (the auto rows are computed from the ledgers).</summary>
    public static readonly IReadOnlyList<string> UserInCategories = ["other_in"];
    public static readonly IReadOnlyList<string> UserOutCategories = ["payroll", "rent", "taxes", "capex", "debt", "other_out"];
    public static readonly IReadOnlyList<string> Frequencies = [OneOff, Weekly, Monthly, Quarterly, Yearly];

    /// <summary>{**TWCF_IN_CATEGORIES, **TWCF_OUT_CATEGORIES}.</summary>
    public static readonly IReadOnlyDictionary<string, string> CategoryLabels =
        InCategories.Concat(OutCategories).ToDictionary(kv => kv.Key, kv => kv.Value);

    public static readonly IReadOnlyDictionary<string, string> FrequencyLabels = new Dictionary<string, string>
    {
        ["oneoff"] = "One-off", ["weekly"] = "Weekly", ["monthly"] = "Monthly", ["quarterly"] = "Quarterly", ["yearly"] = "Yearly",
    };
}

public sealed record TwcfWeek(DateOnly Start, DateOnly End, bool IsPast);

/// <summary>One matrix row. Section rows carry no values; a null value is Python's None.</summary>
public sealed class TwcfRow
{
    public required string Key { get; init; }
    public required string Kind { get; init; }
    public required string Label { get; init; }
    public double?[]? Values { get; init; }
    public double? Total { get; init; }
    public bool Auto { get; init; }
    public string? Source { get; init; }
}

public sealed class TwcfMatrix
{
    public required List<TwcfWeek> Weeks { get; init; }
    public required List<TwcfRow> Rows { get; init; }
    public double Opening { get; init; }
    public double TotalIn { get; init; }
    public double TotalOut { get; init; }
    public double Net { get; init; }
    public required List<double> Closing { get; init; }
    public bool HasPastWeeks { get; init; }
    public double? Floor { get; init; }
    public bool HasScope { get; init; }
    public bool HasPayrollAuto { get; init; }
}

/// <summary>
/// The 13-Week Cash Flow engine (shared/twcf_reports.py): opening cash, the aged
/// AR/AP schedules spread over the weeks, the user's forecast lines (one-off and
/// recurring), actuals for finished weeks with a variance row, a running closing
/// balance and the optional cash-floor headroom. Nothing is stored.
///
/// Python computes this in floats; so does this port (<c>double</c>, the same
/// operations in the same order, <see cref="Round2"/> = Python's <c>round(x, 2)</c>),
/// so every cell is the same binary value Python prints.
/// </summary>
public sealed partial class TwcfReports(FinanceData data)
{
    public const int WeeksInForecast = 13;

    /// <summary>BUCKET_DEFS: age bucket → (week offset, weight) spread over the horizon.</summary>
    private static readonly (int Lo, int? Hi, (int Week, double Weight)[] Spread)[] Buckets =
    [
        (0, 30, [(0, 0.5), (1, 0.5)]),
        (31, 60, [(2, 1.0 / 3), (3, 1.0 / 3), (4, 1.0 / 3)]),
        (61, 90, [(5, 1.0 / 3), (6, 1.0 / 3), (7, 1.0 / 3)]),
        (91, null, [(8, 0.2), (9, 0.2), (10, 0.2), (11, 0.2), (12, 0.2)]),
    ];

    public FinanceData Data => data;
    private AccountixDbContext Db => data.Db;

    // ── floats as Python has them ───────────────────────────────────────

    /// <summary>float(Decimal): the double nearest the exact decimal (parsing is correctly rounded).</summary>
    public static double F(decimal? v) =>
        v is decimal d && d != 0m ? double.Parse(d.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) : 0.0;

    /// <summary>
    /// Python's round(x, 2): the exact binary value rounded half-to-even at the
    /// second decimal, then the double nearest that. Exact (big-integer) arithmetic,
    /// because scaling by 100 in floating point can cross a .5 boundary.
    /// </summary>
    public static double Round2(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x) || x == 0) return x;
        long bits = BitConverter.DoubleToInt64Bits(x);
        bool negative = bits < 0;
        int exp = (int)((bits >> 52) & 0x7FF);
        long mant = bits & 0xFFFFFFFFFFFFFL;
        if (exp == 0) exp = 1; else mant |= 1L << 52;
        exp -= 1075;   // x = mant * 2^exp
        BigInteger scaled = new BigInteger(mant) * 100;
        BigInteger q;
        if (exp >= 0)
            q = scaled << exp;
        else
        {
            BigInteger den = BigInteger.One << -exp;
            q = BigInteger.DivRem(scaled, den, out var r);
            BigInteger twice = r * 2;
            int cmp = twice.CompareTo(den);
            if (cmp > 0 || (cmp == 0 && !q.IsEven)) q += 1;
        }
        double result = (double)q / 100.0;
        return negative ? -result : result;
    }

    /// <summary>
    /// format_amount(float): the float rounded as Python rounds it (half-even on the exact
    /// binary value), grouped in the company's format, negatives in brackets — a tiny
    /// negative prints "(0.00)" as Python does. Html.Amount(double) would round a
    /// decimal approximation instead (433.455 → 433.46 where Python prints 433.45).
    /// </summary>
    public static string Display(double v, string? numberFormat)
    {
        string text = Money.Format((decimal)Round2(Math.Abs(v)), 2, numberFormat);
        return v < 0 ? $"({text})" : text;
    }

    private static int PyWeekday(DateOnly d) => ((int)d.DayOfWeek + 6) % 7;

    // ── the window ──────────────────────────────────────────────────────

    /// <summary>weeks_from: the n 7-day blocks beginning at start.</summary>
    public static List<(DateOnly Start, DateOnly End)> WeeksFrom(DateOnly start, int n = WeeksInForecast) =>
        Enumerable.Range(0, n).Select(i => (start.AddDays(7 * i), start.AddDays(7 * i + 6))).ToList();

    /// <summary>_twcf_default_start: this week's Monday on the company clock.</summary>
    public static DateOnly DefaultStart(DateOnly today) => today.AddDays(-PyWeekday(today));

    // ── cash (gl_daily_balances) ────────────────────────────────────────

    /// <summary>opening_cash: actual cash and equivalents at the end of as_of.</summary>
    public async Task<double> OpeningCashAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var ids = (await data.ChartAsync(ct)).CashIds();
        if (ids.Count == 0) return 0.0;
        var r = await Db.Set<GlDailyBalance>().AsNoTracking().Where(g => ids.Contains(g.AccountId) && g.Day <= asOf)
            .GroupBy(_ => 1).Select(g => new { Dr = g.Sum(x => x.Debit), Cr = g.Sum(x => x.Credit) }).FirstOrDefaultAsync(ct);
        return F(r?.Dr ?? 0m) - F(r?.Cr ?? 0m);
    }

    /// <summary>actual_net_cash for every given week, from one grouped-by-day read (same sums as Python's per-week query).</summary>
    private async Task<Dictionary<int, double>> ActualNetCashAsync(IReadOnlyList<(DateOnly Start, DateOnly End)> weeks,
                                                                   IReadOnlyList<int> weekIndexes, CancellationToken ct)
    {
        var output = new Dictionary<int, double>();
        if (weekIndexes.Count == 0) return output;
        var ids = (await data.ChartAsync(ct)).CashIds();
        if (ids.Count == 0)
        {
            foreach (int i in weekIndexes) output[i] = 0.0;
            return output;
        }
        DateOnly from = weekIndexes.Min(i => weeks[i].Start), to = weekIndexes.Max(i => weeks[i].End);
        var days = await Db.Set<GlDailyBalance>().AsNoTracking()
            .Where(g => ids.Contains(g.AccountId) && g.Day >= from && g.Day <= to)
            .GroupBy(g => g.Day).Select(g => new { Day = g.Key, Dr = g.Sum(x => x.Debit), Cr = g.Sum(x => x.Credit) })
            .ToListAsync(ct);
        foreach (int i in weekIndexes)
        {
            var (s, e) = weeks[i];
            decimal dr = 0m, cr = 0m;
            foreach (var d in days)
                if (d.Day >= s && d.Day <= e) { dr += d.Dr; cr += d.Cr; }
            output[i] = F(dr) - F(cr);
        }
        return output;
    }

    // ── aged AR / AP ────────────────────────────────────────────────────

    /// <summary>_party_scope: the Trade Debtors / Trade Creditors subtrees, else the executive selection.</summary>
    public async Task<HashSet<int>> PartyScopeAsync(CancellationToken ct = default)
    {
        var chart = await data.ChartAsync(ct);
        var ids = new HashSet<int>();
        foreach (string code in new[] { "1-01-02-01", "2-01-01-01" })   // ENTITY_PARENT_CODES customer, supplier
            if (chart.ByCode.TryGetValue(code, out var root))
                ids.UnionWith(chart.Subtree(root.Id));
        if (ids.Count > 0) return ids;
        return (await Db.Set<ExecAccountSelection>().AsNoTracking().Select(s => s.AccountId).ToListAsync(ct)).ToHashSet();
    }

    public sealed record AgedParty(int AccountId, string Name, List<(int Days, double Amount)> Layers, double Amount);

    /// <summary>
    /// _aged_parties for both sides from one read of the posted lines: FIFO, debits
    /// build the receivable stack and credits consume its oldest layer (payables
    /// mirror it). Each side ordered by amount, largest first (stable).
    /// </summary>
    public async Task<(List<AgedParty> Receivable, List<AgedParty> Payable)> AgedPartiesAsync(DateOnly asOf, HashSet<int> scope,
                                                                                             CancellationToken ct = default)
    {
        var recv = new List<AgedParty>();
        var pay = new List<AgedParty>();
        if (scope.Count == 0) return (recv, pay);
        var ids = scope.ToList();
        DateTime before = asOf.AddDays(1).ToDateTime(TimeOnly.MinValue);   // entry_date <= end of as_of
        var lines = await Db.Set<JournalLine>().AsNoTracking()
            .Where(l => ids.Contains(l.AccountId) && l.JournalEntry.IsPosted == true && l.JournalEntry.EntryDate < before)
            .OrderBy(l => l.JournalEntry.EntryDate).ThenBy(l => l.JournalEntryId).ThenBy(l => l.Id)
            .Select(l => new { l.AccountId, l.JournalEntry.EntryDate, l.Debit, l.Credit })
            .ToListAsync(ct);
        var postings = new Dictionary<int, List<(DateTime Date, double Dr, double Cr)>>();
        var order = new List<int>();
        foreach (var l in lines)
        {
            if (!postings.TryGetValue(l.AccountId, out var list))
            {
                postings[l.AccountId] = list = [];
                order.Add(l.AccountId);
            }
            list.Add((l.EntryDate, F(l.Debit), F(l.Credit)));
        }
        var chart = await data.ChartAsync(ct);
        foreach (int accountId in order)
        {
            if (!chart.ById.TryGetValue(accountId, out var acct)) continue;
            var rows = postings[accountId];
            double net = PySum(rows.Select(x => x.Dr - x.Cr));
            if (Math.Abs(net) < 0.005) continue;
            bool receivable = net > 0;
            var layers = new List<(DateTime Date, double Amount)>();
            foreach (var (date, dr, cr) in rows)
            {
                double add = receivable ? dr : cr;
                if (add != 0) layers.Add((date, add));
                double take = receivable ? cr : dr;
                while (take != 0 && layers.Count > 0)
                {
                    if (layers[0].Amount <= take)
                    {
                        take -= layers[0].Amount;
                        layers.RemoveAt(0);
                    }
                    else
                    {
                        layers[0] = (layers[0].Date, layers[0].Amount - take);
                        take = 0.0;
                    }
                }
            }
            if (layers.Count == 0) continue;
            var aged = layers.Select(x => (Math.Max(asOf.DayNumber - DateOnly.FromDateTime(x.Date).DayNumber, 0), x.Amount)).ToList();
            (receivable ? recv : pay).Add(new AgedParty(accountId, acct.Name, aged, Math.Abs(net)));
        }
        return (recv.OrderByDescending(p => p.Amount).ToList(), pay.OrderByDescending(p => p.Amount).ToList());
    }

    /// <summary>_side_forecast: every aged layer spread onto the horizon by its bucket.</summary>
    public static double[] SideForecast(IEnumerable<AgedParty> parties, int nWeeks = WeeksInForecast)
    {
        var cells = new double[nWeeks];
        foreach (var party in parties)
            foreach (var (days, amount) in party.Layers)
                foreach (var (lo, hi, spread) in Buckets)
                    if (lo <= days && (hi is null || days <= hi))
                    {
                        foreach (var (week, weight) in spread)
                            cells[week] += amount * weight;
                        break;
                    }
        return cells.Select(Round2).ToArray();
    }

    // ── recurring lines ─────────────────────────────────────────────────

    /// <summary>A forecast line as the expansion reads it (TwcfLine, or the payroll auto row).</summary>
    public sealed record LineSpec(DateOnly StartDate, string Frequency, int? DayOfWeek, int? DayOfMonth, int? Month, double Amount);

    public static LineSpec Spec(TwcfLine l) => new(l.StartDate, l.Frequency, l.DayOfWeek, l.DayOfMonth, l.Month, F(l.Amount));

    /// <summary>_month_occurrence: day_of_month clamped to the month's last day.</summary>
    private static DateOnly MonthOccurrence(int year, int month, int dayOfMonth) =>
        new(year, month, Math.Max(1, Math.Min(dayOfMonth, DateTime.DaysInMonth(year, month))));

    /// <summary>_line_occurrences: every occurrence from the line's start up to window_end.</summary>
    public static List<DateOnly> Occurrences(LineSpec line, DateOnly windowEnd)
    {
        var start = line.StartDate;
        var output = new List<DateOnly>();
        int dom = line.DayOfMonth ?? 1;
        switch (line.Frequency)
        {
            case Twcf.OneOff:
                output.Add(start);
                break;
            case Twcf.Weekly:
            {
                int offset = (((line.DayOfWeek ?? 0) - PyWeekday(start)) % 7 + 7) % 7;
                for (var cur = start.AddDays(offset); cur <= windowEnd; cur = cur.AddDays(7))
                    output.Add(cur);
                break;
            }
            case Twcf.Monthly:
            case Twcf.Quarterly:
            {
                int step = line.Frequency == Twcf.Monthly ? 1 : 3;
                int y = start.Year, m = start.Month;
                while (new DateOnly(y, m, 1) <= windowEnd)
                {
                    var occ = MonthOccurrence(y, m, dom);
                    if (occ >= start) output.Add(occ);
                    m += step;
                    while (m > 12) { m -= 12; y += 1; }
                }
                break;
            }
            case Twcf.Yearly:
            {
                int month = line.Month is int mo && mo != 0 ? mo : 1;
                for (int y = start.Year; new DateOnly(y, 1, 1) <= windowEnd; y++)
                {
                    var occ = MonthOccurrence(y, month, dom);
                    if (occ >= start) output.Add(occ);
                }
                break;
            }
        }
        return output;
    }

    /// <summary>line_week_values: occurrences bucketed into their week; {week index: amount} in first-hit order.</summary>
    public static List<(int Index, double Amount)> LineWeekValues(LineSpec line, IReadOnlyList<(DateOnly Start, DateOnly End)> weeks)
    {
        var windowEnd = weeks[^1].End;
        var values = new List<(int Index, double Amount)>();
        foreach (var occ in Occurrences(line, windowEnd))
            for (int idx = 0; idx < weeks.Count; idx++)
                if (weeks[idx].Start <= occ && occ <= weeks[idx].End)
                {
                    int at = values.FindIndex(v => v.Index == idx);
                    if (at < 0) values.Add((idx, 0.0 + line.Amount));
                    else values[at] = (idx, values[at].Amount + line.Amount);
                    break;
                }
        return values;
    }

    private static List<TwcfRow> CategoryRows(IReadOnlyList<TwcfLine> lines, IReadOnlyList<(DateOnly, DateOnly)> weeks,
                                              string direction, IReadOnlyList<string> categories)
    {
        var rows = new List<TwcfRow>();
        foreach (string cat in categories)
        {
            var values = new double[weeks.Count];
            foreach (var line in lines)
            {
                if (line.Direction != direction || line.Category != cat) continue;
                foreach (var (idx, amount) in LineWeekValues(Spec(line), weeks))
                    values[idx] += amount;
            }
            rows.Add(new TwcfRow { Key = $"{direction}_{cat}", Kind = "account", Label = cat, Values = values.Select(v => (double?)v).ToArray() });
        }
        return rows;
    }

    /// <summary>_payroll_auto_values: the latest approved payroll run's net, monthly on its pay date; null without one.</summary>
    private async Task<double[]?> PayrollAutoValuesAsync(IReadOnlyList<(DateOnly Start, DateOnly End)> weeks, CancellationToken ct)
    {
        var run = await Db.Set<PayrollRun>().AsNoTracking().Where(r => r.Status == "approved")
            .OrderByDescending(r => r.Year).ThenByDescending(r => r.Month).ThenByDescending(r => r.Id)
            .Select(r => new { r.TotalNet, r.RunDate, r.Year, r.Month }).FirstOrDefaultAsync(ct);
        if (run is null) return null;
        double amount = F(run.TotalNet);
        if (amount <= 0) return null;
        var payDate = run.RunDate is DateTime rd ? DateOnly.FromDateTime(rd) : new DateOnly(run.Year, run.Month, 1);
        var line = new LineSpec(payDate, Twcf.Monthly, 0, payDate.Day, payDate.Month, amount);
        var values = new double[weeks.Count];
        foreach (var (idx, value) in LineWeekValues(line, weeks))
            values[idx] = value;
        return values;
    }

    /// <summary>
    /// Python's built-in sum() of floats. Since 3.12 CPython adds with Neumaier's
    /// compensated summation (the running error is added back at the end), so
    /// sum([0.1] * 10) == 1.0 — a plain loop gives 0.9999999999999999 and the
    /// cents of a rounded total can differ. Jinja's |sum is the same builtin.
    /// </summary>
    public static double PySum(IEnumerable<double> xs)
    {
        double f = 0.0, c = 0.0;
        foreach (double x in xs)
        {
            double t = f + x;
            if (Math.Abs(f) >= Math.Abs(x)) c += (f - t) + x;
            else c += (x - t) + f;
            f = t;
        }
        if (c != 0 && double.IsFinite(c)) f += c;
        return f;
    }

    /// <summary>
    /// build_matrix(start, today): the full 13-week matrix. Python's default today
    /// is the company's date (local_today); the page passes CompanyClock.Today.
    /// </summary>
    public async Task<TwcfMatrix> BuildMatrixAsync(DateOnly start, DateOnly today, CancellationToken ct = default)
    {
        var weeks = WeeksFrom(start);
        var asOf = start.AddDays(-1);
        var lines = await Db.Set<TwcfLine>().AsNoTracking().OrderBy(l => l.StartDate).ThenBy(l => l.Id).ToListAsync(ct);

        double opening = await OpeningCashAsync(asOf, ct);
        var scope = await PartyScopeAsync(ct);
        var (recv, pay) = await AgedPartiesAsync(asOf, scope, ct);
        var coll = SideForecast(recv, weeks.Count);
        var payf = SideForecast(pay, weeks.Count);

        var inRows = new List<TwcfRow>
        {
            new() { Key = "in_collections", Kind = "account", Label = "Collections from customers", Values = coll.Select(v => (double?)v).ToArray(), Auto = true },
        };
        inRows.AddRange(CategoryRows(lines, weeks, Twcf.In, Twcf.UserInCategories));
        var outRows = new List<TwcfRow>
        {
            new() { Key = "out_suppliers", Kind = "account", Label = "Payments to suppliers", Values = payf.Select(v => (double?)v).ToArray(), Auto = true },
        };
        var payroll = await PayrollAutoValuesAsync(weeks, ct);
        if (payroll is not null)
            outRows.Add(new TwcfRow
            {
                Key = "out_payroll_auto", Kind = "account", Label = "Salaries & wages",
                Values = payroll.Select(v => (double?)v).ToArray(), Auto = true, Source = "payroll",
            });
        outRows.AddRange(CategoryRows(lines, weeks, Twcf.Out, Twcf.UserOutCategories));

        int n = weeks.Count;
        var totalIn = Enumerable.Range(0, n).Select(i => PySum(inRows.Select(r => r.Values![i]!.Value))).ToArray();
        var totalOut = Enumerable.Range(0, n).Select(i => PySum(outRows.Select(r => r.Values![i]!.Value))).ToArray();
        var forecastNet = Enumerable.Range(0, n).Select(i => totalIn[i] - totalOut[i]).ToArray();

        var past = Enumerable.Range(0, n).Where(i => weeks[i].End < today).ToList();
        var actualByWeek = await ActualNetCashAsync(weeks, past, ct);
        var actuals = Enumerable.Range(0, n).Select(i => actualByWeek.TryGetValue(i, out double a) ? (double?)a : null).ToArray();
        var net = Enumerable.Range(0, n).Select(i => actuals[i] ?? forecastNet[i]).ToArray();
        var variance = Enumerable.Range(0, n).Select(i => actuals[i] is double a ? (double?)Round2(a - forecastNet[i]) : null).ToArray();

        var closing = new List<double>(n);
        double run = opening;
        for (int i = 0; i < n; i++)
        {
            run += net[i];
            closing.Add(Round2(run));
        }

        var settings = await data.SettingsAsync(ct);
        double? floor = settings?.TwcfCashFloor is decimal fl && fl != 0m ? F(fl) : null;

        var rows = new List<TwcfRow>
        {
            new() { Key = "opening", Kind = "opening", Label = "Opening cash & equivalents",
                    Values = new double?[n].Select((_, i) => i == 0 ? (double?)opening : null).ToArray(), Total = opening },
            new() { Key = "sec_in", Kind = "section", Label = "RECEIPTS" },
        };
        rows.AddRange(inRows);
        rows.Add(new TwcfRow { Key = "total_in", Kind = "total", Label = "Total receipts", Values = totalIn.Select(v => (double?)v).ToArray(), Total = Round2(PySum(totalIn)) });
        rows.Add(new TwcfRow { Key = "sec_out", Kind = "section", Label = "DISBURSEMENTS" });
        rows.AddRange(outRows);
        rows.Add(new TwcfRow { Key = "total_out", Kind = "total", Label = "Total disbursements", Values = totalOut.Select(v => (double?)v).ToArray(), Total = Round2(PySum(totalOut)) });
        rows.Add(new TwcfRow { Key = "net", Kind = "net", Label = "Net cash flow", Values = net.Select(v => (double?)v).ToArray(), Total = Round2(PySum(net)) });
        rows.Add(new TwcfRow { Key = "variance", Kind = "variance", Label = "Variance (actual vs forecast)", Values = variance, Total = null });
        rows.Add(new TwcfRow { Key = "closing", Kind = "grand", Label = "Closing cash & equivalents", Values = closing.Select(v => (double?)v).ToArray(), Total = closing[^1] });
        if (floor is double f)
        {
            rows.Add(new TwcfRow { Key = "floor", Kind = "plain", Label = "Minimum cash (floor)", Values = Enumerable.Repeat((double?)f, n).ToArray(), Total = null });
            rows.Add(new TwcfRow
            {
                Key = "headroom", Kind = "headroom", Label = "Headroom over floor",
                Values = closing.Select(c => (double?)Round2(c - f)).ToArray(), Total = Round2(closing[^1] - f),
            });
        }

        return new TwcfMatrix
        {
            Weeks = weeks.Select(w => new TwcfWeek(w.Start, w.End, w.End < today)).ToList(),
            Rows = rows,
            Opening = opening,
            TotalIn = Round2(PySum(totalIn)),
            TotalOut = Round2(PySum(totalOut)),
            Net = Round2(PySum(net)),
            Closing = closing,
            HasPastWeeks = actuals.Any(a => a is not null),
            Floor = floor,
            HasScope = scope.Count > 0,
            HasPayrollAuto = payroll is not null,
        };
    }

    /// <summary>The week header: "07 Sep – 13 Sep" (_twcf_week_label).</summary>
    public static string WeekLabel(DateOnly start, DateOnly end) =>
        $"{start.ToString("dd MMM", CultureInfo.InvariantCulture)} – {end.ToString("dd MMM", CultureInfo.InvariantCulture)}";
}
