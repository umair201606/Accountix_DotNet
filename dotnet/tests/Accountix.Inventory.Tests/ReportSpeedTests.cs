using Accountix.Core.Data;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Accountix.Inventory.Tests;

/// <summary>
/// The full-year Samba database (read only): who to sign in as and which
/// dates/product to time. From dotnet/tools/perf.json when the coordinator has
/// written it; for a local measurement before that, INV_PERF_DB=1 finds the
/// same database (accountix_ref_perf) and its Samba company directly.
/// </summary>
public static class PerfTarget
{
    public static bool Available => Perf.Available || (Environment.GetEnvironmentVariable("INV_PERF_DB") == "1" && Reference.Available);

    private static readonly Lazy<(string Cs, int Company, int User, DateOnly From, DateOnly To)> Info = new(Load);

    public static string ConnectionString => Info.Value.Cs;
    public static int CompanyId => Info.Value.Company;
    public static int UserId => Info.Value.User;
    public static DateOnly YearStart => Info.Value.From;
    public static DateOnly YearEnd => Info.Value.To;

    private static (string, int, int, DateOnly, DateOnly) Load()
    {
        if (Perf.Available)
            return (Perf.ConnectionString, Perf.CompanyId, Perf.AdminUserId, Perf.YearStart, Perf.YearEnd);
        string cs = Reference.Json.GetProperty("connection_string").GetString()!.Replace("Database=accountix_ref;", "Database=accountix_ref_perf;");
        using var db = new AccountixDbContext(new DbContextOptionsBuilder<AccountixDbContext>().UseNpgsql(cs).Options, new CompanyContext());
        var samba = db.Companies.Single(c => c.Slug == "samba");
        int user = db.Memberships.Where(m => m.CompanyId == samba.Id && m.Status == "active").OrderBy(m => m.UserId).Select(m => m.UserId).First();
        return (cs, samba.Id, user, new DateOnly(2025, 7, 1), new DateOnly(2026, 6, 30));
    }

    /// <summary>The product with the most stock-ledger rows (the busiest ledger to print).</summary>
    public static int BusiestProduct()
    {
        var company = new CompanyContext();
        company.Set(CompanyId);
        using var db = new AccountixDbContext(new DbContextOptionsBuilder<AccountixDbContext>().UseNpgsql(ConnectionString).Options, company);
        return db.Set<Accountix.Core.Entities.StockLedger>().GroupBy(r => r.ProductId)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).First();
    }

    /// <summary>Products whose shown stock differs from the ledger: the dashboard would write them (the DB is read only).</summary>
    public static int Drifted()
    {
        var company = new CompanyContext();
        company.Set(CompanyId);
        using var db = new AccountixDbContext(new DbContextOptionsBuilder<AccountixDbContext>().UseNpgsql(ConnectionString).Options, company);
        return db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM inv_products p
            WHERE p.company_id = {CompanyId} AND p.is_active
              AND abs(coalesce(p.current_stock, 0) - coalesce((SELECT s.running_qty FROM stock_ledger s WHERE s.product_id = p.id
                    ORDER BY s.txn_date DESC, s.id DESC LIMIT 1), 0)) > 0.00005
            """).ToList().Single();
    }
}

public sealed class InvPerfFactAttribute : FactAttribute
{
    public InvPerfFactAttribute()
    {
        if (!PerfTarget.Available)
            Skip = "Speed-test database not available (dotnet/tools/perf.json, or INV_PERF_DB=1 for a local run)";
    }
}

/// <summary>Budgets of docs/SPEED_AND_CONCURRENCY.md on a full year of Samba.</summary>
public class ReportSpeedTests(ITestOutputHelper output)
{
    private static Task<ModuleHost> HostAsync() =>
        ModuleHost.StartOnAsync(PerfTarget.ConnectionString, typeof(InventoryModule).Assembly,
                                typeof(Accountix.Accounting.AccountingModule).Assembly, typeof(ReportsFakeExportModule).Assembly);

    private static string Year => $"from={PerfTarget.YearStart:yyyy-MM-dd}&as_of={PerfTarget.YearEnd:yyyy-MM-dd}";

    public static IReadOnlyList<(string Url, int Ms)> Pages(int busiest) =>
    [
        ($"/inventory/reports/valuation?{Year}", 1500),
        ($"/inventory/reports/stock-ledger?product_id={busiest}", 800),
        ($"/inventory/vouchers/product-ledger?product_id={busiest}", 800),
        ($"/inventory/reports/api/stock-ledger-json?product_id={busiest}", 800),
        ("/inventory/vouchers/product-ledger/list", 800),
        ("/inventory/reports/low-stock", 800),
        ("/inventory/stock/", 800),
        ("/inventory/stock/movements", 800),
        ($"/inventory/stock/movements?product_id={busiest}", 800),
    ];

    [InvPerfFact]
    public async Task Inventory_reports_on_a_full_year_are_within_budget()
    {
        int busiest = PerfTarget.BusiestProduct();
        await using var host = await HostAsync();
        var c = host.ClientFor(PerfTarget.UserId, PerfTarget.CompanyId);
        foreach (var (url, ms) in Pages(busiest))
            output.WriteLine((await host.MeasureAsync(c, url)).Within(ms, 40).ToString());
        if (PerfTarget.Drifted() == 0)          // nothing for the dashboard's self-heal to write
            output.WriteLine((await host.MeasureAsync(c, "/inventory/")).Within(800, 40).ToString());
        else
            output.WriteLine("dashboard not timed: products drifted from the ledger and the speed DB is read only");
    }

    [InvPerfFact]
    public async Task Exports_of_the_full_year_are_within_budget()
    {
        // The file itself is Finance's (IReportExport); this times what this module reads and builds for it.
        int busiest = PerfTarget.BusiestProduct();
        await using var host = await HostAsync();
        var c = host.ClientFor(PerfTarget.UserId, PerfTarget.CompanyId);
        foreach (string url in new[]
                 {
                     $"/inventory/reports/valuation?{Year}&format=excel",
                     $"/inventory/reports/stock-ledger?product_id={busiest}&format=excel",
                     $"/inventory/vouchers/product-ledger?product_id={busiest}&format=excel",
                     "/inventory/vouchers/product-ledger/list?format=excel", "/inventory/reports/low-stock?format=pdf",
                 })
            output.WriteLine((await host.MeasureAsync(c, url)).Within(3000, 40).ToString());
    }
}
