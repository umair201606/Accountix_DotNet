namespace Accountix.Admin.Tests;

/// <summary>admin rebuild-gl-summary: tools/rebuild_gl_summary.py, now a dry run unless --apply, and parity with it.</summary>
public class RebuildGlSummaryTests
{
    private static async Task KnockAsync(AdminApp app, int cid) =>
        await app.InAsync(cid, (_, db) => db.Database.ExecuteSqlAsync(
            $"UPDATE gl_daily_balances SET debit = debit + 1 WHERE id = (SELECT min(id) FROM gl_daily_balances WHERE company_id = {cid})"));

    [ReferenceFact]
    public async Task Rebuilds_the_reporting_table_of_the_company_named_dry_run_first()
    {
        await using var scratch = await ScratchDb.CreateAsync();
        await using var app = await AdminApp.StartAsync(scratch);
        int cid = await app.CompanyAsync("Rebuild Co");
        int other = await app.CompanyAsync("Rebuild Other");
        await app.ParkDuplicatesAsync(cid, 1, "R");
        await app.ParkDuplicatesAsync(other, 1, "S");
        await KnockAsync(app, cid);
        await KnockAsync(app, other);
        Assert.Equal(["reporting_table"], await app.FailingAsync(cid));

        var (exit, text) = await app.RunAsync("rebuild-gl-summary", cid.ToString());
        Assert.Equal(0, exit);
        Assert.Contains($"Rebuild Co (company {cid}): reporting table differs (", text);
        Assert.Contains("matches the ledger after", text);
        Assert.Contains("Dry run — rolled back", text);
        Assert.Equal(["reporting_table"], await app.FailingAsync(cid));

        (exit, text) = await app.RunAsync("rebuild-gl-summary", "--apply", cid.ToString());
        Assert.Equal(0, exit);
        Assert.Contains($"Rebuilt gl_daily_balances for company {cid}", text);
        Assert.Contains("COMMITTED", text);
        Assert.Empty(await app.FailingAsync(cid));
        Assert.Equal(["reporting_table"], await app.FailingAsync(other));     // not named: untouched

        // Every company when none is named; then a second run has nothing to put right.
        (exit, text) = await app.RunAsync("rebuild-gl-summary", "--apply");
        Assert.Equal(0, exit);
        Assert.Contains("for every company", text);
        Assert.Empty(await app.FailingAsync(other));
        (exit, text) = await app.RunAsync("rebuild-gl-summary", "--apply");
        Assert.DoesNotContain("differs", text);

        (exit, text) = await app.RunAsync("rebuild-gl-summary", "999999");
        Assert.Equal(2, exit);
        Assert.Contains("No company 999999.", text);
    }

}
