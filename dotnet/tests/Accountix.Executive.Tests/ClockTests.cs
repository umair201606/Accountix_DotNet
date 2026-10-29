using Accountix.Executive.Services;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Executive.Tests;

/// <summary>
/// With no as-of date, aging and profitability run to the company's today (shared/clock.local_today),
/// not the server's UTC date: in Lahore the UTC date is still yesterday until 05:00.
/// </summary>
public class ClockTests
{
    // 30 Jun 2026 20:30 UTC is already 1 Jul 01:30 in Lahore (Asia/Karachi, the golden company's zone).
    private static readonly DateTimeOffset LateEveningUtc = new(2026, 6, 30, 20, 30, 0, TimeSpan.Zero);

    [ReferenceFact]
    public async Task Aging_and_profitability_without_a_date_run_to_the_companys_today()
    {
        await Exec.SeedAsync();
        await using var db = Exec.Db(Exec.Company);
        Assert.Equal("Asia/Karachi", await db.Companies.Where(c => c.Id == Exec.Company).Select(c => c.Timezone).SingleAsync());
        int c1 = await Exec.AccountIdAsync("1-01-02-01-9101", Exec.Company);
        var reports = Exec.Reports(db, new FixedTime(LateEveningUtc));

        Assert.Equal(new DateOnly(2026, 7, 1), reports.AgeDay(null));
        // Invoiced 16 May: 46 days old on 1 Jul (the UTC date, 30 Jun, would say 45).
        Assert.Equal(46, (await reports.PartyAgingAsync(null))[c1].OldestDays);
        // The chart's last month is the company's month: July, not June.
        var profit = await reports.ProfitabilityAsync();
        Assert.Equal("Jul 2026", profit.Months[^1].FullLabel);
        Assert.Equal(new DateOnly(2025, 7, 1), profit.Prior!.Start);

        // An hour before midnight in Lahore the company's day is still 30 Jun.
        var before = Exec.Reports(db, new FixedTime(new DateTimeOffset(2026, 6, 30, 18, 0, 0, TimeSpan.Zero)));
        Assert.Equal(45, (await before.PartyAgingAsync(null))[c1].OldestDays);
        Assert.Equal("Jun 2026", (await before.ProfitabilityAsync()).Months[^1].FullLabel);
    }
}
