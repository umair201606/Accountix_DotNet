using System.Net;
using System.Text.Json;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Payroll.Tests;

public sealed class SanityHarness : Harness
{
    public int Loan { get; set; }
    public int PfLoan { get; set; }
    public int July { get; set; }
    public int August { get; set; }

    protected override Task<World> BuildAsync() => World.CreateAsync("Sanity Co", adminJoined: "2020-01-01");
    protected override Task SeedAsync() => W.ProfileAsync(W.Admin, PayrollLedgerSanityTests.Basic, "2020-01-01");

    /// <summary>HR too: loan approval and the raise go through its MSS routes.</summary>
    protected override System.Reflection.Assembly[] Modules => [.. base.Modules, typeof(Accountix.Hr.HrModule).Assembly];
}

/// <summary>
/// The payroll half of tests/e2e/test_payroll_fa_ledger_sanity.py: every flow an
/// accountant lives through, the exact accounts it moves, and the Books
/// Integrity (TB, journals, payroll runs = salary journals, staff loans = loan
/// accounts, PF ledger = PF Payable; Executive's port) after each step. Loan approval
/// and the raise go through HR's MSS routes, booted alongside Payroll.
/// </summary>
[TestCaseOrderer(StepOrderer.Name, StepOrderer.Assembly)]
public sealed class PayrollLedgerSanityTests(SanityHarness h) : IClassFixture<SanityHarness>
{
    public const decimal Basic = 200_000;
    private const double B = 200_000;
    private World W => h.W;
    private string Code => W.Codes[0];

    [ReferenceFact]
    public async Task P01_settings_and_salary_package()
    {
        await h.PostAsync("/compensation/tax-settings", ("action", "seed_defaults"));
        await h.PostAsync("/compensation/tax-settings", ("action", "policy"), ("overtime_multiplier", "1.5"), ("overtime_hours_basis", "160"),
            ("eobi_enabled", "1"), ("eobi_wage_base", "37000"), ("eobi_employee_pct", "1"), ("eobi_employer_pct", "5"),
            ("surcharge_threshold", "10000000"), ("surcharge_pct", "9"));
        await h.PostAsync("/pf/config", ("employee_pct", "5"), ("employer_pct", "5"), ("contribution_base", "basic"),
            ("max_loan_pct", "50"), ("interest_rate", "0"), ("min_months", "0"));
        await h.SetComponentsAsync(W.Admin, Basic,
        [
            Harness.Comp("House Rent", "allowance", "percent_of_basic", 40, true),
            Harness.Comp("Medical", "allowance", "percent_of_basic", 10, false),
        ], "2020-01-01");
        await W.AssertBooksTieAsync("setup");
    }

    [ReferenceFact]
    public async Task P02_staff_loan_is_paid_out_before_it_is_recovered()
    {
        await using (var db = W.Db())
        {
            var loan = new LoanAdvanceRequest
            {
                UserId = W.Admin, RequestType = "loan", Amount = 120_000, Purpose = "Car", InstallmentMonths = 12,
                MonthlyInstallment = 10_000, RemainingAmount = 120_000, Status = "pending", CreatedAt = DateTime.UtcNow,
            };
            db.Add(loan);
            await db.SaveChangesAsync();
            h.Loan = loan.Id;
        }
        Assert.Equal(HttpStatusCode.Redirect, (await h.PostAsync($"/mss/approve-loan/{h.Loan}", ("action", "approve"))).StatusCode);
        await using (var db = W.Db())
            Assert.Equal("approved", (await db.Set<LoanAdvanceRequest>().SingleAsync(l => l.Id == h.Loan)).Status);
        Assert.Equal(0, (await h.PreviewAsync(7, 2025)).D("loan_deduction"));
        string page = await (await h.C.GetAsync("/compensation/loans")).Content.ReadAsStringAsync();
        Assert.Contains("Pay out", page);
        var r = await h.PostAsync($"/compensation/loans/{h.Loan}/disburse", ("account_id", W.Cash.ToString()), ("paid_on", "2025-07-05"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var lines = await W.VoucherLinesAsync("LOAN-DSB", h.Loan);
        Assert.Equal(-120_000m, lines["Main Cash"]);
        Assert.Equal(2, lines.Count);
        Assert.Equal(0m, lines.Values.Sum());
        // Paid out twice: refused.
        await h.PostAsync($"/compensation/loans/{h.Loan}/disburse", ("account_id", W.Cash.ToString()), ("paid_on", "2025-07-05"));
        Assert.Contains("already been paid out", await h.FlashesAsync());
        await W.AssertBooksTieAsync("loan paid out");
    }

    [ReferenceFact]
    public async Task P03_payroll_run_journal_account_by_account()
    {
        var p = await h.PreviewAsync(7, 2025);
        Assert.Equal(B * 1.5, p.D("gross"));
        Assert.Equal(B * 1.4, p.D("taxable"));
        Assert.Equal(B * 0.05, p.D("pf_employee"));
        Assert.Equal(370, p.D("eobi_employee"));
        Assert.Equal(10_000, p.D("loan_deduction"));
        var r = await h.PostAsync("/compensation/run-payroll", ("month", "7"), ("year", "2025"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("/compensation/run/", r.Headers.Location!.OriginalString);
        var run = (await W.RunAsync(7, 2025))!;
        var lines = await W.VoucherLinesAsync("PRL", run.Id);
        Assert.Equal(run.TotalGross, lines["Salary Expense"]);
        Assert.Equal(-run.TotalNet, lines["Salary Payable"]);
        Assert.Equal(-(decimal)p.D("monthly_tax"), lines["Income Tax Payable"]);
        Assert.Equal(-(Basic * 0.10m), lines["PF Payable"]);
        Assert.Equal(Basic * 0.05m, lines["PF Employer Expense"]);
        Assert.Equal(-(370m + 1850m), lines["EOBI Payable"]);
        Assert.Equal(1850m, lines["EOBI Employer Expense"]);
        Assert.Equal([-10_000m], lines.Where(kv => kv.Key.StartsWith("Loan #")).Select(kv => kv.Value).ToList());
        Assert.Equal(0m, lines.Values.Sum());
        h.July = run.Id;
        await W.AssertBooksTieAsync("July payroll");
    }

    [ReferenceFact]
    public async Task P04_salary_payment_clears_salary_payable()
    {
        var run = (await W.RunAsync(7, 2025))!;
        string page = await (await h.C.GetAsync($"/compensation/run/{h.July}")).Content.ReadAsStringAsync();
        Assert.Contains("Record payment", page);
        Assert.Contains(Code, page);
        var r = await h.PostAsync($"/compensation/run/{h.July}/pay", ("account_id", W.Cash.ToString()), ("paid_on", "2025-08-01"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(new Dictionary<string, decimal> { ["Salary Payable"] = run.TotalNet!.Value, ["Main Cash"] = -run.TotalNet!.Value },
                     await W.VoucherLinesAsync("PRL-PAY", h.July));
        Assert.Equal(0m, await W.BalanceAsync("Salary Payable"));
        Assert.NotNull((await W.RunAsync(7, 2025))!.PaidAt);
        // A paid run cannot be replaced or deleted until the payment is undone.
        await h.PostAsync($"/compensation/run/{h.July}/delete");
        Assert.Contains("Undo the salary payment", await h.FlashesAsync());
        await h.PostAsync("/compensation/run-payroll", ("month", "7"), ("year", "2025"), ("replace_all", "1"));
        Assert.Contains("already paid", await h.FlashesAsync());
        Assert.Equal(h.July, (await W.RunAsync(7, 2025))!.Id);
        // Paying twice is refused.
        await h.PostAsync($"/compensation/run/{h.July}/pay", ("account_id", W.Cash.ToString()), ("paid_on", "2025-08-01"));
        Assert.Contains("already been paid", await h.FlashesAsync());
        await W.AssertBooksTieAsync("salaries paid");
    }

    [ReferenceFact]
    public async Task P05_bank_sheet_and_slip_agree_with_the_ledger()
    {
        var x = await h.C.GetAsync($"/compensation/export-bank-ledger/{h.July}");
        Assert.Equal("bank_ledger_7_2025.xlsx", x.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        using var wb = new XLWorkbook(new MemoryStream(await x.Content.ReadAsByteArrayAsync()));
        var ws = wb.Worksheet(1);
        Assert.Equal("Payroll_7_2025", ws.Name);
        Assert.Equal(Code, ws.Cell(2, 1).GetString());
        int last = ws.LastRowUsed()!.RowNumber();
        Assert.Equal("Total", ws.Cell(last, 5).GetString());
        Assert.Equal((double)(await W.RunAsync(7, 2025))!.TotalNet!.Value, ws.Cell(last, 6).GetDouble(), 2);
        await using var db = W.Db();
        int sid = (await db.Set<PayrollSlip>().SingleAsync(s => s.PayrollRunId == h.July)).Id;
        Assert.Contains(Code, await (await h.C.GetAsync($"/compensation/slip/{sid}")).Content.ReadAsStringAsync());
        Assert.Equal("%PDF"u8.ToArray(), (await (await h.C.GetAsync($"/compensation/slip/{sid}/pdf")).Content.ReadAsByteArrayAsync())[..4]);
    }

    [ReferenceFact]
    public async Task P06_pf_loan_paid_from_the_fund_and_recovered_by_payroll()
    {
        await using (var db = W.Db())
        {
            var pfl = new PfLoanRequest
            {
                UserId = W.Admin, Amount = 15_000, InstallmentMonths = 5, Purpose = "Fees", MonthlyInstallment = 3_000,
                RemainingAmount = 15_000, Status = "pending", CreatedAt = DateTime.UtcNow,
            };
            db.Add(pfl);
            await db.SaveChangesAsync();
            h.PfLoan = pfl.Id;
        }
        await h.PostAsync($"/pf/approve-loan/{h.PfLoan}");
        Assert.Contains("Choose the bank or cash account", await h.FlashesAsync("/pf/"));
        await h.PostAsync($"/pf/approve-loan/{h.PfLoan}", ("account_id", W.Cash.ToString()), ("paid_on", "2025-08-10"));
        Assert.Equal(new Dictionary<string, decimal> { ["PF Payable"] = 15_000, ["Main Cash"] = -15_000 }, await W.VoucherLinesAsync("PF-LOAN", h.PfLoan));
        await W.AssertBooksTieAsync("PF loan paid out");
        await h.PostAsync("/compensation/run-payroll", ("month", "8"), ("year", "2025"));
        var run = (await W.RunAsync(8, 2025))!;
        Assert.Equal(-(Basic * 0.10m + 3_000), (await W.VoucherLinesAsync("PRL", run.Id))["PF Payable"]);
        await using (var db = W.Db())
            Assert.Equal(12_000m, (await db.Set<PfLoanRequest>().SingleAsync(l => l.Id == h.PfLoan)).RemainingAmount);
        h.August = run.Id;
        await W.AssertBooksTieAsync("August payroll with PF loan recovery");
    }

    [ReferenceFact]
    public async Task P07_deleting_a_run_takes_everything_back_out()
    {
        var r = await h.PostAsync($"/compensation/run/{h.August}/delete");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Null(await W.RunAsync(8, 2025));
        Assert.Empty(await W.VoucherLinesAsync("PRL", h.August));
        await using (var db = W.Db())
        {
            Assert.Equal(110_000m, (await db.Set<LoanAdvanceRequest>().SingleAsync(l => l.Id == h.Loan)).RemainingAmount);
            Assert.Equal(15_000m, (await db.Set<PfLoanRequest>().SingleAsync(l => l.Id == h.PfLoan)).RemainingAmount);
            Assert.Equal(0, await db.Set<PfLedger>().CountAsync(e => e.PayrollRunId == h.August));
            Assert.Equal(1, await db.Set<PayrollAuditLog>().CountAsync(a => a.Action == "payroll_deleted"));
            Assert.True(await db.Set<PayrollAuditLog>().AnyAsync(a => a.Action == "payroll_run" && a.PayrollRunId == null));
        }
        await W.AssertBooksTieAsync("August run deleted");
        await h.PostAsync("/compensation/run-payroll", ("month", "8"), ("year", "2025"));
        await W.AssertBooksTieAsync("August re-run");
    }

    [ReferenceFact]
    public async Task P08_raise_mid_month_is_split_and_old_months_keep_the_old_rate()
    {
        Assert.Equal(HttpStatusCode.Redirect, (await h.PostAsync($"/mss/evaluate/{W.Admin}", ("new_basic", "260000"), ("reason", "Promotion"),
                                                                 ("effective_from", "2025-09-16"))).StatusCode);
        await using (var db = W.Db())
            Assert.Equal(260_000m, (await db.Set<SalaryRevision>().SingleAsync(r => r.UserId == W.Admin && r.Reason == "Promotion")).NewBasic);
        Assert.Equal((B * 15 + 260_000 * 15) / 30, (await h.PreviewAsync(9, 2025)).D("basic"), 2);
        Assert.Equal(B, (await h.PreviewAsync(7, 2025)).D("basic"));
        var oct = await h.PreviewAsync(10, 2025);
        Assert.Equal(260_000, oct.D("basic"));
        Assert.Equal(104_000, oct.D("allowances_breakdown", "House Rent"));
        string revisions = await (await h.C.GetAsync("/compensation/revisions")).Content.ReadAsStringAsync();
        Assert.Contains("Promotion", revisions);
    }

    [ReferenceFact]
    public async Task P09_pf_withdrawal_profit_and_settlement_move_pf_payable()
    {
        await h.PostAsync("/pf/request-withdrawal", ("amount", "5000"), ("reason", "Medical"));
        int wid;
        await using (var db = W.Db())
            wid = await db.Set<PfWithdrawalRequest>().MaxAsync(w => w.Id);
        await h.PostAsync($"/pf/approve-withdrawal/{wid}", ("account_id", W.Cash.ToString()), ("paid_on", "2025-09-05"));
        Assert.Equal(new Dictionary<string, decimal> { ["PF Payable"] = 5_000, ["Main Cash"] = -5_000 }, await W.VoucherLinesAsync("PF-WD", wid));
        await h.PostAsync("/pf/distribute-profit", ("month", "9"), ("year", "2025"), ("total_profit", "2500"));
        Assert.Equal(2_500m, await W.BalanceAsync("PF Profit Credited to Members"));
        await W.AssertBooksTieAsync("PF withdrawal and profit");
        var r = await h.PostAsync($"/pf/settle/{W.Admin}", ("account_id", W.Cash.ToString()), ("paid_on", "2025-09-30"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(0m, await W.BalanceAsync("PF Payable"));
        await W.AssertBooksTieAsync("PF settled");
    }

    [ReferenceFact]
    public async Task P10_undo_payment_then_undo_the_loan_pay_out_is_refused_after_a_recovery()
    {
        var r = await h.PostAsync($"/compensation/run/{h.July}/unpay");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Empty(await W.VoucherLinesAsync("PRL-PAY", h.July));
        Assert.Equal("completed", (await W.RunAsync(7, 2025))!.Status);
        await h.PostAsync($"/compensation/loans/{h.Loan}/undo-disbursement");
        Assert.Contains("Instalments have already been recovered", await h.FlashesAsync());
        await W.AssertBooksTieAsync("payment undone");
    }
}
