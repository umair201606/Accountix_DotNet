using Accountix.HrRecords.Services;

namespace Accountix.HrRecords;

/// <summary>
/// HR records (see docs/modules/hr_records.md): notifications and the bell,
/// employee documents, the workplace pages and the HR report builder. Its
/// sidebar links merge into HR's (key "hr"), placed by Order in Python's
/// sequence (see HrModule.Navigation for the numbering every HR project uses).
/// </summary>
public sealed class HrRecordsModule : IAccountixModule
{
    public string Key => Modules.Hr;

    public void Register(IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<Staff>();
        services.AddScoped<FileStore>();
        services.AddScoped<Communications>();
        services.AddScoped<HrReport>();
    }

    private static bool Admin(EffectiveAccess a, HttpContext _) => a.IsAdmin;
    private static bool AdminOrManager(EffectiveAccess a, HttpContext _) => a.IsAdmin || a.IsManager;

    // NAV["hr"] in shared/navigation.py, this project's entries.
    public ModuleNav? Navigation => new()
    {
        Groups =
        [
            new("Self Service", "&#128736;",
            [
                // prefix digital_files., excluding digital_files.admin
                new("My Files", "/digital-files/", "&#128193;")
                {
                    ActivePrefixes = ["/digital-files"], ExcludePrefixes = ["/digital-files/admin"], Order = 50,
                },
            ]),
        ],
        FlatAfter =
        [
            new("Workplace", "/workplace/", "&#128187;") { ActivePrefixes = ["/workplace"] },
        ],
        GroupsAfter =
        [
            new("Management", "&#128202;",
            [
                new("Reports", "/reports/", "&#128200;") { ActivePrefixes = ["/reports"], Gate = AdminOrManager, Order = 30 },
            ]),
            new("Administration", "&#128295;",
            [
                new("Notifications", "/communications/", "&#128231;") { Gate = Admin, Order = 10 },
                new("All Files", "/digital-files/admin", "&#128193;") { Gate = Admin, Order = 70 },
            ]),
        ],
    };
}
