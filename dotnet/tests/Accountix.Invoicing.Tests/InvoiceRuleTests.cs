using System.Net;
using System.Text.Json;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Invoicing.Tests;

/// <summary>The invoice rules through the .NET pages: versions, locks, refusals, order billing, rights.</summary>
public class InvoiceRuleTests
{
    private const string SiSave = "/inventory/invoices/save", SiPage = "/inventory/invoices/";
    private const string PiSave = "/inventory/purchase-invoice/save", PiPage = "/inventory/purchase-invoice/";

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(PageClient c, string url, object payload, string page)
    {
        var r = await c.PostJsonAsync(url, payload, page);
        string text = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    private static Dictionary<string, object?> With(Dictionary<string, object?> d, params (string K, object? V)[] kv)
    {
        var copy = new Dictionary<string, object?>(d);
        foreach (var (k, v) in kv) copy[k] = v;
        return copy;
    }

    [ReferenceFact]
    public async Task A_new_invoice_is_numbered_from_its_id_and_a_stale_copy_is_refused_with_who_and_when()
    {
        var p = await Fx.PartiesAsync();
        await using var host = await Fx.HostAsync();
        var c = host.ClientFor(Fx.OwnerB, Fx.CompanyB);
        var (st, saved) = await PostAsync(c, SiSave, InvoiceGoldenTests.SiGeneral(p, "save"), SiPage);
        Assert.Equal(HttpStatusCode.OK, st);
        int id = saved.GetProperty("id").GetInt32();
        Assert.Equal(0, saved.GetProperty("edit_version").GetInt32());
        Assert.Equal("unapproved", saved.GetProperty("voucher_status").GetString());
        Assert.Matches($@"^SI-\d{{6}}-{id:D4}$", saved.GetProperty("number").GetString());
        Assert.Equal("Invoice saved", saved.GetProperty("message").GetString());

        var edit = With(InvoiceGoldenTests.SiGeneral(p, "save"), ("id", id), ("edit_version", 0), ("notes", "second"));
        var (st2, second) = await PostAsync(c, SiSave, edit, $"/inventory/invoices/{id}");
        Assert.Equal(HttpStatusCode.OK, st2);
        Assert.Equal(1, second.GetProperty("edit_version").GetInt32());
        Assert.Equal("Invoice changes saved", second.GetProperty("message").GetString());

        var (st3, stale) = await PostAsync(c, SiSave, edit, $"/inventory/invoices/{id}");
        Assert.Equal(HttpStatusCode.Conflict, st3);
        Assert.True(stale.GetProperty("stale").GetBoolean());
        Assert.Contains("after you opened it, so your changes were not saved", stale.GetProperty("error").GetString());
        await using var db = Fx.Db();
        Assert.Equal("second", db.Set<InvInvoice>().Single(i => i.Id == id).Notes);
    }

    [ReferenceFact]
    public async Task Approve_locks_the_invoice_and_unapprove_takes_the_journal_off()
    {
        var p = await Fx.PartiesAsync();
        await using var host = await Fx.HostAsync();
        var c = host.ClientFor(Fx.OwnerB, Fx.CompanyB);
        await Fx.StockUpAsync(c, p);
        var (st, saved) = await PostAsync(c, SiSave, InvoiceGoldenTests.SiGeneral(p), SiPage);
        Assert.Equal(HttpStatusCode.OK, st);
        int id = saved.GetProperty("id").GetInt32();
        Assert.Equal("approved", saved.GetProperty("voucher_status").GetString());
        Assert.NotEmpty(await Fx.JournalAsync("SI", id));
        await using (var db = Fx.Db())
        {
            Assert.Equal(2, db.Set<InvStockMovement>().Count(m => m.ReferenceType == "sales_invoice" && m.ReferenceId == id));
            Assert.Equal(2, await Fx.StockRowsAsync("SI", id));
        }

        var (st2, locked) = await PostAsync(c, SiSave, With(InvoiceGoldenTests.SiGeneral(p, "save"), ("id", id)), $"/inventory/invoices/{id}");
        Assert.Equal(HttpStatusCode.BadRequest, st2);
        Assert.Equal("Cannot modify approved invoice", locked.GetProperty("error").GetString());
        var (st3, del) = await PostAsync(c, $"/inventory/invoices/delete/{id}", new { }, $"/inventory/invoices/{id}");
        Assert.Equal(HttpStatusCode.BadRequest, st3);
        Assert.Equal("Cannot delete an approved invoice. Unapprove it first.", del.GetProperty("error").GetString());

        var (st4, un) = await PostAsync(c, $"/inventory/invoices/unapprove/{id}", new { }, $"/inventory/invoices/{id}");
        Assert.Equal(HttpStatusCode.OK, st4);
        Assert.Equal("Invoice unapproved and unlocked", un.GetProperty("message").GetString());
        Assert.Empty(await Fx.JournalAsync("SI", id));
        await using (var db = Fx.Db())
        {
            var inv = db.Set<InvInvoice>().Single(i => i.Id == id);
            Assert.Equal("unapproved", inv.VoucherStatus);
            Assert.Null(inv.ApprovedAt);
            Assert.Equal(1, inv.EditVersion);
            Assert.Equal(0, db.Set<InvStockMovement>().Count(m => m.ReferenceType == "sales_invoice" && m.ReferenceId == id));
        }
        Assert.Equal(0, await Fx.StockRowsAsync("SI", id));   // the issues are gone from the cost history

        var (st5, gone) = await PostAsync(c, $"/inventory/invoices/delete/{id}", new { }, $"/inventory/invoices/{id}");
        Assert.Equal(HttpStatusCode.OK, st5);
        Assert.Equal("Invoice deleted successfully", gone.GetProperty("message").GetString());
    }

    [ReferenceFact]
    public async Task An_invoice_credited_by_a_return_or_reported_to_fbr_cannot_be_unapproved()
    {
        var p = await Fx.PartiesAsync();
        await using var host = await Fx.HostAsync();
        var c = host.ClientFor(Fx.OwnerB, Fx.CompanyB);
        await Fx.StockUpAsync(c, p);
        var (_, a) = await PostAsync(c, SiSave, InvoiceGoldenTests.SiGeneral(p), SiPage);
        int id = a.GetProperty("id").GetInt32();
        await using (var db = Fx.Db())
        {
            db.Add(new InvSalesReturn
            {
                ReturnNumber = $"CN-T-{p.Tag}", OriginalInvoiceId = id, CustomerId = p.Customer, Status = "approved",
                ReturnDate = DateTime.Today, CreatedAt = DateTime.UtcNow, EditVersion = 0,
            });
            await db.SaveChangesAsync();
        }
        var (st, refused) = await PostAsync(c, $"/inventory/invoices/unapprove/{id}", new { }, $"/inventory/invoices/{id}");
        Assert.Equal(HttpStatusCode.BadRequest, st);
        Assert.Equal($"Cannot unapprove: approved sales return(s) CN-T-{p.Tag} credit this invoice. Unapprove them first.",
            refused.GetProperty("error").GetString());

        var (_, b) = await PostAsync(c, SiSave, InvoiceGoldenTests.SiGeneral(p), SiPage);
        int id2 = b.GetProperty("id").GetInt32();
        await using (var db = Fx.Db())
        {
            db.Add(new FbrInvoiceRecord { InvInvoiceId = id2, Status = "success", FbrInvoiceNumber = "FBR-123", CreatedAt = DateTime.UtcNow, AttemptCount = 1 });
            await db.SaveChangesAsync();
        }
        var (st2, fbr) = await PostAsync(c, $"/inventory/invoices/unapprove/{id2}", new { }, $"/inventory/invoices/{id2}");
        Assert.Equal(HttpStatusCode.BadRequest, st2);
        Assert.Equal("Cannot unapprove: this invoice was reported to FBR as FBR-123. Correct it with a sales return (credit note).",
            fbr.GetProperty("error").GetString());
        Assert.NotEmpty(await Fx.JournalAsync("SI", id2));
    }

    [ReferenceFact]
    public async Task A_purchase_invoice_debited_by_an_approved_return_cannot_be_unapproved()
    {
        var p = await Fx.PartiesAsync();
        await using var host = await Fx.HostAsync();
        var c = host.ClientFor(Fx.OwnerB, Fx.CompanyB);
        int id = await Fx.StockUpAsync(c, p);
        await using (var db = Fx.Db())
        {
            db.Add(new InvPurchaseReturn
            {
                ReturnNumber = $"DN-T-{p.Tag}", OriginalInvoiceId = id, SupplierId = p.Supplier, Status = "approved",
                ReturnDate = DateTime.Today, CreatedAt = DateTime.UtcNow, EditVersion = 0,
            });
            await db.SaveChangesAsync();
        }
        var (st, refused) = await PostAsync(c, $"/inventory/purchase-invoice/unapprove/{id}", new { }, $"/inventory/purchase-invoice/{id}");
        Assert.Equal(HttpStatusCode.BadRequest, st);
        Assert.Equal($"Cannot unapprove: approved purchase return(s) DN-T-{p.Tag} were raised against this invoice. Unapprove them first.",
            refused.GetProperty("error").GetString());
        Assert.NotEmpty(await Fx.JournalAsync("PI", id));
        Assert.Equal(2, await Fx.StockRowsAsync("PI", id));
        // An unapproved return does not block it.
        await using (var db = Fx.Db())
        {
            var r = db.Set<InvPurchaseReturn>().Single(x => x.ReturnNumber == $"DN-T-{p.Tag}");
            r.Status = "unapproved";
            await db.SaveChangesAsync();
        }
        var (ok, _) = await PostAsync(c, $"/inventory/purchase-invoice/unapprove/{id}", new { }, $"/inventory/purchase-invoice/{id}");
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Equal(0, await Fx.StockRowsAsync("PI", id));
    }

    [ReferenceFact]
    public async Task Approval_validates_the_payload_and_the_ranges()
    {
        var p = await Fx.PartiesAsync();
        await using var host = await Fx.HostAsync();
        var c = host.ClientFor(Fx.OwnerB, Fx.CompanyB);
        var bad = With(InvoiceGoldenTests.SiGeneral(p), ("customer_id", ""), ("global_discount_pct", 150));
        var (st, body) = await PostAsync(c, SiSave, bad, SiPage);
        Assert.Equal(HttpStatusCode.BadRequest, st);
        Assert.Equal("Customer is required; Discount must be between 0 and 100%", body.GetProperty("error").GetString());
        var early = With(InvoiceGoldenTests.SiGeneral(p, "save"), ("due_date", "2026-05-01"));
        var (st2, body2) = await PostAsync(c, SiSave, early, SiPage);
        Assert.Equal(HttpStatusCode.BadRequest, st2);
        Assert.Equal("Due date cannot be before the invoice date", body2.GetProperty("error").GetString());
        var date = With(InvoiceGoldenTests.SiGeneral(p, "save"), ("invoice_date", "14/05/2026"));
        var (st3, body3) = await PostAsync(c, SiSave, date, SiPage);
        Assert.Equal(HttpStatusCode.BadRequest, st3);
        Assert.Equal("'14/05/2026' is not a valid date (YYYY-MM-DD).", body3.GetProperty("error").GetString());
        var pi = With(InvoiceGoldenTests.PiFull(p), ("supplier_id", null));
        var (st4, body4) = await PostAsync(c, PiSave, pi, PiPage);
        Assert.Equal(HttpStatusCode.BadRequest, st4);
        Assert.StartsWith("Supplier is required", body4.GetProperty("error").GetString());
    }

    private static async Task<(int OrderId, int LineId)> ApprovedOrderAsync(Fx.Parties p, decimal qty)
    {
        await using var db = Fx.Db();
        var so = new InvSalesOrder
        {
            SoNumber = $"SO-T-{p.Tag}-{Guid.NewGuid():N}"[..30], CustomerId = p.Customer, Status = "approved", FulfilmentStatus = "open",
            TaxMode = "general", OrderDate = new DateOnly(2026, 5, 1), CreatedAt = DateTime.UtcNow, EditVersion = 0,
            Subtotal = qty * 2500, TotalAmount = qty * 2500, TotalTax = 0, GlobalSalesTaxPct = 0,
        };
        db.Add(so);
        await db.SaveChangesAsync();
        var line = new InvSalesOrderItem
        {
            SoId = so.Id, ProductId = p.P1, Description = "Panel on order", Unit = "pcs", Quantity = qty, InvoicedQty = 0,
            UnitPrice = 2500, SalesTaxPct = 0, TotalBeforeDiscount = qty * 2500, TotalAfterDiscount = qty * 2500, TotalPrice = qty * 2500,
        };
        db.Add(line);
        await db.SaveChangesAsync();
        return (so.Id, line.Id);
    }

    public static Dictionary<string, object?> BillOrder(Fx.Parties p, int orderId, int lineId, double qty, string action = "approve") => new()
    {
        ["customer_id"] = p.Customer, ["invoice_date"] = "2026-05-16", ["action"] = action, ["discount_mode"] = "general",
        ["charges_mode"] = "general", ["tax_mode"] = "general", ["global_discount_pct"] = 0, ["global_sales_tax_pct"] = 0,
        ["subtotal"] = qty * 2500, ["total_discount"] = 0,
        ["items"] = new object[]
        {
            new Dictionary<string, object?> { ["product_id"] = p.P1, ["description"] = "Panel on order", ["quantity"] = qty, ["unit_price"] = 2500,
                ["total_before_discount"] = qty * 2500, ["total_after_discount"] = qty * 2500, ["source_order_id"] = orderId,
                ["source_order_item_id"] = lineId, ["source_order_number"] = "SO" },
        },
        ["charges"] = Array.Empty<object>(),
    };

    [ReferenceFact]
    public async Task Billing_an_order_moves_its_balance_and_refuses_over_billing()
    {
        var p = await Fx.PartiesAsync();
        var (orderId, lineId) = await ApprovedOrderAsync(p, 5);
        await using var host = await Fx.HostAsync();
        var c = host.ClientFor(Fx.OwnerB, Fx.CompanyB);
        await Fx.StockUpAsync(c, p);
        var (st, over) = await PostAsync(c, SiSave, BillOrder(p, orderId, lineId, 6), SiPage);
        Assert.Equal(HttpStatusCode.BadRequest, st);
        Assert.Equal("Over-invoicing blocked — Panel on order: billing 6 exceeds the uninvoiced balance 5", over.GetProperty("error").GetString());

        var (st2, ok) = await PostAsync(c, SiSave, BillOrder(p, orderId, lineId, 3), SiPage);
        Assert.Equal(HttpStatusCode.OK, st2);
        int id = ok.GetProperty("id").GetInt32();
        await using (var db = Fx.Db())
        {
            Assert.Equal(3m, db.Set<InvSalesOrderItem>().Single(i => i.Id == lineId).InvoicedQty);
            Assert.Equal("partial", db.Set<InvSalesOrder>().Single(o => o.Id == orderId).FulfilmentStatus);
        }
        // The picker shows what is left to bill.
        var picker = JsonDocument.Parse(await (await c.GetAsync($"/inventory/invoices/api/orders/{p.Customer}")).Content.ReadAsStringAsync()).RootElement;
        var order = picker.EnumerateArray().Single(o => o.GetProperty("id").GetInt32() == orderId);
        Assert.Equal(2.0, order.GetProperty("items")[0].GetProperty("balance_qty").GetDouble());
        Assert.Equal("partial", order.GetProperty("status").GetString());

        await PostAsync(c, $"/inventory/invoices/unapprove/{id}", new { }, $"/inventory/invoices/{id}");
        await using (var db = Fx.Db())
        {
            Assert.Equal(0m, db.Set<InvSalesOrderItem>().Single(i => i.Id == lineId).InvoicedQty);
            Assert.Equal("open", db.Set<InvSalesOrder>().Single(o => o.Id == orderId).FulfilmentStatus);
        }
    }

    [ReferenceFact]
    public async Task Pages_lookups_and_the_retired_pay_route_answer_as_python()
    {
        var p = await Fx.PartiesAsync();
        await using var host = await Fx.HostAsync();
        var c = host.ClientFor(Fx.OwnerB, Fx.CompanyB);
        var (_, saved) = await PostAsync(c, PiSave, InvoiceGoldenTests.PiFull(p, "save"), PiPage);
        Assert.Equal("new", saved.GetProperty("status").GetString());   // Python's column default on a new draft
        int pid = saved.GetProperty("id").GetInt32();
        Assert.Equal("Invoice saved as unapproved", saved.GetProperty("message").GetString());

        foreach (string url in new[] { "/invoicing/", "/inventory/invoices/list", "/inventory/purchase-invoice/list",
                                       "/inventory/invoices/list?month=2026-05", "/inventory/purchase-invoice/list?from=2026-05-01&to=2026-05-31&page=1",
                                       $"/inventory/purchase-invoice/{pid}" })
        {
            host.Queries.Reset();
            var r = await c.GetAsync(url);
            Assert.True(r.StatusCode == HttpStatusCode.OK, url);
            Assert.True(host.Queries.Count <= 40, $"{url}: {host.Queries.Count} queries\n{string.Join("\n", host.Queries.Repeated())}");
        }
        string list = await (await c.GetAsync("/inventory/purchase-invoice/list?month=2026-05")).Content.ReadAsStringAsync();
        Assert.Contains($"href=\"/inventory/purchase-invoice/{pid}\"", list);
        Assert.Contains(p.Tag, list);

        var products = JsonDocument.Parse(await (await c.GetAsync($"/inventory/invoices/api/products?q={p.Tag}")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(2, products.GetArrayLength());
        Assert.Equal(12.5, products.EnumerateArray().First(x => x.GetProperty("id").GetInt32() == p.P1).GetProperty("weight").GetDouble());
        var customers = JsonDocument.Parse(await (await c.GetAsync($"/inventory/invoices/api/customers?q={p.Tag}")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("NTN-1", customers[0].GetProperty("tax_id").GetString());
        var suppliers = JsonDocument.Parse(await (await c.GetAsync($"/inventory/purchase-invoice/api/suppliers?q={p.Tag}")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(p.Supplier, suppliers[0].GetProperty("id").GetInt32());
        var accounts = JsonDocument.Parse(await (await c.GetAsync("/inventory/invoices/api/accounts?q=4-01-01")).Content.ReadAsStringAsync()).RootElement;
        Assert.All(accounts.EnumerateArray(), a => Assert.Equal(5, a.GetProperty("code").GetString()!.Split('-').Length));

        var token = await c.TokenFromAsync($"/inventory/purchase-invoice/{pid}");
        var pay = await c.PostFormWithTokenAsync($"/inventory/purchase-invoice/pay/{pid}", [], token);
        Assert.Equal(HttpStatusCode.Redirect, pay.StatusCode);
        Assert.Equal($"/invoicing/tracking/invoice/PI/{pid}", pay.Headers.Location!.OriginalString);

        var np = await c.PostJsonAsync("/inventory/purchase-invoice/api/new-product",
            new { sku = $"NEW-{p.Tag}", name = "Brand new", unit_price = 10, cost_price = 7, unit = "box" }, PiPage);
        var npBody = JsonDocument.Parse(await np.Content.ReadAsStringAsync()).RootElement;
        Assert.True(npBody.GetProperty("ok").GetBoolean());
        var dup = await c.PostJsonAsync("/inventory/purchase-invoice/api/new-product", new { sku = $"NEW-{p.Tag}", name = "Again" }, PiPage);
        Assert.Equal("SKU already exists", JsonDocument.Parse(await dup.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());
    }

    [ReferenceFact]
    public async Task A_member_without_the_invoicing_module_is_refused()
    {
        await using var host = await Fx.HostAsync();
        var c = host.ClientFor(Fx.Restricted, Fx.CompanyB);
        var list = await c.GetAsync("/inventory/invoices/list");
        Assert.NotEqual(HttpStatusCode.OK, list.StatusCode);
        var dash = await c.GetAsync("/invoicing/");
        Assert.NotEqual(HttpStatusCode.OK, dash.StatusCode);
    }
}
