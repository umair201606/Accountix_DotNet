using Accountix.Accounting.Services;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Accountix.Accounting;

/// <summary>
/// The Accounting module: vouchers, chart of accounts, bank reconciliation,
/// and the general-ledger services every other module posts through.
/// </summary>
public sealed class AccountingModule : IAccountixModule
{
    public string Key => Modules.Accounting;

    public void Register(IServiceCollection services, IConfiguration config)
    {
        // gl_daily_balances follows every save of every context, in its transaction.
        services.AddSingleton<IInterceptor, GlSummaryInterceptor>();
        services.AddScoped<Ledger>();
        services.AddScoped<ILedger>(sp => sp.GetRequiredService<Ledger>());
        services.AddScoped<IVoucherNumbers, VoucherNumbers>();
        services.AddScoped<IPeriods, Periods>();
        services.AddScoped<GlEntries>();
    }

    public ModuleNav? Navigation => new()
    {
        Flat =
        [
            new("Dashboard", "/accounting/", "&#9679;") { Svg = "grid", ActivePrefixes = ["/accounting/dashboard"] },
            new("Create Voucher", "/accounting/vouchers", "&#10133;") { Svg = "doc" },
            new("View Vouchers", "/accounting/vouchers/list", "&#128196;") { Svg = "doc" },
            new("Voucher Register", "/accounting/registers/vouchers", "&#128212;") { ActivePrefixes = ["/accounting/registers/vouchers"] },
            new("Bank Reconciliation", "/accounting/bank-reconciliation", "&#127974;") { ActivePrefixes = ["/accounting/bank-reconciliation"] },
            new("Chart of Accounts (COA)", "/accounting/coa/", "&#128202;") { ActivePrefixes = ["/accounting/coa"] },
        ],
    };
}
