using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.FixedAssets.Tests;

/// <summary>
/// The fixed-asset pages on 300 assets with a year of monthly depreciation each
/// (company A of this module's copy): a fixed number of queries whatever the
/// size (accumulated depreciation is aggregated in SQL, never per asset), within
/// the budgets. (The Python app's times on the same pages, measured on 29 Oct before it
/// was retired, are kept in docs/modules/fixed_assets.md.)
/// </summary>
public class SpeedTests
{
    private const int Assets = 300;
    private static int A => Fa.CompanyA;

    private static readonly string[] Pages =
    [
        "/fixed-assets/", "/fixed-assets/assets/", "/fixed-assets/depreciation/?as_of=2026-01-31", "/fixed-assets/reports/",
        "/fixed-assets/reports/movement?from=2025-01-01&to=2025-12-31", "/fixed-assets/transfers/", "/inventory/transfer-to-fa/",
    ];

    /// <summary>300 assets bought through 2024, charged monthly through 2025 (rows from before the journal link count as live).</summary>
    internal static async Task SeedAsync()
    {
        await using var db = Fa.Db(A);
        if (await db.Set<FixedAsset>().CountAsync(a => a.AssetCode.StartsWith("PERF-")) >= Assets)
            return;
        var cats = await db.Set<FaCategory>().OrderBy(c => c.Id).Select(c => c.Id).ToListAsync();
        var rnd = new Random(300);
        var now = DateTime.UtcNow;
        var assets = new List<FixedAsset>();
        for (int i = 1; i <= Assets; i++)
        {
            decimal cost = Math.Round((decimal)(rnd.NextDouble() * 900_000 + 50_000), 2);
            assets.Add(new FixedAsset
            {
                AssetCode = $"PERF-{i:D4}", Name = $"Perf Asset {i:D3}", Description = "", CategoryId = cats[i % cats.Count],
                PurchaseDate = new DateOnly(2024, 1 + i % 12, 1 + i % 27), PurchaseCost = cost, UsefulLife = 3 + i % 8,
                DepreciationMethod = i % 3 == 0 ? "declining_balance" : "straight_line", SalvageValue = 0m,
                AccumulatedDepreciation = 0m, CurrentBookValue = cost, Status = "active", Location = "", AssignedTo = "", Vendor = "",
                SerialNumber = "", Notes = "", IsActive = true, CreatedAt = now, UpdatedAt = now,
            });
        }
        db.AddRange(assets);
        await db.SaveChangesAsync();
        foreach (var a in assets)
        {
            decimal monthly = Math.Round(a.PurchaseCost / (a.UsefulLife * 12), 2);
            for (int m = 1; m <= 12; m++)
                db.Add(new AssetDepreciation
                {
                    AssetId = a.Id, EntryDate = new DateOnly(2025, m, 1).AddMonths(1).AddDays(-1), Amount = monthly,
                    AccumulatedAfter = monthly * m, NetBookValueAfter = a.PurchaseCost - monthly * m, Notes = "seed", CreatedAt = now,
                });
            a.AccumulatedDepreciation = monthly * 12;
            a.CurrentBookValue = a.PurchaseCost - monthly * 12;
        }
        await db.SaveChangesAsync();
    }

    [ReferenceFact]
    public async Task Pages_on_three_hundred_assets_run_a_fixed_number_of_queries()
    {
        await SeedAsync();
        await using var host = await Fa.HostAsync();
        var c = host.ClientFor(Fa.OwnerA, A);
        int view;
        await using (var db = Fa.Db(A))
            view = await db.Set<FixedAsset>().Where(a => a.AssetCode == "PERF-0007").Select(a => a.Id).SingleAsync();
        foreach (string url in Pages.Append($"/fixed-assets/assets/{view}"))
        {
            var t = (await host.MeasureAsync(c, url)).Within(maxMilliseconds: 1500, maxQueries: 40);
            Assert.True(t.Repeated.All(r => !r.StartsWith("300×") && !r.StartsWith("301×")), t.ToString());
        }
        // The register and the run list every asset: still a handful of queries (7 of them are the sign-in's).
        (await host.MeasureAsync(c, "/fixed-assets/reports/")).Within(1500, 10);
        (await host.MeasureAsync(c, "/fixed-assets/depreciation/?as_of=2026-01-31")).Within(1500, 10);
        (await host.MeasureAsync(c, "/fixed-assets/reports/movement?from=2025-01-01&to=2025-12-31")).Within(1500, 15);
        (await host.MeasureAsync(c, "/fixed-assets/")).Within(800, 12);
        string page = (await host.MeasureAsync(c, "/fixed-assets/depreciation/?as_of=2026-01-31")).Body;
        Assert.Contains("Perf Asset 300", page);
    }

    // ── Samba, the speed-test database (read only; it carries no fixed assets) ──

    [PerfFact]
    public async Task Fixed_asset_pages_are_quick_on_the_speed_test_database()
    {
        await using var host = await ModuleHost.StartOnAsync(Perf.ConnectionString, typeof(FixedAssetsModule).Assembly,
            typeof(Accounting.AccountingModule).Assembly, typeof(TestFakesModule).Assembly);
        var c = host.ClientFor(Perf.AdminUserId, Perf.CompanyId);
        foreach (string url in Pages.Append($"/fixed-assets/reports/movement?from={Perf.YearStart:yyyy-MM-dd}&to={Perf.YearEnd:yyyy-MM-dd}"))
            (await host.MeasureAsync(c, url)).Within(800, 40);
    }
}
