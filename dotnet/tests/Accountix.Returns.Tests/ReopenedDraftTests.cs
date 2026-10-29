using System.Net;
using System.Text.Json;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Returns.Tests;

/// <summary>
/// A return being edited does not count itself as "previously returned" (fixed in both apps, 29 Oct):
/// the form's lookup leaves out the return whose id it is given, so reopening a draft that returns
/// everything still returnable shows the full max and approves. Other returns keep counting.
/// </summary>
public class ReopenedDraftTests
{
    private static int Owner => World.Owner;
    private static int CompanyB => World.CompanyB;

    private sealed record Side(string Key, string Base, string Doc, Func<World, int> Invoice, Func<World, int> Party,
                               Func<int, int, List<Payloads.Line>, string, int?, int?, Dictionary<string, object?>> Payload);

    private static readonly Side Sales = new("sales", "/invoicing/sales-return", "SI", w => w.SalesInvoices[0], w => w.Customer,
        (inv, party, lines, action, id, v) => Payloads.Sales(inv, party, lines, action, id, v));
    private static readonly Side Purchase = new("purchase", "/inventory/purchase-return", "PI", w => w.PurchaseInvoices[0], w => w.Supplier,
        (inv, party, lines, action, id, v) => Payloads.Purchase(inv, party, lines, action, id, v));

    [ReferenceFact]
    public Task A_reopened_sales_draft_returning_everything_shows_the_full_max_and_approves() =>
        RunAsync(Sales, [[new(0, 10, 100)]], [[new(0, 5, 150)]], price: 150);

    [ReferenceFact]
    public Task A_reopened_purchase_draft_returning_everything_shows_the_full_max_and_approves() =>
        RunAsync(Purchase, [[new(0, 5, 100)]], [], price: 100);

    private static async Task RunAsync(Side side, IReadOnlyList<IReadOnlyList<World.PiLine>> purchases,
                                       IReadOnlyList<IReadOnlyList<World.SiLine>> sales, double price)
    {
        await using var host = await ModuleHost.StartAsync(SalesReturnTests.Modules);
        var w = await World.CreateAsync(host, 1, purchases, sales);
        int inv = side.Invoice(w), p = w.Products[0];
        var c = host.ClientFor(Owner, CompanyB);
        string tokenPage = side.Base + "/";

        // A draft returning all 5.
        var draftPayload = side.Payload(inv, side.Party(w), [new(p, 5, 5, price)], "save", null, null);
        var draft = await c.PostJsonAsync($"{side.Base}/save", draftPayload, tokenPage);
        Assert.Equal(HttpStatusCode.OK, draft.StatusCode);
        int id = JsonDocument.Parse(await draft.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();

        // A new return still sees the draft; the draft itself does not.
        string forNew = await (await c.GetAsync($"{side.Base}/api/invoice/{inv}")).Content.ReadAsStringAsync();
        string forDraft = await (await c.GetAsync($"{side.Base}/api/invoice/{inv}?return_id={id}")).Content.ReadAsStringAsync();
        var newItem = JsonDocument.Parse(forNew).RootElement.GetProperty("items")[0];
        var draftItem = JsonDocument.Parse(forDraft).RootElement.GetProperty("items")[0];
        Assert.Equal((5.0, 0.0), (newItem.GetProperty("previously_returned").GetDouble(), newItem.GetProperty("max_returnable_qty").GetDouble()));
        Assert.Equal((0.0, 5.0), (draftItem.GetProperty("previously_returned").GetDouble(), draftItem.GetProperty("max_returnable_qty").GetDouble()));

        // Python's lookup answered the same, with and without the id (Golden/reopened_draft_*.json, Golden/capture/reopened_draft.py).
        var roles = Roles.For(w).Text(await SalesReturnTests.InvoiceNumberAsync(side.Doc, inv), "<" + side.Doc + ">");
        if (side.Doc == "PI")
        {
            await using var db = World.Db();
            var created = await db.Set<Accountix.Core.Entities.InvPurchaseInvoice>().Where(i => i.Id == inv).Select(i => i.CreatedAt).SingleAsync();
            roles.Text($"\"date\":\"{created!.Value:yyyy-MM-dd}\"", "\"date\":\"<created>\"");
        }
        roles.Id("original_invoice_id", inv, side.Doc);
        var invoiceId = new Dictionary<string, IReadOnlyDictionary<long, string>> { ["id"] = new Dictionary<long, string> { [inv] = side.Doc } };
        string name = "reopened_draft_" + side.Key;
        Golden.SameInputs(name, new Dictionary<string, object?>
        {
            ["purchase"] = roles.Mask(World.PiPayload(w.Supplier, w.Products, purchases[0])),
            ["sale"] = sales.Count > 0 ? roles.Mask(World.SiPayload(w.Customer, w.Products, sales[0])) : null,
            ["draft"] = roles.Mask(draftPayload),
        });
        Golden.Match(name, ["new " + roles.Json(forNew, invoiceId), "draft " + roles.Json(forDraft, invoiceId)]);

        // Approving the reopened draft as the form sends it (its max from the lookup) goes through.
        var approve = side.Payload(inv, side.Party(w), [new(p, 5, 5, price)], "approve", id, 0);
        var item = ((List<Dictionary<string, object?>>)approve["items"]!)[0];
        item["max_returnable_qty"] = draftItem.GetProperty("max_returnable_qty").GetDouble();
        item["previously_returned"] = draftItem.GetProperty("previously_returned").GetDouble();
        var ok = await c.PostJsonAsync($"{side.Base}/save", approve, tokenPage);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("approved", JsonDocument.Parse(await ok.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString());

        // The approved return counts against anything else: one more is over the cap.
        var more = await c.PostJsonAsync($"{side.Base}/save", side.Payload(inv, side.Party(w), [new(p, 1, 5, price)], "approve", null, null), tokenPage);
        Assert.Equal(HttpStatusCode.BadRequest, more.StatusCode);
        Assert.Contains($"Product #{p}: returning 1 but only 0 remain returnable on the invoice", await more.Content.ReadAsStringAsync());

        // The reopened form asks the lookup without its own lines.
        string page = await (await c.GetAsync($"{side.Base}/{id}")).Content.ReadAsStringAsync();
        Assert.Contains("(rid ? '?return_id=' + rid : '')", page);
        Pages.AssertScriptsAreRaw(page);
    }
}
