using Accountix.Executive.Services;

namespace Accountix.Executive;

/// <summary>
/// Executive Reports (/executive): the receivable / payable registers, FIFO aging, liquidity and
/// profitability on the dashboard, the party ledger, and Books Integrity (see docs/modules/executive.md).
/// </summary>
public sealed class ExecutiveModule : IAccountixModule
{
    public string Key => Modules.Executive;

    public void Register(IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<ExecutiveReports>();
        services.AddScoped<BooksIntegrity>();
    }

    /// <summary>NAV["executive"] (shared/navigation.py): a read of the ledger, so no create/post actions.</summary>
    public ModuleNav? Navigation => new()
    {
        Flat =
        [
            new("Dashboard", "/executive/", "&#9679;") { Right = ("executive_reports", Rights.View) },
            new("Receivables", "/executive/receivables", "&#128178;")
            {
                ActivePrefixes = ["/executive/receivables", "/executive/party/"], Right = ("executive_reports", Rights.View),
            },
            new("Payables", "/executive/payables", "&#128230;") { Right = ("executive_reports", Rights.View) },
            new("Books Integrity", "/executive/integrity", "&#10003;") { Right = ("executive_reports", Rights.View) },
            new("Settings", "/executive/settings", "&#9881;")
            {
                Gate = (a, _) => a.IsAdmin || a.IsManager, Right = ("executive_reports", Rights.View),
            },
        ],
    };
}
