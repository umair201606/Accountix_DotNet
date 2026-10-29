using Accountix.Admin.Commands;

namespace Accountix.Admin;

/// <summary>
/// The admin commands (docs/DEPLOY.md, "Maintenance commands"). A module only so the host finds
/// it like every other: no pages, no sidebar.
/// </summary>
public sealed class AdminModule : IAccountixModule
{
    public string Key => "admin";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<IAdminCommand, MergeLegacyAccounts>();
        services.AddSingleton<IAdminCommand, RepairOpeningStock>();
        services.AddSingleton<IAdminCommand, RemoveFaultyDemoRecords>();
        services.AddSingleton<IAdminCommand, RebuildGlSummary>();
        services.AddSingleton<IAdminCommand, AddDefaultLeaveTypes>();
        services.AddSingleton<IAdminCommand, BooksIntegrityReport>();
    }

    public ModuleNav? Navigation => null;
}
