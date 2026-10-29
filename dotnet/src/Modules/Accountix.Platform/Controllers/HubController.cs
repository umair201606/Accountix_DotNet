namespace Accountix.Platform.Controllers;

/// <summary>shared/routes/dashboard.py: the hub, a company's front door (one tile per module the person may open).</summary>
[Area("Platform")]
[SignedIn]
public sealed partial class HubController : Controller
{
    /// <summary>The hub's internal route: Python's dashboard.hub is "/dashboard/", HR's dashboard is "/dashboard".</summary>
    public const string HubRoute = "/_platform/hub";

    public sealed record Tile(string Key, string Title, string Url, string Desc, string Icon);

    public sealed record HubModel(string? CompanyName, string UserName, string? RoleName, IReadOnlyList<Tile> Modules, bool HasSettings);

    /// <summary>Every tile is data, in Python's order.</summary>
    private static readonly Tile[] All =
    [
        new(Modules.Hr, "People & HR", "/dashboard", "Employees, attendance, leave, payroll, PF & more", "people"),
        new(Modules.Inventory, "Inventory", "/inventory/dashboard", "Products, stock movements, vouchers & reports", "box"),
        new(Modules.Invoicing, "Invoicing", "/invoicing/", "Customers, suppliers, invoices, returns & tracking", "receipt"),
        new(Modules.Finance, "Finance", "/finance/", "Ledger, trial balance, P&L, balance sheet, cash flow", "chart"),
        new(Modules.Accounting, "Accounting", "/accounting/dashboard", "Vouchers, chart of accounts & period books", "calc"),
        new(Modules.Fbr, "FBR Digital Invoicing", "/fbr/", "DI submissions, validation & compliance", "shield"),
        new(Modules.FixedAssets, "Fixed Assets", "/fixed-assets/dashboard", "Assets, categories, depreciation & reports", "building"),
        new(Modules.Executive, "Executive Reports", "/executive/", "Receivables & payables, live from the ledger", "pulse"),
    ];

    [HttpGet(HubRoute)]   // served at /dashboard/ (HubPath rewrites it; ASP.NET routing would mix it up with HR's /dashboard)
    public IActionResult Index()
    {
        var a = HttpContext.Access()!;
        // With no company active there is nothing to show: pick one first.
        if (a.CompanyId is null)
            return Redirect("/portal/");
        var visible = All.Where(t => a.CanOpenModule(t.Key)).ToList();
        if (visible.Count == 0)
            return View("AccessDenied");
        return View("Index", new HubModel(a.Company!.Name, a.User.FullName, a.RoleName, visible, a.IsAdmin));
    }
}
