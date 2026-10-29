using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Orders.Tests;

/// <summary>
/// Several people on the same orders at the same moment (tests/concurrency
/// test_new_orders_saved_together_are_numbered and the approve / stale
/// scenarios), and the order pages on a full year of Samba. Python's times for
/// the same pages are kept, as measured on 29 Oct 2026, in docs/modules/orders.md.
/// </summary>
public class ManyUsersAndSpeedTests
{
    private static Task<ModuleHost> HostAsync() => ModuleHost.StartAsync(typeof(OrdersModule).Assembly);

    private static async Task<(World W, PageClient[] Clients, string[] Tokens)> PeopleAsync(ModuleHost host, int n)
    {
        var w = await World.SharedAsync();
        var clients = host.ClientsFor(w.Admin, w.Company, n);
        var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync("/inventory/sales/list")));
        return (w, clients, tokens);
    }

    [ReferenceFact]
    public async Task Six_new_orders_saved_at_once_get_six_distinct_numbers()
    {
        await using var host = await HostAsync();
        var (w, clients, tokens) = await PeopleAsync(host, 6);
        string tag = World.NewTag();
        var rs = await Race.RunAsync(6, i =>
            clients[i].PostJsonWithTokenAsync("/inventory/sales/save", w.SalesJson("save", $"race number {tag} {i}"), tokens[i]));
        Race.NoServerErrors(rs);
        Assert.All(rs, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        await using var db = w.Db();
        var orders = await db.Set<InvSalesOrder>().AsNoTracking().Where(o => o.Notes!.StartsWith($"race number {tag}"))
            .Select(o => new { o.Id, o.SoNumber }).ToListAsync();
        Assert.Equal(6, orders.Count);
        Assert.Equal(6, orders.Select(o => o.SoNumber).Distinct().Count());
        Assert.All(orders, o => Assert.EndsWith($"-{o.Id:D4}", o.SoNumber));
        Assert.DoesNotContain(orders, o => o.SoNumber == "(auto)" || o.SoNumber.StartsWith('~'));
        Assert.Equal(12, await db.Set<InvSalesOrderItem>().CountAsync(i => orders.Select(o => o.Id).Contains(i.SoId)));
    }

    [ReferenceFact]
    public async Task Six_people_approving_one_order_at_once_approve_it_once()
    {
        await using var host = await HostAsync();
        var (w, clients, tokens) = await PeopleAsync(host, 6);
        var (_, draft) = await World.PostAsync(clients[0], "/inventory/purchases/save", w.PurchaseJson("save", "race approve " + World.NewTag()), tokens[0]);
        int id = draft.GetProperty("id").GetInt32();

        var rs = await Race.RunAsync(6, i =>
            clients[i].PostJsonWithTokenAsync("/inventory/purchases/save", w.PurchaseJson("approve", $"approved by {i}", id, version: 0), tokens[i]));
        Race.NoServerErrors(rs);
        Assert.Equal(1, rs.Count(r => r.StatusCode == HttpStatusCode.OK));
        var refusals = await Task.WhenAll(rs.Where(r => r.StatusCode != HttpStatusCode.OK).Select(r => r.Content.ReadAsStringAsync()));
        Assert.All(refusals, t => Assert.Contains("Cannot modify approved order", t));

        await using var db = w.Db();
        var po = await db.Set<InvPurchaseOrder>().AsNoTracking().SingleAsync(o => o.Id == id);
        Assert.Equal(("approved", 1), (po.Status, po.EditVersion ?? 0));
        Assert.Equal(2, await db.Set<InvPurchaseOrderItem>().CountAsync(i => i.PoId == id));   // lines replaced once, not stacked
    }

    [ReferenceFact]
    public async Task Six_unapprovals_at_once_unapprove_once_and_six_stale_saves_let_one_through()
    {
        await using var host = await HostAsync();
        var (w, clients, tokens) = await PeopleAsync(host, 6);
        var (_, a) = await World.PostAsync(clients[0], "/inventory/sales/save", w.SalesJson("approve", "race unapprove " + World.NewTag()), tokens[0]);
        int id = a.GetProperty("id").GetInt32();

        var un = await Race.RunAsync(6, i => clients[i].PostJsonWithTokenAsync($"/inventory/sales/unapprove/{id}", new { }, tokens[i]));
        Race.NoServerErrors(un);
        Assert.Equal(1, un.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(un.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));

        // Everyone opened the unapproved order at version 1; all save at once: one wins, the rest are told it changed.
        var saves = await Race.RunAsync(6, i =>
            clients[i].PostJsonWithTokenAsync("/inventory/sales/save", w.SalesJson("save", $"edit {i}", id, version: 1), tokens[i]));
        Race.NoServerErrors(saves);
        Assert.Equal(1, saves.Count(r => r.StatusCode == HttpStatusCode.OK));
        foreach (var r in saves.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
            Assert.True(JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("stale").GetBoolean());
        }
        await using var db = w.Db();
        var o = await db.Set<InvSalesOrder>().AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(("unapproved", 2), (o.Status, o.EditVersion ?? 0));
        Assert.Equal(2, await db.Set<InvSalesOrderItem>().CountAsync(i => i.SoId == id));
    }

    [ReferenceFact]
    public async Task Order_pages_run_a_fixed_number_of_queries()
    {
        await using var host = await HostAsync();
        var w = await World.SharedAsync();
        var c = w.Client(host);
        string token = await c.TokenFromAsync("/inventory/sales/list");
        var (_, so) = await World.PostAsync(c, "/inventory/sales/save", w.SalesJson("approve", "queries"), token);
        var (_, po) = await World.PostAsync(c, "/inventory/purchases/save", w.PurchaseJson("approve", "queries"), token);
        foreach (string url in new[]
                 {
                     "/inventory/sales/list", "/inventory/purchases/list", "/inventory/sales/", "/inventory/purchases/",
                     $"/inventory/sales/{so.GetProperty("id").GetInt32()}", $"/inventory/purchases/{po.GetProperty("id").GetInt32()}",
                     $"/inventory/sales/api/orders/{w.Customer}", $"/inventory/purchases/api/orders/{w.Supplier}",
                     "/inventory/sales/api/products?q=", "/inventory/purchases/api/suppliers?q=",
                 })
            (await host.MeasureAsync(c, url, runs: 1)).Within(maxMilliseconds: 5000, maxQueries: 15);
    }

    // --- Full year of Samba (read only) ---

    private static async Task<(int BigSo, int BigPo, int SoCustomer, int PoSupplier)> SambaAsync()
    {
        var ctx = new CompanyContext();
        ctx.Set(Perf.CompanyId);
        await using var db = Perf.NewDb(ctx);
        var month = (Perf.BusiestMonthStart, Perf.BusiestMonthEnd);
        int bigSo = await db.Set<InvSalesOrder>().Where(o => o.OrderDate >= month.Item1 && o.OrderDate <= month.Item2)
            .OrderByDescending(o => o.InvSalesOrderItems.Count).ThenBy(o => o.Id).Select(o => o.Id).FirstAsync();
        int bigPo = await db.Set<InvPurchaseOrder>().Where(o => o.OrderDate >= month.Item1 && o.OrderDate <= month.Item2)
            .OrderByDescending(o => o.InvPurchaseOrderItems.Count).ThenBy(o => o.Id).Select(o => o.Id).FirstAsync();
        int cust = await db.Set<InvSalesOrder>().Where(o => o.Status == "approved").GroupBy(o => o.CustomerId)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstAsync();
        int sup = await db.Set<InvPurchaseOrder>().Where(o => o.Status == "approved").GroupBy(o => o.SupplierId)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstAsync();
        return (bigSo, bigPo, cust, sup);
    }

    /// <summary>The lists show every order of the company (Python pages none): the whole year.</summary>
    [PerfFact]
    public async Task Order_lists_for_the_whole_year_are_quick()
    {
        await using var host = await ModuleHost.StartOnAsync(Perf.ConnectionString, typeof(OrdersModule).Assembly);
        var c = host.ClientFor(Perf.AdminUserId, Perf.CompanyId);
        var so = (await host.MeasureAsync(c, "/inventory/sales/list")).Within(1500, 10);
        Assert.Equal(Perf.Rows("inv_sales_orders"), CountRows(so.Body, "/inventory/sales/"));
        var po = (await host.MeasureAsync(c, "/inventory/purchases/list")).Within(1500, 10);
        Assert.Equal(Perf.Rows("inv_purchase_orders"), CountRows(po.Body, "/inventory/purchases/"));
        (await host.MeasureAsync(c, "/inventory/sales/list?status=approved")).Within(800, 10);
        (await host.MeasureAsync(c, "/inventory/purchases/list?status=approved")).Within(800, 10);
    }

    private static int CountRows(string html, string prefix) =>
        System.Text.RegularExpressions.Regex.Matches(html, $"href=\"{prefix}\\d+\" class=\"btn btn-sm btn-outline\"").Count;

    /// <summary>The busiest month: its largest orders open in the editor, and the busiest party's orders in the picker.</summary>
    [PerfFact]
    public async Task Editors_and_pickers_for_the_busiest_month_are_quick()
    {
        var (bigSo, bigPo, cust, sup) = await SambaAsync();
        await using var host = await ModuleHost.StartOnAsync(Perf.ConnectionString, typeof(OrdersModule).Assembly);
        var c = host.ClientFor(Perf.AdminUserId, Perf.CompanyId);
        (await host.MeasureAsync(c, $"/inventory/sales/{bigSo}")).Within(800, 15);
        (await host.MeasureAsync(c, $"/inventory/purchases/{bigPo}")).Within(800, 15);
        (await host.MeasureAsync(c, "/inventory/sales/")).Within(800, 15);
        (await host.MeasureAsync(c, $"/inventory/sales/api/orders/{cust}")).Within(800, 10);
        (await host.MeasureAsync(c, $"/inventory/purchases/api/orders/{sup}")).Within(800, 10);
        (await host.MeasureAsync(c, "/inventory/sales/api/products?q=")).Within(800, 10);
        (await host.MeasureAsync(c, "/inventory/sales/api/customers?q=")).Within(800, 10);
    }
}
