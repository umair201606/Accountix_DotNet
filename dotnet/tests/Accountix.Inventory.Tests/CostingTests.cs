using Accountix.Core.Entities;
using Accountix.Core.Shared;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Inventory.Tests;

/// <summary>
/// tests/unit/test_costing.py, case for case: cost accuracy, immutability of
/// posted cost, and the layer/ledger invariant that keeps inventory tied to COGS.
/// </summary>
public class CostingTests
{
    private const string Fifo = "fifo";
    private const string Wa = "weighted_average";

    private static List<(decimal, decimal)> L(params (decimal, decimal)[] layers) => layers.ToList();

    // ── Cost accuracy across multiple prices ──

    [ReferenceFact]
    public async Task Weighted_average_issues_at_running_average()
    {
        await using var w = await CostingWorld.StartAsync();
        await w.BuyAsync(10, 10, 1);
        await w.BuyAsync(10, 20, 2);
        var (unit, total) = await w.IssueAsync(10);
        Assert.Equal(15.0000m, unit);
        Assert.Equal(150.00m, total);
        Assert.Equal(150m, await w.BookValueAsync());
        await w.TiesAsync("after WA issue");
    }

    [ReferenceFact]
    public async Task Weighted_average_reaverages_on_each_receipt()
    {
        await using var w = await CostingWorld.StartAsync();
        await w.BuyAsync(10, 10, 1);
        Assert.Equal(10.0000m, await w.Costing.CurrentUnitCostAsync(w.Product));
        await w.BuyAsync(30, 20, 2);
        Assert.Equal(17.5000m, await w.Costing.CurrentUnitCostAsync(w.Product));
        Assert.Single(await w.LayersAsync());
        await w.TiesAsync("after re-average");
    }

    [ReferenceFact]
    public async Task Fifo_issues_oldest_layer_first()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        await w.BuyAsync(10, 10, 1);
        await w.BuyAsync(10, 20, 2);
        var (unit, total) = await w.IssueAsync(10);
        Assert.Equal(10.0000m, unit);
        Assert.Equal(100.00m, total);
        Assert.Equal(L((20m, 10m)), await w.LayersAsync());
        await w.TiesAsync("after FIFO issue");
    }

    [ReferenceFact]
    public async Task Fifo_issue_spanning_layers_blends_cost()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        await w.BuyAsync(10, 10, 1);
        await w.BuyAsync(10, 20, 2);
        var (unit, total) = await w.IssueAsync(15);
        Assert.Equal(200.00m, total);
        Assert.Equal(13.3333m, unit);
        await w.TiesAsync("after spanning issue");
    }

    [ReferenceFact]
    public async Task Scrap_and_consumption_issue_at_historic_cost()
    {
        await using var w = await CostingWorld.StartAsync();
        await w.BuyAsync(10, 10, 1);
        await w.BuyAsync(10, 20, 2);
        var (unit, total) = await w.IssueAsync(4, 1, "SCRAP");
        Assert.Equal(15.0000m, unit);
        Assert.Equal(60.00m, total);
        (unit, _) = await w.IssueAsync(4, 1, "CONS");
        Assert.Equal(15.0000m, unit);
        await w.TiesAsync("after scrap + consumption");
    }

    // ── Switching valuation method ──

    [ReferenceFact]
    public async Task Method_switch_preserves_book_value()
    {
        await using var w = await CostingWorld.StartAsync();
        await w.BuyAsync(10, 10, 1);
        await w.BuyAsync(10, 20, 2);
        var (_, first) = await w.IssueAsync(10);
        Assert.Equal(150.00m, first);
        Assert.Equal(150m, await w.BookValueAsync());

        await w.Costing.RevalueForMethodChangeAsync(Fifo);
        await w.SetAsync(Fifo);
        Assert.Equal(150m, await w.Costing.StockValueAsync(w.Product));
        await w.TiesAsync("immediately after switch to FIFO");

        var (_, second) = await w.IssueAsync(10, 2);
        Assert.Equal(150.00m, second);
        Assert.Equal(300.00m, first + second);
        Assert.Equal(0m, await w.Costing.OnHandAsync(w.Product));
        Assert.Equal(0m, await w.BookValueAsync());
    }

    [ReferenceFact]
    public async Task Method_switch_does_not_change_already_posted_costs()
    {
        await using var w = await CostingWorld.StartAsync();
        await w.BuyAsync(10, 10, 1);
        await w.BuyAsync(10, 20, 2);
        await w.IssueAsync(10);
        async Task<List<string>> Posted() => (await w.Db.Set<StockLedger>().AsNoTracking().Where(r => r.ProductId == w.Product)
            .OrderBy(r => r.Id).ToListAsync()).Select(r => $"{r.Id}|{r.UnitCost:F4}|{r.TotalCost:F4}").ToList();
        var before = await Posted();
        await w.Costing.RevalueForMethodChangeAsync(Fifo);
        await w.SetAsync(Fifo);
        Assert.Equal(before, await Posted());
    }

    [ReferenceFact]
    public async Task Fifo_to_weighted_average_collapses_layers_at_book_value()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        await w.BuyAsync(10, 10, 1);
        await w.BuyAsync(10, 20, 2);
        Assert.Equal(2, (await w.LayersAsync()).Count);
        await w.Costing.RevalueForMethodChangeAsync(Wa);
        await w.SetAsync(Wa);
        Assert.Equal(L((15m, 20m)), await w.LayersAsync());
        await w.TiesAsync("after FIFO -> WA collapse");
    }

    [ReferenceFact]
    public async Task Ledger_rows_record_the_method_that_priced_them()
    {
        await using var w = await CostingWorld.StartAsync();
        await w.BuyAsync(10, 10, 1);
        await w.IssueAsync(5);
        await w.Costing.RevalueForMethodChangeAsync(Fifo);
        await w.SetAsync(Fifo);
        await w.BuyAsync(10, 20, 2);
        await w.IssueAsync(5, 2);
        var methods = await w.Db.Set<StockLedger>().AsNoTracking().Where(r => r.ProductId == w.Product).OrderBy(r => r.Id)
            .Select(r => r.ValuationMethod).ToListAsync();
        Assert.Equal([Wa, Wa, Fifo, Fifo], methods);
    }

    // ── Immutability of a posted charge ──

    [ReferenceFact]
    public async Task Employee_receivable_survives_later_activity()
    {
        await using var w = await CostingWorld.StartAsync();
        await w.BuyAsync(10, 10, 1);
        var (unit, total) = await w.IssueAsync(1, 1, "SCRAP");
        Assert.Equal(10.00m, total);
        await w.BuyAsync(50, 99, 2);
        await w.IssueAsync(20, 2);
        await w.Costing.RevalueForMethodChangeAsync(Fifo);
        await w.SetAsync(Fifo);
        await w.BuyAsync(5, 3, 3);
        await w.IssueAsync(2, 3, "CONS");
        var scrap = await w.Db.Set<StockLedger>().AsNoTracking().SingleAsync(r => r.ProductId == w.Product && r.VoucherType == "SCRAP");
        Assert.Equal(unit, scrap.UnitCost);
        Assert.Equal(total, scrap.TotalCost);
    }

    [ReferenceFact]
    public async Task Consumption_records_which_purchases_backed_the_charge()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        await w.BuyAsync(10, 10, 1);
        await w.BuyAsync(10, 20, 2);
        await w.IssueAsync(15, 1, "CONS");
        var outRow = await w.Db.Set<StockLedger>().AsNoTracking().SingleAsync(r => r.ProductId == w.Product && r.VoucherType == "CONS");
        var cons = await w.Db.Set<LayerConsumption>().AsNoTracking().Where(c => c.OutLedgerId == outRow.Id).ToListAsync();
        Assert.Equal(2, cons.Count);
        Assert.Equal(outRow.TotalCost, cons.Sum(c => c.TotalCost));
    }

    // ── Negative stock ──

    [ReferenceFact]
    public async Task Issuing_more_than_on_hand_is_refused()
    {
        await using var w = await CostingWorld.StartAsync();
        await w.BuyAsync(5, 10, 1);
        var e = await w.RefusedAsync<NegativeStockException>(() => w.IssueAsync(10));
        Assert.Contains("only", e.Message);
        Assert.Equal(
            $"Cannot issue 10 of product {w.Product}: only 5.0000 on hand. The uncovered 5.0000 unit(s) have no acquisition cost, " +
            "so any value posted for them would be invented. Receive the stock first, or enable 'allow negative stock' in Inventory Settings.",
            e.Message);
        Assert.Equal(5m, await w.Costing.OnHandAsync(w.Product));
        await w.TiesAsync("after refused issue");
    }

    [ReferenceFact]
    public async Task Negative_stock_allowed_when_configured()
    {
        await using var w = await CostingWorld.StartAsync(allowNegative: true);
        await w.BuyAsync(5, 10, 1);
        var (unit, _) = await w.IssueAsync(10);
        Assert.Equal(10.0000m, unit);
        Assert.Equal(-5m, await w.Costing.OnHandAsync(w.Product));
        await w.TiesAsync("after an over-issue with negative stock allowed");
    }

    [ReferenceFact]
    public async Task Over_issue_carries_its_cost_as_a_short_layer()
    {
        await using var w = await CostingWorld.StartAsync(allowNegative: true);
        await w.BuyAsync(5, 10, 1);
        await w.IssueAsync(8, 1);
        await w.TiesAsync("after an over-issue");
        await w.ReverseAsync("SI", 1);
        Assert.Equal(5m, await w.Costing.OnHandAsync(w.Product));
        await w.TiesAsync("after reversing the over-issue");
    }

    [ReferenceTheory]
    [InlineData(Wa)]
    [InlineData(Fifo)]
    public async Task Receipt_covers_an_open_short_before_opening_stock(string method)
    {
        await using var w = await CostingWorld.StartAsync(method, allowNegative: true);
        await w.BuyAsync(5, 10, 1);
        await w.IssueAsync(8, 1);
        await w.BuyAsync(10, 12, 2);
        await w.TiesAsync("after a receipt over a short");
        Assert.Equal(7m, (await w.LayersAsync()).Sum(l => l.Qty));
        Assert.Equal(7m, await w.Costing.OnHandAsync(w.Product));

        await w.SetAsync(allowNegative: false);
        await w.RefusedAsync<NegativeStockException>(() => w.IssueAsync(8, 2));
        await w.IssueAsync(7, 3);
        Assert.Equal(0m, await w.Costing.OnHandAsync(w.Product));
        await w.TiesAsync("after emptying stock that once covered a short");
    }

    [ReferenceTheory]
    [InlineData(Wa)]
    [InlineData(Fifo)]
    public async Task Reversing_a_receipt_that_covered_a_short_reopens_it(string method)
    {
        await using var w = await CostingWorld.StartAsync(method, allowNegative: true);
        await w.BuyAsync(2, 10, 1);
        await w.IssueAsync(5, 1);
        await w.BuyAsync(10, 12, 2);
        await w.RefusedAsync<ConsumedLayerException>(() => w.ReverseAsync("PI", 2));

        await w.ReverseAsync("PI", 2, allowVariance: true);
        Assert.Equal(-3m, await w.Costing.OnHandAsync(w.Product));
        Assert.Equal(0m, (await w.LayersAsync()).Sum(l => l.Qty));
        await w.TiesAsync("after reversing the covering receipt");

        await w.BuyAsync(4, 10, 3);
        Assert.Equal(1m, await w.Costing.OnHandAsync(w.Product));
        Assert.Equal(1m, (await w.LayersAsync()).Sum(l => l.Qty));
        await w.TiesAsync("after re-covering the short");
    }

    [ReferenceFact]
    public async Task Reversing_an_over_issue_after_its_short_was_covered()
    {
        await using var w = await CostingWorld.StartAsync(Fifo, allowNegative: true);
        await w.BuyAsync(2, 10, 1);
        await w.IssueAsync(5, 1);
        await w.BuyAsync(10, 12, 2);
        await w.ReverseAsync("SI", 1);
        Assert.Equal(12m, await w.Costing.OnHandAsync(w.Product));
        Assert.Equal(12m, (await w.LayersAsync()).Sum(l => l.Qty));
        await w.TiesAsync("after reversing the issue that opened the short");
        var rowIds = await w.Db.Set<StockLedger>().Where(r => r.ProductId == w.Product).Select(r => (int?)r.Id).ToListAsync();
        Assert.Equal(0, await w.Db.Set<StockLayer>().CountAsync(l => l.ProductId == w.Product && !rowIds.Contains(l.SourceLedgerId)));
    }

    [ReferenceFact]
    public async Task Record_in_refuses_non_positive_quantities()
    {
        await using var w = await CostingWorld.StartAsync();
        var e = await w.RefusedAsync<Accountix.Inventory.Services.StockQuantityException>(() => w.BuyAsync(0, 10));
        Assert.Contains("must be positive", e.Message);
        await w.RefusedAsync<Accountix.Inventory.Services.StockQuantityException>(() => w.BuyAsync(-5, 10));
        await w.TiesAsync("after refused receipts");
    }

    [ReferenceFact]
    public async Task Wa_receipt_reversal_refuses_stranded_posted_value()
    {
        await using var w = await CostingWorld.StartAsync();
        await w.BuyAsync(10, 10, 1);
        await w.BuyAsync(10, 20, 2);
        await w.IssueAsync(10, 1);
        var e = await w.RefusedAsync<ConsumedLayerException>(() => w.ReverseAsync("PI", 2));
        Assert.Contains("already been issued", e.Message);
        Assert.Equal(10m, await w.Costing.OnHandAsync(w.Product));
        await w.TiesAsync("after refused WA reversal");
    }

    // ── Reversal ──

    [ReferenceFact]
    public async Task Reversing_an_issue_gives_back_the_layer_quantity()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        await w.BuyAsync(10, 10, 1);
        await w.BuyAsync(10, 20, 2);
        await w.IssueAsync(10, 1);
        Assert.Equal(L((20m, 10m)), await w.LayersAsync());
        await w.ReverseAsync("SI", 1);
        Assert.Equal(L((10m, 10m), (20m, 10m)), await w.LayersAsync());
        Assert.Equal(20m, await w.Costing.OnHandAsync(w.Product));
        await w.TiesAsync("after reversing an issue");
        Assert.Equal(0, await w.Db.Set<LayerConsumption>().CountAsync(c => c.ProductId == w.Product));
    }

    [ReferenceFact]
    public async Task Reversing_a_receipt_withdraws_its_layer()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        await w.BuyAsync(10, 10, 1);
        await w.BuyAsync(10, 20, 2);
        await w.ReverseAsync("PI", 2);
        Assert.Equal(L((10m, 10m)), await w.LayersAsync());
        Assert.Equal(10m, await w.Costing.OnHandAsync(w.Product));
        await w.TiesAsync("after reversing a receipt");
    }

    [ReferenceFact]
    public async Task Reversing_a_consumed_receipt_is_refused()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        await w.BuyAsync(10, 10, 1);
        await w.BuyAsync(10, 20, 2);
        await w.IssueAsync(10, 1);
        var e = await w.RefusedAsync<ConsumedLayerException>(() => w.ReverseAsync("PI", 1));
        Assert.Contains("already been issued", e.Message);
        Assert.Equal(10m, await w.Costing.OnHandAsync(w.Product));
        await w.TiesAsync("after refused reversal");
    }

    [ReferenceFact]
    public async Task Consumed_receipt_reversible_once_its_issue_is_reversed()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        await w.BuyAsync(10, 10, 1);
        await w.BuyAsync(10, 20, 2);
        await w.IssueAsync(10, 1);
        await w.ReverseAsync("SI", 1);
        await w.ReverseAsync("PI", 1);
        Assert.Equal(L((20m, 10m)), await w.LayersAsync());
        Assert.Equal(10m, await w.Costing.OnHandAsync(w.Product));
        await w.TiesAsync("after reversing issue then receipt");
    }

    [ReferenceFact]
    public async Task Reversing_a_merged_weighted_average_receipt_ties()
    {
        await using var w = await CostingWorld.StartAsync();
        await w.BuyAsync(10, 10, 1);
        await w.BuyAsync(10, 20, 2);
        Assert.Single(await w.LayersAsync());
        await w.ReverseAsync("PI", 2);
        Assert.Equal(10m, await w.Costing.OnHandAsync(w.Product));
        Assert.Equal(L((10m, 10m)), await w.LayersAsync());
        await w.TiesAsync("after reversing a merged WA receipt");
    }

    [ReferenceFact]
    public async Task Reversing_a_consumed_weighted_average_receipt_is_refused()
    {
        await using var w = await CostingWorld.StartAsync();
        await w.BuyAsync(10, 10, 1);
        await w.BuyAsync(10, 20, 2);
        await w.IssueAsync(15, 1);
        var e = await w.RefusedAsync<ConsumedLayerException>(() => w.ReverseAsync("PI", 2));
        Assert.Contains("remain on hand", e.Message);
        Assert.Equal(5m, await w.Costing.OnHandAsync(w.Product));
        await w.TiesAsync("after refused WA reversal");
    }

    [ReferenceFact]
    public async Task Reversing_an_issue_under_weighted_average_ties()
    {
        await using var w = await CostingWorld.StartAsync();
        await w.BuyAsync(10, 10, 1);
        await w.BuyAsync(10, 20, 2);
        await w.IssueAsync(8, 1);
        await w.ReverseAsync("SI", 1);
        Assert.Equal(20m, await w.Costing.OnHandAsync(w.Product));
        Assert.Equal(L((15m, 20m)), await w.LayersAsync());
        await w.TiesAsync("after reversing a WA issue");
    }

    // ── Sales returns ──

    [ReferenceFact]
    public async Task Original_issue_cost_reads_the_frozen_cost()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        await w.BuyAsync(10, 10, 1);
        await w.BuyAsync(10, 20, 2);
        var (_, cogs) = await w.IssueAsync(10, 1);
        Assert.Equal(10.0000m, await w.Costing.OriginalIssueCostAsync("SI", w.V(1), w.Product));
        Assert.Equal(cogs, (await w.Costing.OriginalIssueCostAsync("SI", w.V(1), w.Product))!.Value * 10);
        Assert.Equal(20.0000m, await w.Costing.CurrentUnitCostAsync(w.Product));
        Assert.Null(await w.Costing.OriginalIssueCostAsync("SI", w.V(99), w.Product));
    }

    [ReferenceFact]
    public async Task Sell_then_return_is_value_neutral()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        await w.BuyAsync(10, 10, 1);
        await w.BuyAsync(10, 20, 2);
        await w.IssueAsync(10, 1);
        decimal basis = (await w.Costing.OriginalIssueCostAsync("SI", w.V(1), w.Product))!.Value;
        await w.Costing.ReceiveAsync(w.Product, "SRV", w.V(1), "CN-000001", 10, basis, "", w.User);
        Assert.Equal(20m, await w.Costing.OnHandAsync(w.Product));
        Assert.Equal(300m, await w.Costing.StockValueAsync(w.Product));
        await w.TiesAsync("after sell + return");
    }

    [ReferenceFact]
    public async Task Returned_goods_form_a_new_layer_at_the_sold_cost()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        await w.BuyAsync(10, 10, 1);
        await w.BuyAsync(10, 20, 2);
        await w.IssueAsync(10, 1);
        decimal basis = (await w.Costing.OriginalIssueCostAsync("SI", w.V(1), w.Product))!.Value;
        await w.Costing.ReceiveAsync(w.Product, "SRV", w.V(1), "CN-000001", 10, basis, "", w.User);
        Assert.Equal(L((20m, 10m), (10m, 10m)), await w.LayersAsync());
        var (_, next) = await w.IssueAsync(10, 2);
        Assert.Equal(200.00m, next);
        await w.TiesAsync("after re-selling");
    }

    [ReferenceFact]
    public async Task Weighted_average_return_reaverages_at_the_sold_cost()
    {
        await using var w = await CostingWorld.StartAsync();
        await w.BuyAsync(10, 10, 1);
        await w.BuyAsync(10, 20, 2);
        var (_, cogs) = await w.IssueAsync(10, 1);
        Assert.Equal(150.00m, cogs);
        decimal basis = (await w.Costing.OriginalIssueCostAsync("SI", w.V(1), w.Product))!.Value;
        Assert.Equal(15.0000m, basis);
        await w.Costing.ReceiveAsync(w.Product, "SRV", w.V(1), "CN-000001", 10, basis, "", w.User);
        Assert.Equal(20m, await w.Costing.OnHandAsync(w.Product));
        Assert.Equal(15.0000m, await w.Costing.CurrentUnitCostAsync(w.Product));
        Assert.Equal(300m, await w.Costing.StockValueAsync(w.Product));
        await w.TiesAsync("after WA return");
    }

    [ReferenceFact]
    public async Task Reversing_a_sales_return_withdraws_the_returned_stock()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        await w.BuyAsync(10, 10, 1);
        await w.IssueAsync(10, 1);
        decimal basis = (await w.Costing.OriginalIssueCostAsync("SI", w.V(1), w.Product))!.Value;
        await w.Costing.ReceiveAsync(w.Product, "SRV", w.V(1), "CN-000001", 10, basis, "", w.User);
        Assert.Equal(10m, await w.Costing.OnHandAsync(w.Product));
        await w.ReverseAsync("SRV", 1);
        Assert.Equal(0m, await w.Costing.OnHandAsync(w.Product));
        Assert.Equal(0m, await w.Costing.StockValueAsync(w.Product));
        await w.TiesAsync("after reversing a sales return");
    }

    [ReferenceFact]
    public async Task Reversing_a_resold_sales_return_is_refused()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        await w.BuyAsync(10, 10, 1);
        await w.IssueAsync(10, 1);
        decimal basis = (await w.Costing.OriginalIssueCostAsync("SI", w.V(1), w.Product))!.Value;
        await w.Costing.ReceiveAsync(w.Product, "SRV", w.V(1), "CN-000001", 10, basis, "", w.User);
        await w.IssueAsync(10, 2);
        await w.RefusedAsync<ConsumedLayerException>(() => w.ReverseAsync("SRV", 1));
    }

    // ── Correcting an earlier receipt: later issues are re-costed ──

    [ReferenceFact]
    public async Task Withdrawing_a_consumed_receipt_recosts_the_issue()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        await w.BuyAsync(10, 10, 1);
        await w.BuyAsync(10, 20, 2);
        var (_, cogs) = await w.IssueAsync(10, 1);
        Assert.Equal(100.00m, cogs);
        var moved = await w.ReverseAsync("PI", 1, allowVariance: true);
        Assert.Equal(100.00m, moved[w.Product]);
        Assert.Equal(0m, await w.Costing.OnHandAsync(w.Product));
        Assert.Equal(0m, await w.Costing.StockValueAsync(w.Product));
        await w.TiesAsync("after withdrawing a consumed receipt");
        Assert.Equal(200.00m, await w.CostOfAsync("SI", 1));
        var adj = await w.AdjustmentsAsync();
        Assert.Equal([("SI-00001", 100.00m, 200.00m)], adj.Select(a => (a.VoucherNumber, a.OldCost, a.NewCost)).ToList());
        Assert.NotNull(adj[0].JournalEntryId);
    }

    [ReferenceFact]
    public async Task No_variance_rows_are_written_any_more()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        await w.BuyAsync(10, 10, 1);
        await w.BuyAsync(10, 20, 2);
        await w.IssueAsync(10, 1);
        await w.ReverseAsync("PI", 1, allowVariance: true);
        Assert.Equal(0, await w.Db.Set<StockLedger>().CountAsync(r => r.ProductId == w.Product && r.VoucherType == "VAR"));
    }

    [ReferenceFact]
    public async Task No_adjustment_when_the_reversed_receipt_was_untouched()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        await w.BuyAsync(10, 10, 1);
        await w.BuyAsync(10, 20, 2);
        var moved = await w.ReverseAsync("PI", 2, allowVariance: true);
        Assert.Empty(moved);
        Assert.Empty(await w.AdjustmentsAsync());
        await w.TiesAsync("after a clean reversal");
    }

    [ReferenceFact]
    public async Task Weighted_average_recosts_the_issue_from_the_surviving_pool()
    {
        await using var w = await CostingWorld.StartAsync();
        await w.BuyAsync(10, 10, 1);
        await w.BuyAsync(10, 20, 2);
        var (_, cogs) = await w.IssueAsync(5, 1);
        Assert.Equal(75.00m, cogs);
        var moved = await w.ReverseAsync("PI", 1, allowVariance: true);
        Assert.Equal(25.00m, moved[w.Product]);
        Assert.Equal(5m, await w.Costing.OnHandAsync(w.Product));
        Assert.Equal(20.0000m, await w.Costing.CurrentUnitCostAsync(w.Product));
        Assert.Equal(100.00m, await w.Costing.StockValueAsync(w.Product));
        await w.TiesAsync("after WA re-cost");
    }

    [ReferenceFact]
    public async Task Withdrawn_receipt_leaves_a_short_until_it_is_corrected()
    {
        await using var w = await CostingWorld.StartAsync();
        var d1 = CostingWorld.Day(10);
        var d2 = CostingWorld.Day(5);
        await w.BuyAsync(10, 10, 1, d1);
        var (_, cogs) = await w.IssueAsync(5, 1, when: d2);
        Assert.Equal(50.00m, cogs);

        await w.ReverseAsync("PI", 1, allowVariance: true);
        Assert.Equal(-5m, await w.Costing.OnHandAsync(w.Product));
        Assert.Equal(50.00m, await w.CostOfAsync("SI", 1));
        Assert.Empty(await w.AdjustmentsAsync());
        await w.TiesAsync("while the purchase is withdrawn");

        await w.BuyAsync(10, 12, 1, d1);
        Assert.Equal(60.00m, await w.CostOfAsync("SI", 1));
        Assert.Equal(5m, await w.Costing.OnHandAsync(w.Product));
        Assert.Equal(60.00m, await w.Costing.StockValueAsync(w.Product));
        Assert.Equal([(50.00m, 60.00m)], (await w.AdjustmentsAsync()).Select(a => (a.OldCost, a.NewCost)).ToList());
        await w.TiesAsync("after the corrected purchase is re-approved");
    }

    [ReferenceFact]
    public async Task Reversal_still_refuses_by_default()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        await w.BuyAsync(10, 10, 1);
        await w.BuyAsync(10, 20, 2);
        await w.IssueAsync(10, 1);
        await w.RefusedAsync<ConsumedLayerException>(() => w.ReverseAsync("PI", 1));
    }

    [ReferenceFact]
    public async Task Reversal_names_the_vouchers_that_consumed_the_stock()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        await w.BuyAsync(10, 10, 1);
        await w.IssueAsync(4, 1, "CONS");
        await w.IssueAsync(3, 2, "SCRAP");
        var e = await w.RefusedAsync<ConsumedLayerException>(() => w.ReverseAsync("PI", 1));
        Assert.Contains("CONS-00001", e.Message);
        Assert.Contains("SCRAP-00002", e.Message);
        Assert.Equal(2, (await w.Costing.ConsumersOfVoucherAsync("PI", w.V(1))).Count);
    }

    // ── Rounding drift ──

    [ReferenceFact]
    public async Task Reaveraging_the_pool_does_not_drift_from_the_ledger()
    {
        await using var w = await CostingWorld.StartAsync();
        for (int n = 1; n <= 40; n++)
        {
            await w.BuyAsync(7, 10.33m + n / 100m, n);
            await w.IssueAsync(3, n);
        }
        var (_, layers, running) = await w.Costing.AssertInvariantAsync(w.Product);
        Assert.Equal(running, layers);
        var stale = await w.Db.Set<StockLayer>().Where(l => l.ProductId == w.Product && l.QtyRemaining > 0)
            .SumAsync(l => l.QtyRemaining * l.UnitCost);
        Assert.NotEqual(running, stale);
    }

    [ReferenceFact]
    public async Task Fifo_issues_spanning_layers_do_not_drift()
    {
        await using var w = await CostingWorld.StartAsync(Fifo);
        for (int n = 1; n <= 30; n++)
            await w.BuyAsync(2.5m, 7.77m + (decimal)n / 300m, n);
        for (int n = 1; n <= 20; n++)
            await w.IssueAsync(3.5m, n);
        var (_, layers, running) = await w.Costing.AssertInvariantAsync(w.Product);
        Assert.Equal(running, layers);
    }

    [ReferenceFact]
    public async Task Reversing_an_issue_restores_the_layer_value_exactly()
    {
        await using var w = await CostingWorld.StartAsync();
        await w.BuyAsync(3, 10.0001m, 1);
        await w.BuyAsync(4, 13.3333m, 2);
        decimal before = await w.Costing.StockValueAsync(w.Product);
        await w.IssueAsync(2.5m, 9);
        await w.ReverseAsync("SI", 9);
        Assert.Equal(before, await w.Costing.StockValueAsync(w.Product));
        await w.TiesAsync("after an issue was reversed");
    }
}

/// <summary>A theory that needs the reference database.</summary>
public sealed class ReferenceTheoryAttribute : TheoryAttribute
{
    public ReferenceTheoryAttribute()
    {
        if (!Reference.Available)
            Skip = "Reference database not available: run dotnet/tools/make_reference_db.py";
    }
}
