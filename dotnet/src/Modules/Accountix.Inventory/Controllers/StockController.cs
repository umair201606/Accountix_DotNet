using Accountix.Inventory.Services;

namespace Accountix.Inventory.Controllers;

/// <summary>Stock on hand and stock movements (inventory_app/routes/stock.py).</summary>
[Area("Inventory")]
[Module(Modules.Inventory)]
[Route("inventory/stock")]
public sealed class StockController(AccountixDbContext db) : Controller
{
    public sealed record ListModel(IReadOnlyList<InventoryReports.StockRow> Products, string Q, bool LowOnly);

    public sealed record MovementsModel(IReadOnlyList<InventoryReports.MovementRow> Movements,
                                        IReadOnlyList<InventoryReports.ProductOption> Products, string? Selected);

    [HttpGet("")]
    [Right("stock", Rights.View)]
    public async Task<IActionResult> List([FromQuery] string? q, [FromQuery(Name = "low_only")] string? lowOnly, CancellationToken ct)
    {
        bool low = lowOnly == "1";
        return View(new ListModel(await new InventoryReports(db).StockListAsync(q, low, ct), q ?? "", low));
    }

    [HttpGet("movements")]
    [Right("stock", Rights.View)]
    public async Task<IActionResult> Movements(CancellationToken ct)
    {
        string? raw = Request.Query["product_id"].FirstOrDefault();
        int? productId = int.TryParse(raw, out int p) ? p : null;
        var reports = new InventoryReports(db);
        return View(new MovementsModel(await reports.MovementsAsync(productId, ct),
                                       await reports.ActiveProductsAsync(byName: false, ct), raw));
    }
}
