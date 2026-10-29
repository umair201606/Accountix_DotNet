using System.Diagnostics;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Accountix.Inventory.Tests;

/// <summary>
/// The costing engine on a product with a year of history (a receipt and three
/// issues every day: 1,460 rows, more than the busiest product of the Samba year):
/// a back-dated receipt replays the whole year and re-costs every later issue,
/// with its re-costing journals, in well under a second and a fixed number of
/// statements (set-based, not one query per layer or per issue).
/// </summary>
public class CostingSpeedTests(ITestOutputHelper output)
{
    private static async Task LoadYearAsync(CostingWorld w, DateTime start, int days, string method)
    {
        var rows = new List<StockLedger>();
        for (int d = 0; d < days; d++)
        {
            var date = DateOnly.FromDateTime(start.AddDays(d));
            decimal unit = 10m + d % 7 * 0.37m;
            rows.Add(Row(w, "PI", 10_000 + d, "IN", 20m, unit, Math.Round(20m * unit, 2), date, method));
            for (int k = 0; k < 3; k++)
                rows.Add(Row(w, "SI", 20_000 + d * 3 + k, "OUT", 5m, 0m, 0m, date, method));
        }
        w.Db.AddRange(rows);
        await w.Db.SaveChangesAsync();
        await w.Costing.RecostFromStartAsync(w.Product, w.User);
    }

    private static StockLedger Row(CostingWorld w, string vt, int n, string type, decimal qty, decimal unit, decimal total, DateOnly date,
                                   string method) => new()
    {
        ProductId = w.Product, VoucherType = vt, VoucherId = w.V(n), VoucherNumber = $"{vt}-{n:D5}", TransactionType = type,
        Quantity = qty, UnitCost = unit, TotalCost = total, RunningQty = 0, RunningCost = 0, RunningAvg = 0,
        ValuationMethod = method, TxnDate = date, CreatedBy = w.User, CreatedAt = DateTime.UtcNow, Notes = "",
    };

    [ReferenceTheory]
    [InlineData("weighted_average")]
    [InlineData("fifo")]
    public async Task A_backdated_receipt_replays_a_year_of_history_quickly(string method)
    {
        var queries = new QueryCounter();
        await using var w = await CostingWorld.StartAsync(method, queries: queries);
        var start = DateTime.Today.AddDays(-400);
        // Warm up (JIT, statement preparation) on a short history of its own.
        int main = w.Product;
        w.Product = await w.NewProductAsync("Warm-up");
        await LoadYearAsync(w, start, 5, method);
        await w.BuyAsync(1, 1, 98, start);
        w.Product = main;
        await LoadYearAsync(w, start, 365, method);
        await w.TiesAsync("after loading the year");
        int rows = await w.Db.Set<StockLedger>().CountAsync(r => r.ProductId == w.Product);

        queries.Reset();
        var sw = Stopwatch.StartNew();
        await w.BuyAsync(30, 7.5m, 99, start.AddDays(1));
        sw.Stop();
        int statements = queries.Count;
        int recosted = (await w.AdjustmentsAsync()).Count;
        output.WriteLine($"{method}: {rows} rows, back-dated receipt in {sw.ElapsedMilliseconds} ms, {statements} statements, {recosted} issues re-costed");
        output.WriteLine(string.Join("\n", queries.Repeated(8)));
        await w.TiesAsync("after the back-dated receipt");
        Assert.True(recosted >= 400, $"only {recosted} issues re-costed: the scenario no longer exercises the replay");
        // Statements do not grow with the history (set-based replay): the stable measure on a shared machine.
        Assert.True(statements <= 40, $"{statements} statements: {string.Join("\n", queries.Repeated(5))}");
        // Quiet machine: 0.45-0.6 s with 404 re-costed issues, ~1.1 s with 1,087 (Python: 18.5 s / 29.4 s).
        Assert.True(sw.ElapsedMilliseconds < 2500, $"{sw.ElapsedMilliseconds} ms");
    }
}
