using System.Net;
using Accountix.Core.Entities;
using Accountix.Finance.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Accountix.Finance.Tests;

/// <summary>
/// Reports read one snapshot (consistent_snapshot): while six people post vouchers,
/// every cash-flow statement still reconciles opening + change = closing (it reads
/// four separate figures), and every trial balance and balance sheet still balances.
/// </summary>
public class StatementManyUsersTests(ITestOutputHelper output)
{
    private static List<KeyValuePair<string, string>> Cpv(int cash, int expense, string note) =>
    [
        new("action", "approve"), new("voucher_type", "CPV"), new("voucher_date", "2026-05-12T11:00"),
        new("cash_bank_account_id", cash.ToString()), new("label_id", ""), new("notes", note),
        new("account_id[]", expense.ToString()), new("description[]", note), new("debit[]", "37.5"), new("credit[]", ""), new("label_id[]", ""),
    ];

    [ReferenceFact]
    public async Task Statements_stay_consistent_while_six_people_post_vouchers()
    {
        // Company B: the race posts there, never into the golden books.
        int fy26;
        int cash, expense;
        await using (var db = World.Db(World.CompanyB))
        {
            fy26 = db.Set<AccountingPeriod>().Single(p => p.PeriodName == "FY 2026").Id;
            cash = db.Set<ChartOfAccount>().Single(a => a.Code == "1-01-01-01-0001").Id;
            expense = db.Set<ChartOfAccount>().Single(a => a.Code == "5-04-01-01-0001").Id;
        }
        await using var host = await ModuleHost.StartAsync(typeof(FinanceModule).Assembly, typeof(Accountix.Accounting.AccountingModule).Assembly);
        var reader = host.ClientFor(StatementSeed.OwnerB, StatementSeed.CompanyB);
        string q = $"filter_mode=period&period_id={fy26}";
        // The statements need figures to say "IN BALANCE" (an empty year shows no verdict): post one first,
        // so the test never depends on what earlier runs left in company B.
        string tag = Guid.NewGuid().ToString("N")[..6];
        var seed = await reader.PostFormAsync("/accounting/vouchers", Cpv(cash, expense, $"Statement seed {tag}"), "/accounting/vouchers?type=CPV");
        Assert.Equal(HttpStatusCode.Redirect, seed.StatusCode);
        Assert.Contains("Reconciled with cash-account movement", await (await reader.GetAsync($"/finance/cash-flow?{q}")).Content.ReadAsStringAsync());

        var clients = host.ClientsFor(StatementSeed.OwnerB, StatementSeed.CompanyB, 6);
        var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync("/accounting/vouchers?type=CPV")));
        using var stop = new CancellationTokenSource();
        int reads = 0;
        var reading = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (string page in new[] { "cash-flow", "trial-balance", "balance-sheet" })
                {
                    var r = await reader.GetAsync($"/finance/{page}?{q}");
                    Assert.Equal(HttpStatusCode.OK, r.StatusCode);
                    string html = await r.Content.ReadAsStringAsync();
                    Assert.Contains(page switch
                    {
                        "cash-flow" => "Reconciled with cash-account movement",
                        "trial-balance" => "Trial Balance is <strong>IN BALANCE</strong>",
                        _ => "Balance Sheet is <strong>IN BALANCE</strong>",
                    }, html);
                    Interlocked.Increment(ref reads);
                }
            }
        });
        var posted = new List<HttpResponseMessage>();
        for (int round = 0; round < 3; round++)
            posted.AddRange(await Race.RunAsync(6, i => clients[i].PostFormWithTokenAsync("/accounting/vouchers",
                Cpv(cash, expense, $"Statement race {tag} {round}.{i}"), tokens[i])));
        await Task.Delay(300);
        stop.Cancel();
        await reading;

        Race.NoServerErrors(posted);
        Assert.All(posted, r => Assert.Equal(HttpStatusCode.Redirect, r.StatusCode));
        await using (var db = World.Db(World.CompanyB))
        {
            var ids = db.Set<AccountingVoucher>().Where(v => v.Notes!.StartsWith($"Statement race {tag}")).Select(v => v.Id).ToList();
            Assert.Equal(18, ids.Count);
            Assert.Equal(18, await db.Set<JournalEntry>().CountAsync(e => e.VoucherType == "CPV" && ids.Contains(e.VoucherId) && e.IsPosted == true));
        }
        output.WriteLine($"{reads} statement reads during 18 parallel postings, all consistent");
        Assert.True(reads >= 3);
    }

    /// <summary>
    /// Runs a "report" of two reads under [ConsistentSnapshot] (or without it), with
    /// another connection committing a new row between the reads; returns both counts.
    /// </summary>
    private static async Task<(int First, int Second)> TwoReadsAsync(bool snapshot)
    {
        await using var db = World.Db(World.CompanyB);
        var sp = new ServiceCollection().AddSingleton<Accountix.Core.Data.AccountixDbContext>(db).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = sp };
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var executing = new ActionExecutingContext(action, [], new Dictionary<string, object?>(), controller: new object());
        string name = "Snapshot probe " + Guid.NewGuid().ToString("N")[..8];
        int first = 0, second = 0;
        async Task<ActionExecutedContext> Report()
        {
            first = await db.Set<ProjectLabel>().CountAsync();
            await using (var other = World.Db(World.CompanyB))   // another user's posting lands mid-report
            {
                other.Add(new ProjectLabel { Name = name, IsActive = true, CreatedAt = DateTime.UtcNow });
                await other.SaveChangesAsync();
            }
            second = await db.Set<ProjectLabel>().CountAsync();
            return new ActionExecutedContext(action, [], new object());
        }
        try
        {
            if (snapshot) await new ConsistentSnapshotAttribute().OnActionExecutionAsync(executing, Report);
            else await Report();
        }
        finally
        {
            await using var cleanup = World.Db(World.CompanyB);
            await cleanup.Set<ProjectLabel>().Where(l => l.Name == name).ExecuteDeleteAsync();
        }
        return (first, second);
    }

    [ReferenceFact]
    public async Task A_report_reads_one_snapshot_even_when_another_user_commits_mid_report()
    {
        var (a, b) = await TwoReadsAsync(snapshot: true);
        Assert.Equal(a, b);                 // REPEATABLE READ: the second read sees the books as the first did
        var (c, d) = await TwoReadsAsync(snapshot: false);
        Assert.Equal(c + 1, d);             // without it, the commit shows up halfway through
    }
}
