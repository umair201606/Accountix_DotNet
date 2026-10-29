using Accountix.Fbr.Services;

namespace Accountix.Fbr;

/// <summary>
/// FBR Digital Invoicing (fbr_app): submissions dashboard, Send / Validate /
/// Retry, settings per company (see docs/modules/fbr.md).
/// </summary>
public sealed class FbrModule : IAccountixModule
{
    public string Key => Modules.Fbr;

    public void Register(IServiceCollection services, IConfiguration config)
    {
        // FBR's endpoints through IHttpClientFactory; tests replace the handler.
        services.AddHttpClient(FbrClient.HttpClientName, c => c.Timeout = FbrClient.Timeout);
        services.AddScoped<FbrClient>();
        services.AddScoped<FbrConfigs>();
        services.AddScoped<FbrMapper>();
    }

    // shared/navigation.py NAV["fbr"]: both endpoints fall back to the "doc" icon (icons.html).
    public ModuleNav? Navigation => new()
    {
        Flat =
        [
            new("Dashboard", "/fbr/", "&#9679;") { Right = ("fbr_dashboard", Rights.View) },
            new("Settings", "/fbr/settings/", "&#9881;")
            {
                ActivePrefixes = ["/fbr/settings"],
                Gate = (a, _) => a.IsAdmin,
                Right = ("fbr_settings", Rights.View),
            },
        ],
    };
}
