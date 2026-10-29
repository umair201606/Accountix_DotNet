using Accountix.Finance.Services;

namespace Accountix.Finance;

/// <summary>Financial reports and the report export pipeline every module uses.</summary>
public sealed class FinanceModule : IAccountixModule
{
    public string Key => Modules.Finance;

    public void Register(IServiceCollection services, IConfiguration config)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ReportExport>();
        services.AddScoped<IReportExport>(sp => sp.GetRequiredService<ReportExport>());
        services.AddScoped<FinanceData>();
        services.AddScoped<FinanceStatements>();
        services.AddScoped<TwcfReports>();
    }

    /// <summary>NAV["finance"] (shared/navigation.py).</summary>
    public ModuleNav? Navigation => new()
    {
        Flat =
        [
            new("Dashboard", "/finance/", "&#9679;") { Right = ("financial_reports", Rights.View) },
            new("General Ledger", "/finance/ledger", "&#128212;") { Right = ("financial_reports", Rights.View) },
            new("Trial Balance", "/finance/trial-balance", "&#9878;") { Right = ("financial_reports", Rights.View) },
            new("P&L Statement", "/finance/profit-loss", "&#128200;") { Right = ("financial_reports", Rights.View) },
            new("P&L by Label", "/finance/label-pl", "&#127991;") { Right = ("financial_reports", Rights.View) },
            new("Balance Sheet", "/finance/balance-sheet", "&#128203;") { Right = ("financial_reports", Rights.View) },
            new("SOCIE", "/finance/socie", "&#128218;") { Right = ("financial_reports", Rights.View) },
            new("Cash Flow", "/finance/cash-flow", "&#128181;") { Right = ("financial_reports", Rights.View) },
            new("13-Week Cash Flow", "/finance/twcf", "&#128179;") { Right = ("financial_reports", Rights.View) },
        ],
    };
}
