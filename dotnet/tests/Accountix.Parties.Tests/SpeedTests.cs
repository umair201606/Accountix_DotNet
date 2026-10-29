using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Accountix.Parties.Tests;

/// <summary>
/// The Parties pages on a full financial year of Samba (2,760 sales invoices; docs/SPEED_AND_CONCURRENCY.md).
/// Read only. Budgets: one month ≤ 800 ms, a year with paging ≤ 1,500 ms, exports ≤ 3,000 ms, ≤ 40 queries.
/// Python's times for the same pages (measured on 29 Oct 2026, before it was retired) are kept in docs/modules/parties.md.
/// </summary>
public class SpeedTests(ITestOutputHelper output)
{
    private static string Month => $"mode=month&month={Perf.BusiestMonth}";
    private static string Year => $"mode=custom&from={Perf.YearStart:yyyy-MM-dd}&to={Perf.YearEnd:yyyy-MM-dd}";

    private static Task<ModuleHost> HostAsync() =>
        ModuleHost.StartOnAsync(Perf.ConnectionString, typeof(PartiesModule).Assembly, typeof(Accountix.Invoicing.InvoicingModule).Assembly,
                                typeof(Accountix.Accounting.AccountingModule).Assembly, typeof(Accountix.Finance.FinanceModule).Assembly);

    private static async Task<(int Invoice, int PurchaseInvoice, int Line)> SamplesAsync()
    {
        var company = new Accountix.Core.Data.CompanyContext();
        company.Set(Perf.CompanyId);
        await using var db = Perf.NewDb(company);
        // The customer with the most invoices: its history page is the heaviest.
        int customer = await db.Set<InvInvoice>().GroupBy(i => i.CustomerId).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstAsync();
        int inv = await db.Set<InvInvoice>().Where(i => i.CustomerId == customer && i.VoucherStatus == "approved").OrderBy(i => i.Id).Select(i => i.Id).FirstAsync();
        int pinv = await db.Set<InvPurchaseInvoice>().Where(i => i.Status == "approved").OrderBy(i => i.Id).Select(i => i.Id).FirstAsync();
        string parent = Accountix.Parties.Services.TrackingRules.CustomerParent + "-";
        int line = await (from l in db.Set<AccountingVoucherLine>()
                          join v in db.Set<AccountingVoucher>() on l.VoucherId equals v.Id
                          join a in db.Set<ChartOfAccount>() on l.AccountId equals a.Id
                          where v.Status == "approved" && a.Code.StartsWith(parent) && l.Credit > 0 && v.VoucherType == "BRV"
                          orderby l.Id
                          select l.Id).FirstAsync();
        return (inv, pinv, line);
    }

    public static IEnumerable<(string Url, int Ms)> Pages(int inv, int pinv, int line) =>
    [
        ($"/invoicing/registers/?doc=sales&{Month}&view=1", 800),
        ($"/invoicing/registers/?doc=purchase&{Month}&view=1", 800),
        ($"/invoicing/registers/data?doc=sales&{Month}", 800),
        ($"/invoicing/registers/data?doc=sales&{Year}&limit=200&offset=0", 1500),
        ($"/invoicing/registers/data?doc=sales&{Year}&limit=200&offset=2600", 1500),
        ($"/invoicing/registers/?doc=sales&{Year}&view=1", 1500),
        ("/invoicing/tracking/", 1100),
        ("/invoicing/tracking/?state=", 1100),
        ("/invoicing/tracking/sales", 1500),
        ("/invoicing/tracking/sales?pay=&sort=late&page=3", 1500),
        ("/invoicing/tracking/purchases", 1500),
        ($"/invoicing/tracking/invoice/SI/{inv}", 800),
        ($"/invoicing/tracking/invoice/PI/{pinv}", 800),
        ($"/invoicing/tracking/assign/{line}", 800),
        ("/inventory/customers/", 800),
        ("/inventory/suppliers/", 800),
        ($"/invoicing/registers/export?doc=sales&{Month}&fmt=excel", 3000),
        ($"/invoicing/registers/export?doc=sales&{Month}&fmt=pdf", 3000),
        ($"/invoicing/registers/export?doc=sales&{Month}&fmt=csv", 3000),
        // 2,760 rows: ~40 ms to read, the rest is Finance's exporter styling each cell (ClosedXML), 2.4–3.3 s here.
        // Reported to the coordinator; held to Python's own time (5.7 s) until that writer styles by column.
        ($"/invoicing/registers/export?doc=sales&{Year}&fmt=excel", 5700),
        // Bulk print: the first twenty documents with the page, then a hundred a chunk; the invoices' workbook.
        ($"/invoicing/registers/documents?doc=sales&{Month}", 800),
        ($"/invoicing/registers/data?full=1&doc=sales&{Month}&limit=100&offset=20&total={Perf.Json.GetProperty("busiest_month_sales_invoices").GetInt32()}", 800),
        ($"/invoicing/registers/data?full=1&doc=sales&{Year}&limit=100&offset=2600", 1500),
        ($"/invoicing/registers/documents?doc=sales&{Month}&view=1", 1500),
        ($"/invoicing/registers/documents?doc=purchase&{Month}", 800),
        ($"/invoicing/registers/documents/export?doc=sales&{Month}", 3000),
        ($"/invoicing/registers/documents/export?doc=purchase&{Month}", 3000),
    ];

    [PerfFact]
    public async Task Every_parties_page_meets_its_budget_on_a_full_year()
    {
        var (inv, pinv, line) = await SamplesAsync();
        await using var host = await HostAsync();
        var c = host.ClientFor(Perf.AdminUserId, Perf.CompanyId);
        var failures = new List<string>();
        foreach (var (url, ms) in Pages(inv, pinv, line))
        {
            var t = await host.MeasureAsync(c, url);
            output.WriteLine($"{t.Elapsed.TotalMilliseconds:F0} ms, {t.Queries} queries, {url}");
            try
            {
                t.Within(ms, 40);
            }
            catch (Exception e)
            {
                failures.Add(e.Message);
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n\n", failures));
    }
}
