using Accountix.Inventory.Services;

namespace Accountix.Inventory.Controllers;

/// <summary>The inventory dashboard (inventory_app/routes/auth.py dashboard).</summary>
[Area("Inventory")]
[Module(Modules.Inventory)]
[Route("inventory")]
public sealed class DashboardController(AccountixDbContext db) : Controller
{
    /// <summary>_landing: anyone who may view one of the inventory sections.</summary>
    [HttpGet("")]
    [HttpGet("dashboard")]
    [AnyRight("products:view", "categories:view", "stock:view", "consumption_vouchers:view", "scrap_vouchers:view",
              "adjustment_vouchers:view", "stock_take_vouchers:view", "inventory_reports:view")]
    public async Task<IActionResult> Index(CancellationToken ct) =>
        View(await new InventoryReports(db).DashboardAsync(ct));
}
