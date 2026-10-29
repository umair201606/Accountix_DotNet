using Accountix.Invoicing.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Accountix.Returns;

/// <summary>
/// Sales returns (credit notes, invoicing_app/routes/sales_return.py) and
/// purchase returns (debit notes, invoicing_app/routes/purchase_return.py).
/// Part of the Invoicing module: its sidebar items merge into NAV["invoicing"].
/// See docs/modules/returns.md.
/// </summary>
public sealed class ReturnsModule : IAccountixModule
{
    public string Key => Modules.Invoicing;

    public void Register(IServiceCollection services, IConfiguration config)
    {
        // Invoicing's shared helpers (InvoicingModule registers them too; TryAdd keeps one).
        services.TryAddScoped<EditVersions>();
        services.TryAddScoped<TrackingSync>();
        services.TryAddScoped<OrderLinkage>();
    }

    /// <summary>
    /// shared/navigation.py NAV["invoicing"]: Purchase Return is the last item of
    /// "Procurement", Sales Return the last of "Sales" (Order 40 sorts them after
    /// the suppliers / orders / invoices other projects put in those groups).
    /// </summary>
    public ModuleNav? Navigation => new()
    {
        Groups =
        [
            new("Procurement", "&#128230;",
            [
                new("Purchase Return", "/inventory/purchase-return/list", "&#9632;")
                {
                    ActivePrefixes = ["/inventory/purchase-return"], Svg = "return",
                    Right = ("purchase_returns", Rights.View), Order = 40,
                },
            ]),
            new("Sales", "&#128176;",
            [
                new("Sales Return", "/invoicing/sales-return/list", "&#9632;")
                {
                    ActivePrefixes = ["/invoicing/sales-return"], Svg = "return",
                    Right = ("sales_returns", Rights.View), Order = 40,
                },
            ]),
        ],
    };
}
