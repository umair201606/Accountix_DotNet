using Accountix.TestSupport;
using Xunit.Abstractions;

namespace Accountix.SuperAdmin.Tests;

/// <summary>
/// The console on the speed-test database (a full year of Samba, 46k audit
/// rows; read-only). Budgets: docs/SPEED_AND_CONCURRENCY.md (a list: ≤ 800 ms,
/// ≤ 40 queries). Python's times for the same pages (measured on 29 Oct 2026, before
/// the Python app was retired) stay in docs/modules/superadmin.md as history.
/// </summary>
public class SpeedTests(ITestOutputHelper output)
{
    public static readonly string[] AuditPages =
    [
        "/superadmin/audit/",
        "/superadmin/audit/?page=200",
        "/superadmin/audit/?q=INV",
        "/superadmin/audit/?company_id=platform",
        "/superadmin/audit/?company_id=2&action=create&page=40",
        "/superadmin/audit/?action=login_failed&q=admin",
    ];

    public static readonly string[] OtherPages =
    [
        "/superadmin/", "/superadmin/users/", "/superadmin/companies/", "/superadmin/companies/2/",
        "/superadmin/my-companies/", "/superadmin/requests/", "/superadmin/users/1/",
    ];

    [PerfFact]
    public async Task The_audit_page_is_quick_on_a_full_year()
    {
        await using var host = await ModuleHost.StartOnAsync(Perf.ConnectionString, typeof(SuperAdminModule).Assembly);
        var c = host.ClientFor(Perf.AdminUserId);
        foreach (string url in AuditPages)
            output.WriteLine((await host.MeasureAsync(c, url)).Within(800, 40).ToString());
    }

    [PerfFact]
    public async Task Every_other_console_page_is_quick_on_a_full_year()
    {
        await using var host = await ModuleHost.StartOnAsync(Perf.ConnectionString, typeof(SuperAdminModule).Assembly);
        var c = host.ClientFor(Perf.AdminUserId);
        foreach (string url in OtherPages)
            output.WriteLine((await host.MeasureAsync(c, url)).Within(800, 40).ToString());
    }
}
