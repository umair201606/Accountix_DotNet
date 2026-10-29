using System.Net;
using System.Text.Json;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Returns.Tests;

/// <summary>Order balances a credit note gives back, rights, a closed period, and the sidebar.</summary>
public class LinkageAndRightsTests
{
    private static int Owner => World.Owner;
    private static int CompanyB => World.CompanyB;
    private static int Restricted => Reference.UserId(Reference.Email("restricted"));

    private static async Task<(decimal? Invoiced, string? Status)> OrderLineAsync(int soItemId)
    {
        await using var db = World.Db();
        var line = await db.Set<InvSalesOrderItem>().AsNoTracking().SingleAsync(i => i.Id == soItemId);
        var status = await db.Set<InvSalesOrder>().AsNoTracking().Where(o => o.Id == line.SoId).Select(o => o.FulfilmentStatus).SingleAsync();
        return (line.InvoicedQty, status);
    }

    [ReferenceFact]
    public async Task A_credit_note_reopens_the_sales_order_and_unapproving_it_closes_it_again()
    {
        await using var host = await ModuleHost.StartAsync(SalesReturnTests.Modules);
        var w = await World.CreateAsync(host, 1, [[new(0, 10, 100)]], []);
        int p = w.Products[0];
        var c = host.ClientFor(Owner, CompanyB);
        // An approved sales order for 5 (Orders' own route), billed in full by an approved invoice (Invoicing's).
        int so = await World.SaveAsync(c, "/inventory/sales/save", "/inventory/sales/", new Dictionary<string, object?>
        {
            ["customer_id"] = w.Customer, ["order_date"] = World.InvoiceDate, ["so_number"] = "(auto)", ["tax_mode"] = "general",
            ["global_sales_tax_pct"] = 0, ["subtotal"] = 750, ["total_tax"] = 0, ["total_amount"] = 750, ["action"] = "approve",
            ["items"] = new[]
            {
                new Dictionary<string, object?> { ["product_id"] = p, ["description"] = "Part", ["quantity"] = 5, ["unit"] = "pcs", ["unit_price"] = 150,
                    ["sales_tax_pct"] = 0, ["total_before_discount"] = 750, ["total_after_discount"] = 750, ["total_price"] = 750 },
            },
        });
        int soi;
        await using (var db = World.Db())
            soi = await db.Set<InvSalesOrderItem>().Where(i => i.SoId == so).OrderBy(i => i.Id).Select(i => i.Id).FirstAsync();
        int si = await World.SaveAsync(c, "/inventory/invoices/save", "/inventory/invoices/", new Dictionary<string, object?>
        {
            ["customer_id"] = w.Customer, ["invoice_date"] = World.InvoiceDate, ["discount_mode"] = "general", ["charges_mode"] = "general",
            ["tax_mode"] = "general", ["global_discount_pct"] = 0, ["global_discount_value"] = 0, ["global_delivery"] = 0, ["global_installation"] = 0,
            ["global_sales_tax_pct"] = 0, ["further_tax_pct"] = 0, ["apply_further_tax"] = false, ["withholding_tax_pct"] = 0, ["subtotal"] = 750,
            ["total_discount"] = 0, ["total_charges"] = 0, ["total_tax"] = 0, ["total_amount"] = 750, ["action"] = "approve",
            ["items"] = new[]
            {
                new Dictionary<string, object?> { ["product_id"] = p, ["quantity"] = 5, ["unit_price"] = 150, ["description"] = "Part", ["unit"] = "pcs",
                    ["discount_pct"] = 0, ["delivery"] = 0, ["installation"] = 0, ["sales_tax_pct"] = 0, ["total_before_discount"] = 750,
                    ["total_after_discount"] = 750, ["source_order_id"] = so, ["source_order_item_id"] = soi },
            },
        });
        Assert.Equal((5m, "invoiced"), await OrderLineAsync(soi));

        var r = await c.PostJsonAsync("/invoicing/sales-return/save", Payloads.Sales(si, w.Customer, [new(p, 2, 5, 150)], "approve"),
                                      "/invoicing/sales-return/");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        int id = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();
        Assert.Equal((3m, "partial"), await OrderLineAsync(soi));

        Assert.Equal(HttpStatusCode.OK, (await SalesReturnTests.UnapproveAsync(c, "/invoicing/sales-return", id)).StatusCode);
        Assert.Equal((5m, "invoiced"), await OrderLineAsync(soi));
    }

    [ReferenceFact]
    public async Task A_return_into_a_closed_period_is_refused_and_nothing_is_saved()
    {
        await using var host = await ModuleHost.StartAsync(SalesReturnTests.Modules);
        var w = await World.CreateAsync(host, 1, [[new(0, 5, 100)]], [[new(0, 5, 150)]]);
        int si = w.SalesInvoices[0];
        await using (var db = World.Db())
        {
            // Provisioning gives company B a period per year up to two years ahead.
            Assert.True(await db.Set<AccountingPeriod>().AnyAsync(x => x.FiscalYear == "2028"), "company B has no 2028 period");
            await db.Set<AccountingPeriod>().Where(x => x.FiscalYear == "2028").ExecuteUpdateAsync(s => s.SetProperty(x => x.IsClosed, true));
        }
        try
        {
            var c = host.ClientFor(Owner, CompanyB);
            var r = await c.PostJsonAsync("/invoicing/sales-return/save",
                Payloads.Sales(si, w.Customer, [new(w.Products[0], 1, 5, 150)], "approve", date: "2028-02-01"), "/invoicing/sales-return/");
            Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
            var body = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
            Assert.False(body.GetProperty("ok").GetBoolean());
            Assert.Contains("closed", body.GetProperty("error").GetString()!, StringComparison.OrdinalIgnoreCase);
            await using var db = World.Db();
            Assert.False(await db.Set<InvSalesReturn>().AnyAsync(x => x.OriginalInvoiceId == si));
        }
        finally
        {
            await using var db = World.Db();
            await db.Set<AccountingPeriod>().Where(x => x.FiscalYear == "2028").ExecuteUpdateAsync(s => s.SetProperty(x => x.IsClosed, false));
        }
    }

    /// <summary>
    /// Settings → Rights for the restricted manager of company B, written as Platform's form writes it
    /// (SettingsController.SaveUserRights): module switches on the membership, then every section's row
    /// rewritten from the grid. <paramref name="extra"/> is added to the grid the reference database gave them.
    /// </summary>
    private static async Task SetGridAsync(params string[] extra)
    {
        var on = new HashSet<string>(new[] { "module_accounting", "module_hr", "perm_journal_vouchers_view",
                                             "perm_journal_vouchers_create", "perm_chart_of_accounts_view" }.Concat(extra));
        await using var db = World.Db();
        await using var tx = await db.Database.BeginTransactionAsync();
        var membership = await db.Memberships.FirstAsync(x => x.CompanyId == CompanyB && x.UserId == Restricted
                                                              && x.Status == CompanyMembership.StatusActive);
        membership.SetModuleFlags(Modules.All.ToDictionary(x => x.Key, x => on.Contains($"module_{x.Key}")));
        await db.UserPermissions.Where(x => x.UserId == Restricted && x.CompanyId == CompanyB).ExecuteDeleteAsync();
        foreach (var mod in Modules.All)
            foreach (var sec in mod.Sections)
                db.UserPermissions.Add(new UserPermission
                {
                    UserId = Restricted, Resource = sec.Key,
                    CanView = on.Contains($"perm_{sec.Key}_view"), CanCreate = on.Contains($"perm_{sec.Key}_create"),
                    CanEdit = on.Contains($"perm_{sec.Key}_edit"), CanApprove = on.Contains($"perm_{sec.Key}_approve"),
                    CanDelete = on.Contains($"perm_{sec.Key}_delete"),
                });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    [ReferenceFact]
    public async Task Rights_follow_the_settings_grid_and_refuse_in_json()
    {
        await using (var setup = await ModuleHost.StartAsync(SalesReturnTests.Modules))
        {
            var w = await World.CreateAsync(setup, 1, [[new(0, 5, 100)]], [[new(0, 5, 150)]]);
            await SetGridAsync("module_invoicing", "perm_sales_returns_view", "perm_sales_returns_create", "perm_sales_returns_edit");
            try
            {
                await RestrictedAsync(w);
            }
            finally
            {
                await SetGridAsync();
            }
        }

        // Without the Invoicing module the whole module is closed.
        await using var host2 = await ModuleHost.StartAsync(SalesReturnTests.Modules);
        var c2 = host2.ClientFor(Restricted, CompanyB);
        Assert.Equal(HttpStatusCode.Forbidden, (await c2.GetAsync("/invoicing/sales-return/list")).StatusCode);
    }

    private static async Task RestrictedAsync(World w)
    {
        int si = w.SalesInvoices[0];
        await using var host = await ModuleHost.StartAsync(SalesReturnTests.Modules);
        var c = host.ClientFor(Restricted, CompanyB);

        var list = await c.GetAsync("/invoicing/sales-return/list");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        string html = await list.Content.ReadAsStringAsync();
        Assert.Contains("href=\"/invoicing/sales-return/list\"", html);
        Assert.DoesNotContain("href=\"/inventory/purchase-return/list\"", html);   // no purchase_returns view: hidden

        var pr = await c.GetAsync("/inventory/purchase-return/list");
        Assert.Equal(HttpStatusCode.Redirect, pr.StatusCode);
        var prApi = await c.GetAsync("/inventory/purchase-return/api/invoices");
        Assert.Equal(HttpStatusCode.Forbidden, prApi.StatusCode);

        const string NoApprove = "You don't have 'approve' rights for this section. Ask an administrator to grant access in Settings.";
        var lines = new List<Payloads.Line> { new(w.Products[0], 1, 5, 150) };
        var approve = await c.PostJsonAsync("/invoicing/sales-return/save", Payloads.Sales(si, w.Customer, lines, "approve"), "/invoicing/sales-return/");
        Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);
        Assert.Equal(NoApprove, JsonDocument.Parse(await approve.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());

        var save = await c.PostJsonAsync("/invoicing/sales-return/save", Payloads.Sales(si, w.Customer, lines, "save"), "/invoicing/sales-return/");
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        int id = JsonDocument.Parse(await save.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();

        var un = await SalesReturnTests.UnapproveAsync(c, "/invoicing/sales-return", id);
        Assert.Equal(HttpStatusCode.Forbidden, un.StatusCode);   // JSON, though the request asked for nothing in particular
        Assert.Equal(NoApprove, JsonDocument.Parse(await un.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());
    }

    [ReferenceFact]
    public async Task The_sidebar_puts_returns_last_in_procurement_and_sales()
    {
        await using var host = await ModuleHost.StartAsync(SalesReturnTests.Modules);
        var c = host.ClientFor(Owner, CompanyB);
        string html = await (await c.GetAsync("/inventory/purchase-return/list")).Content.ReadAsStringAsync();
        int procurement = html.IndexOf("Procurement", StringComparison.Ordinal);
        int sales = html.IndexOf(">Sales<", StringComparison.Ordinal) is var s and >= 0 ? s : html.IndexOf("Sales", procurement + 1, StringComparison.Ordinal);
        int pr = html.IndexOf("href=\"/inventory/purchase-return/list\"", StringComparison.Ordinal);
        int sr = html.IndexOf("href=\"/invoicing/sales-return/list\"", StringComparison.Ordinal);
        Assert.True(procurement >= 0 && pr > procurement && sr > pr, $"{procurement} {pr} {sales} {sr}");
        // The page's own item is the highlighted one.
        int active = html.IndexOf("active", pr - 200, StringComparison.Ordinal);
        Assert.True(active >= 0 && active < pr + 200);
    }
}
