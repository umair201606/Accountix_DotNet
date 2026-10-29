using System.Text;
using Accountix.TestSupport;

namespace Accountix.Tests;

/// <summary>
/// The biggest pages, on the 4× Samba company (10,405 sales invoices in the year, 1,108 in its busiest
/// month, ~90k journal lines), with every module loaded as in production. One table of results
/// (ACCOUNTIX_TIMINGS) and one failure listing every page over budget.
/// </summary>
public class BigCompanySweepTests
{
    private static System.Reflection.Assembly[] AllModules =>
    [
        typeof(Accountix.Accounting.AccountingModule).Assembly, typeof(Accountix.Finance.FinanceModule).Assembly,
        typeof(Accountix.Executive.ExecutiveModule).Assembly, typeof(Accountix.Inventory.InventoryModule).Assembly,
        typeof(Accountix.Invoicing.InvoicingModule).Assembly, typeof(Accountix.Parties.PartiesModule).Assembly,
        typeof(Accountix.Orders.OrdersModule).Assembly, typeof(Accountix.Returns.ReturnsModule).Assembly,
        typeof(Accountix.Hr.HrModule).Assembly, typeof(Accountix.HrRecords.HrRecordsModule).Assembly,
        typeof(Accountix.Payroll.PayrollModule).Assembly, typeof(Accountix.FixedAssets.FixedAssetsModule).Assembly,
        typeof(Accountix.Fbr.FbrModule).Assembly, typeof(Accountix.Platform.PlatformModule).Assembly,
    ];

    [PerfLargeFact]
    public async Task The_biggest_pages_stay_within_budget_for_a_company_four_times_samba()
    {
        await using var host = await ModuleHost.StartOnAsync(PerfLarge.ConnectionString, AllModules);
        var c = host.ClientFor(PerfLarge.AdminUserId, PerfLarge.CompanyId);
        string d1 = PerfLarge.BusiestMonthStart.ToString("yyyy-MM-dd"), d2 = PerfLarge.BusiestMonthEnd.ToString("yyyy-MM-dd");
        string y1 = PerfLarge.YearStart.ToString("yyyy-MM-dd"), y2 = PerfLarge.YearEnd.ToString("yyyy-MM-dd");
        string m = PerfLarge.BusiestMonth;
        const int Month = 800, Year = 1500, Export = 3000;

        (string Url, int Budget)[] pages =
        [
            // The user's example: sales invoices in a month.
            ($"/inventory/invoices/list?from={d1}&to={d2}", Month),
            ($"/inventory/invoices/list?from={d1}&to={d2}&page=6", Month),
            ($"/inventory/invoices/list?from={y1}&to={y2}&page=50", Year),
            ($"/inventory/purchase-invoice/list?from={d1}&to={d2}", Month),
            ("/invoicing/", Month),
            ($"/invoicing/registers/?view=1&mode=month&month={m}", Month),
            ($"/invoicing/registers/?doc=purchase&view=1&mode=month&month={m}", Month),
            ($"/invoicing/registers/data?mode=custom&from={y1}&to={y2}", Year),
            ($"/invoicing/registers/?view=1&mode=custom&from={y1}&to={y2}", Year),
            ($"/invoicing/registers/data?full=1&doc=sales&mode=month&month={m}", Year),
            ($"/invoicing/registers/export?mode=month&month={m}&fmt=excel", Export),
            ($"/invoicing/registers/export?mode=month&month={m}&fmt=pdf", Export),
            ($"/invoicing/registers/export?mode=month&month={m}&fmt=csv", Export),
            ($"/invoicing/registers/export?mode=custom&from={y1}&to={y2}&fmt=excel", 4000),
            ("/invoicing/tracking/", Year),
            ("/invoicing/tracking/sales", Year),
            ("/inventory/customers/", Month),
            ("/inventory/sales/list", Month),
            ("/invoicing/sales-return/list", Month),
            // Financial statements, the year and the month.
            ($"/finance/trial-balance?filter_mode=custom&from={y1}&to={y2}", Year),
            ($"/finance/profit-loss?filter_mode=custom&from={y1}&to={y2}", Year),
            ($"/finance/balance-sheet?filter_mode=custom&from={y1}&to={y2}", Year),
            ($"/finance/socie?filter_mode=custom&from={y1}&to={y2}", Year),
            ($"/finance/cash-flow?filter_mode=custom&from={y1}&to={y2}", Year),
            ($"/finance/ledger?filter_mode=custom&from={d1}&to={d2}&mode=all", Year),
            ($"/finance/trial-balance?filter_mode=custom&from={y1}&to={y2}&format=excel", Export),
            ("/finance/", Month),
            ($"/accounting/registers/vouchers?view=1&mode=month&month={m}", Month),
            ($"/accounting/registers/vouchers?view=1&mode=custom&from={y1}&to={y2}", Year),
            ("/accounting/", Month),
            // Executive and integrity.
            ("/executive/", Month),
            ("/executive/receivables", Month),
            ("/executive/integrity?run=1", Year),
            ("/executive/integrity?format=excel", Export),
            // Stock.
            ($"/inventory/reports/valuation?as_of={y2}", Year),
            ("/inventory/", Month),
        ];

        var results = new List<PageTiming>();
        foreach (var (url, _) in pages)
            results.Add(await host.MeasureAsync(c, url, runs: 2));

        var report = new StringBuilder($"4x Samba ({PerfLarge.Rows("inv_invoices")} SI, {PerfLarge.Rows("journal_lines")} journal lines):\n");
        var over = new List<string>();
        for (int i = 0; i < pages.Length; i++)
        {
            var t = results[i];
            report.AppendLine($"  {t.Elapsed.TotalMilliseconds,6:F0} ms {t.Queries,3} q {(int)t.Status} {Size(t.Body),8} {t.Url}");
            if ((int)t.Status >= 400 || t.Elapsed.TotalMilliseconds > pages[i].Budget || t.Queries > 40)
                over.Add($"{t} (budget {pages[i].Budget} ms)");
        }
        static string Size(string body) =>
            body.StartsWith('<') && body.EndsWith("bytes", StringComparison.Ordinal) is false && body.StartsWith("<") && body.Contains(" bytes ")
                ? body.Split(' ')[0].TrimStart('<') + " B" : $"{System.Text.Encoding.UTF8.GetByteCount(body) / 1024} KB";
        if (Environment.GetEnvironmentVariable("ACCOUNTIX_SWEEP") is { Length: > 0 } file)
            File.WriteAllText(file, report.ToString());
        Assert.True(over.Count == 0, "Over budget:\n" + string.Join("\n", over) + "\n\n" + report);
    }
}
