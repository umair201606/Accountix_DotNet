using System.Net;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Payroll.Tests;

public sealed class PfHarness : Harness
{
    public int A => W.Employees[0];
    public int Bm => W.Employees[1];
    public int Cm => W.Employees[2];
    public PageClient Asma { get; private set; } = null!;

    protected override Task<World> BuildAsync() => World.CreateAsync("Fund Co",
    [
        new("Asma Saver", "2015-01-01"), new("Bilal Saver", "2015-01-01"), new("Chand Overdrawn", "2015-01-01"),
    ]);

    protected override async Task SeedAsync()
    {
        await using var db = W.Db();
        db.Add(new PfConfig
        {
            EmployeeContributionPct = 5, EmployerContributionPct = 5, ContributionBase = "basic", MaxLoanPercentage = 50,
            InterestRate = 0, MinServiceMonthsForLoan = 0, IsActive = true, UpdatedAt = DateTime.UtcNow,
        });
        foreach (var (u, amt, credit) in new[] { (A, 10000m, true), (Bm, 5000m, true), (Cm, 100m, false) })
            db.Add(new PfLedger
            {
                UserId = u, TransactionDate = new(2026, 1, 31), TransactionType = "contribution", Description = "seed",
                Credit = credit ? amt : 0, Debit = credit ? 0 : amt, Balance = 0, CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
    }

    public async Task<PageClient> AsmaAsync() => Asma ??= Host.ClientFor(A, W.Company);
}

/// <summary>tests/e2e/test_pf_flow.py through the .NET routes: every movement leaves the member's balance right.</summary>
[TestCaseOrderer(StepOrderer.Name, StepOrderer.Assembly)]
public sealed class PfFlowTests(PfHarness h) : IClassFixture<PfHarness>
{
    private World W => h.W;
    private (string, string)[] Pay => [("account_id", W.Cash.ToString()), ("paid_on", "2026-06-30")];

    private async Task<decimal> BalAsync(int user)
    {
        await using var db = W.Db();
        return await db.Set<PfLedger>().Where(r => r.UserId == user).SumAsync(r => (r.Credit ?? 0) - (r.Debit ?? 0));
    }

    private async Task<(int Id, string? Status)?> LatestAsync<T>() where T : class
    {
        await using var db = W.Db();
        var row = await db.Set<T>().OrderByDescending(e => EF.Property<int>(e, "Id")).FirstOrDefaultAsync();
        return row is null ? null : (db.Entry(row).Property<int>("Id").CurrentValue, db.Entry(row).Property<string?>("Status").CurrentValue);
    }

    private async Task<HttpResponseMessage> AsmaPostAsync(string url, params (string, string)[] fields)
    {
        var c = await h.AsmaAsync();
        return await c.PostFormWithTokenAsync(url, fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)), await c.TokenFromAsync("/pf/"));
    }

    [ReferenceFact]
    public async Task S01_withdrawal_over_the_balance_is_refused()
    {
        await AsmaPostAsync("/pf/request-withdrawal", ("amount", "20000"), ("reason", "car"));
        Assert.Null(await LatestAsync<PfWithdrawalRequest>());
    }

    [ReferenceFact]
    public async Task S02_withdrawal_approves_once()
    {
        await AsmaPostAsync("/pf/request-withdrawal", ("amount", "3000"), ("reason", "fees"));
        var (wid, status) = (await LatestAsync<PfWithdrawalRequest>())!.Value;
        Assert.Equal("pending", status);
        var r = await h.PostAsync($"/pf/approve-withdrawal/{wid}");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("Choose the bank or cash account", await h.FlashesAsync("/pf/"));
        Assert.Equal(10000m, await BalAsync(h.A));
        r = await h.PostAsync($"/pf/approve-withdrawal/{wid}", Pay);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(7000m, await BalAsync(h.A));
        Assert.Equal(new Dictionary<string, decimal> { ["PF Payable"] = 3000, ["Main Cash"] = -3000 }, await W.VoucherLinesAsync("PF-WD", wid));
        await h.PostAsync($"/pf/approve-withdrawal/{wid}", Pay);
        Assert.Equal(7000m, await BalAsync(h.A));
    }

    [ReferenceFact]
    public async Task S03_loan_debits_the_fund()
    {
        await AsmaPostAsync("/pf/request-loan", ("amount", "2000"), ("installments", "0"), ("purpose", "x"));
        Assert.Null(await LatestAsync<PfLoanRequest>());
        await AsmaPostAsync("/pf/request-loan", ("amount", "2000"), ("installments", "10"), ("purpose", "roof"));
        var (lid, status) = (await LatestAsync<PfLoanRequest>())!.Value;
        Assert.Equal("pending", status);
        await h.PostAsync($"/pf/approve-loan/{lid}", Pay);
        Assert.Equal(5000m, await BalAsync(h.A));
        await h.PostAsync($"/pf/approve-loan/{lid}", Pay);
        Assert.Equal(5000m, await BalAsync(h.A));
        await using var db = W.Db();
        var loan = await db.Set<PfLoanRequest>().FirstAsync(l => l.Id == lid);
        Assert.Equal(200m, loan.MonthlyInstallment);
        Assert.Equal(2000m, loan.RemainingAmount);
    }

    [ReferenceFact]
    public async Task S04_reject_leaves_the_balance()
    {
        await AsmaPostAsync("/pf/request-withdrawal", ("amount", "1000"), ("reason", "trip"));
        var (wid, _) = (await LatestAsync<PfWithdrawalRequest>())!.Value;
        await h.PostAsync($"/pf/approve-withdrawal/{wid}", ("action", "reject"));
        Assert.Equal("rejected", (await LatestAsync<PfWithdrawalRequest>())!.Value.Status);
        Assert.Equal(5000m, await BalAsync(h.A));
    }

    [ReferenceFact]
    public async Task S05_profit_shares_add_up_and_skip_negative_balances()
    {
        var before = new[] { h.A, h.Bm, h.Cm }.ToDictionary(u => u, u => BalAsync(u).Result);
        var r = await h.PostAsync("/pf/distribute-profit", ("month", "6"), ("year", "2026"), ("total_profit", "1000"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var gained = before.ToDictionary(kv => kv.Key, kv => BalAsync(kv.Key).Result - kv.Value);
        Assert.Equal(0m, gained[h.Cm]);
        Assert.Equal(500m, gained[h.A]);
        Assert.Equal(500m, gained[h.Bm]);
        Assert.Equal(1000m, await W.BalanceAsync("PF Profit Credited to Members"));
    }

    [ReferenceFact]
    public async Task S06_employee_page_shows_the_balance_after_profit()
    {
        var c = await h.AsmaAsync();
        string html = await (await c.GetAsync("/pf/")).Content.ReadAsStringAsync();
        Assert.Contains("My Provident Fund", html);
        Assert.Contains("5,500", html);
    }

    [ReferenceFact]
    public async Task S07_settlement_closes_the_ledger_once()
    {
        await h.PostAsync($"/pf/settle/{h.A}", Pay);
        Assert.Equal(0m, await BalAsync(h.A));
        await using (var db = W.Db())
            Assert.Equal(5500m, (await db.Set<PfSettlement>().SingleAsync(s => s.UserId == h.A)).NetSettlement);
        await h.PostAsync($"/pf/settle/{h.A}", Pay);
        await using (var db = W.Db())
        {
            Assert.Equal(1, await db.Set<PfSettlement>().CountAsync(s => s.UserId == h.A));
            // The open PF loan was netted off what was paid out.
            Assert.Equal("settled", (await db.Set<PfLoanRequest>().SingleAsync(l => l.UserId == h.A)).Status);
        }
    }

    [ReferenceFact]
    public async Task S08_settling_a_non_member_is_refused()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/pf/settle/999999")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", await h.TokenAsync())]),
        };
        req.Headers.Add("Accept", "application/json");
        var r = await h.C.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Contains("Not a member of this company.", await r.Content.ReadAsStringAsync());
    }

    [ReferenceFact]
    public async Task S09_admin_page_lists_pending_requests_and_balances()
    {
        await AsmaPostAsync("/pf/request-withdrawal", ("amount", "1"), ("reason", "nothing left"));   // refused: balance 0
        string html = await (await h.C.GetAsync("/pf/")).Content.ReadAsStringAsync();
        Assert.Contains("Provident Fund Management", html);
        Assert.Contains("Bilal Saver", html);
        Assert.Contains("Owes the fund", html);           // Chand, negative balance
        Assert.Contains("/pf/distribute-profit", html);
        var cfg = await (await h.C.GetAsync("/pf/config")).Content.ReadAsStringAsync();
        Assert.Contains("Provident Fund Configuration", cfg);
        Assert.Equal("/dashboard", (await (await h.AsmaAsync()).GetAsync("/pf/config")).Headers.Location!.OriginalString);
    }

    [ReferenceFact]
    public async Task S10_manual_ledger_entry_posts_the_other_side()
    {
        var bad = await h.PostAsync("/pf/add-ledger-entry", ("user_id", h.Bm.ToString()), ("type", "credit"), ("amount", "250"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        int equity;
        await using (var db = W.Db())
            equity = await db.Set<ChartOfAccount>().Where(a => a.Level >= 5 && a.Type == "equity").OrderBy(a => a.Code).Select(a => a.Id).FirstAsync();
        decimal before = await BalAsync(h.Bm);
        var r = await h.PostAsync("/pf/add-ledger-entry", ("user_id", h.Bm.ToString()), ("type", "credit"), ("amount", "250"),
                                  ("description", "Opening balance"), ("counter_account_id", equity.ToString()));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(before + 250, await BalAsync(h.Bm));
        await using var db2 = W.Db();
        int entry = await db2.Set<PfLedger>().Where(e => e.UserId == h.Bm).MaxAsync(e => e.Id);
        var lines = await W.VoucherLinesAsync("PF-ADJ", entry);
        Assert.Equal(-250m, lines["PF Payable"]);
        Assert.Equal(0m, lines.Values.Sum());
    }
}
