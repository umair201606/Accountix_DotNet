using System.Net;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Platform.Tests;

/// <summary>Settings: procurement / sales documents, invoice defaults, invoice templates (test_settings_access.py, test_settings_cleanup.py).</summary>
public class SettingsInvoicingTests
{
    [ReferenceFact]
    public async Task Invoicing_tabs_render_and_unknown_document_types_are_404()
    {
        await using var host = await P.HostAsync();
        var (cid, admin) = await P.FreshCompanyAsync(host);
        var c = host.ClientFor(admin, cid);
        foreach (string tab in new[] { "purchase", "sales", "invoicing", "templates" })
        {
            await c.GetAsync($"/settings/?tab={tab}");
            host.Queries.Reset();
            var r = await c.GetAsync($"/settings/?tab={tab}");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.True(host.Queries.Count <= 40, $"{tab}: {host.Queries.Count} queries");
        }
        string templates = await (await c.GetAsync("/settings/?tab=templates")).Content.ReadAsStringAsync();
        Assert.Contains("Standard Sales Invoice", templates);
        Assert.Contains("Purchase Invoice (Item Wise)", templates);
        Assert.Equal(HttpStatusCode.NotFound, (await P.PostAsync(c, "/settings/documents/bogus", "/settings/?tab=sales", ("party_mode", "all"))).StatusCode);
    }

    [ReferenceFact]
    public async Task An_employee_cannot_change_the_party_mode()
    {
        await using var host = await P.HostAsync();
        var (cid, _) = await P.FreshCompanyAsync(host);
        int emp = await P.AddMemberAsync(host, cid, Role.Employee, await P.NewUserAsync());
        var e = host.ClientFor(emp, cid);
        var r = await P.PostAsync(e, "/settings/documents/sales", "/settings/", ("party_mode", "all"));
        Assert.Equal("/settings/", r.Headers.Location?.OriginalString);
        await using var db = P.Db(cid);
        Assert.NotEqual("all", (await db.Set<ReportSetting>().SingleAsync()).SalesPartyMode);
    }

    private static readonly (string, string)[] InvoiceForm =
    [
        ("default_sales_tax_pct", "18"), ("default_further_tax_pct", "3"), ("default_withholding_tax_pct", "4.5"), ("default_discount_pct", ""),
        ("default_charges_mode", "individual"), ("default_discount_mode", "general"), ("default_tax_mode", "individual"),
        ("show_discount_column", "on"), ("auto_add_line", "on"), ("require_approval", "on"), ("default_party_mode", "all"),
        ("over_invoice_tolerance_pct", "2.5"), ("withholding_base", "gross"), ("create_from_orders_enabled", "on"),
        ("per_line_tax_enabled", "on"), ("show_transport_block", "on"),
    ];

    private static async Task<string> InvoiceSettingsShapeAsync(int cid)
    {
        await using var db = P.Db(cid);
        return ProvisioningTests.Row(db, await db.Set<InvoiceSetting>().AsNoTracking().SingleAsync());
    }

    [ReferenceFact]
    public async Task Saving_invoice_defaults_and_visibility_switches_writes_what_python_wrote()
    {
        await using var host = await P.HostAsync();
        var (net, netAdmin) = await P.FreshCompanyAsync(host);
        var c = host.ClientFor(netAdmin, net);
        var r = await P.PostAsync(c, "/settings/invoicing", "/settings/?tab=invoicing", InvoiceForm);
        Assert.Contains("Invoice settings updated.", await P.FollowAsync(c, r));
        Golden.Match("InvoiceSettings", new { invoice_settings = await InvoiceSettingsShapeAsync(net) });
        await using var dn = P.Db(net);
        // Visibility toggles render once each and show off once turned off.
        string page = await (await c.GetAsync("/settings/?tab=invoicing")).Content.ReadAsStringAsync();
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "name=\"show_further_tax\""));
        Assert.DoesNotContain("name=\"show_further_tax\" checked", page);
        Assert.Contains("name=\"show_transport_block\" checked", page);
        Assert.Contains("value=\"18.0\"", page);
        // Party mode and print template per document type.
        await P.PostAsync(c, "/settings/documents/purchase", "/settings/?tab=purchase", ("party_mode", "all"));
        int tid = await dn.Set<InvoiceTemplate>().Where(t => t.Type == "purchase").OrderBy(t => t.Id).Select(t => t.Id).FirstAsync();
        var sel = await P.PostAsync(c, "/settings/documents/purchase/select-template", "/settings/?tab=purchase", ("template_id", tid.ToString()));
        Assert.Contains("Procurement print template updated.", await P.FollowAsync(c, sel));
        var bad = await P.PostAsync(c, "/settings/documents/sales/select-template", "/settings/?tab=sales", ("template_id", tid.ToString()));
        Assert.Contains("Invalid template.", await P.FollowAsync(c, bad));
        dn.ChangeTracker.Clear();
        var rs = await dn.Set<ReportSetting>().SingleAsync();
        Assert.Equal("all", rs.PurchasePartyMode);
        Assert.Equal(tid, rs.PurchaseTemplateId);
        // Deleting the default design promotes the next one (a design a document setting points at is refused by the
        // foreign key, in both apps).
        int sid = await dn.Set<InvoiceTemplate>().Where(t => t.Type == "sales" && t.IsDefault == true).Select(t => t.Id).SingleAsync();
        var del = await P.PostAsync(c, $"/settings/templates/delete/{sid}", "/settings/?tab=templates");
        Assert.Contains("deleted.", await P.FollowAsync(c, del));
        dn.ChangeTracker.Clear();
        Assert.Equal(1, await dn.Set<InvoiceTemplate>().CountAsync(t => t.Type == "sales" && t.IsDefault == true));
    }
}
