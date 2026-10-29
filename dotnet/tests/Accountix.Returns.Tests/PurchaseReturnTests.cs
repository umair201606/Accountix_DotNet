using System.Net;
using System.Text.Json;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Returns.Tests;

/// <summary>Purchase returns (debit notes): Python's answers held as golden fixtures, refusals, pages.</summary>
public class PurchaseReturnTests
{
    private static int Owner => World.Owner;
    private static int CompanyB => World.CompanyB;

    private static readonly IReadOnlyList<IReadOnlyList<World.PiLine>> GoldenPurchase =
        [[new(0, 7, 1234.56, Freight: 49, TaxPct: 17, WhtPct: 4.5), new(1, 10, 99.99, TaxPct: 17)]];

    private static List<Payloads.Line> GoldenLines(World w) =>
        [new(w.Products[0], 3, 7, 1234.56, TaxPct: 17, WhtPct: 4.5, Freight: 49), new(w.Products[1], 2.5, 10, 99.99, TaxPct: 17)];

    /// <summary>
    /// A debit note saved as a draft, approved, then unapproved, against a purchase (landed 1,241.56 and
    /// 99.99 a unit) with tax, withholding and freight. The supplier does not credit the freight back
    /// (reverse expenses off), so every journal line is exercised: AP, WHT reversal, Inventory at landed
    /// cost, input tax reversal, price difference. Every answer and row equals what the Python routes
    /// wrote for the same steps (Golden/purchase_return_approve_unapprove.json, Golden/capture/purchase_return.py).
    /// </summary>
    [ReferenceFact]
    public async Task Approve_and_unapprove_write_what_python_wrote()
    {
        const string Name = "purchase_return_approve_unapprove";
        await using var host = await ModuleHost.StartAsync(SalesReturnTests.Modules);
        var w = await World.CreateAsync(host, 2, GoldenPurchase, []);
        int pi = w.PurchaseInvoices[0];
        var lines = GoldenLines(w);
        string created;
        await using (var db = World.Db())
            created = (await db.Set<InvPurchaseInvoice>().Where(i => i.Id == pi).Select(i => i.CreatedAt).SingleAsync())!.Value.ToString("yyyy-MM-dd");
        // The lookup dates a purchase invoice by when it was keyed (created_at), as Python's does.
        var roles = Roles.For(w).Text(await SalesReturnTests.InvoiceNumberAsync("PI", pi), "<PI>").Id("original_invoice_id", pi, "PI")
            .Text($"\"date\":\"{created}\"", "\"date\":\"<created>\"");
        var c = host.ClientFor(Owner, CompanyB);
        var invoiceId = new Dictionary<string, IReadOnlyDictionary<long, string>> { ["id"] = new Dictionary<long, string> { [pi] = "PI" } };

        Golden.Match(Name,
        [
            "detail " + roles.Json(await (await c.GetAsync($"/inventory/purchase-return/api/invoice/{pi}")).Content.ReadAsStringAsync(), invoiceId),
            "listing " + roles.Json(await (await c.GetAsync("/inventory/purchase-return/api/invoices?q=" + Uri.EscapeDataString(w.SupplierName)))
                                        .Content.ReadAsStringAsync(), invoiceId),
        ], "lookups");

        var save = Payloads.Purchase(pi, w.Supplier, lines, "save", reverseExpenses: false);
        var r = await c.PostJsonAsync("/inventory/purchase-return/save", save, "/inventory/purchase-return/");
        string saved = await r.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var savedDoc = JsonDocument.Parse(saved).RootElement;
        int id = savedDoc.GetProperty("id").GetInt32();
        string number = savedDoc.GetProperty("return_number").GetString()!;
        Assert.StartsWith("DN-" + DateTime.Now.ToString("yyyyMM"), number);
        roles.Text(number, "<DN>");
        var returnId = new Dictionary<string, IReadOnlyDictionary<long, string>> { ["id"] = new Dictionary<long, string> { [id] = "DN" } };
        Golden.Match(Name, ["answer " + roles.Json(saved, returnId), .. await Dump.ReturnStateAsync(roles, w, false, id, pi)], "saved");

        var approve = Payloads.Purchase(pi, w.Supplier, lines, "approve", id, 0, reverseExpenses: false);
        Golden.SameInputs(Name, new Dictionary<string, object?>
        {
            ["purchase"] = roles.Mask(World.PiPayload(w.Supplier, w.Products, GoldenPurchase[0])),
            ["save"] = roles.Mask(save), ["approve"] = roles.Mask(approve, returnId),
        });
        r = await c.PostJsonAsync("/inventory/purchase-return/save", approve, "/inventory/purchase-return/");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Golden.Match(Name, ["answer " + roles.Json(await r.Content.ReadAsStringAsync(), returnId),
                            .. await Dump.ReturnStateAsync(roles, w, false, id, pi)], "approved");
        await World.AssertStockTiesAsync(host, w.Products);

        var un = await SalesReturnTests.UnapproveAsync(c, "/inventory/purchase-return", id);
        Assert.Equal(HttpStatusCode.OK, un.StatusCode);
        Golden.Match(Name, ["answer " + roles.Json(await un.Content.ReadAsStringAsync(), returnId),
                            .. await Dump.ReturnStateAsync(roles, w, false, id, pi)], "unapproved");
        Assert.Equal(0, await SalesReturnTests.StockRowCountAsync("PRV", id));
        await World.AssertStockTiesAsync(host, w.Products);
    }

    [ReferenceFact]
    public async Task A_debit_note_over_the_purchase_is_refused()
    {
        await using var host = await ModuleHost.StartAsync(SalesReturnTests.Modules);
        var w = await World.CreateAsync(host, 1, [[new(0, 5, 200)]], []);
        int pi = w.PurchaseInvoices[0], p = w.Products[0];
        var c = host.ClientFor(Owner, CompanyB);
        var tamper = Payloads.Purchase(pi, w.Supplier, [new(p, 6, 5, 200)], "approve");
        ((List<Dictionary<string, object?>>)tamper["items"]!)[0]["max_returnable_qty"] = 50;
        var r = await c.PostJsonAsync("/inventory/purchase-return/save", tamper, "/inventory/purchase-return/");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal($"{{\"error\":\"Product #{p}: returning 6 but only 5 remain returnable on the invoice\",\"ok\":false}}",
                     await r.Content.ReadAsStringAsync());
        await using var db = World.Db();
        Assert.False(await db.Set<InvPurchaseReturn>().AnyAsync(x => x.OriginalInvoiceId == pi));
    }

    [ReferenceFact]
    public async Task Pages_render_with_the_python_markup_and_few_queries()
    {
        await using var host = await ModuleHost.StartAsync(SalesReturnTests.Modules);
        var c = host.ClientFor(Owner, CompanyB);
        var list = (await host.MeasureAsync(c, "/inventory/purchase-return/list", runs: 1)).Within(5000, 12);
        Assert.Contains("Purchase Returns (Debit Notes)", list.Body);
        var form = (await host.MeasureAsync(c, "/inventory/purchase-return/", runs: 1)).Within(5000, 12);
        foreach (string id in new[] { "originalInvoiceSelect", "itemsBody", "reverseExpenses", "summaryWithholdingTax", "refNetPayable", "saveBtn" })
            Assert.Contains($"id=\"{id}\"", form.Body);
        Assert.Contains("New Purchase Return", form.Body);
        Assert.Contains("fetch('/inventory/purchase-return/save'", form.Body);
        Pages.AssertScriptsAreRaw(form.Body);
    }
}
