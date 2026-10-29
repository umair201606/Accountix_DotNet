using System.Net;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Payroll.Tests;

/// <summary>
/// The accounts payroll creates without a fixed slot (EOBI Payable 2125, EOBI Employer
/// Expense 5123, Gratuity 5124, Leave Encashment 5125, PF Profit Credited 5126) are
/// parked under the catch-all heads and found again by (parent, name, type). Before
/// 29 Oct every run and every profit distribution made a new set. One company runs
/// four payroll months (July and August each with a leaver, so both post gratuity and
/// leave encashment) and two PF profit distributions: each name must exist once, and
/// every journal must post to the same account ids. (Until the Python app was removed
/// the two apps took turns here, Python running July and September and the second
/// distribution; the lookup key is still Python's.)
/// </summary>
public sealed class SharedAccountTests
{
    private static readonly string[] Parked =
        ["EOBI Payable", "EOBI Employer Expense", "Gratuity Expense", "Leave Encashment Expense", "PF Profit Credited to Members"];

    private static readonly string[] Fixed =
        ["Salary Expense", "Salary Payable", "Income Tax Payable", "PF Payable", "PF Employer Expense", "Other Payroll Deductions Payable"];

    [ReferenceFact]
    public async Task Four_runs_and_two_profit_shares_use_one_set_of_payroll_accounts()
    {
        var w = await World.CreateAsync("Shared Accounts Co", [new("Kamal Leaver", "2010-03-17"), new("Nadia Leaver", "2012-06-01")]);
        int kamal = w.Employees[0], nadia = w.Employees[1];
        await w.ProfileAsync(w.Admin, 150_000, "2000-01-01", ("House Rent", "allowance", "percent_of_basic", 40m, true),
                             ("Welfare fund", "deduction", "fixed", 500m, false));
        await w.ProfileAsync(kamal, 260_000, "2010-03-17", ("House Rent", "allowance", "percent_of_basic", 45m, true));
        await w.ProfileAsync(nadia, 180_000, "2012-06-01");
        int al = await w.LeaveTypeAsync("Annual leave", "AL", paid: true, carryForward: true);
        await using (var db = w.Db())
        {
            db.Add(new PfConfig { EmployeeContributionPct = 5, EmployerContributionPct = 5, ContributionBase = "basic", MaxLoanPercentage = 50,
                                  InterestRate = 0, MinServiceMonthsForLoan = 0, IsActive = true, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            db.Add(new LeaveQuota { UserId = kamal, LeaveTypeId = al, Year = 2025, Total = 14, Used = 9, Pending = 0, Remaining = 5 });
            db.Add(new LeaveQuota { UserId = nadia, LeaveTypeId = al, Year = 2025, Total = 14, Used = 6.5m, Pending = 0, Remaining = 7.5m });
            await db.SaveChangesAsync();
            await db.Memberships.Where(m => m.CompanyId == w.Company && m.UserId == kamal)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.DateOfLeaving, new DateOnly(2025, 7, 20)));
            await db.Memberships.Where(m => m.CompanyId == w.Company && m.UserId == nadia)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.DateOfLeaving, new DateOnly(2025, 8, 22)));
        }
        await using var host = await ModuleHost.StartAsync(typeof(PayrollModule).Assembly, typeof(Accountix.Accounting.AccountingModule).Assembly);
        var c = host.ClientFor(w.Admin, w.Company);
        string token = await c.TokenFromAsync("/compensation/");
        async Task<HttpResponseMessage> Post(string url, params (string, string)[] f) =>
            await c.PostFormWithTokenAsync(url, f.Select(x => new KeyValuePair<string, string>(x.Item1, x.Item2)), token);

        // Slabs, policy (EOBI, gratuity on gross, leave encashment), July (Kamal leaves), August (Nadia leaves),
        // a profit distribution, a second one, September and October.
        Assert.Equal(HttpStatusCode.Redirect, (await Post("/compensation/tax-settings", ("action", "seed_defaults"))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Post("/compensation/tax-settings", ParityHarness.Policy)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Post("/compensation/run-payroll", ("month", "7"), ("year", "2025"))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Post("/compensation/run-payroll", ("month", "8"), ("year", "2025"))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Post("/pf/distribute-profit", ("month", "8"), ("year", "2025"), ("total_profit", "700"))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Post("/pf/distribute-profit", ("month", "9"), ("year", "2025"), ("total_profit", "500"))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Post("/compensation/run-payroll", ("month", "9"), ("year", "2025"))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Post("/compensation/run-payroll", ("month", "10"), ("year", "2025"))).StatusCode);

        await using var check = w.Db();
        foreach (string name in Parked.Concat(Fixed))
            Assert.True(await check.Set<ChartOfAccount>().CountAsync(a => a.Name == name && a.Level >= 5) == 1,
                        $"{name}: {string.Join(", ", await check.Set<ChartOfAccount>().Where(a => a.Name == name).Select(a => a.Code).ToListAsync())}");

        // Every journal of either app hits the one account of each name.
        async Task<Dictionary<string, HashSet<int>>> IdsAsync(string type, int month)
        {
            var rows = await check.Set<JournalLine>()
                .Where(l => l.JournalEntry.VoucherType == type && l.JournalEntry.IsPosted == true
                            && (type != "PRL" || l.JournalEntry.EntryDate.Month == month)
                            && (type != "PF-PROFIT" || l.JournalEntry.VoucherNumber.StartsWith($"PF-PROFIT-2025{month:00}")))
                .Select(l => new { l.Account.Name, l.AccountId }).ToListAsync();
            return rows.GroupBy(r => r.Name).ToDictionary(g => g.Key, g => g.Select(r => r.AccountId).ToHashSet());
        }
        var july = await IdsAsync("PRL", 7);
        var august = await IdsAsync("PRL", 8);
        var september = await IdsAsync("PRL", 9);
        var october = await IdsAsync("PRL", 10);
        foreach (string name in new[] { "EOBI Payable", "EOBI Employer Expense", "Gratuity Expense", "Leave Encashment Expense" }.Concat(Fixed))
        {
            Assert.True(july.ContainsKey(name) && august.ContainsKey(name), $"{name} posted by both July and August");
            Assert.Equal(july[name], august[name]);
            Assert.Single(july[name]);
        }
        foreach (string name in new[] { "EOBI Payable", "EOBI Employer Expense", "Salary Expense", "Salary Payable" })
        {
            Assert.Equal(july[name], september[name]);
            Assert.Equal(july[name], october[name]);
        }
        var first = await IdsAsync("PF-PROFIT", 8);
        var second = await IdsAsync("PF-PROFIT", 9);
        Assert.Single(first["PF Profit Credited to Members"]);
        Assert.Equal(first["PF Profit Credited to Members"], second["PF Profit Credited to Members"]);
        Assert.Equal(first["PF Payable"], second["PF Payable"]);
        Assert.Equal(july["PF Payable"], first["PF Payable"]);
        Assert.Equal(1_200m, await w.BalanceAsync("PF Profit Credited to Members"));
        await w.AssertBooksTieAsync("four payroll months and two PF profit shares in one company");
    }
}
