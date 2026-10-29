using Accountix.Inventory.Services;

namespace Accountix.Inventory;

/// <summary>
/// The Inventory module: products, units, categories, stock vouchers and
/// reports, and the costing engine (IStockCosting) every stock-moving document
/// goes through. See dotnet/docs/modules/inventory.md.
/// </summary>
public sealed class InventoryModule : IAccountixModule
{
    public string Key => Modules.Inventory;

    public void Register(IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<InventorySettingsStore>();
        services.AddScoped<StockCosting>();
        services.AddScoped<IStockCosting>(sp => sp.GetRequiredService<StockCosting>());
        services.AddScoped<StockEntry>();
    }

    private static readonly string[] Sections =
        ["products", "categories", "stock", "consumption_vouchers", "scrap_vouchers", "adjustment_vouchers",
         "stock_take_vouchers", "inventory_reports"];

    /// <summary>shared/navigation.py NAV["inventory"].</summary>
    public ModuleNav? Navigation => new()
    {
        Flat =
        [
            // _landing: open to anyone who may view any inventory section.
            new("Dashboard", "/inventory/", "&#9679;") { Gate = (a, _) => Sections.Any(s => a.Can(s, Rights.View)) },
        ],
        Groups =
        [
            new("Inventory", "&#128230;",
            [
                new("Products", "/inventory/products/") { ActivePrefixes = ["/inventory/products"], Right = ("products", Rights.View) },
                new("Categories", "/inventory/categories/") { ActivePrefixes = ["/inventory/categories"], Right = ("categories", Rights.View) },
                new("Units", "/inventory/units/") { ActivePrefixes = ["/inventory/units"], Right = ("products", Rights.View) },
                new("Stock Movements", "/inventory/stock/") { ActivePrefixes = ["/inventory/stock"], Right = ("stock", Rights.View) },
            ]),
            new("Vouchers", "&#128196;",
            [
                new("Consumption", "/inventory/vouchers/consumption/list")
                    { ActivePrefixes = ["/inventory/vouchers/consumption"], Right = ("consumption_vouchers", Rights.View) },
                new("Scrap", "/inventory/vouchers/scrap/list")
                    { ActivePrefixes = ["/inventory/vouchers/scrap"], Right = ("scrap_vouchers", Rights.View) },
                new("Stock Adjustment", "/inventory/vouchers/adjustment/list")
                    { ActivePrefixes = ["/inventory/vouchers/adjustment"], Right = ("adjustment_vouchers", Rights.View) },
                new("Stock Taking", "/inventory/vouchers/stock-take/list")
                    { ActivePrefixes = ["/inventory/vouchers/stock-take"], Right = ("stock_take_vouchers", Rights.View) },
                // Owned by the Fixed Assets module (inventory_app/routes/transfers.py).
                new("Transfer to FA", "/inventory/transfer-to-fa/")
                    { ActivePrefixes = ["/inventory/transfer-to-fa"], Right = ("stock", Rights.View) },
            ]),
            new("Reports", "&#128202;",
            [
                new("Stock Ledger", "/inventory/reports/stock-ledger") { Right = ("inventory_reports", Rights.View) },
                new("Stock Valuation", "/inventory/reports/valuation") { Right = ("inventory_reports", Rights.View) },
                new("Low Stock Alert", "/inventory/reports/low-stock") { Right = ("inventory_reports", Rights.View) },
            ]),
        ],
    };
}
