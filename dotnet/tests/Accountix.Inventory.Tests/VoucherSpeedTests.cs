using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Accountix.Inventory.Tests;

/// <summary>The stock voucher pages on the full Samba year (read only), against the budgets.</summary>
public class VoucherSpeedTests(ITestOutputHelper output)
{
    private static List<string> Pages()
    {
        var company = new CompanyContext();
        company.Set(PerfTarget.CompanyId);
        using var db = new AccountixDbContext(new DbContextOptionsBuilder<AccountixDbContext>().UseNpgsql(PerfTarget.ConnectionString).Options, company);
        var urls = new List<string>
        {
            "/inventory/vouchers/consumption/list", "/inventory/vouchers/scrap/list", "/inventory/vouchers/adjustment/list",
            "/inventory/vouchers/stock-take/list", "/inventory/vouchers/consumption", "/inventory/vouchers/adjustment",
            "/inventory/vouchers/api/products", "/inventory/vouchers/api/products?q=a",
        };
        if (db.Set<ConsumptionVoucher>().OrderByDescending(v => v.Id).Select(v => (int?)v.Id).FirstOrDefault() is int c)
            urls.Add($"/inventory/vouchers/consumption/{c}");
        if (db.Set<ScrapVoucher>().OrderByDescending(v => v.Id).Select(v => (int?)v.Id).FirstOrDefault() is int s)
            urls.Add($"/inventory/vouchers/scrap/{s}");
        if (db.Set<StockLedger>().GroupBy(r => r.ProductId).OrderByDescending(g => g.Count()).Select(g => (int?)g.Key).FirstOrDefault() is int p)
            urls.Add($"/inventory/vouchers/api/product-stock/{p}");
        return urls;
    }

    [InvPerfFact]
    public async Task Voucher_pages_on_a_full_year_are_within_budget()
    {
        var urls = Pages();
        await using var host = await ModuleHost.StartOnAsync(PerfTarget.ConnectionString, typeof(InventoryModule).Assembly,
                                                             typeof(Accountix.Accounting.AccountingModule).Assembly);
        var c = host.ClientFor(PerfTarget.UserId, PerfTarget.CompanyId);
        var net = new Dictionary<string, PageTiming>();
        foreach (string url in urls)
            net[url] = (await host.MeasureAsync(c, url)).Within(800, 40);
        // Python's times for these pages (22-85 ms on the same data) are kept in docs/modules/inventory.md as history.
        foreach (var t in net.Values) output.WriteLine(t.ToString());
    }
}
