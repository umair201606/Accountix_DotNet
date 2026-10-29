using System.Net;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;
using static Accountix.Inventory.Tests.VoucherKit;

namespace Accountix.Inventory.Tests;

/// <summary>
/// Several people moving one product's stock at the same instant
/// (tests/concurrency/test_concurrent_users.py, the stock scenarios): stock is
/// never sold twice, nothing is posted twice, nobody deadlocks, and the layers
/// keep backing the ledger.
/// </summary>
public class StockRaceTests
{
    private const int N = 6;

    private static async Task<List<(int Voucher, string Status)>> ConsumptionsOfAsync(int productId)
    {
        await using var db = Db();
        return await db.Set<ConsumptionItem>().Where(i => i.ProductId == productId)
            .Select(i => new ValueTuple<int, string>(i.VoucherId, i.Voucher.Status ?? "")).ToListAsync();
    }

    [ReferenceFact]
    public async Task Six_people_approving_one_consumption_move_its_stock_once()
    {
        int p = await StockedProductAsync(20, 10, Day(10));
        await using var host = await HostAsync();
        var first = host.ClientFor(OwnerB, CompanyB);
        var r = await first.PostFormAsync("/inventory/vouchers/consumption", IssueForm("unapproved", p, "3", Day(1), "Repairs"),
                                          "/inventory/vouchers/consumption");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        int vid = (await ConsumptionsOfAsync(p)).Single().Voucher;

        var clients = host.ClientsFor(OwnerB, CompanyB, N);
        var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync($"/inventory/vouchers/consumption/{vid}")));
        var rs = await Race.RunAsync(N, i => clients[i].PostFormWithTokenAsync($"/inventory/vouchers/consumption/{vid}",
            IssueForm("approved", p, "3", Day(1), "Repairs"), tokens[i]));

        Race.NoServerErrors(rs);
        Assert.Equal(17m, await OnHandAsync(p));
        await using var db = Db();
        Assert.Equal(1, await db.Set<JournalEntry>().CountAsync(e => e.VoucherType == "CONS" && e.VoucherId == vid && e.IsPosted == true));
        Assert.Equal(1, await db.Set<StockLedger>().CountAsync(x => x.VoucherType == "CONS" && x.VoucherId == vid));
        Assert.True(await TiesAsync(p));
    }

    [ReferenceFact]
    public async Task Stock_is_never_issued_twice()
    {
        // 10 on hand, six people each consume 4 at once, negative stock off: exactly two go through,
        // the rest are refused in words (and leave no voucher behind).
        int p = await StockedProductAsync(10, 100, Day(10));
        await using var host = await HostAsync();
        var clients = host.ClientsFor(OwnerB, CompanyB, N);
        var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync("/inventory/vouchers/consumption")));
        var rs = await Race.RunAsync(N, i => clients[i].PostFormWithTokenAsync("/inventory/vouchers/consumption",
            IssueForm("approved", p, "4", Day(1), $"Race {i}"), tokens[i]));

        Race.NoServerErrors(rs);
        var vouchers = await ConsumptionsOfAsync(p);
        Assert.Equal(2, vouchers.Count(v => v.Status == "approved"));
        Assert.Equal(2, vouchers.Count);
        Assert.Equal(2m, await OnHandAsync(p));
        Assert.True(await TiesAsync(p));
        await using var db = Db();
        var ids = vouchers.Select(v => v.Voucher).ToList();
        Assert.Equal(2, await db.Set<JournalEntry>().CountAsync(e => e.VoucherType == "CONS" && ids.Contains(e.VoucherId) && e.IsPosted == true));
    }

    [ReferenceFact]
    public async Task Vouchers_listing_two_products_in_opposite_orders_never_deadlock()
    {
        int a = await StockedProductAsync(50, 10, Day(10)), b = await StockedProductAsync(50, 10, Day(10));
        await using var host = await HostAsync();
        var clients = host.ClientsFor(OwnerB, CompanyB, N);
        var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync("/inventory/vouchers/scrap")));
        var rs = await Race.RunAsync(N, i =>
        {
            var (x, y) = i % 2 == 0 ? (a, b) : (b, a);
            return clients[i].PostFormWithTokenAsync("/inventory/vouchers/scrap",
            [
                new("reason", $"Mixed {i}"), new("date", Iso(Day(1))), new("charge_account_id", ""), new("label_id", ""),
                new("status", "approved"), new("product_id[]", x.ToString()), new("qty[]", "1"),
                new("product_id[]", y.ToString()), new("qty[]", "1"),
            ], tokens[i]);
        });
        Race.NoServerErrors(rs);
        Assert.All(rs, r => Assert.Equal(HttpStatusCode.Redirect, r.StatusCode));
        Assert.Equal(50m - N, await OnHandAsync(a));
        Assert.Equal(50m - N, await OnHandAsync(b));
        Assert.True(await TiesAsync(a));
        Assert.True(await TiesAsync(b));
    }

    [ReferenceFact]
    public async Task A_stock_take_reads_the_system_quantity_after_the_lock()
    {
        // 20 on hand; five people consume 2 each while a sixth approves a count of 25. Whatever the
        // order, the take's system quantity is what was on hand when it got the product: 20 less the
        // consumptions before it, and the count holds less the consumptions after it.
        int p = await StockedProductAsync(20, 10, Day(10));
        await using var host = await HostAsync();
        var clients = host.ClientsFor(OwnerB, CompanyB, N);
        var tokens = await Task.WhenAll(clients.Select((c, i) =>
            c.TokenFromAsync(i == 0 ? "/inventory/vouchers/stock-take" : "/inventory/vouchers/consumption")));
        var rs = await Race.RunAsync(N, i => i == 0
            ? clients[i].PostFormWithTokenAsync("/inventory/vouchers/stock-take",
            [
                new("location", "Race store"), new("date", Iso(Day(0))), new("label_id", ""), new("status", "approved"),
                new("product_id[]", p.ToString()), new("system_qty[]", "20"), new("physical_qty[]", "25"),
            ], tokens[i])
            : clients[i].PostFormWithTokenAsync("/inventory/vouchers/consumption",
                IssueForm("approved", p, "2", Day(0), $"Race {i}"), tokens[i]));
        Race.NoServerErrors(rs);

        await using var db = Db();
        var rows = await db.Set<StockLedger>().AsNoTracking().Where(x => x.ProductId == p).OrderBy(x => x.Id).ToListAsync();
        var adjRow = rows.Single(x => x.VoucherType == "ADJ");
        int before = rows.Count(x => x.VoucherType == "CONS" && x.Id < adjRow.Id);
        int after = rows.Count(x => x.VoucherType == "CONS" && x.Id > adjRow.Id);
        Assert.Equal(5, before + after);
        var item = await db.Set<StockTakeItem>().AsNoTracking().SingleAsync(i => i.ProductId == p);
        Assert.Equal(20m - 2 * before, item.SystemQty);
        Assert.Equal(25m - 2 * after, await OnHandAsync(p));
        Assert.True(await TiesAsync(p));
    }
}
