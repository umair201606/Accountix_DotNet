using Accountix.Core.Entities;
using Accountix.Core.Shared;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Inventory.Tests;

/// <summary>
/// tests/unit/test_costing_backdated.py: documents keyed out of date order,
/// deletions and edits. Checks the issue costs, that the layers tie to the
/// ledger, and that every re-costed issue is journalled for the exact difference.
/// </summary>
public class CostingBackdatedTests
{
    private const string Fifo = "fifo";
    private static DateTime Day(int n) => CostingWorld.Day(n);

    private static Task<StockLedger> Buy(CostingWorld w, decimal qty, decimal cost, int n, DateTime when) => w.BuyAsync(qty, cost, n, when);

    private static Task<(decimal UnitCost, decimal TotalCost)> Sell(CostingWorld w, decimal qty, int n, DateTime when, string vtype = "SI") =>
        w.IssueAsync(qty, n, vtype, when);

    [ReferenceFact]
    public async Task Rows_carry_their_document_date()
    {
        await using var w = await CostingWorld.StartAsync();
        await Buy(w, 10, 10, 1, Day(30));
        var row = await w.Db.Set<StockLedger>().AsNoTracking().SingleAsync(r => r.ProductId == w.Product);
        Assert.Equal(DateOnly.FromDateTime(Day(30)), row.TxnDate);
    }

    [ReferenceFact]
    public async Task Backdated_purchase_recosts_later_fifo_sale()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        await Buy(w, 10, 20, 2, Day(10));
        var (_, cogs) = await Sell(w, 10, 1, Day(5));
        Assert.Equal(200.00m, cogs);
        await Buy(w, 10, 10, 1, Day(20));
        Assert.Equal(100.00m, await w.CostOfAsync("SI", 1));
        Assert.Equal([(20.0000m, 10.0000m)], await w.LayersAsync());
        Assert.Equal(200.00m, await w.Costing.StockValueAsync(w.Product));
        await w.TiesAsync("back-dated FIFO purchase");
        Assert.Equal(-100.00m, await w.AdjustmentJournalTotalAsync());
    }

    [ReferenceFact]
    public async Task Backdated_purchase_reaverages_later_wa_sale()
    {
        await using var w = await CostingWorld.StartAsync();
        await Buy(w, 10, 20, 1, Day(10));
        var (_, cogs) = await Sell(w, 5, 1, Day(5));
        Assert.Equal(100.00m, cogs);
        await Buy(w, 10, 10, 2, Day(7));
        Assert.Equal(75.00m, await w.CostOfAsync("SI", 1));
        Assert.Equal(15m, await w.Costing.OnHandAsync(w.Product));
        Assert.Equal(225.00m, await w.Costing.StockValueAsync(w.Product));
        await w.TiesAsync("back-dated WA purchase");
    }

    [ReferenceFact]
    public async Task Backdated_purchase_after_every_sale_changes_nothing()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        await Buy(w, 10, 10, 1, Day(10));
        await Sell(w, 5, 1, Day(5));
        await Buy(w, 10, 30, 2, Day(1));
        Assert.Equal(50.00m, await w.CostOfAsync("SI", 1));
        Assert.Empty(await w.AdjustmentsAsync());
        await w.TiesAsync("purchase after every sale");
    }

    [ReferenceFact]
    public async Task Backdated_sale_is_costed_at_its_own_date()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        await Buy(w, 10, 10, 1, Day(20));
        await Buy(w, 10, 30, 2, Day(5));
        await Sell(w, 10, 2, Day(0));
        Assert.Equal(100.00m, await w.CostOfAsync("SI", 2));
        var (_, cogs) = await Sell(w, 10, 1, Day(10));
        Assert.Equal(100.00m, cogs);
        Assert.Equal(300.00m, await w.CostOfAsync("SI", 2));
        Assert.Equal(0m, await w.Costing.OnHandAsync(w.Product));
        Assert.Equal(0m, await w.Costing.StockValueAsync(w.Product));
        await w.TiesAsync("back-dated sale");
        Assert.Equal(200.00m, await w.AdjustmentJournalTotalAsync());
    }

    [ReferenceFact]
    public async Task Backdated_sale_refused_when_stock_was_not_on_hand_then()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        await Buy(w, 10, 10, 1, Day(5));
        var e = await w.RefusedAsync<NegativeStockException>(() => Sell(w, 3, 1, Day(10)));
        Assert.Equal($"Cannot issue 3 of product {w.Product} on {Day(10):dd MMM yyyy}: only 0 were on hand at that date. " +
                     "Receive the stock first, or enable 'allow negative stock' in Inventory Settings.", e.Message);
    }

    [ReferenceFact]
    public async Task Backdated_sale_refused_when_it_would_strip_a_later_sale()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        await Buy(w, 10, 10, 1, Day(20));
        await Sell(w, 8, 2, Day(5));
        var e = await w.RefusedAsync<NegativeStockException>(() => Sell(w, 5, 1, Day(10)));
        Assert.Contains("would be left", e.Message);
        Assert.Equal($"Cannot post this change: SI SI-00002 dated {Day(5):dd MMM yyyy} would be left 3 unit(s) short -- the stock " +
                     "it issued would no longer have been on hand. Receive the stock first, or enable 'allow negative stock' in Inventory Settings.",
                     e.Message);
    }

    [ReferenceFact]
    public async Task Backdated_sale_allowed_short_when_negative_stock_enabled()
    {
        await using var w = await CostingWorld.StartAsync(Fifo, allowNegative: true);
        await Buy(w, 10, 10, 1, Day(5));
        var (_, cogs) = await Sell(w, 3, 1, Day(10));
        Assert.Equal(30.00m, cogs);
        Assert.Equal(7m, await w.Costing.OnHandAsync(w.Product));
        await w.TiesAsync("short back-dated sale");
    }

    [ReferenceFact]
    public async Task Deleting_a_middle_sale_recosts_the_later_one()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        await Buy(w, 10, 10, 1, Day(30));
        await Buy(w, 10, 20, 2, Day(25));
        await Sell(w, 10, 1, Day(20));
        await Sell(w, 10, 2, Day(10));
        Assert.Equal(200.00m, await w.CostOfAsync("SI", 2));
        await w.ReverseAsync("SI", 1);
        Assert.Equal(100.00m, await w.CostOfAsync("SI", 2));
        Assert.Equal([(20.0000m, 10.0000m)], await w.LayersAsync());
        await w.TiesAsync("middle sale deleted");
        Assert.Equal(-100.00m, await w.AdjustmentJournalTotalAsync());
    }

    [ReferenceFact]
    public async Task Deleting_the_latest_document_touches_nothing_else()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        await Buy(w, 10, 10, 1, Day(30));
        await Sell(w, 4, 1, Day(20));
        await Sell(w, 3, 2, Day(10));
        await w.ReverseAsync("SI", 2);
        Assert.Equal(40.00m, await w.CostOfAsync("SI", 1));
        Assert.Empty(await w.AdjustmentsAsync());
        Assert.Equal(6m, await w.Costing.OnHandAsync(w.Product));
        await w.TiesAsync("latest deleted");
    }

    [ReferenceFact]
    public async Task Deleting_a_receipt_a_later_sale_needs_is_refused()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        await Buy(w, 10, 10, 1, Day(30));
        await Sell(w, 5, 1, Day(10));
        await w.RefusedAsync<ConsumedLayerException>(() => w.ReverseAsync("PI", 1));
    }

    [ReferenceFact]
    public async Task Editing_an_old_purchase_price_flows_to_every_later_issue()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        await Buy(w, 10, 10, 1, Day(30));
        await Sell(w, 4, 1, Day(20));
        await Sell(w, 2, 1, Day(10), "CONS");
        await w.ReverseAsync("PI", 1, allowVariance: true);
        await Buy(w, 10, 15, 1, Day(30));
        Assert.Equal(60.00m, await w.CostOfAsync("SI", 1));
        Assert.Equal(30.00m, await w.CostOfAsync("CONS", 1));
        Assert.Equal(60.00m, await w.Costing.StockValueAsync(w.Product));
        await w.TiesAsync("edited purchase price");
        Assert.Equal(30.00m, await w.AdjustmentJournalTotalAsync());
    }

    [ReferenceFact]
    public async Task Editing_an_old_purchase_quantity_below_what_was_issued_is_refused()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        await Buy(w, 10, 10, 1, Day(30));
        await Sell(w, 8, 1, Day(20));
        await w.ReverseAsync("PI", 1, allowVariance: true);
        await w.RefusedAsync<NegativeStockException>(() => Buy(w, 5, 10, 1, Day(30)));
    }

    [ReferenceFact]
    public async Task On_hand_and_value_as_of_a_past_date()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        await Buy(w, 10, 10, 1, Day(30));
        await Sell(w, 4, 1, Day(20));
        await Buy(w, 5, 12, 2, Day(10));
        Assert.Equal(10m, await w.Costing.OnHandAsync(w.Product, Day(25)));
        Assert.Equal(100.00m, await w.Costing.ValueAtAsync(w.Product, Day(25)));
        Assert.Equal(6m, await w.Costing.OnHandAsync(w.Product, Day(15)));
        Assert.Equal(60.00m, await w.Costing.ValueAtAsync(w.Product, Day(15)));
        Assert.Equal(11m, await w.Costing.OnHandAsync(w.Product, Day(0)));
        Assert.Equal(120.00m, await w.Costing.ValueAtAsync(w.Product, Day(0)));
    }

    [ReferenceFact]
    public async Task Adjustment_posts_in_the_issue_period()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        await Buy(w, 10, 20, 2, Day(10));
        await Sell(w, 10, 1, Day(5));
        await Buy(w, 10, 10, 1, Day(20));
        var adj = Assert.Single(await w.AdjustmentsAsync());
        Assert.Equal(DateOnly.FromDateTime(Day(5)), adj.EntryDate);
        var je = await w.Db.Set<JournalEntry>().AsNoTracking().SingleAsync(e => e.Id == adj.JournalEntryId);
        Assert.Equal(Day(5), je.EntryDate);
        Assert.Equal("CADJ-SI", je.VoucherType);
        Assert.Equal(w.V(1), je.VoucherId);
        Assert.Equal("CADJ-SI-00001", je.VoucherNumber);
    }

    [ReferenceFact]
    public async Task Adjustment_moves_to_today_when_issue_period_is_closed()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        await Buy(w, 10, 20, 2, Day(40));
        await Sell(w, 10, 1, Day(35));
        w.Db.Add(new AccountingPeriod
        {
            FiscalYear = "FY", PeriodName = "Closed", StartDate = DateOnly.FromDateTime(Day(36)), EndDate = DateOnly.FromDateTime(Day(34)),
            IsClosed = true, IsOpen = false, IsActive = true, CreatedAt = DateTime.UtcNow,
        });
        await w.Db.SaveChangesAsync();
        await Buy(w, 10, 10, 1, Day(50));
        var adj = Assert.Single(await w.AdjustmentsAsync());
        Assert.Equal(DateOnly.FromDateTime(DateTime.Today), adj.EntryDate);
    }

    [ReferenceFact]
    public async Task Sales_return_follows_its_recosted_sale()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        int invoiceId = await ReturnDocs.SalesInvoiceAsync(w);
        int returnId = await ReturnDocs.SalesReturnAsync(w, invoiceId);
        await w.Costing.ReceiveAsync(w.Product, "PI", w.V(2), "PI-00002", 10, 20, "", w.User, Day(10));
        await w.Costing.IssueAsync(w.Product, "SI", invoiceId, "SI-00001", 5, "", w.User, null, Day(5));
        await w.Costing.ReceiveAsync(w.Product, "SRV", returnId, "SRV-00007", 2, 20, "", w.User, Day(3));
        await w.Costing.ReceiveAsync(w.Product, "PI", w.V(1), "PI-00001", 10, 10, "", w.User, Day(20));
        Assert.Equal(50.00m, (await w.Db.Set<StockLedger>().AsNoTracking().SingleAsync(r => r.VoucherType == "SI" && r.VoucherId == invoiceId)).TotalCost);
        Assert.Equal(20.00m, (await w.Db.Set<StockLedger>().AsNoTracking().SingleAsync(r => r.VoucherType == "SRV" && r.VoucherId == returnId)).TotalCost);
        await w.TiesAsync("return follows the re-costed sale");
    }
}
