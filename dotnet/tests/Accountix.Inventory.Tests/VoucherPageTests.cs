using System.Net;
using System.Text.RegularExpressions;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;
using static Accountix.Inventory.Tests.VoucherKit;

namespace Accountix.Inventory.Tests;

/// <summary>
/// The stock voucher pages (inventory_app/routes/vouchers.py) through the .NET app. The four
/// golden tests post what the Python app's own form posted for the same case (captured before
/// Python was removed): the voucher, its items, stock rows and journal lines to the paisa.
/// </summary>
public class VoucherPageTests
{
    private static async Task<int> LatestIdAsync<T>(Func<IQueryable<T>, IQueryable<int>> query) where T : class
    {
        await using var db = Db();
        return await query(db.Set<T>()).OrderByDescending(x => x).FirstAsync();
    }

    private static Task<int> ConsumptionWithAsync(int productId) =>
        LatestIdAsync<ConsumptionItem>(q => q.Where(i => i.ProductId == productId).Select(i => i.VoucherId));

    [ReferenceFact]
    public async Task Every_voucher_page_renders_in_a_fixed_number_of_queries()
    {
        await using var host = await HostAsync();
        var c = host.ClientFor(OwnerB, CompanyB);
        foreach (string url in new[]
                 {
                     "/inventory/vouchers/consumption/list", "/inventory/vouchers/scrap/list", "/inventory/vouchers/adjustment/list",
                     "/inventory/vouchers/stock-take/list", "/inventory/vouchers/consumption", "/inventory/vouchers/scrap",
                     "/inventory/vouchers/adjustment", "/inventory/vouchers/stock-take", "/inventory/vouchers/api/products?q=Voucher",
                 })
            (await host.MeasureAsync(c, url, runs: 1)).Within(maxMilliseconds: 5000, maxQueries: 40);
        string form = await BodyAsync(c, "/inventory/vouchers/consumption");
        Assert.Contains("id=\"items-body\"", form);
        Assert.Contains("name=\"charge_account_id\"", form);
        Assert.Contains("id=\"docDate\"", form);
        Assert.Contains("Consumption Expense (default)", form);
        Assert.Contains("__RequestVerificationToken", form);
        // Data inside <script> is never HTML-encoded.
        foreach (Match m in Regex.Matches(form, "<script>(.*?)</script>", RegexOptions.Singleline))
            Assert.DoesNotContain("&quot;", m.Groups[1].Value);
    }

    // ── Golden: what the Python form posted for the same case (Golden/voucher_*.json, Golden/capture/vouchers.py) ──

    [ReferenceFact]
    public async Task Approving_a_consumption_posts_what_the_python_form_posted()
    {
        await using var host = await HostAsync();
        var c = host.ClientFor(OwnerB, CompanyB);
        var posted = await PostGoldenAsync("voucher_consumption", c, new GoldenVoucher("/inventory/vouchers/consumption", "CONS",
            new() { ["P"] = ["10", "12.5", 10] },
            [["department", "Workshop"], ["reason", "Repairs"], ["date", "D-2"], ["charge_account_id", ""], ["label_id", ""],
             ["status", "approved"], ["product_id[]", "P"], ["qty[]", "3"]]));
        int a = posted.Ids["P"];
        string page = await FollowAsync(c, posted.Response);
        int id = await ConsumptionWithAsync(a);
        await using (var db = Db())
            Assert.Contains($"Consumption voucher {(await db.Set<ConsumptionVoucher>().SingleAsync(x => x.Id == id)).VoucherNumber} saved.", page);
        Assert.Equal(7m, await OnHandAsync(a));
        Assert.True(await TiesAsync(a));
    }

    [ReferenceFact]
    public async Task Approving_a_scrap_charged_to_an_account_with_a_label_posts_what_python_posted()
    {
        await using var host = await HostAsync();
        var c = host.ClientFor(OwnerB, CompanyB);
        var posted = await PostGoldenAsync("voucher_scrap", c, new GoldenVoucher("/inventory/vouchers/scrap", "SCRAP",
            new() { ["P"] = ["8", "7.3333", 9] },
            [["reason", "Broken in transit"], ["date", "D-1"], ["charge_account_id", "CHARGE"], ["label_id", "LABEL"],
             ["status", "approved"], ["product_id[]", "P"], ["qty[]", "2.5"]], ChargeCode: "5-04-01-01-0001"));
        Assert.Equal(5.5m, await OnHandAsync(posted.Ids["P"]));
        Assert.True(await TiesAsync(posted.Ids["P"]));
    }

    [ReferenceFact]
    public async Task Stock_adjustment_reads_the_system_quantity_from_the_ledger_as_python_did()
    {
        await using var host = await HostAsync();
        var c = host.ClientFor(OwnerB, CompanyB);
        // The form's system quantity (99) is ignored: the ledger's at the voucher date is used.
        var posted = await PostGoldenAsync("voucher_adjustment", c, new GoldenVoucher("/inventory/vouchers/adjustment", "ADJ",
            new() { ["P"] = ["10", "20", 10] },
            [["reason", "Count"], ["date", "D-3"], ["label_id", ""], ["status", "approved"],
             ["product_id[]", "P"], ["system_qty[]", "99"], ["physical_qty[]", "7"]]));
        int a = posted.Ids["P"];
        await using var db = Db();
        Assert.Equal(10m, (await db.Set<StockAdjustmentItem>().Where(i => i.ProductId == a).SingleAsync()).SystemQty);
        Assert.Equal(7m, await OnHandAsync(a));
    }

    [ReferenceFact]
    public async Task Approving_a_stock_take_posts_its_adjustment_as_python_did()
    {
        await using var host = await HostAsync();
        var c = host.ClientFor(OwnerB, CompanyB);
        var posted = await PostGoldenAsync("voucher_stock_take", c, new GoldenVoucher("/inventory/vouchers/stock-take", "ST",
            new() { ["P"] = ["10", "5", 10], ["Q"] = ["4", "9", 10] },
            [["location", "Main store"], ["date", "D-1"], ["label_id", ""], ["status", "approved"],
             ["product_id[]", "P"], ["system_qty[]", ""], ["physical_qty[]", "12"],
             ["product_id[]", "Q"], ["system_qty[]", ""], ["physical_qty[]", "1.5"]]));
        string page = await FollowAsync(c, posted.Response);
        Assert.Contains("saved.", page);
        Assert.Equal(12m, await OnHandAsync(posted.Ids["P"]));
        Assert.Equal(1.5m, await OnHandAsync(posted.Ids["Q"]));
    }

    [ReferenceFact]
    public async Task Draft_then_approve_then_unapprove_then_delete()
    {
        int p = await StockedProductAsync(10, 10, Day(10));
        await using var host = await HostAsync();
        var c = host.ClientFor(OwnerB, CompanyB);
        var r = await c.PostFormAsync("/inventory/vouchers/consumption", IssueForm("unapproved", p, "4", Day(2), "Draft"),
                                      "/inventory/vouchers/consumption");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        int id = await ConsumptionWithAsync(p);
        Assert.Equal(10m, await OnHandAsync(p));
        string edit = await BodyAsync(c, $"/inventory/vouchers/consumption/{id}");
        Assert.Contains("value=\"4.0000\"", edit);
        Assert.Contains($"/inventory/vouchers/consumption/{id}/delete", edit);

        // Unapproving a draft is refused in Python's words.
        r = await c.PostFormAsync($"/inventory/vouchers/consumption/{id}/unapprove", [], $"/inventory/vouchers/consumption/{id}");
        Assert.Contains("Voucher is not approved.", await FollowAsync(c, r));

        r = await c.PostFormAsync($"/inventory/vouchers/consumption/{id}", IssueForm("approved", p, "4", Day(2), "Draft"),
                                  $"/inventory/vouchers/consumption/{id}");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(6m, await OnHandAsync(p));
        string approved = await BodyAsync(c, $"/inventory/vouchers/consumption/{id}");
        Assert.Contains("Approved and posted on", approved);
        Assert.Contains("disabled=\"disabled\"", approved);

        // An approved voucher is final: submitting it again is refused, nothing moves twice.
        r = await c.PostFormAsync($"/inventory/vouchers/consumption/{id}", IssueForm("approved", p, "4", Day(2), "Draft"),
                                  "/inventory/vouchers/consumption/list");
        Assert.Contains("is approved and posted. Unapprove it to make changes.", await FollowAsync(c, r));
        Assert.Equal(6m, await OnHandAsync(p));
        r = await c.PostFormAsync($"/inventory/vouchers/consumption/{id}/delete", [], "/inventory/vouchers/consumption/list");
        Assert.Contains("Cannot delete an approved voucher. Unapprove it first.", await FollowAsync(c, r));

        r = await c.PostFormAsync($"/inventory/vouchers/consumption/{id}/unapprove", [], $"/inventory/vouchers/consumption/{id}");
        Assert.Contains("unapproved and stock reversed.", await FollowAsync(c, r));
        Assert.Equal(10m, await OnHandAsync(p));
        await using (var db = Db())
            Assert.False(await db.Set<JournalEntry>().AnyAsync(e => e.VoucherType == "CONS" && e.VoucherId == id && e.IsPosted == true));

        r = await c.PostFormAsync($"/inventory/vouchers/consumption/{id}/delete", [], "/inventory/vouchers/consumption/list");
        Assert.Contains("Consumption voucher deleted.", await FollowAsync(c, r));
        await using var db2 = Db();
        Assert.False(await db2.Set<ConsumptionVoucher>().AnyAsync(v => v.Id == id));
        Assert.False(await db2.Set<ConsumptionItem>().AnyAsync(i => i.VoucherId == id));
    }

    [ReferenceFact]
    public async Task Issuing_more_than_on_hand_is_refused_and_nothing_is_saved()
    {
        int p = await StockedProductAsync(3, 10, Day(10));
        await using var host = await HostAsync();
        var c = host.ClientFor(OwnerB, CompanyB);
        int before;
        await using (var db = Db())
            before = await db.Set<ConsumptionVoucher>().CountAsync();
        var r = await c.PostFormAsync("/inventory/vouchers/consumption", IssueForm("approved", p, "5", Day(2), "Too many"),
                                      "/inventory/vouchers/consumption");
        // Python's handle_costing_refusal: flash and back to where the form came from.
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        string page = System.Net.WebUtility.HtmlDecode(await BodyAsync(c, "/inventory/vouchers/consumption/list"));
        Assert.Contains($"Cannot issue 5.0 of product {p}: only 3.0000 on hand.", page);
        await using var db2 = Db();
        Assert.Equal(before, await db2.Set<ConsumptionVoucher>().CountAsync());
        Assert.Equal(3m, await OnHandAsync(p));
    }

    [ReferenceFact]
    public async Task A_form_without_items_is_shown_again_with_python_words()
    {
        await using var host = await HostAsync();
        var c = host.ClientFor(OwnerB, CompanyB);
        var r = await c.PostFormAsync("/inventory/vouchers/scrap",
            [new("reason", "x"), new("date", Iso(Day(0))), new("status", "approved"), new("product_id[]", ""), new("qty[]", "")],
            "/inventory/vouchers/scrap");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Add at least one item with quantity &gt; 0.", await r.Content.ReadAsStringAsync());
        r = await c.PostFormAsync("/inventory/vouchers/adjustment",
            [new("reason", "x"), new("date", Iso(Day(0))), new("status", "approved")], "/inventory/vouchers/adjustment");
        Assert.Contains("No items with quantity differences to adjust.", await r.Content.ReadAsStringAsync());
        r = await c.PostFormAsync("/inventory/vouchers/stock-take",
            [new("location", "x"), new("date", Iso(Day(0))), new("status", "unapproved")], "/inventory/vouchers/stock-take");
        Assert.Contains("Add at least one product to the stock take.", await r.Content.ReadAsStringAsync());
        r = await c.GetAsync("/inventory/vouchers/consumption/999999999");
        Assert.Contains("Voucher not found.", await FollowAsync(c, r));
    }

    [ReferenceFact]
    public async Task Unapproving_a_stock_take_adjustment_reopens_the_take()
    {
        int p = await StockedProductAsync(10, 5, Day(10));
        await using var host = await HostAsync();
        var c = host.ClientFor(OwnerB, CompanyB);
        var r = await c.PostFormAsync("/inventory/vouchers/stock-take",
            [new("location", "Shelf"), new("date", Iso(Day(1))), new("status", "approved"),
             new("product_id[]", p.ToString()), new("physical_qty[]", "8")], "/inventory/vouchers/stock-take");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(8m, await OnHandAsync(p));
        int adjId, takeId;
        await using (var db = Db())
        {
            var take = await db.Set<StockTake>().Where(t => t.StockTakeItems.Any(i => i.ProductId == p)).SingleAsync();
            takeId = take.Id;
            adjId = take.AdjustmentVoucherId!.Value;
        }
        string list = await BodyAsync(c, "/inventory/vouchers/stock-take/list");
        Assert.Contains($"/inventory/vouchers/adjustment/{adjId}\">View</a>", list);
        r = await c.PostFormAsync($"/inventory/vouchers/stock-take/{takeId}", [new("location", "x"), new("status", "approved")],
                                  "/inventory/vouchers/stock-take/list");
        Assert.Contains("is approved and posted. Unapprove its adjustment voucher to make changes.", await FollowAsync(c, r));

        r = await c.PostFormAsync($"/inventory/vouchers/adjustment/{adjId}/unapprove", [], "/inventory/vouchers/adjustment/list");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(10m, await OnHandAsync(p));
        await using var db2 = Db();
        var t2 = await db2.Set<StockTake>().AsNoTracking().SingleAsync(t => t.Id == takeId);
        Assert.Equal("in_progress", t2.Status);
        Assert.Null(t2.AdjustmentVoucherId);
    }

    [ReferenceFact]
    public async Task Product_lookups_answer_python_json()
    {
        int p = await StockedProductAsync(6, 4, Day(5), "Lookup Gadget");
        await using var host = await HostAsync();
        var c = host.ClientFor(OwnerB, CompanyB);
        string name;
        await using (var db = Db())
            name = (await db.Set<InvProduct>().SingleAsync(x => x.Id == p)).Name;
        string json = await BodyAsync(c, "/inventory/vouchers/api/products?q=" + Uri.EscapeDataString(name));
        Assert.Contains($"\"id\":{p},", json);
        Assert.Contains("\"stock\":6", json);
        string stock = await BodyAsync(c, $"/inventory/vouchers/api/product-stock/{p}");
        Assert.Equal($"{{\"product_id\":{p},\"qty\":6,\"cost\":4}}", stock);
    }

    [ReferenceFact]
    public async Task A_person_without_inventory_is_refused()
    {
        await using var host = await HostAsync();
        var c = host.ClientFor(Reference.UserId(Reference.Email("restricted")), CompanyB);
        var r = await c.GetAsync("/inventory/vouchers/consumption/list");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        r = await c.GetAsync("/inventory/vouchers/api/products");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }
}
