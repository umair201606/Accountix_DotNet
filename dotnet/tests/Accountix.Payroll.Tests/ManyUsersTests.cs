using System.Net;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Payroll.Tests;

/// <summary>
/// Several admins pressing the same button at the same instant (tests/concurrency/
/// test_concurrent_users.py and the routes' serialize/for_update): one payroll run
/// per month, one salary payment, PF never overdrawn, a loan paid out once. (The race
/// between the Python app and .NET on one month was dropped with the Python app; the
/// advisory key is still Python's, see docs/modules/payroll.md.)
/// </summary>
public sealed class ManyUsersTests
{
    private const int N = 6;

    private static async Task<(World W, ModuleHost Host)> SetUpAsync(string name)
    {
        var w = await World.CreateAsync(name, [new World.Person("Saad Saver", "2015-01-01")]);
        await w.ProfileAsync(w.Admin, 60_000);
        await w.ProfileAsync(w.Employees[0], 40_000, "2015-01-01", ("House Rent", "allowance", "percent_of_basic", 25m, true));
        var host = await ModuleHost.StartAsync(typeof(PayrollModule).Assembly, typeof(Accountix.Accounting.AccountingModule).Assembly);
        return (w, host);
    }

    private static async Task<HttpResponseMessage[]> RaceAsync(ModuleHost host, World w, string url, params (string Key, string Value)[] fields)
    {
        var clients = host.ClientsFor(w.Admin, w.Company, N);
        var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync("/compensation/")));
        var form = fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)).ToList();
        var rs = await Race.RunAsync(N, i => clients[i].PostFormWithTokenAsync(url, form, tokens[i]));
        Race.NoServerErrors(rs);
        return rs;
    }

    [ReferenceFact]
    public async Task Six_admins_running_one_month_at_once_run_it_once()
    {
        var (w, host) = await SetUpAsync("Race Run Co");
        await using var _ = host;
        await RaceAsync(host, w, "/compensation/run-payroll", ("month", "9"), ("year", "2025"));
        await using var db = w.Db();
        Assert.Equal(1, await db.Set<PayrollRun>().CountAsync(r => r.Month == 9 && r.Year == 2025));
        int runId = (await w.RunAsync(9, 2025))!.Id;
        Assert.Equal(2, await db.Set<PayrollSlip>().CountAsync(s => s.PayrollRunId == runId));
        Assert.Equal(1, await db.Set<JournalEntry>().CountAsync(e => e.VoucherType == "PRL" && e.VoucherId == runId && e.IsPosted == true));
        await w.AssertBooksTieAsync("simultaneous payroll runs");
    }

    [ReferenceFact]
    public async Task Six_record_payment_clicks_post_one_payment()
    {
        var (w, host) = await SetUpAsync("Race Pay Co");
        await using var _ = host;
        var c = host.ClientFor(w.Admin, w.Company);
        await c.PostFormWithTokenAsync("/compensation/run-payroll", [new("month", "9"), new("year", "2025")], await c.TokenFromAsync("/compensation/"));
        int runId = (await w.RunAsync(9, 2025))!.Id;
        await RaceAsync(host, w, $"/compensation/run/{runId}/pay", ("account_id", w.Cash.ToString()), ("paid_on", "2025-10-01"));
        await using var db = w.Db();
        Assert.Equal(1, await db.Set<JournalEntry>().CountAsync(e => e.VoucherType == "PRL-PAY" && e.VoucherId == runId && e.IsPosted == true));
        Assert.Equal(0m, await w.BalanceAsync("Salary Payable"));
        Assert.Equal(1, await db.Set<PayrollAuditLog>().CountAsync(a => a.Action == "salary_paid"));
        await w.AssertBooksTieAsync("simultaneous salary payments");
    }

    [ReferenceFact]
    public async Task Six_withdrawals_approved_at_once_never_overdraw_the_member()
    {
        var (w, host) = await SetUpAsync("Race PF Co");
        await using var _ = host;
        int saver = w.Employees[0];
        var ids = new List<int>();
        await using (var db = w.Db())
        {
            db.Add(new PfLedger { UserId = saver, TransactionDate = new(2025, 6, 30), TransactionType = "contribution", Description = "seed",
                                  Credit = 10_000, Debit = 0, Balance = 0, CreatedAt = DateTime.UtcNow });
            var reqs = Enumerable.Range(0, N).Select(_ => new PfWithdrawalRequest
            {
                UserId = saver, Amount = 3_000, Reason = "race", Status = "pending", CreatedAt = DateTime.UtcNow,
            }).ToList();
            db.AddRange(reqs);
            await db.SaveChangesAsync();
            ids.AddRange(reqs.Select(r => r.Id));
        }
        var clients = host.ClientsFor(w.Admin, w.Company, N);
        var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync("/pf/")));
        var rs = await Race.RunAsync(N, i => clients[i].PostFormWithTokenAsync($"/pf/approve-withdrawal/{ids[i]}",
            [new("account_id", w.Cash.ToString()), new("paid_on", "2025-07-05")], tokens[i]));
        Race.NoServerErrors(rs);
        await using var check = w.Db();
        Assert.Equal(3, await check.Set<PfWithdrawalRequest>().CountAsync(r => r.Status == "approved"));
        Assert.Equal(1_000m, await check.Set<PfLedger>().Where(r => r.UserId == saver).SumAsync(r => (r.Credit ?? 0) - (r.Debit ?? 0)));
        Assert.Equal(3, await check.Set<JournalEntry>().CountAsync(e => e.VoucherType == "PF-WD" && e.IsPosted == true));
    }

    [ReferenceFact]
    public async Task A_loan_paid_out_by_six_admins_at_once_is_paid_once()
    {
        var (w, host) = await SetUpAsync("Race Loan Co");
        await using var _ = host;
        int lid;
        await using (var db = w.Db())
        {
            var loan = new LoanAdvanceRequest { UserId = w.Employees[0], RequestType = "loan", Amount = 24_000, InstallmentMonths = 12,
                                                MonthlyInstallment = 2_000, RemainingAmount = 24_000, Purpose = "bike", Status = "approved",
                                                CreatedAt = DateTime.UtcNow };
            db.Add(loan);
            await db.SaveChangesAsync();
            lid = loan.Id;
        }
        await RaceAsync(host, w, $"/compensation/loans/{lid}/disburse", ("account_id", w.Cash.ToString()), ("paid_on", "2025-07-05"));
        await using var check = w.Db();
        Assert.Equal(1, await check.Set<JournalEntry>().CountAsync(e => e.VoucherType == "LOAN-DSB" && e.VoucherId == lid && e.IsPosted == true));
        Assert.Equal(1, await check.Set<Notification>().CountAsync(n => n.Title == "Loan Paid"));
        Assert.Equal(-24_000m, await w.BalanceAsync("Main Cash"));
        await w.AssertBooksTieAsync("simultaneous loan pay-outs");
    }
}
