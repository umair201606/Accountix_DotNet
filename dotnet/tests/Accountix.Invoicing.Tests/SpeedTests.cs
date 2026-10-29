using System.Globalization;
using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Invoicing.Tests;

/// <summary>
/// The invoicing pages on a full financial year of Samba (read only): the busiest month's
/// list, the year with paging, the dashboard, and the editor (with its print) of the
/// biggest invoices. Budgets from docs/SPEED_AND_CONCURRENCY.md. (Python's times for the same
/// pages, measured while both apps existed, are kept in docs/modules/invoicing.md.)
/// </summary>
public class SpeedTests
{
    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Month => $"from={D(Perf.BusiestMonthStart)}&to={D(Perf.BusiestMonthEnd)}";
    private static string Year => $"from={D(Perf.YearStart)}&to={D(Perf.YearEnd)}";

    private static Task<ModuleHost> HostAsync() =>
        ModuleHost.StartOnAsync(Perf.ConnectionString, typeof(InvoicingModule).Assembly, typeof(Accountix.Accounting.AccountingModule).Assembly);

    private static async Task<(int Si, int Pi)> BiggestAsync()
    {
        var c = new CompanyContext();
        c.Set(Perf.CompanyId);
        await using var db = Perf.NewDb(c);
        int si = await db.Set<InvInvoiceItem>().GroupBy(i => i.InvoiceId).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).FirstAsync();
        int pi = await db.Set<InvPurchaseInvoiceItem>().GroupBy(i => i.InvoiceId).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).FirstAsync();
        return (si, pi);
    }

    [PerfFact]
    public async Task Invoice_lists_for_the_busiest_month_and_the_year_are_within_budget()
    {
        await using var host = await HostAsync();
        var c = host.ClientFor(Perf.AdminUserId, Perf.CompanyId);
        var month = (await host.MeasureAsync(c, $"/inventory/invoices/list?{Month}")).Within(800, 40);
        Assert.Contains("of 318", month.Body);   // all 318 sales invoices of the month, 100 a page
        (await host.MeasureAsync(c, $"/inventory/invoices/list?{Month}&page=4")).Within(800, 40);
        (await host.MeasureAsync(c, $"/inventory/invoices/list?{Year}&page=2")).Within(1500, 40);
        (await host.MeasureAsync(c, "/inventory/invoices/list?status=approved")).Within(800, 40);
        (await host.MeasureAsync(c, $"/inventory/purchase-invoice/list?{Month}")).Within(800, 40);
        (await host.MeasureAsync(c, $"/inventory/purchase-invoice/list?{Year}&page=3")).Within(1500, 40);
    }

    [PerfFact]
    public async Task The_dashboard_and_the_biggest_invoices_open_within_budget()
    {
        var (si, pi) = await BiggestAsync();
        await using var host = await HostAsync();
        var c = host.ClientFor(Perf.AdminUserId, Perf.CompanyId);
        (await host.MeasureAsync(c, "/invoicing/")).Within(800, 40);
        (await host.MeasureAsync(c, "/invoicing/?year=2025")).Within(800, 40);
        var editor = (await host.MeasureAsync(c, $"/inventory/invoices/{si}")).Within(800, 40);
        Assert.Contains("class=\"inv-items\"", editor.Body);   // the print is rendered with the page
        (await host.MeasureAsync(c, $"/inventory/purchase-invoice/{pi}")).Within(800, 40);
        (await host.MeasureAsync(c, "/inventory/invoices/api/products?q=")).Within(800, 10);
        (await host.MeasureAsync(c, "/inventory/invoices/api/accounts?q=4")).Within(800, 10);
    }

    /// <summary>The speed-test database's document counts: the fixture is valid only for the database it was captured on.</summary>
    private static async Task<Dictionary<string, int>> CountsAsync()
    {
        var c = new CompanyContext();
        c.Set(Perf.CompanyId);
        await using var db = Perf.NewDb(c);
        return new Dictionary<string, int>
        {
            ["inv_invoices"] = await db.Set<InvInvoice>().CountAsync(),
            ["inv_purchase_invoices"] = await db.Set<InvPurchaseInvoice>().CountAsync(),
            ["inv_sales_returns"] = await db.Set<InvSalesReturn>().CountAsync(),
            ["inv_purchase_returns"] = await db.Set<InvPurchaseReturn>().CountAsync(),
        };
    }

    /// <summary>
    /// On a full year (2,760 sales invoices) the dashboard's figures are the Python app's, to the last
    /// digit: Python added the rows in floats in the scan's order (Golden/dashboard_perf.json, captured by
    /// Golden/capture/dashboard_perf.py on this read-only database).
    /// </summary>
    [PerfFact]
    public async Task The_full_year_dashboard_shows_the_figures_python_showed()
    {
        var fixture = Golden.Load("dashboard_perf").GetProperty("inputs");
        var counts = await CountsAsync();
        foreach (var (table, n) in counts)
            Assert.True(fixture.GetProperty("rows").GetProperty(table).GetInt32() == n,
                $"the speed-test database is not the one the fixture was captured on ({table}: {n})");
        await using var host = await HostAsync();
        var c = host.ClientFor(Perf.AdminUserId, Perf.CompanyId);
        foreach (var page in fixture.GetProperty("pages").EnumerateArray().Select(x => x.GetString()!))
        {
            var r = await c.GetAsync(page);
            Assert.True(r.StatusCode == System.Net.HttpStatusCode.OK, page);
            Golden.Match("dashboard_perf", InvoiceGoldenTests.PerfLines(await r.Content.ReadAsStringAsync()), page);
        }
    }
}
