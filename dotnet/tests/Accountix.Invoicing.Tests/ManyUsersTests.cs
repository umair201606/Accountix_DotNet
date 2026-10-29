using System.Net;
using System.Text.Json;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Invoicing.Tests;

/// <summary>
/// Several people on one invoice at the same instant (tests/concurrency/test_concurrent_users.py):
/// posted once, numbered apart, never billed past an order, never overwritten from a stale copy.
/// </summary>
public class ManyUsersTests
{
    private const string SiSave = "/inventory/invoices/save", PiSave = "/inventory/purchase-invoice/save";

    private static async Task<int> PostedEntriesAsync(string type, int id)
    {
        await using var db = Fx.Db();
        return await db.Set<JournalEntry>().CountAsync(e => e.VoucherType == type && e.VoucherId == id && e.IsPosted == true);
    }

    private static Dictionary<string, object?> With(Dictionary<string, object?> d, params (string K, object? V)[] kv)
    {
        var copy = new Dictionary<string, object?>(d);
        foreach (var (k, v) in kv) copy[k] = v;
        return copy;
    }

    private static async Task<int> DraftAsync(PageClient c, string url, string page, Dictionary<string, object?> payload)
    {
        var r = await c.PostJsonAsync(url, With(payload, ("action", "save")), page);
        using var j = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return j.RootElement.GetProperty("id").GetInt32();
    }

    [ReferenceFact]
    public async Task Six_people_approving_one_sales_invoice_post_it_once()
    {
        var p = await Fx.PartiesAsync();
        await using var host = await Fx.HostAsync();
        await Fx.StockUpAsync(host.ClientFor(Fx.OwnerB, Fx.CompanyB), p);
        int id = await DraftAsync(host.ClientFor(Fx.OwnerB, Fx.CompanyB), SiSave, "/inventory/invoices/", InvoiceGoldenTests.SiGeneral(p));
        var clients = host.ClientsFor(Fx.OwnerB, Fx.CompanyB, 6);
        var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync($"/inventory/invoices/{id}")));
        var payload = With(InvoiceGoldenTests.SiGeneral(p), ("id", id));
        var rs = await Race.RunAsync(6, i => clients[i].PostJsonWithTokenAsync(SiSave, payload, tokens[i]));

        Race.NoServerErrors(rs);
        Assert.Equal(1, rs.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(5, rs.Count(r => r.StatusCode == HttpStatusCode.BadRequest));
        Assert.Equal(1, await PostedEntriesAsync("SI", id));
        Assert.Equal(2, await Fx.StockRowsAsync("SI", id));   // stock moved once: one issue per line
        await using var db = Fx.Db();
        Assert.Equal(97m, db.Set<InvProduct>().Single(x => x.Id == p.P1).CurrentStock);
    }

    [ReferenceFact]
    public async Task Six_people_approving_one_purchase_invoice_post_it_once()
    {
        var p = await Fx.PartiesAsync();
        await using var host = await Fx.HostAsync();
        int id = await DraftAsync(host.ClientFor(Fx.OwnerB, Fx.CompanyB), PiSave, "/inventory/purchase-invoice/", InvoiceGoldenTests.PiFull(p));
        var clients = host.ClientsFor(Fx.OwnerB, Fx.CompanyB, 6);
        var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync($"/inventory/purchase-invoice/{id}")));
        var payload = With(InvoiceGoldenTests.PiFull(p), ("id", id));
        var rs = await Race.RunAsync(6, i => clients[i].PostJsonWithTokenAsync(PiSave, payload, tokens[i]));

        Race.NoServerErrors(rs);
        Assert.Equal(1, rs.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, await PostedEntriesAsync("PI", id));
        Assert.Equal(2, await Fx.StockRowsAsync("PI", id));
    }

    [ReferenceFact]
    public async Task Six_new_invoices_saved_together_get_distinct_numbers_from_their_ids()
    {
        var p = await Fx.PartiesAsync();
        await using var host = await Fx.HostAsync();
        var clients = host.ClientsFor(Fx.OwnerB, Fx.CompanyB, 6);
        var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync("/inventory/invoices/")));
        var rs = await Race.RunAsync(6, i => clients[i].PostJsonWithTokenAsync(SiSave,
            With(InvoiceGoldenTests.SiGeneral(p, "save"), ("invoice_number", "(auto)"), ("notes", $"race {i}")), tokens[i]));

        Race.NoServerErrors(rs);
        var bodies = await Task.WhenAll(rs.Select(async r => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone()));
        Assert.All(bodies, b => Assert.True(b.GetProperty("ok").GetBoolean()));
        var numbers = bodies.Select(b => (Id: b.GetProperty("id").GetInt32(), Number: b.GetProperty("number").GetString()!)).ToList();
        Assert.Equal(6, numbers.Select(n => n.Number).Distinct().Count());
        Assert.All(numbers, n => Assert.EndsWith($"-{n.Id:D4}", n.Number));
        await using var db = Fx.Db();
        var ids = numbers.Select(n => n.Id).ToList();
        Assert.All(db.Set<InvInvoice>().Where(i => ids.Contains(i.Id)).ToList(), i => Assert.Equal(i.InvoiceNumber, i.VoucherNumber));
    }

    [ReferenceFact]
    public async Task Invoices_billing_one_order_line_together_never_bill_past_its_balance()
    {
        var p = await Fx.PartiesAsync();
        await using (var setup = Fx.Db())
        {
            var so = new InvSalesOrder
            {
                SoNumber = $"SO-R-{p.Tag}", CustomerId = p.Customer, Status = "approved", FulfilmentStatus = "open", TaxMode = "general",
                OrderDate = new DateOnly(2026, 5, 1), CreatedAt = DateTime.UtcNow, EditVersion = 0, Subtotal = 12500, TotalAmount = 12500,
            };
            setup.Add(so);
            await setup.SaveChangesAsync();
            setup.Add(new InvSalesOrderItem
            {
                SoId = so.Id, ProductId = p.P1, Description = "Panel on order", Unit = "pcs", Quantity = 5, InvoicedQty = 0,
                UnitPrice = 2500, SalesTaxPct = 0, TotalBeforeDiscount = 12500, TotalAfterDiscount = 12500, TotalPrice = 12500,
            });
            await setup.SaveChangesAsync();
        }
        int orderId, lineId;
        await using (var db = Fx.Db())
        {
            var o = db.Set<InvSalesOrder>().Single(x => x.SoNumber == $"SO-R-{p.Tag}");
            orderId = o.Id;
            lineId = db.Set<InvSalesOrderItem>().Single(x => x.SoId == o.Id).Id;
        }
        await using var host = await Fx.HostAsync();
        await Fx.StockUpAsync(host.ClientFor(Fx.OwnerB, Fx.CompanyB), p);
        var clients = host.ClientsFor(Fx.OwnerB, Fx.CompanyB, 6);
        var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync("/inventory/invoices/")));
        var rs = await Race.RunAsync(6, i => clients[i].PostJsonWithTokenAsync(SiSave, InvoiceRuleTests.BillOrder(p, orderId, lineId, 2), tokens[i]));

        Race.NoServerErrors(rs);
        int billed = rs.Count(r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(2, billed);   // 2 + 2 fit in 5; a third would take it to 6
        await using var check = Fx.Db();
        Assert.Equal(4m, check.Set<InvSalesOrderItem>().Single(x => x.Id == lineId).InvoicedQty);
        Assert.Equal("partial", check.Set<InvSalesOrder>().Single(x => x.Id == orderId).FulfilmentStatus);
    }

    [ReferenceFact]
    public async Task Two_invoices_selling_the_last_units_cannot_both_succeed()
    {
        var p = await Fx.PartiesAsync();
        await using var host = await Fx.HostAsync();
        await Fx.StockUpAsync(host.ClientFor(Fx.OwnerB, Fx.CompanyB), p, q1: 5, q2: 50);
        Dictionary<string, object?> Sell(int i) => new()
        {
            ["customer_id"] = p.Customer, ["invoice_date"] = "2026-05-16", ["action"] = "approve", ["discount_mode"] = "general",
            ["charges_mode"] = "general", ["tax_mode"] = "general", ["subtotal"] = 10000, ["total_discount"] = 0, ["notes"] = $"last units {i}",
            ["items"] = new object[]
            {
                new Dictionary<string, object?> { ["product_id"] = p.P1, ["description"] = "Panel", ["quantity"] = 4, ["unit_price"] = 2500,
                    ["total_before_discount"] = 10000, ["total_after_discount"] = 10000 },
            },
        };
        var clients = host.ClientsFor(Fx.OwnerB, Fx.CompanyB, 2);
        var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync("/inventory/invoices/")));
        var rs = await Race.RunAsync(2, i => clients[i].PostJsonWithTokenAsync(SiSave, Sell(i), tokens[i]));

        Race.NoServerErrors(rs);
        Assert.Equal(1, rs.Count(r => r.StatusCode == HttpStatusCode.OK));
        var refused = rs.Single(r => r.StatusCode != HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.False(JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement.GetProperty("ok").GetBoolean());
        await using var db = Fx.Db();
        Assert.Equal(1m, db.Set<InvProduct>().Single(x => x.Id == p.P1).CurrentStock);
        Assert.Equal(1, db.Set<StockLedger>().Count(s => s.ProductId == p.P1 && s.TransactionType == "OUT"));
        Assert.Equal(1, db.Set<InvInvoice>().Count(i => i.CustomerId == p.Customer && i.VoucherStatus == "approved"));
    }

    [ReferenceFact]
    public async Task Two_saves_from_the_same_copy_keep_the_first_and_refuse_the_second()
    {
        var p = await Fx.PartiesAsync();
        await using var host = await Fx.HostAsync();
        int id = await DraftAsync(host.ClientFor(Fx.OwnerB, Fx.CompanyB), SiSave, "/inventory/invoices/", InvoiceGoldenTests.SiGeneral(p));
        var clients = host.ClientsFor(Fx.OwnerB, Fx.CompanyB, 2);
        var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync($"/inventory/invoices/{id}")));
        var rs = await Race.RunAsync(2, i => clients[i].PostJsonWithTokenAsync(SiSave,
            With(InvoiceGoldenTests.SiGeneral(p, "save"), ("id", id), ("edit_version", 0), ("notes", $"copy {i}")), tokens[i]));

        Race.NoServerErrors(rs);
        Assert.Equal(1, rs.Count(r => r.StatusCode == HttpStatusCode.OK));
        var refused = rs.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement;
        Assert.True(body.GetProperty("stale").GetBoolean());
        await using var db = Fx.Db();
        Assert.Equal(1, db.Set<InvInvoice>().Single(i => i.Id == id).EditVersion);
    }
}
