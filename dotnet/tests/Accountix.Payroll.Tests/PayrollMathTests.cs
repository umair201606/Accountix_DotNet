using Accountix.Payroll.Services;

namespace Accountix.Payroll.Tests;

/// <summary>
/// hr_app/payroll_engine.py, rule by rule, as pure functions (the e2e figures of
/// tests/e2e/test_payroll_engine.py are plain slab arithmetic: basic 150,000,
/// annual taxable 1,800,000 → 6,000 + 11% × 600,000 = 72,000, i.e. 6,000 a month).
/// </summary>
public class PayrollMathTests
{
    private static readonly IReadOnlyList<TaxSlab> Ty2026 =
        PayrollMath.SlabsTy2026.Select(s => new TaxSlab(s.From, s.To, s.Rate, s.Fixed)).ToList();

    private static readonly PayrollPolicy Defaults = new();
    private const double Basic = 150_000;

    [Fact]
    public void No_slabs_means_no_tax()
    {
        Assert.Equal(0, PayrollMath.CalculateTax([], 5_000_000));
        Assert.Equal(0, PayrollMath.Withholding([], Defaults, (0, 0), 7, 2025, Basic));
    }

    [Fact]
    public void Tax_year_2026_slabs()
    {
        Assert.Equal(72_000, PayrollMath.CalculateTax(Ty2026, 1_800_000));
        Assert.Equal(0, PayrollMath.CalculateTax(Ty2026, 600_000));
        Assert.Equal(616_000 + 0.35 * 900_000, PayrollMath.CalculateTax(Ty2026, 5_000_000));
        Assert.Equal(6_000, PayrollMath.Withholding(Ty2026, Defaults, (0, 0), 7, 2025, Basic));
    }

    [Fact]
    public void Exempt_allowance_is_not_taxed()
    {
        // 150,000 + 40% rent (taxable) = 210,000 a month → 2,520,000 → 116,000 + 23% × 320,000 = 189,600
        Assert.Equal(15_800, PayrollMath.Withholding(Ty2026, Defaults, (0, 0), 7, 2025, 210_000), 2);
    }

    [Fact]
    public void A_bonus_is_taxed_once_not_annualised()
    {
        double baseTax = PayrollMath.Withholding(Ty2026, Defaults, (0, 0), 7, 2025, Basic);
        double withBonus = PayrollMath.Withholding(Ty2026, Defaults, (0, 0), 7, 2025, Basic, 300_000, Basic);
        Assert.Equal(baseTax + 33_000, withBonus, 2);
    }

    [Fact]
    public void Withholding_is_cumulative_over_the_tax_year()
    {
        // July paid 450,000 (with the bonus) and withheld 39,000.
        var ytd = PayrollMath.YearToDate([new YtdSlip(7, 2025, 450_000m, 450_000m, "{\"deductions\": {\"Income Tax\": 39000.0}}")], 8, 2025);
        Assert.Equal((450_000.0, 39_000.0), ytd);
        double aug = PayrollMath.Withholding(Ty2026, Defaults, ytd, 8, 2025, Basic);
        Assert.Equal((105_000 - 39_000) / 11.0, aug, 2);
        Assert.Equal(105_000, 39_000 + aug * 11, 0);
    }

    [Fact]
    public void Year_to_date_reads_only_this_tax_year_before_this_month()
    {
        YtdSlip[] slips =
        [
            new(6, 2025, 100m, 100m, "{\"deductions\": {\"Income Tax\": 5.0}}"),   // previous tax year
            new(7, 2025, null, 200m, "{\"deductions\": {\"Income Tax\": 7.5}}"),   // taxable missing: gross counts
            new(9, 2025, 300m, 300m, "[1, 2]"),                                   // unreadable components: no tax
            new(10, 2025, 999m, 999m, "{}"),                                       // this month: not yet
        ];
        Assert.Equal((500.0, 7.5), PayrollMath.YearToDate(slips, 10, 2025));
    }

    [Fact]
    public void Tax_year_months_left()
    {
        Assert.Equal(((7, 2025), 12), PayrollMath.TaxYearMonths(7, 2025));
        Assert.Equal(((7, 2025), 11), PayrollMath.TaxYearMonths(8, 2025));
        Assert.Equal(((7, 2025), 1), PayrollMath.TaxYearMonths(6, 2026));
        Assert.Equal(((7, 2025), 6), PayrollMath.TaxYearMonths(1, 2026));
    }

    [Fact]
    public void Surcharge_on_high_income()
    {
        double baseTax = PayrollMath.CalculateTax(Ty2026, 12_000_000);
        Assert.Equal(baseTax * 1.09, PayrollMath.TaxOn(Ty2026, Defaults, 12_000_000), 2);
        Assert.Equal(PayrollMath.CalculateTax(Ty2026, 9_000_000), PayrollMath.TaxOn(Ty2026, Defaults, 9_000_000));
        Assert.Equal(baseTax, PayrollMath.TaxOn(Ty2026, Defaults with { SurchargePct = 0 }, 12_000_000));
    }

    [Fact]
    public void Unpaid_leave_and_joiners_prorate_by_calendar_days()
    {
        double unpaid = PayrollMath.UnpaidLeaveDays([new LeaveRow(new(2025, 3, 10), new(2025, 3, 12), 3m)], 3, 2025);
        Assert.Equal(3, unpaid);
        var march = PayrollMath.PaidRatio(new DateOnly(2000, 1, 1), null, unpaid, 3, 2025)!.Value;
        Assert.Equal(Py.Round(Basic * 28 / 31, 2), Py.Round(Basic * march.Ratio, 2));
        Assert.Equal(31, march.DaysInMonth);
        var april = PayrollMath.PaidRatio(new DateOnly(2025, 4, 21), null, 0, 4, 2025)!.Value;
        Assert.Equal(Basic * 10 / 30, Py.Round(Basic * april.Ratio, 2), 2);
        Assert.Null(PayrollMath.PaidRatio(new DateOnly(2025, 4, 21), null, 0, 3, 2025));    // not joined yet
    }

    [Fact]
    public void Leave_across_the_month_end_counts_its_share_of_total_days()
    {
        // 28 Feb – 3 Mar, 4 days on the calendar but 3 working days: March holds 3 of 4 → 2.25.
        Assert.Equal(2.25, PayrollMath.UnpaidLeaveDays([new LeaveRow(new(2025, 2, 28), new(2025, 3, 3), 3m)], 3, 2025));
    }

    [Fact]
    public void A_leaver_is_prorated_then_stops()
    {
        var jan = PayrollMath.PaidRatio(new DateOnly(2000, 1, 1), new DateOnly(2026, 1, 10), 0, 1, 2026)!.Value;
        Assert.Equal(Basic * 10 / 31, Py.Round(Basic * jan.Ratio, 2), 2);
        Assert.Null(PayrollMath.PaidRatio(new DateOnly(2000, 1, 1), new DateOnly(2026, 1, 10), 0, 2, 2026));
        Assert.True(PayrollMath.InPayroll(false, new DateOnly(2026, 1, 10), 1, 2026));
        Assert.False(PayrollMath.InPayroll(false, new DateOnly(2026, 1, 10), 2, 2026));
    }

    [Fact]
    public void Unpaid_days_beyond_employment_take_the_employed_days_as_an_int()
    {
        var r = PayrollMath.PaidRatio(new DateOnly(2025, 4, 28), null, 5.5, 4, 2025)!.Value;
        Assert.True(r.UnpaidDays.IsInt);
        Assert.Equal(3, r.UnpaidDays.Value);
        Assert.Equal(0, r.Ratio);
    }

    [Fact]
    public void Overtime_follows_the_policy()
    {
        var policy = Defaults with { OvertimeMultiplier = 2, OvertimeHoursBasis = 208 };
        Assert.Equal(10 * Basic / 208 * 2, PayrollMath.OvertimePay(policy, 10, Basic), 2);
        Assert.Equal(Py.Round(10 * (Basic / 160) * 1.5, 2), PayrollMath.OvertimePay(Defaults with { OvertimeMultiplier = 0, OvertimeHoursBasis = 0 }, 10, Basic));
    }

    [Fact]
    public void Eobi_on_the_wage_base_prorated()
    {
        Assert.Equal((0.0, 0.0), PayrollMath.Eobi(Defaults));
        var on = Defaults with { EobiEnabled = true };
        Assert.Equal((370.0, 1850.0), PayrollMath.Eobi(on));
        Assert.Equal((185.0, 925.0), PayrollMath.Eobi(on, 0.5));
        Assert.Equal((370.0, 1850.0), PayrollMath.Eobi(on, 1.2));
    }

    [Fact]
    public void Pf_on_basic_or_gross()
    {
        Assert.Equal(100, PayrollMath.PfBase("basic", 100, 150));
        Assert.Equal(150, PayrollMath.PfBase("gross", 100, 150));
        Assert.Equal(150, PayrollMath.PfBase(null, 100, 150));     // an old config without the column: gross
    }

    [Theory]
    [InlineData("2000-01-01", "2026-03-15", 26)]   // 26 years 2 months
    [InlineData("2000-01-01", "2026-08-01", 27)]   // 26 years 7 months: the part year counts
    [InlineData("2000-01-15", "2000-07-14", 0)]    // five months
    [InlineData("2020-05-01", "2019-01-01", 0)]
    public void Service_years(string joined, string left, int years) =>
        Assert.Equal(years, PayrollMath.ServiceYears(DateOnly.Parse(joined), DateOnly.Parse(left)));

    [Fact]
    public void Final_settlement_in_the_leaving_month()
    {
        var policy = Defaults with { GratuityEnabled = true, LeaveEncashmentEnabled = true };
        var s = PayrollMath.FinalSettlement(policy, new DateOnly(2000, 1, 1), new DateOnly(2026, 3, 15), 3, 2026, Basic, Basic, [10.0]);
        Assert.Equal(Basic * 26, s["Gratuity"]);
        Assert.Equal(Basic / 30 * 10, s["Leave encashment"], 2);
        Assert.Empty(PayrollMath.FinalSettlement(policy, new DateOnly(2000, 1, 1), new DateOnly(2026, 3, 15), 2, 2026, Basic, Basic, [10.0]));
        Assert.Empty(PayrollMath.FinalSettlement(Defaults, new DateOnly(2000, 1, 1), new DateOnly(2026, 3, 15), 3, 2026, Basic, Basic, [10.0]));
        var gross = PayrollMath.FinalSettlement(policy with { GratuityBasis = "gross" }, new DateOnly(2000, 1, 1), new DateOnly(2026, 3, 15), 3, 2026, Basic, 200_000, []);
        Assert.Equal(200_000 * 26, gross["Gratuity"]);
        Assert.False(gross.ContainsKey("Leave encashment"));
    }

    [Fact]
    public void A_raise_mid_month_is_split_and_old_months_keep_the_old_rate()
    {
        RevisionRow[] raise = [new(new DateOnly(2025, 9, 16), 200_000m, 260_000m)];
        Assert.Equal((200_000 * 15 + 260_000 * 15) / 30.0, PayrollMath.BasicForMonth(200_000m, raise, 9, 2025), 2);
        Assert.Equal(200_000, PayrollMath.BasicForMonth(260_000m, raise, 7, 2025));
        Assert.Equal(260_000, PayrollMath.BasicForMonth(260_000m, raise, 10, 2025));
        Assert.Equal(55_000, PayrollMath.BasicForMonth(55_000m, [], 10, 2025));
        // Before the first revision with no previous figure: the profile's basic.
        Assert.Equal(1_000, PayrollMath.BasicOn(1_000m, [new RevisionRow(new DateOnly(2025, 9, 1), null, 2_000m)], new DateOnly(2025, 8, 1)));
    }

    [Fact]
    public void Components_are_fixed_or_a_percentage_of_basic()
    {
        Assert.Equal(60_000, PayrollMath.ComponentAmount("percent_of_basic", 40m, Basic));
        Assert.Equal(60_000, PayrollMath.ComponentAmount("percentage", 40m, Basic));
        Assert.Equal(15_000, PayrollMath.ComponentAmount("fixed", 15_000m, Basic));
        Assert.Equal(15_000, PayrollMath.ComponentAmount(null, 15_000m, Basic));
        Assert.Equal(0, PayrollMath.ComponentAmount("fixed", null, Basic));
    }

    [Fact]
    public void Loan_instalment_falls_back_to_amount_over_months()
    {
        Assert.Equal(1_000, PayrollCalculator.Due(1_000m, 12_000m, 12));
        Assert.Equal(1_000, PayrollCalculator.Due(null, 12_000m, 12));
        Assert.Equal(12_000, PayrollCalculator.Due(0m, 12_000m, 0));
    }
}

public class LedgerCodeTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(57)]
    [InlineData(12345)]
    public void Loan_account_codes_are_the_ledgers_entity_codes(int loanId) =>
        Assert.Equal($"{Accountix.Accounting.Services.Coa.EntityParentCodes["loan"]}-{(loanId + Accountix.Accounting.Services.Coa.EntityIdOffset).ToString().PadLeft(4, '0')}",
                     Accountix.Payroll.Services.PayrollRunner.LoanAccountCode(loanId));
}
