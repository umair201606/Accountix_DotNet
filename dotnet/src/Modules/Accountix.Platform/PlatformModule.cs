using Accountix.Platform.Audit;
using Accountix.Platform.Provisioning;
using Accountix.Platform.Services;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Accountix.Platform;

/// <summary>
/// The Platform module: landing, sign-up, sign-in / sign-out, portal, hub,
/// company switching, Settings, the super admin console and the audit trail
/// (IAuditTrail + the automatic audit interceptor every module relies on).
/// See dotnet/docs/modules/platform.md.
/// </summary>
public sealed class PlatformModule : IAccountixModule
{
    public string Key => "platform";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        services.AddHttpContextAccessor();
        // "/dashboard/" is the hub, "/dashboard" HR's dashboard: told apart before routing.
        services.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, Controllers.HubPath>();
        // The audit trail follows every save of every context (shared/audit.py's session hook).
        services.AddSingleton<IInterceptor, AuditInterceptor>();
        services.AddScoped<AuditTrail>();
        services.AddScoped<IAuditTrail>(sp => sp.GetRequiredService<AuditTrail>());

        services.AddScoped<AccountSecurity>();
        services.AddScoped<SignInService>();
        services.AddScoped<Memberships>();
        services.AddScoped<CompanySetup>();
        services.AddScoped<FirstUse>();
        // Sign-in itself (the ax_session cookie, SessionAuthHandler) is registered by AccountixApp for every host.
    }

    public ModuleNav? Navigation => null;
}

/// <summary>Settings' sidebar (shared/navigation.py NAV["settings"]): one route, one item per ?tab=.</summary>
public sealed class SettingsModule : IAccountixModule
{
    public string Key => "settings";

    public void Register(IServiceCollection services, IConfiguration config) { }

    private static bool Finance(EffectiveAccess a, HttpContext _) => a.CanOpenModule(Modules.Finance);
    private static bool Inventory(EffectiveAccess a, HttpContext _) => a.CanOpenModule(Modules.Inventory);
    private static bool Invoicing(EffectiveAccess a, HttpContext _) => a.CanOpenModule(Modules.Invoicing);
    private static bool Admin(EffectiveAccess a, HttpContext _) => a.IsAdmin;

    private static NavItem Tab(string label, string tab, string icon, Func<EffectiveAccess, HttpContext, bool>? gate = null) =>
        new(label, "/settings/", icon) { Tab = tab, Gate = gate, ActivePrefixes = ["/settings"] };

    public ModuleNav? Navigation => new()
    {
        Flat =
        [
            Tab("My Account", "account", "&#128100;"),
            Tab("Invitations", "invites", "&#9993;"),
        ],
        Groups =
        [
            new("Company & Finance", "&#127970;",
            [
                Tab("Company Profile", "company", "&#127970;", Finance),
                Tab("Financial Periods", "periods", "&#128197;", Finance),
                Tab("Report Structure", "reports", "&#128200;", Finance),
                Tab("Cash Flow Method", "cash_flow", "&#128181;", Finance),
                Tab("Project Labels", "labels", "&#127991;", Finance),
            ]),
            new("Inventory", "&#128230;", [Tab("Inventory", "inventory", "&#128230;", Inventory)]),
            new("Invoicing", "&#128207;",
            [
                Tab("Procurement", "purchase", "&#128228;", Invoicing),
                Tab("Sales", "sales", "&#128229;", Invoicing),
                Tab("Invoice Defaults", "invoicing", "&#128207;", Invoicing),
                Tab("Invoice Templates", "templates", "&#128196;", Invoicing),
            ]),
            new("Administration", "&#128295;",
            [
                Tab("Rights & Access", "rights", "&#128273;", Admin),
                Tab("Members & Roles", "members", "&#128101;", Admin),
                Tab("Audit Log", "audit", "&#128220;", Admin),
            ]),
        ],
    };
}
