using System.Net;
using System.Text.Json;
using Accountix.Accounting;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Returns.Tests;

/// <summary>Sales returns (credit notes): Python's answers held as golden fixtures, refusals, pages.</summary>
public class SalesReturnTests
{
    /// <summary>
    /// Returns + the real services its routes call (Inventory's costing engine, Parties' invoice
    /// tracking, the general ledger) and the Invoicing / Orders routes the worlds are built through.
    /// </summary>
    internal static readonly System.Reflection.Assembly[] Modules =
    [
        typeof(ReturnsModule).Assembly, typeof(AccountingModule).Assembly, typeof(Accountix.Inventory.InventoryModule).Assembly,
        typeof(Accountix.Parties.PartiesModule).Assembly, typeof(Accountix.Invoicing.InvoicingModule).Assembly,
        typeof(Accountix.Orders.OrdersModule).Assembly,
    ];

    private static int Owner => World.Owner;
    private static int CompanyB => World.CompanyB;

    /// <summary>Stock rows the engine wrote for a voucher.</summary>
    internal static async Task<int> StockRowCountAsync(string voucherType, int id)
    {
        await using var db = World.Db();
        return await db.Set<StockLedger>().CountAsync(s => s.VoucherType == voucherType && s.VoucherId == id);
    }

    internal static async Task<string> InvoiceNumberAsync(string docType, int id)
    {
        await using var db = World.Db();
        return docType == "SI"
            ? await db.Set<InvInvoice>().Where(i => i.Id == id).Select(i => i.InvoiceNumber).SingleAsync()
            : await db.Set<InvPurchaseInvoice>().Where(i => i.Id == id).Select(i => i.InvoiceNumber).SingleAsync();
    }

    internal static async Task<HttpResponseMessage> UnapproveAsync(PageClient c, string baseUrl, int id)
    {
        string token = await c.TokenFromAsync($"{baseUrl}/{id}");
        return await c.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/unapprove/{id}") { Headers = { { "X-CSRF-Token", token } } });
    }

    private static readonly IReadOnlyList<IReadOnlyList<World.PiLine>> GoldenPurchase = [[new(0, 7, 1234.56, Freight: 49), new(1, 10, 99.99)]];
    private static readonly IReadOnlyList<IReadOnlyList<World.SiLine>> GoldenSale = [[new(0, 3, 1500.25, TaxPct: 18), new(1, 4, 180.5, TaxPct: 18)]];

    private static List<Payloads.Line> GoldenLines(World w) =>
        [new(w.Products[0], 2, 3, 1500.25, TaxPct: 18), new(w.Products[1], 1.25, 4, 180.5, TaxPct: 18)];

    /// <summary>
    /// A credit note saved as a draft, approved (with edit_version), then unapproved, against a sale of
    /// two products bought at landed costs that divide evenly (1,241.56 and 99.99 a unit). Every answer,
    /// the return and its lines, the journal (Sales Returns, output tax, AR, Inventory, COGS; 1.25 × 99.99
    /// rounded on its way to the journal), the stock rows and layers, the movements, the products and the
    /// invoice's settled cache equal what the Python routes wrote for the same steps
    /// (Golden/sales_return_approve_unapprove.json, Golden/capture/sales_return.py).
    /// </summary>
    [ReferenceFact]
    public async Task Approve_and_unapprove_write_what_python_wrote()
    {
        const string Name = "sales_return_approve_unapprove";
        await using var host = await ModuleHost.StartAsync(Modules);
        var w = await World.CreateAsync(host, 2, GoldenPurchase, GoldenSale);
        int si = w.SalesInvoices[0];
        var lines = GoldenLines(w);
        var roles = Roles.For(w).Text(await InvoiceNumberAsync("SI", si), "<SI>").Text(await InvoiceNumberAsync("PI", w.PurchaseInvoices[0]), "<PI>")
            .Id("original_invoice_id", si, "SI");
        var c = host.ClientFor(Owner, CompanyB);
        var invoiceId = new Dictionary<string, IReadOnlyDictionary<long, string>> { ["id"] = new Dictionary<long, string> { [si] = "SI" } };

        // The form's two lookups before anything is returned.
        Golden.Match(Name,
        [
            "detail " + roles.Json(await (await c.GetAsync($"/invoicing/sales-return/api/invoice/{si}")).Content.ReadAsStringAsync(), invoiceId),
            "listing " + roles.Json(await (await c.GetAsync("/invoicing/sales-return/api/invoices?q=" + Uri.EscapeDataString(w.CustomerName)))
                                        .Content.ReadAsStringAsync(), invoiceId),
        ], "lookups");

        var save = Payloads.Sales(si, w.Customer, lines, "save");
        var r = await c.PostJsonAsync("/invoicing/sales-return/save", save, "/invoicing/sales-return/");
        string saved = await r.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var savedDoc = JsonDocument.Parse(saved).RootElement;
        int id = savedDoc.GetProperty("id").GetInt32();
        string number = savedDoc.GetProperty("return_number").GetString()!;
        Assert.StartsWith("CN-" + DateTime.Now.ToString("yyyyMM"), number);
        roles.Text(number, "<CN>");
        var returnId = new Dictionary<string, IReadOnlyDictionary<long, string>> { ["id"] = new Dictionary<long, string> { [id] = "CN" } };
        Golden.Match(Name, ["answer " + roles.Json(saved, returnId), .. await Dump.ReturnStateAsync(roles, w, true, id, si)], "saved");

        var approve = Payloads.Sales(si, w.Customer, lines, "approve", id, 0);
        // The fixture was captured for exactly these payloads (the invoices' and the return's).
        Golden.SameInputs(Name, new Dictionary<string, object?>
        {
            ["purchase"] = roles.Mask(World.PiPayload(w.Supplier, w.Products, GoldenPurchase[0])),
            ["sale"] = roles.Mask(World.SiPayload(w.Customer, w.Products, GoldenSale[0])),
            ["save"] = roles.Mask(save), ["approve"] = roles.Mask(approve, returnId),
        });
        r = await c.PostJsonAsync("/invoicing/sales-return/save", approve, "/invoicing/sales-return/");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Golden.Match(Name, ["answer " + roles.Json(await r.Content.ReadAsStringAsync(), returnId),
                            .. await Dump.ReturnStateAsync(roles, w, true, id, si)], "approved");
        await World.AssertStockTiesAsync(host, w.Products);

        // Unapprove: the journal comes off, the stock goes back out, the version is bumped.
        var un = await UnapproveAsync(c, "/invoicing/sales-return", id);
        Assert.Equal(HttpStatusCode.OK, un.StatusCode);
        Golden.Match(Name, ["answer " + roles.Json(await un.Content.ReadAsStringAsync(), returnId),
                            .. await Dump.ReturnStateAsync(roles, w, true, id, si)], "unapproved");
        Assert.Equal(0, await StockRowCountAsync("SRV", id));
        await World.AssertStockTiesAsync(host, w.Products);
    }

    [ReferenceFact]
    public async Task Refusals_answer_in_pythons_words()
    {
        await using var host = await ModuleHost.StartAsync(Modules);
        var w = await World.CreateAsync(host, 1, [[new(0, 10, 100)]], [[new(0, 4, 150)]]);
        int si = w.SalesInvoices[0];
        int p = w.Products[0];
        var c = host.ClientFor(Owner, CompanyB);

        async Task<(HttpStatusCode, JsonElement)> Post(object payload)
        {
            var r = await c.PostJsonAsync("/invoicing/sales-return/save", payload, "/invoicing/sales-return/");
            return (r.StatusCode, JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone());
        }

        // The browser's max is not trusted: 5 of the 4 sold is refused on the server.
        var over = Payloads.Sales(si, w.Customer, [new(p, 5, 4, 150)], "approve");
        ((List<Dictionary<string, object?>>)over["items"]!)[0]["max_returnable_qty"] = 99;
        var (code, body) = await Post(over);
        Assert.Equal(HttpStatusCode.BadRequest, code);
        Assert.Equal($"Product #{p}: returning 5 but only 4 remain returnable on the invoice", body.GetProperty("error").GetString());

        (code, body) = await Post(Payloads.Sales(si, w.Customer, [new(p, 5, 4, 150)], "approve"));
        Assert.Equal("Row 1: Return quantity (5.0) exceeds max returnable (4.0)", body.GetProperty("error").GetString());

        (code, body) = await Post(Payloads.Sales(si, w.Customer, [new(p, 1, 4, 150)], "approve", date: "2026-10-01"));
        Assert.Equal("Return date cannot be before the invoice date (20 Oct 2026)", body.GetProperty("error").GetString());

        (code, body) = await Post(Payloads.Sales(si, w.Customer, [new(p, 1, 4, 150)], "approve", date: "22/10/2026"));
        Assert.Equal(HttpStatusCode.Conflict, code);
        Assert.Equal("'22/10/2026' is not a valid date (YYYY-MM-DD).", body.GetProperty("error").GetString());

        (code, body) = await Post(Payloads.Sales(si, w.Customer, [new(p, 0, 4, 150)], "approve"));
        Assert.Equal("At least one item must have a return quantity greater than 0", body.GetProperty("error").GetString());

        // Approve, then: an approved return cannot be changed, and unapproving twice is refused.
        (code, body) = await Post(Payloads.Sales(si, w.Customer, [new(p, 3, 4, 150)], "approve"));
        Assert.Equal(HttpStatusCode.OK, code);
        int id = body.GetProperty("id").GetInt32();
        Assert.Equal(0, body.GetProperty("edit_version").GetInt32());
        (code, body) = await Post(Payloads.Sales(si, w.Customer, [new(p, 1, 4, 150)], "save", id));
        Assert.Equal(HttpStatusCode.BadRequest, code);
        Assert.Equal("Cannot modify approved return", body.GetProperty("error").GetString());
        // The second return finds only 1 left.
        (code, body) = await Post(Payloads.Sales(si, w.Customer, [new(p, 2, 4, 150)], "approve"));
        Assert.Equal($"Product #{p}: returning 2 but only 1 remain returnable on the invoice", body.GetProperty("error").GetString());

        // The invoice detail now shows 3 returned, 1 still returnable.
        var detail = JsonDocument.Parse(await (await c.GetAsync($"/invoicing/sales-return/api/invoice/{si}")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(3.0, detail.GetProperty("items")[0].GetProperty("previously_returned").GetDouble());
        Assert.Equal(1.0, detail.GetProperty("items")[0].GetProperty("max_returnable_qty").GetDouble());
        var missing = await c.GetAsync("/invoicing/sales-return/api/invoice/999999");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("{\"error\":\"Invoice not found or not approved\",\"ok\":false}", await missing.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (await UnapproveAsync(c, "/invoicing/sales-return", id)).StatusCode);
        var again = await UnapproveAsync(c, "/invoicing/sales-return", id);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("{\"error\":\"Only approved returns can be unapproved\",\"ok\":false}", await again.Content.ReadAsStringAsync());
    }

    [ReferenceFact]
    public async Task A_save_from_a_stale_copy_is_refused()
    {
        await using var host = await ModuleHost.StartAsync(Modules);
        var w = await World.CreateAsync(host, 1, [[new(0, 10, 100)]], [[new(0, 4, 150)]]);
        int si = w.SalesInvoices[0];
        var c = host.ClientFor(Owner, CompanyB);
        var lines = new List<Payloads.Line> { new(w.Products[0], 1, 4, 150) };
        async Task<string> Post(object payload) =>
            await (await c.PostJsonAsync("/invoicing/sales-return/save", payload, "/invoicing/sales-return/")).Content.ReadAsStringAsync();
        var first = JsonDocument.Parse(await Post(Payloads.Sales(si, w.Customer, lines, "save"))).RootElement;
        int id = first.GetProperty("id").GetInt32();
        Assert.Equal(0, first.GetProperty("edit_version").GetInt32());
        Assert.Equal("unapproved", first.GetProperty("status").GetString());
        var second = JsonDocument.Parse(await Post(Payloads.Sales(si, w.Customer, lines, "save", id, 0))).RootElement;
        Assert.Equal(1, second.GetProperty("edit_version").GetInt32());
        Assert.Equal("Return changes saved", second.GetProperty("message").GetString());
        var r = await c.PostJsonAsync("/invoicing/sales-return/save", Payloads.Sales(si, w.Customer, lines, "save", id, 0), "/invoicing/sales-return/");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        var stale = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        Assert.True(stale.GetProperty("stale").GetBoolean());
        Assert.StartsWith($"Return {first.GetProperty("return_number").GetString()} was changed", stale.GetProperty("error").GetString());
        Assert.EndsWith("after you opened it, so your changes were not saved. Reload it to see the latest version, then make your changes again.",
                        stale.GetProperty("error").GetString());
    }

    [ReferenceFact]
    public async Task Pages_render_with_the_python_markup_and_few_queries()
    {
        await using var host = await ModuleHost.StartAsync(Modules);
        var w = await World.CreateAsync(host, 1, [[new(0, 10, 100)]], [[new(0, 4, 150)]]);
        var c = host.ClientFor(Owner, CompanyB);
        int sid = await World.SaveAsync(c, "/invoicing/sales-return/save", "/invoicing/sales-return/",
            Payloads.Sales(w.SalesInvoices[0], w.Customer, [new(w.Products[0], 1, 4, 150)], "save"));

        var list = (await host.MeasureAsync(c, "/invoicing/sales-return/list", runs: 1)).Within(5000, 12);
        Assert.Contains("Sales Returns (Credit Notes)", list.Body);
        Assert.Contains("href=\"/invoicing/sales-return/\"", list.Body);
        Assert.Contains("table-responsive", list.Body);
        var form = (await host.MeasureAsync(c, "/invoicing/sales-return/", runs: 1)).Within(5000, 12);
        foreach (string id in new[] { "originalInvoiceSelect", "itemsBody", "returnDate", "reverseCharges", "summaryCostBack", "saveBtn",
                                      "editVersion", "gleFab", "confirmModal" })
            Assert.Contains($"id=\"{id}\"", form.Body);
        Assert.Contains("<title>", form.Body);
        Assert.Contains("New Sales Return", form.Body);
        Assert.Contains("/static/js/edit_version.js", form.Body);
        Assert.Contains("@media(max-width:768px)", form.Body);
        Assert.Contains("fetch('/invoicing/sales-return/save'", form.Body);
        Pages.AssertScriptsAreRaw(form.Body);

        var page = (await host.MeasureAsync(c, $"/invoicing/sales-return/{sid}", runs: 1)).Within(5000, 14);
        Assert.Contains("id=\"statusBadge\"", page.Body);
        Assert.Contains("await loadInvoiceData(sel.value);", page.Body);
        Pages.AssertScriptsAreRaw(page.Body);
        // The sidebar carries the Sales Return item under Sales, highlighted on these pages.
        Assert.Contains("href=\"/invoicing/sales-return/list\"", list.Body);
        Assert.Contains("href=\"/inventory/purchase-return/list\"", list.Body);
    }
}
