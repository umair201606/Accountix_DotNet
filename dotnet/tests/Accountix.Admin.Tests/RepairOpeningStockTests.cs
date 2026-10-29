using Accountix.Inventory.Services;

namespace Accountix.Admin.Tests;

/// <summary>
/// admin repair-opening-stock: tests/e2e/test_order_actions.py's
/// test_opening_stock_without_a_journal_is_found_and_repaired, the tool's dry run, refusal and
/// second run, and parity with tools/repair_opening_stock.py.
/// </summary>
public class RepairOpeningStockTests
{
    private static readonly DateTime When = new(2025, 3, 1);

    private static Task<List<int>> UnjournaledAsync(AdminApp app, int cid) => app.InAsync(cid, async (sp, _) =>
        (await sp.GetRequiredService<StockEntry>().UnjournaledOpeningsAsync()).Select(r => r.ProductId).ToList());

    [ReferenceFact]
    public async Task Opening_stock_without_a_journal_is_found_and_repaired()
    {
        await using var scratch = await ScratchDb.CreateAsync();
        await using var app = await AdminApp.StartAsync(scratch);
        int cid = await app.CompanyAsync("Opening Repair Co");
        int legacy = await app.UnjournaledOpeningAsync(cid, "LEG", 4, 25_000, When);

        Assert.Equal([legacy], await UnjournaledAsync(app, cid));
        var check = await app.InAsync(cid, (sp, _) =>
            ActivatorUtilities.CreateInstance<Accountix.Executive.Services.BooksIntegrity>(sp).InventoryVsGlAsync());
        Assert.False(check.Ok);
        Assert.Contains("admin repair-opening-stock", check.Detail);       // the page names the command

        // Dry run: says what it would post and what Books Integrity would say, posts nothing.
        var (exit, text) = await app.RunAsync("repair-opening-stock", cid.ToString());
        Assert.Equal(0, exit);
        Assert.Contains("Opening Repair Co: 1 opening rows without a journal, 100,000.00", text);
        Assert.Contains("Books Integrity passes afterwards", text);
        Assert.Contains("would post 1 journals", text);
        Assert.Contains("Dry run — nothing posted. Re-run with --apply.", text);
        Assert.Equal([legacy], await UnjournaledAsync(app, cid));

        (exit, text) = await app.RunAsync("repair-opening-stock", "--apply", cid.ToString());
        Assert.Equal(0, exit);
        Assert.Contains("posted 1 journals", text);
        Assert.Empty(await UnjournaledAsync(app, cid));
        Assert.Empty(await app.FailingAsync(cid));
        await app.InAsync(cid, async (_, db) =>
        {
            var je = await db.Set<JournalEntry>().Include(e => e.JournalLines).SingleAsync(e => e.VoucherType == "OPENING" && e.VoucherId == legacy);
            Assert.Equal(When, je.EntryDate);                 // on the row's own date
            Assert.Equal(100_000m, je.JournalLines.Sum(l => l.Debit ?? 0));
        });

        // A second run finds nothing.
        (exit, text) = await app.RunAsync("repair-opening-stock", "--apply", cid.ToString());
        Assert.Equal(0, exit);
        Assert.Contains("Opening Repair Co: 0 opening rows without a journal, 0.00", text);
    }

    [ReferenceFact]
    public async Task A_closed_period_refuses_and_nothing_is_posted()
    {
        await using var scratch = await ScratchDb.CreateAsync();
        await using var app = await AdminApp.StartAsync(scratch);
        int cid = await app.CompanyAsync("Opening Closed Co");
        int p = await app.UnjournaledOpeningAsync(cid, "CLO", 2, 1_500, When);
        await app.InAsync(cid, async (_, db) =>
        {
            var period = await db.Set<AccountingPeriod>().SingleAsync(x => x.StartDate <= DateOnly.FromDateTime(When) && x.EndDate >= DateOnly.FromDateTime(When));
            period.IsClosed = true;
            period.IsOpen = false;
            await db.SaveChangesAsync();
        });
        var (exit, text) = await app.RunAsync("repair-opening-stock", "--apply", cid.ToString());
        Assert.Equal(1, exit);
        Assert.Contains("REFUSED: Opening Closed Co", text);
        Assert.Contains("Nothing was posted for this company.", text);
        Assert.Equal([p], await UnjournaledAsync(app, cid));
    }

    [ReferenceFact]
    public async Task Only_the_companies_named_are_repaired()
    {
        await using var scratch = await ScratchDb.CreateAsync();
        await using var app = await AdminApp.StartAsync(scratch);
        int one = await app.CompanyAsync("Opening One");
        int two = await app.CompanyAsync("Opening Two");
        await app.UnjournaledOpeningAsync(one, "O1", 1, 10, When);
        int p2 = await app.UnjournaledOpeningAsync(two, "T1", 1, 10, When);
        var (exit, text) = await app.RunAsync("repair-opening-stock", "--apply", one.ToString());
        Assert.Equal(0, exit);
        Assert.DoesNotContain("Opening Two", text);
        Assert.Empty(await UnjournaledAsync(app, one));
        Assert.Equal([p2], await UnjournaledAsync(app, two));
    }

}
