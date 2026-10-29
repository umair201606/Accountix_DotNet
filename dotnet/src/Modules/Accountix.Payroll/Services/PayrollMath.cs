using System.Text.Json;

namespace Accountix.Payroll.Services;

/// <summary>The company's payroll policy (payroll_settings), with Python's raw values: null and 0 differ ("x or 1.5").</summary>
public sealed record PayrollPolicy
{
    public double? OvertimeMultiplier { get; init; } = 1.5;
    public double? OvertimeHoursBasis { get; init; } = 160;
    public bool EobiEnabled { get; init; }
    public double? EobiWageBase { get; init; } = 37000;
    public double? EobiEmployeePct { get; init; } = 1;
    public double? EobiEmployerPct { get; init; } = 5;
    public double? SurchargeThreshold { get; init; } = 10_000_000;
    public double? SurchargePct { get; init; } = 9;
    public bool GratuityEnabled { get; init; }
    public string? GratuityBasis { get; init; } = "basic";
    public double? GratuityDaysPerYear { get; init; } = 30;
    public bool LeaveEncashmentEnabled { get; init; }

    public static PayrollPolicy From(PayrollSetting s) => new()
    {
        OvertimeMultiplier = N(s.OvertimeMultiplier),
        OvertimeHoursBasis = N(s.OvertimeHoursBasis),
        EobiEnabled = s.EobiEnabled == true,
        EobiWageBase = N(s.EobiWageBase),
        EobiEmployeePct = N(s.EobiEmployeePct),
        EobiEmployerPct = N(s.EobiEmployerPct),
        SurchargeThreshold = N(s.SurchargeThreshold),
        SurchargePct = N(s.SurchargePct),
        GratuityEnabled = s.GratuityEnabled == true,
        GratuityBasis = s.GratuityBasis,
        GratuityDaysPerYear = N(s.GratuityDaysPerYear),
        LeaveEncashmentEnabled = s.LeaveEncashmentEnabled == true,
    };

    private static double? N(decimal? d) => d is null ? null : Py.F(d);
}

/// <summary>One income tax slab (income_tax_slabs), as floats.</summary>
public sealed record TaxSlab(double MinIncome, double MaxIncome, double RatePct, double FixedAmount)
{
    public static TaxSlab From(IncomeTaxSlab s) => new(Py.F(s.MinIncome), Py.F(s.MaxIncome), Py.F(s.RatePct), Py.F(s.FixedAmount));
}

public sealed record RevisionRow(DateOnly EffectiveFrom, decimal? PreviousBasic, decimal NewBasic);
public sealed record LeaveRow(DateOnly StartDate, DateOnly EndDate, decimal TotalDays);
public sealed record YtdSlip(int Month, int Year, decimal? TaxablePay, decimal? GrossPay, string? ComponentsJson);

/// <summary>
/// hr_app/payroll_engine.py: the arithmetic that has to be right, as pure
/// functions over data already loaded (one query per table for the whole
/// company, never per employee). Floats and Python's rounding throughout.
/// </summary>
public static class PayrollMath
{
    public const int TaxYearStartMonth = 7;

    /// <summary>Salaried individuals, tax year 2026 (Finance Act 2025): from, to, rate %, tax on the amount up to "from".</summary>
    public static readonly IReadOnlyList<(double From, double To, double Rate, double Fixed)> SlabsTy2026 =
    [
        (0, 600_000, 0, 0),
        (600_000, 1_200_000, 1, 0),
        (1_200_000, 2_200_000, 11, 6_000),
        (2_200_000, 3_200_000, 23, 116_000),
        (3_200_000, 4_100_000, 30, 346_000),
        (4_100_000, 999_999_999, 35, 616_000),
    ];

    /// <summary>component_amount: a component's full-month amount (fixed, or % of basic).</summary>
    public static double ComponentAmount(string? calculationMethod, decimal? value, double basic)
    {
        double v = Py.F(value);
        if ((string.IsNullOrEmpty(calculationMethod) ? "fixed" : calculationMethod) is "percentage" or "percent_of_basic")
            return Py.Round(basic * v / 100, 2);
        return Py.Round(v, 2);
    }

    public static (DateOnly First, DateOnly Last) MonthBounds(int month, int year) =>
        (new DateOnly(year, month, 1), new DateOnly(year, month, DateTime.DaysInMonth(year, month)));

    /// <summary>IncomeTaxSlab.calculate_tax: slabs ordered by min income; no slabs, no tax.</summary>
    public static double CalculateTax(IReadOnlyList<TaxSlab> slabs, double annual)
    {
        double income = annual;
        for (int i = slabs.Count - 1; i >= 0; i--)
        {
            var s = slabs[i];
            if (income > s.MinIncome)
                return Py.Round(s.FixedAmount + (income - s.MinIncome) * s.RatePct / 100, 2);
        }
        return 0.0;
    }

    /// <summary>tax_on: annual tax from the slabs plus the surcharge on high salaried income.</summary>
    public static double TaxOn(IReadOnlyList<TaxSlab> slabs, PayrollPolicy p, double annual)
    {
        double income = Py.Max(0.0, annual);
        double tax = CalculateTax(slabs, income);
        if (p.SurchargePct is double sur && sur != 0 && income > (p.SurchargeThreshold ?? 0))
            tax = tax * (1 + sur / 100);
        return Py.Round(tax, 2);
    }

    /// <summary>overtime_pay: hours × (basic / hours a month) × the overtime rate.</summary>
    public static double OvertimePay(PayrollPolicy p, double hours, double fullBasic)
    {
        double basis = Or(p.OvertimeHoursBasis, 160);
        if (basis == 0) basis = 160;
        return Py.Round(hours * (fullBasic / basis) * Or(p.OvertimeMultiplier, 1.5), 2);
    }

    /// <summary>eobi: (employee share, employer share), prorated with the paid part of the month.</summary>
    public static (double Employee, double Employer) Eobi(PayrollPolicy p, double ratio = 1.0)
    {
        if (!p.EobiEnabled)
            return (0.0, 0.0);
        double b = (p.EobiWageBase ?? 0) * Py.Min(1.0, ratio);
        return (Py.Round(b * (p.EobiEmployeePct ?? 0) / 100, 2), Py.Round(b * (p.EobiEmployerPct ?? 0) / 100, 2));
    }

    /// <summary>pf_base: contributions on basic or on gross ("gross" when the config says nothing).</summary>
    public static double PfBase(string? contributionBase, double basic, double gross) =>
        (string.IsNullOrEmpty(contributionBase) ? "gross" : contributionBase) == "basic" ? basic : gross;

    /// <summary>tax_year_months: (start month, start year) of the tax year, months left including this one.</summary>
    public static ((int Month, int Year) Start, int Left) TaxYearMonths(int month, int year)
    {
        int startYear = month >= TaxYearStartMonth ? year : year - 1;
        int elapsed = ((month - TaxYearStartMonth) % 12 + 12) % 12;
        return ((TaxYearStartMonth, startYear), 12 - elapsed);
    }

    /// <summary>year_to_date: taxable pay and income tax on the employee's earlier slips in the tax year.</summary>
    public static (double Taxable, double Tax) YearToDate(IEnumerable<YtdSlip> slips, int month, int year)
    {
        var ((sm, sy), _) = TaxYearMonths(month, year);
        int key = year * 12 + month, startKey = sy * 12 + sm;
        double taxable = 0.0, tax = 0.0;
        foreach (var s in slips)
        {
            int k = s.Year * 12 + s.Month;
            if (!(startKey <= k && k < key))
                continue;
            taxable += s.TaxablePay is not null ? Py.F(s.TaxablePay) : Py.F(s.GrossPay);
            tax += IncomeTaxOf(s.ComponentsJson);
        }
        return (Py.Round(taxable, 2), Py.Round(tax, 2));
    }

    /// <summary>float((comps.get("deductions") or {}).get("Income Tax", 0) or 0), 0 when unreadable.</summary>
    public static double IncomeTaxOf(string? componentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(componentsJson) ? "{}" : componentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return 0.0;
            if (!doc.RootElement.TryGetProperty("deductions", out var d) || d.ValueKind is JsonValueKind.Null or JsonValueKind.False)
                return 0.0;
            if (d.ValueKind != JsonValueKind.Object)
                return 0.0;
            if (!d.TryGetProperty("Income Tax", out var t))
                return 0.0;
            return t.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.False => 0.0,
                JsonValueKind.Number => t.GetDouble(),
                JsonValueKind.String => t.GetString() is { Length: > 0 } s ? Py.ParseFloat(s) ?? 0.0 : 0.0,
                _ => 0.0,
            };
        }
        catch (JsonException)
        {
            return 0.0;
        }
    }

    /// <summary>withholding: this month's income tax by the cumulative method over the tax year.</summary>
    public static double Withholding(IReadOnlyList<TaxSlab> slabs, PayrollPolicy p, (double Taxable, double Tax) ytd,
                                     int month, int year, double regularTaxable, double oneOffTaxable = 0.0,
                                     double? fullMonthTaxable = null)
    {
        var (_, left) = TaxYearMonths(month, year);
        double full = fullMonthTaxable ?? regularTaxable;
        double projected = ytd.Taxable + regularTaxable + full * (left - 1);
        double baseTax = TaxOn(slabs, p, projected);
        double regularPart = Py.Max(0.0, (baseTax - ytd.Tax) / left);
        double oneOffPart = oneOffTaxable != 0 ? TaxOn(slabs, p, projected + oneOffTaxable) - baseTax : 0.0;
        return Py.Round(regularPart + Py.Max(0.0, oneOffPart), 2);
    }

    /// <summary>unpaid_leave_days: approved unpaid leave falling in the month (the same share of total_days).</summary>
    public static double UnpaidLeaveDays(IEnumerable<LeaveRow> leaves, int month, int year)
    {
        var (first, last) = MonthBounds(month, year);
        double days = 0.0;
        foreach (var lr in leaves)
        {
            int span = lr.EndDate.DayNumber - lr.StartDate.DayNumber + 1;
            var to = lr.EndDate < last ? lr.EndDate : last;
            var from = lr.StartDate > first ? lr.StartDate : first;
            int inside = to.DayNumber - from.DayNumber + 1;
            if (span <= 0 || inside <= 0)
                continue;
            double total = lr.TotalDays != 0 ? Py.F(lr.TotalDays) : span;
            days += total * inside / span;
        }
        return Py.Round(days, 2);
    }

    /// <summary>paid_ratio: (paid share of the month, days in month, unpaid days), or null when not employed in it.</summary>
    public static (double Ratio, int DaysInMonth, Py.Num UnpaidDays)? PaidRatio(DateOnly? joined, DateOnly? left, double unpaidLeave,
                                                                               int month, int year)
    {
        var (first, last) = MonthBounds(month, year);
        int dim = last.Day;
        if ((joined is DateOnly j && j > last) || (left is DateOnly l && l < first))
            return null;
        var start = joined is DateOnly j2 && j2 > first ? j2 : first;
        var end = left is DateOnly l2 && l2 < last ? l2 : last;
        int employed = end.DayNumber - start.DayNumber + 1;
        // min(unpaid_leave_days(...), employed): the int wins only when it is smaller.
        Py.Num unpaid = employed < unpaidLeave ? Py.Num.Int(employed) : unpaidLeave;
        return (Py.Max(0.0, (employed - unpaid.Value) / dim), dim, unpaid);
    }

    /// <summary>in_payroll: active staff, plus a leaver deactivated before their final month was run.</summary>
    public static bool InPayroll(bool userActive, DateOnly? left, int month, int year) =>
        userActive || (left is DateOnly l && l >= MonthBounds(month, year).First);

    /// <summary>service_years: completed years, a final part year over six months counting as a full year.</summary>
    public static int ServiceYears(DateOnly? joined, DateOnly? left)
    {
        if (joined is not DateOnly j || left is not DateOnly l || l < j)
            return 0;
        int months = (l.Year - j.Year) * 12 + (l.Month - j.Month);
        if (l.Day < j.Day) months -= 1;
        months = Math.Max(0, months);
        return months / 12 + (months % 12 > 6 ? 1 : 0);
    }

    /// <summary>
    /// final_settlement: {"Gratuity": x, "Leave encashment": y} when the last working
    /// day falls in this month and the company pays them; else empty.
    /// <paramref name="carryForwardRemaining"/>: remaining days of the leaver's paid,
    /// carry-forward leave quotas for the leaving year.
    /// </summary>
    public static OrderedDictionary<string, double> FinalSettlement(PayrollPolicy p, DateOnly? joined, DateOnly? left,
                                                                    int month, int year, double fullBasic, double fullGross,
                                                                    IEnumerable<double> carryForwardRemaining)
    {
        var output = new OrderedDictionary<string, double>();
        var (first, last) = MonthBounds(month, year);
        if (left is not DateOnly l || !(first <= l && l <= last))
            return output;
        if (p.GratuityEnabled)
        {
            double b = (string.IsNullOrEmpty(p.GratuityBasis) ? "basic" : p.GratuityBasis) == "gross" ? fullGross : fullBasic;
            int years = ServiceYears(joined, l);
            double amount = Py.Round(b / 30 * Or(p.GratuityDaysPerYear, 30) * years, 2);
            if (amount != 0)
                output["Gratuity"] = amount;
        }
        if (p.LeaveEncashmentEnabled)
        {
            double days = Py.Sum(carryForwardRemaining);
            double amount = Py.Round(fullBasic / 30 * Py.Max(0.0, days), 2);
            if (amount != 0)
                output["Leave encashment"] = amount;
        }
        return output;
    }

    /// <summary>basic_on: the basic in force on a day (revisions ordered by effective_from, id).</summary>
    public static double BasicOn(decimal? profileBasic, IReadOnlyList<RevisionRow> revisions, DateOnly day)
    {
        if (revisions.Count == 0)
            return Py.F(profileBasic);
        double? current = null;
        foreach (var r in revisions)
            if (r.EffectiveFrom <= day)
                current = Py.F(r.NewBasic);
        if (current is double c)
            return c;
        var first = revisions[0];
        return first.PreviousBasic is not null ? Py.F(first.PreviousBasic) : Py.F(profileBasic);
    }

    /// <summary>basic_for_month: the full-month basic, day-weighted when a revision takes effect inside the month.</summary>
    public static double BasicForMonth(decimal? profileBasic, IReadOnlyList<RevisionRow> revisions, int month, int year)
    {
        var (first, last) = MonthBounds(month, year);
        if (revisions.Count == 0)
            return Py.F(profileBasic);
        double total = 0.0;
        for (var d = first; d <= last; d = d.AddDays(1))
            total += BasicOn(profileBasic, revisions, d);
        return Py.Round(total / last.Day, 2);
    }

    private static double Or(double? v, double fallback) => v is double x && x != 0 ? x : fallback;
}
