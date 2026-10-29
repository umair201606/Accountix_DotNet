using Accountix.SuperAdmin.Services;

namespace Accountix.SuperAdmin;

/// <summary>
/// The super admin console (superadmin_app): its own door, users, companies,
/// module entitlements, member blocking, the super admin's own books, global
/// limits, access requests and the platform-wide audit trail. It renders in
/// its own layout (superadmin/base.html), not the module app shell, and has
/// no sidebar. See docs/modules/superadmin.md.
/// </summary>
public sealed class SuperAdminModule : IAccountixModule
{
    public string Key => "superadmin";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        // Sign-in, the throttle, the audit trail, provisioning and memberships
        // are Platform's public services. In the full host PlatformModule has
        // registered them already (modules register in name order, Platform
        // first); booted on its own (this module's tests) the console
        // registers them here, once, so its writes are audited and its
        // sessions recorded exactly as in the host.
        if (!services.Any(d => d.ServiceType == typeof(Accountix.Platform.Services.SignInService)))
            new Accountix.Platform.PlatformModule().Register(services, config);

        services.AddScoped<ConsoleQueries>();
        services.AddScoped<ConsoleAccounts>();
    }

    public ModuleNav? Navigation => null;
}
