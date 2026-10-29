using Accountix.FixedAssets.Services;

namespace Accountix.FixedAssets.Controllers;

/// <summary>Fixed assets dashboard (fixed_assets_app/routes/auth.py).</summary>
[Area("FixedAssets")]
[Module(Modules.FixedAssets)]
[Route("fixed-assets")]
public sealed class DashboardController(AccountixDbContext db, AssetBooks books) : Controller
{
    [HttpGet("")]
    [HttpGet("dashboard")]
    [Right("fixed_assets_dashboard", Rights.View)]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        // Counts by status, money from the assets in service, depreciation
        // derived from the posted charges (one aggregate query).
        var active = db.Set<FixedAsset>().Where(a => a.Status == "active");
        var inService = await active.AsNoTracking().OrderBy(a => a.Id).ToListAsync(ct);
        var live = await books.LiveByAssetAsync(active.Select(a => a.Id), ct);
        double totalCost = Py.Sum(inService.Select(a => Py.F(a.PurchaseCost)));
        double totalDep = Py.Sum(inService.Select(a => new AssetCalc(a, live.GetValueOrDefault(a.Id, LiveCharges.None)).PostedDepreciation));
        var counts = await db.Set<FixedAsset>().GroupBy(a => a.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        var model = new FaDashboardModel
        {
            TotalAssets = inService.Count,
            TotalCost = totalCost,
            TotalDepreciation = totalDep,
            NetBookValue = totalCost - totalDep,
            ActiveCount = counts.FirstOrDefault(c => c.Key == "active")?.N ?? 0,
            DisposedCount = counts.FirstOrDefault(c => c.Key == "disposed")?.N ?? 0,
            Categories = await db.Set<FaCategory>().CountAsync(c => c.IsActive == true, ct),
            Recent = await db.Set<FixedAsset>().AsNoTracking().Include(a => a.Category)
                .Where(a => a.Status != null && a.Status != "draft" && a.Status != "cancelled")
                .OrderByDescending(a => a.CreatedAt).Take(5).ToListAsync(ct),
        };
        return View(model);
    }
}

public sealed class FaDashboardModel
{
    public int TotalAssets { get; init; }
    public double TotalCost { get; init; }
    public double TotalDepreciation { get; init; }
    public double NetBookValue { get; init; }
    public int ActiveCount { get; init; }
    public int DisposedCount { get; init; }
    public int Categories { get; init; }
    public IReadOnlyList<FixedAsset> Recent { get; init; } = [];
}
