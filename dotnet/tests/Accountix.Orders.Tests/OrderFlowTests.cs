using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Orders.Tests;

/// <summary>
/// Sales and purchase orders through the pages, in a company of the test's own (<see cref="World"/>). The golden
/// tests compare with what the Python app wrote for the same steps (Golden/*.json, captured 29 Oct 2026).
/// </summary>
public class OrderFlowTests
{
    private static Task<ModuleHost> HostAsync() => ModuleHost.StartAsync(typeof(OrdersModule).Assembly);

    private static async Task<(World W, PageClient C, string Token)> SignedInAsync(ModuleHost host, string page = "/inventory/sales/list")
    {
        var w = await World.SharedAsync();
        var c = w.Client(host);
        return (w, c, await c.TokenFromAsync(page));
    }

    // ── Saving: numbers, defaults, parity with Python ──────────────────

    [ReferenceFact]
    public async Task A_new_order_is_numbered_from_its_own_id_never_auto()
    {
        await using var host = await HostAsync();
        var (w, c, token) = await SignedInAsync(host);
        var (status, body) = await World.PostAsync(c, "/inventory/sales/save", w.SalesJson("save", "n " + World.NewTag()), token);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("ok").GetBoolean());
        int id = body.GetProperty("id").GetInt32();
        string number = body.GetProperty("number").GetString()!;
        Assert.Matches($"^SO-\\d{{6}}-{id:D4}$", number);
        Assert.Equal("unapproved", body.GetProperty("status").GetString());
        Assert.Equal(0, body.GetProperty("edit_version").GetInt32());
        Assert.Equal("Order saved as unapproved", body.GetProperty("message").GetString());

        await using var db = w.Db();
        var o = await db.Set<InvSalesOrder>().AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(number, o.SoNumber);
        Assert.Equal("open", o.FulfilmentStatus);
        Assert.Equal(2, await db.Set<InvSalesOrderItem>().CountAsync(i => i.SoId == id));
    }

    /// <summary>Golden/so_saved_then_approved.json: two drafts (one with notes sent as 0, stored "0") and the approval.</summary>
    [ReferenceFact]
    public async Task A_sales_order_saved_then_approved_writes_what_the_python_app_wrote()
    {
        await using var host = await HostAsync();
        var (w, c, token) = await SignedInAsync(host);
        w.ClearRoles();
        const string note = "parity sale";
        // Notes sent as 0 too: the form's g() sends 0 for an empty field, and Python stores "0".
        var draft = w.SalesJson("save", note);
        var zero = w.SalesJson("save", "x");
        zero["notes"] = 0;
        var answers = new List<string>();
        var a = await w.SaveAsync(c, "/inventory/sales/save", draft, token, "SO1");
        answers.Add(w.Answer(a.Status, a.Raw));
        var z = await w.SaveAsync(c, "/inventory/sales/save", zero, token, "SO2");
        answers.Add(w.Answer(z.Status, z.Raw));
        int so1 = a.Body.GetProperty("id").GetInt32(), so2 = z.Body.GetProperty("id").GetInt32();
        var approve = w.SalesJson("approve", note, so1, version: 0, qty: 4, price: 12.345);
        var b = await w.SaveAsync(c, "/inventory/sales/save", approve, token);
        answers.Add(w.Answer(b.Status, b.Raw));

        const string name = "so_saved_then_approved";
        Golden.SameInputs(name, new { draft = w.RoleJson(draft), zero = w.RoleJson(zero), approve = w.RoleJson(approve) });
        Golden.Match(name, answers, "answers");
        Golden.Match(name, await w.SalesRowsAsync(so1), "SO1");
        Golden.Match(name, await w.SalesRowsAsync(so2), "SO2");
    }

    /// <summary>Golden/po_saved_then_approved.json: logistics fields sent as 0, no expected date, totals recomputed on approval.</summary>
    [ReferenceFact]
    public async Task A_purchase_order_saved_then_approved_writes_what_the_python_app_wrote()
    {
        await using var host = await HostAsync();
        var (w, c, token) = await SignedInAsync(host, "/inventory/purchases/list");
        w.ClearRoles();
        var draft = w.PurchaseJson("save", "po parity");
        var answers = new List<string>();
        var a = await w.SaveAsync(c, "/inventory/purchases/save", draft, token, "PO1");
        answers.Add(w.Answer(a.Status, a.Raw));
        int po1 = a.Body.GetProperty("id").GetInt32();
        Assert.Matches($"^PO-\\d{{6}}-{po1:D4}$", a.Body.GetProperty("number").GetString()!);
        var approve = w.PurchaseJson("approve", "po parity v2", po1, version: 0);
        var b = await w.SaveAsync(c, "/inventory/purchases/save", approve, token);
        answers.Add(w.Answer(b.Status, b.Raw));

        const string name = "po_saved_then_approved";
        Golden.SameInputs(name, new { draft = w.RoleJson(draft), approve = w.RoleJson(approve) });
        Golden.Match(name, answers, "answers");
        Golden.Match(name, await w.PurchaseRowsAsync(po1), "PO1");
        await using var db = w.Db();
        var po = await db.Set<InvPurchaseOrder>().SingleAsync(o => o.Id == po1);
        Assert.Equal(("0", "0", (DateOnly?)null), (po.DriverContact, po.GatePass, po.ExpectedDate));
    }

    /// <summary>
    /// Golden/order_answers_and_refusals.json: on one approved order, an edit, a delete, two unapprovals, a stale save,
    /// a delete and a delete of a missing order: the same status codes and JSON, refusal texts word for word (who/when aside).
    /// </summary>
    [ReferenceFact]
    public async Task The_json_answers_and_refusals_are_the_python_routes_answers()
    {
        await using var host = await HostAsync();
        var (w, c, token) = await SignedInAsync(host);
        w.ClearRoles();
        var created = w.SalesJson("approve", "answers");
        var cr = await w.SaveAsync(c, "/inventory/sales/save", created, token, "SO1");
        int id = cr.Body.GetProperty("id").GetInt32();
        var answers = new List<string> { w.Answer(cr.Status, cr.Raw) };
        var calls = new (string Url, JsonNode? Body)[]
        {
            ("/inventory/sales/save", w.SalesJson("save", "edit approved", id)),
            ($"/inventory/sales/delete/{id}", null),
            ($"/inventory/sales/unapprove/{id}", null),
            ($"/inventory/sales/unapprove/{id}", null),
            ("/inventory/sales/save", w.SalesJson("save", "stale", id, version: 0)),
            ($"/inventory/sales/delete/{id}", null),
            ("/inventory/sales/delete/99999999", null),
        };
        foreach (var (url, body) in calls)
        {
            var r = await c.PostJsonWithTokenAsync(url, body ?? new JsonObject(), token);
            bool json = r.Content.Headers.ContentType?.MediaType == "application/json";
            answers.Add(json ? w.Answer(r.StatusCode, await r.Content.ReadAsStringAsync()) : $"{(int)r.StatusCode} null");
        }
        const string name = "order_answers_and_refusals";
        Golden.SameInputs(name, new
        {
            created = w.RoleJson(created),
            calls = calls.Select(x => new object?[] { Regex.Replace(x.Url, $"/{id}$", "/SO1"), x.Body is null ? null : w.RoleJson(x.Body) }),
        });
        Golden.Match(name, answers, "answers");
    }

    [ReferenceFact]
    public async Task Unapprove_bumps_the_version_and_a_draft_can_then_be_deleted()
    {
        await using var host = await HostAsync();
        var (w, c, token) = await SignedInAsync(host, "/inventory/purchases/list");
        var (_, a) = await World.PostAsync(c, "/inventory/purchases/save", w.PurchaseJson("approve", "un " + World.NewTag()), token);
        int id = a.GetProperty("id").GetInt32();
        var (s1, u) = await World.PostAsync(c, $"/inventory/purchases/unapprove/{id}", new JsonObject(), token);
        Assert.Equal(HttpStatusCode.OK, s1);
        Assert.Equal(1, u.GetProperty("edit_version").GetInt32());
        Assert.Equal("Order unapproved", u.GetProperty("message").GetString());
        await using (var db = w.Db())
        {
            var po = await db.Set<InvPurchaseOrder>().AsNoTracking().SingleAsync(o => o.Id == id);
            Assert.Equal(("unapproved", (int?)null, (DateTime?)null), (po.Status, po.ApprovedBy, po.ApprovedAt));
        }
        var (s2, d) = await World.PostAsync(c, $"/inventory/purchases/delete/{id}", new JsonObject(), token);
        Assert.Equal(HttpStatusCode.OK, s2);
        Assert.Equal("Order deleted", d.GetProperty("message").GetString());
        await using var db2 = w.Db();
        Assert.False(await db2.Set<InvPurchaseOrder>().AnyAsync(o => o.Id == id));
        Assert.False(await db2.Set<InvPurchaseOrderItem>().AnyAsync(i => i.PoId == id));
    }

    // ── Totals: the browser's figures only while they agree with the lines ──

    /// <summary>Golden/totals_recomputed.json, plus the figures themselves.</summary>
    [ReferenceFact]
    public async Task Totals_that_disagree_with_the_lines_are_recomputed_as_python_did()
    {
        await using var host = await HostAsync();
        var (w, c, token) = await SignedInAsync(host);
        w.ClearRoles();
        // A stale editor: lines worth 2 x 1,000.25 + 0.5 x 3 at 17%, totals of 0 (the old reopened-order bug).
        var stale = w.SalesJson("save", "totals", qty: 2, price: 1000.25);
        stale["subtotal"] = 0; stale["total_tax"] = 0; stale["total_amount"] = 0;
        ((JsonArray)stale["items"]!)[1]!["quantity"] = 0.5;
        ((JsonArray)stale["items"]!)[1]!["unit_price"] = 3;
        ((JsonArray)stale["items"]!)[1]!["total_before_discount"] = 0;
        ((JsonArray)stale["items"]!)[1]!["total_price"] = 0;
        // A request that agrees with its lines to the paisa (tax not rounded, as the purchase editor sends it).
        var exact = w.SalesJson("save", "exact", qty: 3, price: 30750.1666666);
        ((JsonArray)exact["items"]!).RemoveAt(1);
        exact["tax_mode"] = "individual";
        ((JsonArray)exact["items"]!)[0]!["sales_tax_pct"] = 17;
        exact["subtotal"] = 92250.5; exact["total_tax"] = 15682.585; exact["total_amount"] = 107933.085;

        var a = await w.SaveAsync(c, "/inventory/sales/save", stale, token, "SO1");
        var b = await w.SaveAsync(c, "/inventory/sales/save", exact, token, "SO2");
        int staleId = a.Body.GetProperty("id").GetInt32(), exactId = b.Body.GetProperty("id").GetInt32();
        await using (var db = w.Db())
        {
            var o = await db.Set<InvSalesOrder>().AsNoTracking().SingleAsync(x => x.Id == staleId);
            Assert.Equal((2002.0m, 340.34m, 2342.34m), (o.Subtotal, o.TotalTax, o.TotalAmount));
            var lines = await db.Set<InvSalesOrderItem>().AsNoTracking().Where(i => i.SoId == staleId).OrderBy(i => i.Id).ToListAsync();
            Assert.Equal([2000.5m, 1.5m], lines.Select(l => l.TotalBeforeDiscount!.Value));
            Assert.Equal([2000.5m, 1.5m], lines.Select(l => l.TotalPrice!.Value));
            var e = await db.Set<InvSalesOrder>().AsNoTracking().SingleAsync(x => x.Id == exactId);
            Assert.Equal((92250.5m, 15682.585m, 107933.085m), (e.Subtotal, e.TotalTax, e.TotalAmount));
        }
        const string name = "totals_recomputed";
        Golden.SameInputs(name, new { stale = w.RoleJson(stale), exact = w.RoleJson(exact) });
        Golden.Match(name, [w.Answer(a.Status, a.Raw), w.Answer(b.Status, b.Raw)], "answers");
        Golden.Match(name, await w.SalesRowsAsync(staleId), "SO1");
        Golden.Match(name, await w.SalesRowsAsync(exactId), "SO2");
    }

    // ── Billed orders ──────────────────────────────────────────────────

    [ReferenceFact]
    public async Task An_order_an_invoice_was_billed_from_cannot_be_unapproved_edited_or_deleted()
    {
        await using var host = await HostAsync();
        var (w, c, token) = await SignedInAsync(host);
        var (_, a) = await World.PostAsync(c, "/inventory/sales/save", w.SalesJson("approve", "billed " + World.NewTag()), token);
        int approvedId = a.GetProperty("id").GetInt32();
        string approvedNo = a.GetProperty("number").GetString()!;
        var (_, b) = await World.PostAsync(c, "/inventory/sales/save", w.SalesJson("save", "billed draft " + World.NewTag()), token);
        int draftId = b.GetProperty("id").GetInt32();
        string draftNo = b.GetProperty("number").GetString()!;

        // A draft invoice with lines imported from both orders: the rows the invoice editor's "Import from order" leaves
        // (the line carries its order and order line), with the model's defaults.
        string tag = World.NewTag();
        int invoiceId;
        await using (var db = w.Db())
        {
            var inv = new InvInvoice
            {
                InvoiceNumber = $"SI-T-{tag}", VoucherNumber = $"SI-T-{tag}", CustomerId = w.Customer, VoucherStatus = "unapproved",
                PaymentStatus = "unpaid", InvoiceDate = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, EditVersion = 0,
            };
            db.Add(inv);
            await db.SaveChangesAsync();
            invoiceId = inv.Id;
            foreach (int so in new[] { approvedId, draftId })
            {
                var line = await db.Set<InvSalesOrderItem>().Where(i => i.SoId == so).OrderBy(i => i.Id).FirstAsync();
                db.Add(new InvInvoiceItem
                {
                    InvoiceId = inv.Id, ProductId = line.ProductId, Quantity = 1, UnitPrice = 1, SourceOrderId = so, SourceOrderItemId = line.Id,
                });
            }
            await db.SaveChangesAsync();
        }
        try
        {
            var (s1, u) = await World.PostAsync(c, $"/inventory/sales/unapprove/{approvedId}", new JsonObject(), token);
            Assert.Equal(HttpStatusCode.BadRequest, s1);
            Assert.Equal($"Cannot unapprove {approvedNo}: invoice(s) SI-T-{tag} were billed from it. Unapprove and delete those " +
                         "invoices first, or cancel the order to stop further billing.", u.GetProperty("error").GetString());

            var (s2, e) = await World.PostAsync(c, "/inventory/sales/save", w.SalesJson("save", "edit", draftId), token);
            Assert.Equal(HttpStatusCode.BadRequest, s2);
            Assert.Equal($"Cannot change {draftNo}: invoice(s) SI-T-{tag} were billed from it. Unapprove and delete those invoices first.",
                         e.GetProperty("error").GetString());

            var (s3, d) = await World.PostAsync(c, $"/inventory/sales/delete/{draftId}", new JsonObject(), token);
            Assert.Equal(HttpStatusCode.BadRequest, s3);
            Assert.Equal($"Cannot delete {draftNo}: invoice(s) SI-T-{tag} were billed from it.", d.GetProperty("error").GetString());

            await using var db = w.Db();
            Assert.Equal("approved", (await db.Set<InvSalesOrder>().AsNoTracking().SingleAsync(o => o.Id == approvedId)).Status);
            Assert.Equal(2, await db.Set<InvSalesOrderItem>().CountAsync(i => i.SoId == draftId));
        }
        finally
        {
            await using var db = w.Db();
            await db.Set<InvInvoiceItem>().Where(i => i.InvoiceId == invoiceId).ExecuteDeleteAsync();
            await db.Set<InvInvoice>().Where(i => i.Id == invoiceId).ExecuteDeleteAsync();
        }
    }

    // ── Stale copies ───────────────────────────────────────────────────

    [ReferenceFact]
    public async Task A_save_from_a_stale_copy_is_refused_naming_who_changed_it_and_when()
    {
        await using var host = await HostAsync();
        var (w, c, token) = await SignedInAsync(host);
        var (_, a) = await World.PostAsync(c, "/inventory/sales/save", w.SalesJson("save", "stale " + World.NewTag()), token);
        int id = a.GetProperty("id").GetInt32();
        string number = a.GetProperty("number").GetString()!;
        int opened = a.GetProperty("edit_version").GetInt32();

        // Another person saves the order after this copy was opened (a second browser), and the audit trail records it
        // as Platform's audit interceptor does in the full app (this host boots Orders alone): the who / when of the refusal.
        var other = w.Client(host);
        string otherToken = await other.TokenFromAsync("/inventory/sales/list");
        var (theirs, _) = await World.PostAsync(other, "/inventory/sales/save", w.SalesJson("save", "their edit", id, version: opened), otherToken);
        Assert.Equal(HttpStatusCode.OK, theirs);
        await using (var db = World.Db(null))
        {
            db.AuditLogs.Add(new AuditLog
            {
                CompanyId = w.Company, CreatedAt = DateTime.UtcNow, UserId = w.Admin, UserName = w.AdminName, Action = "update",
                Module = "invoicing", EntityType = "Sales order", EntityId = id, Reference = number,
                Summary = $"Sales order {number} updated", Path = "/inventory/sales/save",
            });
            await db.SaveChangesAsync();
        }

        var (st, body) = await World.PostAsync(c, "/inventory/sales/save", w.SalesJson("save", "my edit", id, version: opened), token);
        Assert.Equal(HttpStatusCode.Conflict, st);
        Assert.True(body.GetProperty("stale").GetBoolean());
        Assert.Matches($"^Order {number} was changed by {Regex.Escape(w.AdminName)} at \\d\\d:\\d\\d on \\d\\d [A-Z][a-z]{{2}} after you opened it, " +
                       "so your changes were not saved\\. Reload it to see the latest version, then make your changes again\\.$",
                       body.GetProperty("error").GetString()!);
        await using (var db = w.Db())
        {
            var o = await db.Set<InvSalesOrder>().AsNoTracking().SingleAsync(x => x.Id == id);
            Assert.Equal(("their edit", opened + 1), (o.Notes, o.EditVersion ?? 0));
        }

        // With the current version the save goes through.
        var (ok, saved) = await World.PostAsync(c, "/inventory/sales/save", w.SalesJson("save", "my edit", id, version: opened + 1), token);
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Equal(opened + 2, saved.GetProperty("edit_version").GetInt32());
        Assert.Equal("Order changes saved", saved.GetProperty("message").GetString());
    }

    // ── Cancel, Bill (deliver / receive) ───────────────────────────────

    [ReferenceFact]
    public async Task Cancel_is_post_only_and_refuses_an_order_already_closed()
    {
        await using var host = await HostAsync();
        var (w, c, token) = await SignedInAsync(host);
        var (_, a) = await World.PostAsync(c, "/inventory/sales/save", w.SalesJson("approve", "cancel " + World.NewTag()), token);
        int id = a.GetProperty("id").GetInt32();
        string number = a.GetProperty("number").GetString()!;

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await c.GetAsync($"/inventory/sales/cancel/{id}")).StatusCode);
        var r = await c.PostFormWithTokenAsync($"/inventory/sales/cancel/{id}", [], token);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/inventory/sales/list", r.Headers.Location!.OriginalString);
        string list = await (await c.GetAsync("/inventory/sales/list")).Content.ReadAsStringAsync();
        Assert.Contains($"SO {number} cancelled; what is still unbilled can no longer be invoiced.", list);
        await using (var db = w.Db())
            Assert.Equal("cancelled", (await db.Set<InvSalesOrder>().AsNoTracking().SingleAsync(o => o.Id == id)).Status);

        await c.PostFormWithTokenAsync($"/inventory/sales/cancel/{id}", [], token);
        list = await (await c.GetAsync("/inventory/sales/list")).Content.ReadAsStringAsync();
        Assert.Contains($"SO {number} is cancelled; it cannot be cancelled.", list);
    }

    [ReferenceFact]
    public async Task Deliver_and_receive_send_the_user_to_bill_the_order()
    {
        await using var host = await HostAsync();
        var (w, c, token) = await SignedInAsync(host);
        var (_, so) = await World.PostAsync(c, "/inventory/sales/save", w.SalesJson("approve", "deliver " + World.NewTag()), token);
        var (_, po) = await World.PostAsync(c, "/inventory/purchases/save", w.PurchaseJson("approve", "receive " + World.NewTag()), token);
        int soId = so.GetProperty("id").GetInt32(), poId = po.GetProperty("id").GetInt32();
        decimal stockBefore;
        await using (var db = w.Db())
            stockBefore = await db.Set<InvProduct>().Where(p => p.Id == w.Products[0]).Select(p => p.CurrentStock ?? 0).SingleAsync();

        var d = await c.GetAsync($"/inventory/sales/deliver/{soId}");
        Assert.Equal(("/inventory/invoices/", HttpStatusCode.Redirect), (d.Headers.Location!.OriginalString, d.StatusCode));
        var r = await c.GetAsync($"/inventory/purchases/receive/{poId}");
        Assert.Equal("/inventory/purchase-invoice/", r.Headers.Location!.OriginalString);
        string page = await (await c.GetAsync("/inventory/sales/list")).Content.ReadAsStringAsync();
        Assert.Contains($"Bill {so.GetProperty("number").GetString()} with a sales invoice (Import from order)", page);
        Assert.Contains($"Bill {po.GetProperty("number").GetString()} with a purchase invoice (Import from order)", page);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/inventory/sales/deliver/99999999")).StatusCode);

        await using var db2 = w.Db();
        Assert.Equal(stockBefore, await db2.Set<InvProduct>().Where(p => p.Id == w.Products[0]).Select(p => p.CurrentStock ?? 0).SingleAsync());
    }

    // ── Rights ─────────────────────────────────────────────────────────

    [ReferenceFact]
    public async Task Each_action_needs_its_right_pages_refuse_with_a_flash_and_json_with_403()
    {
        // "restricted" (a manager in company B) with Invoicing switched on and only "view" on sales orders.
        int restricted = Reference.UserId(Reference.Email("restricted"));
        int companyB = Reference.Company("b");
        await using var db = World.Db(companyB);
        var membership = await db.Memberships.SingleAsync(m => m.UserId == restricted && m.CompanyId == companyB);
        var soRight = await db.UserPermissions.SingleAsync(p => p.UserId == restricted && p.CompanyId == companyB && p.Resource == "sales_orders");
        string? flags = membership.ModuleFlags;
        membership.ModuleFlags = flags!.Replace("\"invoicing\": false", "\"invoicing\": true");
        soRight.CanView = true;
        await db.SaveChangesAsync();
        try
        {
            await using var host = await HostAsync();
            var c = host.ClientFor(restricted, companyB);
            var list = await c.GetAsync("/inventory/sales/list");
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            string html = await list.Content.ReadAsStringAsync();
            Assert.Contains("href=\"/inventory/sales/list\"", html);
            Assert.DoesNotContain("href=\"/inventory/purchases/list\"", html);       // the sidebar hides what is refused
            string token = await c.TokenFromAsync("/inventory/sales/list");

            foreach (string url in new[] { "/inventory/sales/", "/inventory/purchases/list", "/inventory/purchases/1" })
            {
                var r = await c.GetAsync(url);
                Assert.True(r.StatusCode == HttpStatusCode.Redirect, url);
            }
            string flashed = WebUtility.HtmlDecode(await (await c.GetAsync("/inventory/sales/list")).Content.ReadAsStringAsync());
            Assert.Contains("No 'create' rights here. Ask an admin in Settings.", flashed);

            var api = await c.GetAsync("/inventory/purchases/api/products?q=a");
            Assert.Equal(HttpStatusCode.Forbidden, api.StatusCode);
            Assert.Contains("You don't have 'view' rights for this section.", System.Text.RegularExpressions.Regex.Unescape(await api.Content.ReadAsStringAsync()));
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/inventory/sales/api/products?q=a")).StatusCode);

            var (s1, b1) = await World.PostAsync(c, "/inventory/sales/save", new JsonObject { ["action"] = "approve" }, token);
            Assert.Equal(HttpStatusCode.Forbidden, s1);
            Assert.Equal("You don't have 'create' rights for this section. Ask an administrator to grant access in Settings.",
                         b1.GetProperty("error").GetString());
            var (s2, b2) = await World.PostAsync(c, "/inventory/sales/save", new JsonObject { ["id"] = "1", ["action"] = "approve" }, token);
            Assert.Equal(HttpStatusCode.Forbidden, s2);
            Assert.Contains("'approve' rights", b2.GetProperty("error").GetString());
            var (s3, b3) = await World.PostAsync(c, "/inventory/sales/unapprove/1", new JsonObject(), token);
            Assert.Equal(HttpStatusCode.Forbidden, s3);
            Assert.Contains("'approve' rights", b3.GetProperty("error").GetString());
            var (s4, b4) = await World.PostAsync(c, "/inventory/sales/delete/1", new JsonObject(), token);
            Assert.Equal(HttpStatusCode.Forbidden, s4);
            Assert.Contains("'delete' rights", b4.GetProperty("error").GetString());
            var cancel = await c.PostFormWithTokenAsync("/inventory/sales/cancel/1", [], token);
            Assert.Equal(HttpStatusCode.Redirect, cancel.StatusCode);
            Assert.Contains("No 'edit' rights here.", WebUtility.HtmlDecode(await (await c.GetAsync("/inventory/sales/list")).Content.ReadAsStringAsync()));
        }
        finally
        {
            membership.ModuleFlags = flags;
            soRight.CanView = false;
            await db.SaveChangesAsync();
        }
    }

    // ── Bad input answers as Python does ───────────────────────────────

    [ReferenceFact]
    public async Task Unreadable_figures_are_a_server_error_and_save_nothing()
    {
        await using var host = await HostAsync();
        var (w, c, token) = await SignedInAsync(host);
        string note = "bad " + World.NewTag();
        var bad = w.SalesJson("save", note);
        bad["subtotal"] = "12,000";
        var r = await c.PostJsonWithTokenAsync("/inventory/sales/save", bad, token);
        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
        var badDate = w.SalesJson("save", note);
        badDate["order_date"] = "12/05/2026";
        Assert.Equal(HttpStatusCode.InternalServerError, (await c.PostJsonWithTokenAsync("/inventory/sales/save", badDate, token)).StatusCode);
        var noCustomer = w.SalesJson("save", note);
        noCustomer["customer_id"] = 0;
        var gone = await World.PostAsync(c, "/inventory/sales/save", noCustomer, token);
        Assert.Equal(HttpStatusCode.Conflict, gone.Status);
        Assert.Equal("A record this depends on was removed by another user. Nothing was saved.", gone.Body.GetProperty("error").GetString());
        await using var db = w.Db();
        Assert.False(await db.Set<InvSalesOrder>().AnyAsync(o => o.Notes == note));
    }
}
