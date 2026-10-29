using Accountix.Hr.Services;

namespace Accountix.Hr;

/// <summary>
/// The HR module (hr_app): dashboard, attendance, leave, timesheets, self
/// service, my team and the HR user pages. See dotnet/docs/modules/hr.md.
/// HrRecords (messages, files, workplace, reports) and Payroll add their
/// links under the same key (see the Order scheme on Navigation).
/// </summary>
public sealed class HrModule : IAccountixModule
{
    public string Key => Modules.Hr;

    public void Register(IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<People>();
        services.AddScoped<LeaveFlow>();
        services.AddScoped<AttendanceRules>();
        services.AddScoped<FileStore>();
        services.AddScoped<AccessDefaults>();
        services.AddScoped<OwnSession>();
        services.AddScoped<Notify>();
    }

    private static bool AdminOrManager(EffectiveAccess a, HttpContext _) => a.IsAdmin || a.IsManager;
    private static bool Admin(EffectiveAccess a, HttpContext _) => a.IsAdmin;

    // Sidebar NAV["hr"] (shared/navigation.py), the pages this project serves. Three projects fill the
    // "hr" key: HrRecords (messages, files, workplace, reports) and Payroll add theirs; groups of one
    // name merge and sort by Order, so every project numbers its items in Python's sequence:
    //   Self Service:   Attendance 10, Leaves 20, Timesheets 30, My Profile 40, My Files 50 (HrRecords)
    //   Workplace:      FlatAfter (HrRecords)
    //   Management:     My Team 10, Approvals 20, Reports 30 (HrRecords)
    //   Administration: Notifications 10 (HrRecords), All Attendance 20, Time Policies 30, Holidays 40,
    //                   Workflows 50, Projects 60, All Files 70 (HrRecords), PF Config 80, Tax & Payroll
    //                   Settings 90, Staff Loans 100 (Payroll), Users 110
    //   Financial:      Payroll's own group (Groups, after Self Service)
    public ModuleNav? Navigation => new()
    {
        Flat =
        [
            new("Dashboard", "/dashboard", "&#9679;"),
        ],
        Groups =
        [
            new("Self Service", "&#128736;",
            [
                new("Attendance", "/attendance/", "&#9200;")
                {
                    ActivePrefixes = ["/attendance"], ExcludePrefixes = ["/attendance/admin", "/attendance/policies"], Order = 10,
                },
                new("Leaves", "/leaves/", "&#128197;")
                {
                    ActivePrefixes = ["/leaves"], ExcludePrefixes = ["/leaves/holidays", "/leaves/workflows"], Order = 20,
                },
                new("Timesheets", "/timesheets/", "&#128203;")
                {
                    ActivePrefixes = ["/timesheets"], ExcludePrefixes = ["/timesheets/projects"], Order = 30,
                },
                new("My Profile", "/ess/", "&#128100;") { ActivePrefixes = ["/ess"], Order = 40 },
            ]),
        ],
        GroupsAfter =
        [
            new("Management", "&#128202;",
            [
                // exact: mss.index, mss.team, mss.evaluate, mss.team_calendar, mss.team_availability
                new("My Team", "/mss/", "&#128101;")
                {
                    ActivePrefixes = ["/mss/"], ExcludePrefixes = ["/mss/approvals", "/mss/approve-", "/mss/bulk-"],
                    Gate = AdminOrManager, Order = 10,
                },
                new("Approvals", "/mss/approvals", "&#9989;") { Gate = AdminOrManager, Order = 20 },
            ]),
            new("Administration", "&#128295;",
            [
                new("All Attendance", "/attendance/admin", "&#128197;") { Gate = Admin, Order = 20 },
                new("Time Policies", "/attendance/policies", "&#9881;") { Gate = Admin, Order = 30 },
                new("Holidays", "/leaves/holidays", "&#127775;") { Gate = Admin, Order = 40 },
                new("Workflows", "/leaves/workflows", "&#128295;") { Gate = Admin, Order = 50 },
                new("Projects", "/timesheets/projects", "&#128194;") { Gate = Admin, Order = 60 },
                new("Users", "/auth/users", "&#128101;")
                {
                    ActivePrefixes = ["/auth/users", "/auth/members/assign"], Gate = Admin, Order = 110,
                },
            ]),
        ],
    };
}
