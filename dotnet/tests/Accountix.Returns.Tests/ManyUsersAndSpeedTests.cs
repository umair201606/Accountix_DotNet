using System.Net;
using System.Text.Json;
using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Returns.Tests;

/// <summary>
/// Several people returning goods against one invoice at the same moment, and the
/// speed of the return pages on a full year of data (docs/SPEED_AND_CONCURRENCY.md).
/// </summary>
public class ManyUsersAndSpeedTests
{
    private static int Owner => World.Owner;
    private static int CompanyB => World.CompanyB;

    private static async Task<(int Approved, double Qty, int Journals)> ApprovedAgainstAsync(int salesInvoiceId)
    {
        await using var db = World.Db();
        var ids = await db.Set<InvSalesReturn>().Where(r => r.OriginalInvoiceId == salesInvoiceId && r.Status == "approved")
            .Select(r => r.Id).ToListAsync();
        double qty = (double)(await db.Set<InvSalesReturnItem>().Where(i => ids.Contains(i.ReturnId)).SumAsync(i => i.CurrentReturnQty) ?? 0);
        int journals = await db.Set<JournalEntry>().CountAsync(e => e.VoucherType == "SR" && ids.Contains(e.VoucherId) && e.IsPosted == true);
        return (ids.Count, qty, journals);
    }

    private static async Task<int> PostedAsync(string type, int id)
    {
        await using var db = World.Db();
        return await db.Set<JournalEntry>().CountAsync(e => e.VoucherType == type && e.VoucherId == id && e.IsPosted == true);
    }

    [ReferenceFact]
    public async Task Six_returns_raised_together_cannot_return_more_than_was_sold()
    {
        // 10 sold; six people each credit 4 of them at once: two go through, four are refused.
        await using var host = await ModuleHost.StartAsync(SalesReturnTests.Modules);
        var w = await World.CreateAsync(host, 1, [[new(0, 10, 100)]], [[new(0, 10, 150)]]);
        int si = w.SalesInvoices[0];
        var clients = host.ClientsFor(Owner, CompanyB, 6);
        var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync("/invoicing/sales-return/")));
        var payload = Payloads.Sales(si, w.Customer, [new(w.Products[0], 4, 10, 150)], "approve");
        var rs = await Race.RunAsync(6, i => clients[i].PostJsonWithTokenAsync("/invoicing/sales-return/save", payload, tokens[i]));

        Race.NoServerErrors(rs);
        Assert.Equal(2, rs.Count(r => r.StatusCode == HttpStatusCode.OK));
        foreach (var r in rs.Where(r => r.StatusCode != HttpStatusCode.OK))
            Assert.Contains("remain returnable on the invoice", await r.Content.ReadAsStringAsync());
        Assert.Equal((2, 8.0, 2), await ApprovedAgainstAsync(si));
    }

    [ReferenceFact]
    public async Task Six_people_approving_one_return_post_it_once()
    {
        await using var host = await ModuleHost.StartAsync(SalesReturnTests.Modules);
        var w = await World.CreateAsync(host, 1, [[new(0, 10, 100)]], [[new(0, 6, 150)]]);
        int si = w.SalesInvoices[0];
        var c = host.ClientFor(Owner, CompanyB);
        var lines = new List<Payloads.Line> { new(w.Products[0], 2, 6, 150) };
        var draft = await c.PostJsonAsync("/invoicing/sales-return/save", Payloads.Sales(si, w.Customer, lines, "save"), "/invoicing/sales-return/");
        int id = JsonDocument.Parse(await draft.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();

        var clients = host.ClientsFor(Owner, CompanyB, 6);
        var tokens = await Task.WhenAll(clients.Select(x => x.TokenFromAsync($"/invoicing/sales-return/{id}")));
        var payload = Payloads.Sales(si, w.Customer, lines, "approve", id, 0);
        var rs = await Race.RunAsync(6, i => clients[i].PostJsonWithTokenAsync("/invoicing/sales-return/save", payload, tokens[i]));

        Race.NoServerErrors(rs);
        Assert.Single(rs, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(1, await PostedAsync("SR", id));
        Assert.Equal(1, await SalesReturnTests.StockRowCountAsync("SRV", id));
    }

    [ReferenceFact]
    public async Task Six_people_approving_one_debit_note_post_it_once()
    {
        await using var host = await ModuleHost.StartAsync(SalesReturnTests.Modules);
        var w = await World.CreateAsync(host, 1, [[new(0, 10, 100)]], []);
        int pi = w.PurchaseInvoices[0];
        var c = host.ClientFor(Owner, CompanyB);
        var lines = new List<Payloads.Line> { new(w.Products[0], 3, 10, 100) };
        var draft = await c.PostJsonAsync("/inventory/purchase-return/save", Payloads.Purchase(pi, w.Supplier, lines, "save"), "/inventory/purchase-return/");
        int id = JsonDocument.Parse(await draft.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();

        var clients = host.ClientsFor(Owner, CompanyB, 6);
        var tokens = await Task.WhenAll(clients.Select(x => x.TokenFromAsync($"/inventory/purchase-return/{id}")));
        var payload = Payloads.Purchase(pi, w.Supplier, lines, "approve", id, 0);
        var rs = await Race.RunAsync(6, i => clients[i].PostJsonWithTokenAsync("/inventory/purchase-return/save", payload, tokens[i]));

        Race.NoServerErrors(rs);
        Assert.Single(rs, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(1, await PostedAsync("PR", id));
        Assert.Equal(1, await SalesReturnTests.StockRowCountAsync("PRV", id));
    }

    // --- Full year of Samba (read only) ---

    private static async Task<(int Sr, int Pr, int Si, int Pi, string Customer, string Supplier)> PerfDocsAsync()
    {
        var company = new CompanyContext();
        company.Set(Perf.CompanyId);
        await using var db = Perf.NewDb(company);
        int sr = await db.Set<InvSalesReturn>().OrderByDescending(r => r.Id).Select(r => r.Id).FirstAsync();
        int pr = await db.Set<InvPurchaseReturn>().OrderByDescending(r => r.Id).Select(r => r.Id).FirstAsync();
        var from = Perf.BusiestMonthStart.ToDateTime(TimeOnly.MinValue);
        var to = Perf.BusiestMonthEnd.ToDateTime(TimeOnly.MaxValue);
        // The busiest month's invoice with the most lines.
        int si = await db.Set<InvInvoice>().Where(i => i.VoucherStatus == "approved" && i.InvoiceDate >= from && i.InvoiceDate <= to)
            .OrderByDescending(i => i.InvInvoiceItems.Count).ThenBy(i => i.Id).Select(i => i.Id).FirstAsync();
        int pi = await db.Set<InvPurchaseInvoice>().Where(i => i.Status == "approved")
            .OrderByDescending(i => i.InvPurchaseInvoiceItems.Count).ThenBy(i => i.Id).Select(i => i.Id).FirstAsync();
        string customer = await db.Set<InvInvoice>().Where(i => i.Id == si).Select(i => i.Customer.Name).FirstAsync();
        string supplier = await db.Set<InvPurchaseInvoice>().Where(i => i.Id == pi).Select(i => i.Supplier.Name).FirstAsync();
        return (sr, pr, si, pi, customer, supplier);
    }

    private static string Q(string s) => Uri.EscapeDataString(s.Length > 6 ? s[..6] : s);

    [PerfFact]
    public async Task Return_pages_on_a_full_year_are_quick()
    {
        var d = await PerfDocsAsync();
        await using var host = await ModuleHost.StartOnAsync(Perf.ConnectionString, SalesReturnTests.Modules);
        var c = host.ClientFor(Perf.AdminUserId, Perf.CompanyId);
        // The lists have no date filter (Python's neither): every return of the year, the year budget.
        (await host.MeasureAsync(c, "/invoicing/sales-return/list")).Within(1500, 40);
        (await host.MeasureAsync(c, "/inventory/purchase-return/list")).Within(1500, 40);
        // The forms list every approved invoice of the year in their picker.
        (await host.MeasureAsync(c, "/invoicing/sales-return/")).Within(1500, 40);
        (await host.MeasureAsync(c, $"/invoicing/sales-return/{d.Sr}")).Within(1500, 40);
        (await host.MeasureAsync(c, "/inventory/purchase-return/")).Within(1500, 40);
        (await host.MeasureAsync(c, $"/inventory/purchase-return/{d.Pr}")).Within(1500, 40);
        // The lookups the form makes while it is open: one invoice, or one page of the search.
        (await host.MeasureAsync(c, $"/invoicing/sales-return/api/invoice/{d.Si}")).Within(800, 40);
        (await host.MeasureAsync(c, "/invoicing/sales-return/api/invoices")).Within(800, 40);
        (await host.MeasureAsync(c, $"/invoicing/sales-return/api/invoices?q={Q(d.Customer)}")).Within(800, 40);
        (await host.MeasureAsync(c, $"/inventory/purchase-return/api/invoice/{d.Pi}")).Within(800, 40);
        (await host.MeasureAsync(c, "/inventory/purchase-return/api/invoices")).Within(800, 40);
        (await host.MeasureAsync(c, $"/inventory/purchase-return/api/invoices?q={Q(d.Supplier)}")).Within(800, 40);
    }
}
