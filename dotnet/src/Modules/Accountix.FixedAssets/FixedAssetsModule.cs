using Accountix.FixedAssets.Services;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Accountix.FixedAssets;

/// <summary>
/// The Fixed Assets module (fixed_assets_app): assets, depreciation, categories,
/// stock ↔ asset transfers, the register and the movement schedule; also the
/// Inventory → Fixed Assets transfer pages under /inventory/transfer-to-fa
/// (inventory_app/routes/transfers.py). See dotnet/docs/modules/fixed_assets.md.
/// </summary>
public sealed class FixedAssetsModule : IAccountixModule
{
    public string Key => Modules.FixedAssets;

    public void Register(IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<IInterceptor, UpdatedAtInterceptor>();
        services.AddScoped<AssetBooks>();
        services.AddScoped<Capitalisations>();
        services.AddScoped<Movement>();
    }

    private static readonly (string, string) Assets = ("fixed_assets_assets", Rights.View);

    public ModuleNav? Navigation => new()
    {
        Flat =
        [
            // fa_auth.dashboard serves /fixed-assets/ and /fixed-assets/dashboard.
            new("Dashboard", "/fixed-assets/dashboard", "&#9679;")
            {
                ActivePrefixes = ["/fixed-assets"],
                ExcludePrefixes = ["/fixed-assets/assets", "/fixed-assets/transfers", "/fixed-assets/depreciation",
                                   "/fixed-assets/categories", "/fixed-assets/reports"],
                Right = ("fixed_assets_dashboard", Rights.View),
            },
        ],
        Groups =
        [
            new("Assets", "&#128230;",
            [
                new("All Assets", "/fixed-assets/assets/") { ActivePrefixes = ["/fixed-assets/assets"], Right = Assets },
                new("Transfers", "/fixed-assets/transfers/") { ActivePrefixes = ["/fixed-assets/transfers"], Right = Assets },
                new("Depreciation", "/fixed-assets/depreciation/") { ActivePrefixes = ["/fixed-assets/depreciation"], Right = Assets },
                new("Categories", "/fixed-assets/categories/")
                {
                    ActivePrefixes = ["/fixed-assets/categories"], Right = ("fixed_assets_categories", Rights.View),
                },
            ]),
            new("Reports", "&#128202;",
            [
                new("Asset Register", "/fixed-assets/reports/") { Right = ("fixed_assets_reports", Rights.View) },
                new("Movement Schedule", "/fixed-assets/reports/movement") { Right = ("fixed_assets_reports", Rights.View) },
            ]),
        ],
    };
}
