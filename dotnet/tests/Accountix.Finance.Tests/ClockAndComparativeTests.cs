using System.Net;
using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.Finance.Services;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Accountix.Finance.Tests;

/// <summary>
/// A stopped clock for the hosts that ask for it: this test assembly is passed to
/// ModuleHost as a module, and modules register after the app's TimeProvider.System.
/// </summary>
public sealed class StoppedClockModule : Accountix.Web.Infrastructure.IAccountixModule
{
    public static DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;

    private sealed class Stopped : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => At;
    }

    public string Key => "finance-test-clock";
    public void Register(IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<TimeProvider>(new Stopped());
        // The stopped clock may run up to a day ahead of the session the test signs in with.
        config["SESSION_IDLE_MINUTES"] = "2880";
        config["SESSION_MAX_HOURS"] = "48";
    }
    public Accountix.Web.Infrastructure.ModuleNav? Navigation => null;
}

public class ClockAndComparativeTests(ITestOutputHelper output)
{
    /// <summary>
    /// Each comparative column of the cash-flow statement equals the statement of that
    /// period on its own, for both methods, with each of three periods as the base.
    /// The golden books' 2026 has an expense refund (a net credit on Office Supplies), which the
    /// old comparative formula turned into a cut in profit.
    /// </summary>
    [ReferenceFact]
    public async Task Each_comparative_cash_flow_column_equals_that_period_on_its_own()
    {
        await StatementSeed.EnsureAsync();
        await using var db = StatementSeed.Db();
        var periods = await db.Set<AccountingPeriod>().AsNoTracking()
            .Where(p => p.PeriodName == "FY 2024" || p.PeriodName == "FY 2025" || p.PeriodName == "FY 2026")
            .OrderBy(p => p.StartDate).ToListAsync();
        Assert.Equal(3, periods.Count);
        foreach (string method in new[] { "indirect", "direct" })
        {
            var single = new Dictionary<int, CashFlowResult>();
            foreach (var p in periods)
                single[p.Id] = await new FinanceStatements(new FinanceData(db)).CashFlowAsync(p.StartDate, p.EndDate, method, [], []);
            foreach (var basePeriod in periods)
            {
                var comps = periods.Where(p => p.Id != basePeriod.Id).ToList();
                var r = await new FinanceStatements(new FinanceData(db)).CashFlowAsync(basePeriod.StartDate, basePeriod.EndDate, method, comps, []);
                for (int i = 0; i < comps.Count; i++)
                {
                    var alone = single[comps[i].Id];
                    var map = r.CompMaps[i];
                    string where = $"{method}, base {basePeriod.PeriodName}, column {comps[i].PeriodName}";
                    // Every line of the period's own statement, and nothing else.
                    var items = alone.Operating.Concat(alone.Investing).Concat(alone.Financing).ToList();
                    Assert.Equal(items.Select(x => x.Name).Order(StringComparer.Ordinal),
                                 map.Keys.Where(k => !k.StartsWith("__")).Order(StringComparer.Ordinal));
                    foreach (var item in items)
                        Assert.True(Math.Abs(map[item.Name] - item.Value) < 1e-6, $"{where}: {item.Name} {map[item.Name]} vs {item.Value}");
                    foreach (var (key, value) in new[]
                             {
                                 ("__net_op__", alone.NetOperating), ("__net_inv__", alone.NetInvesting), ("__net_fin__", alone.NetFinancing),
                                 ("__net_chg__", alone.NetChange), ("__open__", alone.OpeningCash), ("__close__", alone.ClosingCash),
                             })
                        Assert.True(Math.Abs(map[key] - value) < 1e-6, $"{where}: {key} {map[key]} vs {value}");
                    // The rows the comparative statement prints carry the same figures in that column.
                    foreach (var item in r.Operating.Concat(r.Investing).Concat(r.Financing))
                    {
                        double expected = items.FirstOrDefault(x => x.Name == item.Name)?.Value ?? 0.0;
                        Assert.True(Math.Abs(item.Comps![i]!.Value - expected) < 1e-6, $"{where}: row {item.Name}");
                    }
                }
                // And the base column is the period's own statement.
                Assert.True(Math.Abs(r.NetChange - single[basePeriod.Id].NetChange) < 1e-6);
            }
            var fy26 = single[periods[2].Id];
            output.WriteLine($"{method}: FY 2026 net profit line {fy26.Operating[0].Name} = {fy26.Operating[0].Value:F2}, net change {fy26.NetChange:F2}");
        }
    }

    /// <summary>
    /// The 13-week forecast marks a week finished (actuals, variance) by the company's
    /// date. With the clock stopped at 19:30 UTC — still yesterday in UTC, already
    /// 00:30 today in Lahore — the week ending on the UTC date is finished and the next is not.
    /// </summary>
    [ReferenceFact]
    public async Task Finished_twcf_weeks_follow_the_company_clock_across_lahore_midnight()
    {
        var now = DateTimeOffset.UtcNow;
        var stopped = new DateTimeOffset(now.Year, now.Month, now.Day, 19, 30, 0, TimeSpan.Zero);
        if (stopped < now) stopped = stopped.AddDays(1);   // the next 19:30 UTC, so session cookies stay in the past
        StoppedClockModule.At = stopped;
        var utcDate = DateOnly.FromDateTime(stopped.UtcDateTime);
        var lahoreDate = DateOnly.FromDateTime(stopped.ToOffset(TimeSpan.FromHours(5)).DateTime);
        Assert.Equal(utcDate.AddDays(1), lahoreDate);

        // Week 1 ends on the UTC date: finished in Lahore, not yet in UTC.
        var start = utcDate.AddDays(-6);
        int companyB = StatementSeed.CompanyB;
        await using var host = await ModuleHost.StartAsync(typeof(FinanceModule).Assembly, typeof(StoppedClockModule).Assembly);
        var c = host.ClientFor(StatementSeed.OwnerB, companyB);
        var r = await c.GetAsync($"/finance/twcf?start={start:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        string html = await r.Content.ReadAsStringAsync();
        string Wk(int n, DateOnly s) =>
            $"Wk {n}<small>{s:dd MMM} – {s.AddDays(6):dd MMM}";
        Assert.Contains(Wk(1, start) + " <i class=\"fas fa-check-circle\"></i></small>", html);
        Assert.Contains(Wk(2, start.AddDays(7)) + "</small>", html);

        // The engine agrees: given the company's date, week 1 is past; given the UTC date it is not.
        var company = new CompanyContext();
        company.Set(companyB);
        await using var db = Reference.NewDb(company);
        var twcf = new TwcfReports(new FinanceData(db));
        Assert.True((await twcf.BuildMatrixAsync(start, lahoreDate)).Weeks[0].IsPast);
        Assert.False((await twcf.BuildMatrixAsync(start, utcDate)).Weeks[0].IsPast);
        output.WriteLine($"Clock stopped at {stopped:u}: UTC date {utcDate}, Lahore date {lahoreDate}; week {start}..{utcDate} finished.");
    }
}
