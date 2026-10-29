using System.Net;
using Accountix.Core.Entities;
using Accountix.Payroll.Services;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Payroll.Tests;

public sealed class EngineHarness : Harness
{
    protected override Task<World> BuildAsync() => World.CreateAsync("Payroll Engine Co");
    protected override Task SeedAsync() => W.ProfileAsync(W.Admin, PayrollEngineTests.Basic);
}

/// <summary>
/// tests/e2e/test_payroll_engine.py through the .NET routes: proration, taxable
/// income, cumulative withholding, loan cap, percentage components, EOBI, PF base,
/// leaving date and final settlement. One fresh company, one employee (basic 150,000).
/// </summary>
[TestCaseOrderer(StepOrderer.Name, StepOrderer.Assembly)]
public sealed class PayrollEngineTests(EngineHarness h) : IClassFixture<EngineHarness>
{
    public const decimal Basic = 150_000;
    private const double B = 150_000;
    private World W => h.W;
    private object Bonus(string amount) => new Dictionary<string, object> { [W.Admin.ToString()] = new Dictionary<string, string> { ["bonus"] = amount } };

    [ReferenceFact]
    public async Task S01_no_slabs_means_no_tax()
    {
        Assert.Equal(0, (await h.PreviewAsync(3, 2025)).D("monthly_tax"));
        string page = await (await h.C.GetAsync("/compensation/run-payroll")).Content.ReadAsStringAsync();
        Assert.Contains("No income tax slabs", page);
    }

    [ReferenceFact]
    public async Task S02_tax_year_2026_slabs_and_monthly_tax()
    {
        var r = await h.PostAsync("/compensation/tax-settings", ("action", "seed_defaults"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        await using var db = W.Db();
        var slabs = await PayrollData.SlabsAsync(db);
        Assert.Equal(72_000, PayrollMath.CalculateTax(slabs, 1_800_000));
        Assert.Equal(0, PayrollMath.CalculateTax(slabs, 600_000));
        Assert.Equal(616_000 + 0.35 * 900_000, PayrollMath.CalculateTax(slabs, 5_000_000));
        Assert.Equal(6_000, (await h.PreviewAsync(7, 2025)).D("monthly_tax"));
    }

    [ReferenceFact]
    public async Task S03_exempt_allowance_is_not_taxed_and_percent_components()
    {
        await h.SetComponentsAsync(W.Admin, Basic,
        [
            Harness.Comp("House Rent", "allowance", "percent_of_basic", 40, true),
            Harness.Comp("Medical", "allowance", "fixed", 15_000, false),
        ]);
        var p = await h.PreviewAsync(7, 2025);
        Assert.Equal(60_000, p.D("allowances_breakdown", "House Rent"));
        Assert.Equal(B + 60_000 + 15_000, p.D("gross"));
        Assert.Equal(B + 60_000, p.D("taxable"));
        Assert.Equal(189_600 / 12.0, p.D("monthly_tax"), 2);
        await h.SetComponentsAsync(W.Admin, Basic, []);
    }

    [ReferenceFact]
    public async Task S04_bad_component_is_refused()
    {
        var r = await h.PostAsync($"/compensation/edit-profile/{W.Admin}", ("basic_salary", "150000"), ("effective_from", "2000-01-01"),
            ("components_json", """[{"name": "Odd", "type": "allowance", "method": "percent_of_basic", "value": 250}]"""));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("at most 100", await h.FlashesAsync());
    }

    [ReferenceFact]
    public async Task S05_bonus_is_taxed_once_not_annualised()
    {
        double baseTax = (await h.PreviewAsync(7, 2025)).D("monthly_tax");
        var withBonus = await h.PreviewAsync(7, 2025, Bonus("300000"));
        Assert.Equal(baseTax + 33_000, withBonus.D("monthly_tax"), 2);
        Assert.Equal(B + 300_000, withBonus.D("taxable"));
    }

    [ReferenceFact]
    public async Task S06_withholding_is_cumulative_over_the_tax_year()
    {
        var r = await h.PostAsync("/compensation/run-payroll", ("month", "7"), ("year", "2025"),
                                  ("adjustments", System.Text.Json.JsonSerializer.Serialize(Bonus("300000"))));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.StartsWith("/compensation/run/", r.Headers.Location!.OriginalString);
        await using var db = W.Db();
        var slip = await db.Set<PayrollSlip>().SingleAsync(s => s.UserId == W.Admin);
        Assert.Equal(B + 300_000, Py.F(slip.TaxablePay));
        double julyTax = PayrollMath.IncomeTaxOf(slip.ComponentsJson);
        var aug = await h.PreviewAsync(8, 2025);
        Assert.Equal((105_000 - julyTax) / 11, aug.D("monthly_tax"), 2);
        Assert.Equal(105_000, julyTax + aug.D("monthly_tax") * 11, 0);
    }

    [ReferenceFact]
    public async Task S07_unpaid_leave_and_joiners_prorate_by_calendar_days()
    {
        await using (var db = W.Db())
        {
            var lwp = new LeaveType { Name = "Leave without pay", Code = "LWP", IsPaid = false, RequiresApproval = true, CarryForward = false };
            db.Add(lwp);
            await db.SaveChangesAsync();
            db.Add(new LeaveRequest
            {
                UserId = W.Admin, LeaveTypeId = lwp.Id, StartDate = new(2025, 3, 10), EndDate = new(2025, 3, 12), TotalDays = 3,
                Reason = "personal", Status = "approved", IsHalfDay = false, CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var p = await h.PreviewAsync(3, 2025);
        Assert.Equal(3, p.D("unpaid_days"));
        Assert.Equal(Py.Round(B * 28 / 31, 2), p.D("basic"), 2);
        await SetJoinedAsync(new DateOnly(2025, 4, 21));
        Assert.Equal(B * 10 / 30, (await h.PreviewAsync(4, 2025)).D("basic"), 2);
        Assert.Null(await h.PreviewOrNullAsync(3, 2025));
        await SetJoinedAsync(new DateOnly(2000, 1, 1));
    }

    private async Task SetJoinedAsync(DateOnly d)
    {
        await using var db = W.Db();
        await db.Users.Where(u => u.Id == W.Admin).ExecuteUpdateAsync(s => s.SetProperty(u => u.DateOfJoining, d));
    }

    [ReferenceFact]
    public async Task S08_loan_instalment_never_drives_net_below_zero()
    {
        await using (var db = W.Db())
        {
            db.Add(new LoanAdvanceRequest
            {
                UserId = W.Admin, RequestType = "loan", Amount = 1_000_000, InstallmentMonths = 1, MonthlyInstallment = 1_000_000,
                RemainingAmount = 1_000_000, Purpose = "house", Status = "approved", DisbursedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var p = await h.PreviewAsync(9, 2025);
        Assert.Equal(0, p.D("net"));
        Assert.Equal(p.D("gross") - p.D("monthly_tax") - p.D("pf_employee"), p.D("loan_deduction"), 2);
    }

    [ReferenceFact]
    public async Task S09_payroll_settings_page_validates_and_saves()
    {
        string page = await (await h.C.GetAsync("/compensation/tax-settings")).Content.ReadAsStringAsync();
        Assert.Contains("Payroll settings", page);
        Assert.Contains("Surcharge", page);
        var bad = await h.PostAsync("/compensation/tax-settings", ("action", "policy"), ("overtime_multiplier", "9"),
            ("overtime_hours_basis", "160"), ("eobi_wage_base", "37000"), ("eobi_employee_pct", "1"), ("eobi_employer_pct", "5"),
            ("surcharge_threshold", "10000000"), ("surcharge_pct", "9"));
        Assert.Equal(HttpStatusCode.Redirect, bad.StatusCode);
        Assert.Contains("Overtime rate 1", await h.FlashesAsync());
        var ok = await h.PostAsync("/compensation/tax-settings", ("action", "policy"), ("overtime_multiplier", "2"),
            ("overtime_hours_basis", "208"), ("eobi_enabled", "1"), ("eobi_wage_base", "37000"), ("eobi_employee_pct", "1"),
            ("eobi_employer_pct", "5"), ("surcharge_threshold", "10000000"), ("surcharge_pct", "9"));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        await using var db = W.Db();
        var s = await db.Set<PayrollSetting>().OrderBy(x => x.Id).FirstAsync();
        Assert.Equal(2m, s.OvertimeMultiplier);
        Assert.True(s.EobiEnabled);
        Assert.Equal(1, await db.Set<PayrollSetting>().CountAsync());
    }

    [ReferenceFact]
    public async Task S10_overtime_follows_the_policy()
    {
        await using (var db = W.Db())
        {
            db.Add(new OvertimeAccount { UserId = W.Admin, Date = new(2025, 10, 6), RegularHours = 8, OvertimeHours = 10, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        Assert.Equal(10 * B / 208 * 2, (await h.PreviewAsync(10, 2025)).D("ot_pay"), 2);
    }

    [ReferenceFact]
    public async Task S11_eobi_is_deducted_and_posted()
    {
        await using (var db = W.Db())
            await db.Set<LoanAdvanceRequest>().ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, "rejected"));
        var p = await h.PreviewAsync(11, 2025);
        Assert.Equal(370, p.D("eobi_employee"));
        Assert.Equal(1850, p.D("eobi_employer"));
        Assert.Equal(370, p.D("deductions_breakdown", "EOBI (employee)"));
        var r = await h.PostAsync("/compensation/run-payroll", ("month", "11"), ("year", "2025"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var run = await W.RunAsync(11, 2025);
        var net = await W.VoucherLinesAsync("PRL", run!.Id);
        Assert.Equal(-(370m + 1850m), net["EOBI Payable"]);
        Assert.Equal(1850m, net["EOBI Employer Expense"]);
        Assert.Equal(0m, net.Values.Sum());
        await W.AssertBooksTieAsync("EOBI payroll");
    }

    [ReferenceFact]
    public async Task S12_surcharge_on_high_income()
    {
        await using var db = W.Db();
        var slabs = await PayrollData.SlabsAsync(db);
        var policy = PayrollPolicy.From(await db.Set<PayrollSetting>().OrderBy(x => x.Id).FirstAsync());
        Assert.Equal(PayrollMath.CalculateTax(slabs, 12_000_000) * 1.09, PayrollMath.TaxOn(slabs, policy, 12_000_000), 2);
        Assert.Equal(PayrollMath.CalculateTax(slabs, 9_000_000), PayrollMath.TaxOn(slabs, policy, 9_000_000));
    }

    [ReferenceFact]
    public async Task S13_pf_on_basic_or_gross()
    {
        await h.SetComponentsAsync(W.Admin, Basic, [Harness.Comp("Conveyance", "allowance", "fixed", 10_000, true)]);
        await using (var db = W.Db())
        {
            // ProvidentFundConfig(employee=5, employer=5): new configs default to basic.
            db.Add(new PfConfig
            {
                EmployeeContributionPct = 5, EmployerContributionPct = 5, ContributionBase = "basic", MaxLoanPercentage = 50,
                InterestRate = 0, MinServiceMonthsForLoan = 12, IsActive = true, UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        Assert.Equal(B * 0.05, (await h.PreviewAsync(12, 2025)).D("pf_employee"));
        await h.PostAsync("/pf/config", ("employee_pct", "5"), ("employer_pct", "5"), ("contribution_base", "gross"),
                          ("max_loan_pct", "50"), ("interest_rate", "0"), ("min_months", "12"));
        var p = await h.PreviewAsync(12, 2025);
        Assert.Equal(p.D("gross") * 0.05, p.D("pf_employee"), 2);
        await h.SetComponentsAsync(W.Admin, Basic, []);
    }

    private async Task SetLeavingAsync(DateOnly? d)
    {
        await using var db = W.Db();
        await db.Memberships.Where(m => m.CompanyId == W.Company && m.UserId == W.Admin)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.DateOfLeaving, d));
    }

    [ReferenceFact]
    public async Task S14_leaving_date_prorates_and_then_stops()
    {
        await SetLeavingAsync(new DateOnly(2026, 1, 10));
        Assert.Equal(B * 10 / 31, (await h.PreviewAsync(1, 2026)).D("basic"), 2);
        Assert.Null(await h.PreviewOrNullAsync(2, 2026));
        await SetLeavingAsync(null);
        Assert.NotNull(await h.PreviewOrNullAsync(2, 2026));
    }

    [ReferenceFact]
    public async Task S15_final_settlement_gratuity_and_leave_encashment()
    {
        var r = await h.PostAsync("/compensation/tax-settings", ("action", "policy"), ("overtime_multiplier", "2"),
            ("overtime_hours_basis", "208"), ("eobi_wage_base", "37000"), ("eobi_employee_pct", "1"), ("eobi_employer_pct", "5"),
            ("surcharge_threshold", "10000000"), ("surcharge_pct", "9"), ("gratuity_enabled", "1"), ("gratuity_days_per_year", "30"),
            ("gratuity_basis", "basic"), ("leave_encashment_enabled", "1"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        int al = await W.LeaveTypeAsync("Annual leave", "AL", paid: true, carryForward: true);
        await using (var db = W.Db())
        {
            db.Add(new LeaveQuota { UserId = W.Admin, LeaveTypeId = al, Year = 2026, Total = 14, Used = 4, Pending = 0, Remaining = 10 });
            await db.SaveChangesAsync();
        }
        await SetLeavingAsync(new DateOnly(2026, 3, 15));
        var p = await h.PreviewAsync(3, 2026);
        Assert.Equal(B * 26, p.D("settlement", "Gratuity"));
        Assert.Equal(B / 30 * 10, p.D("settlement", "Leave encashment"), 2);
        Assert.Equal(B * 15 / 31, p.D("basic"), 2);
        Assert.Empty((await h.PreviewAsync(2, 2026)).GetProperty("settlement").EnumerateObject());
        r = await h.PostAsync("/compensation/run-payroll", ("month", "3"), ("year", "2026"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var run = await W.RunAsync(3, 2026);
        var dr = await W.VoucherLinesAsync("PRL", run!.Id);
        Assert.Equal(Basic * 26, dr["Gratuity Expense"]);
        Assert.Equal(50_000m, dr["Leave Encashment Expense"]);
        Assert.Equal(run.TotalGross!.Value - Basic * 26 - 50_000m, dr["Salary Expense"]);
        Assert.Equal(0m, dr.Values.Sum());
        await W.AssertBooksTieAsync("final settlement");
        await SetLeavingAsync(null);
    }
}
