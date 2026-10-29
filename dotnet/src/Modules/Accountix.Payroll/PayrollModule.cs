using Accountix.Payroll.Services;

namespace Accountix.Payroll;

/// <summary>
/// Payroll, provident fund and staff loans (hr_app/routes/compensation.py, pf.py,
/// payroll_engine.py, payroll_posting.py). It shares the "hr" key with the HR
/// project: the Financial group is ours, and our Administration items carry
/// HR's Order numbers (PF Config 80, Tax &amp; Payroll Settings 90, Staff Loans 100).
/// See dotnet/docs/modules/payroll.md.
/// </summary>
public sealed class PayrollModule : IAccountixModule
{
    public string Key => Modules.Hr;

    public void Register(IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<PayrollPosting>();
        services.AddScoped<PayrollRunner>();
        services.AddScoped<PayrollExports>();
        services.AddScoped<PayrollPeople>();
    }

    private static bool Admin(EffectiveAccess a, HttpContext _) => a.IsAdmin;

    public ModuleNav? Navigation => new()
    {
        Groups =
        [
            new("Financial", "&#128188;",
            [
                new("Compensation", "/compensation/", "&#128176;")
                {
                    ActivePrefixes = ["/compensation"], ExcludePrefixes = ["/compensation/tax-settings", "/compensation/loans"],
                    Order = 10,
                },
                new("Provident Fund", "/pf/", "&#128179;")
                {
                    ActivePrefixes = ["/pf"], ExcludePrefixes = ["/pf/config", "/pf/button-permissions"], Order = 20,
                },
            ]),
        ],
        GroupsAfter =
        [
            new("Administration", "&#128295;",
            [
                new("PF Config", "/pf/config", "&#9881;") { Gate = Admin, Order = 80 },
                new("Tax & Payroll Settings", "/compensation/tax-settings", "&#9881;") { Gate = Admin, Order = 90 },
                new("Staff Loans", "/compensation/loans", "&#128179;") { Gate = Admin, Order = 100 },
            ]),
        ],
    };
}
