using Accountix.Invoicing.Services;
using Accountix.Orders.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Accountix.Orders;

/// <summary>
/// Sales orders and purchase orders (invoicing_app/routes/sales.py and
/// purchases.py). Part of the Invoicing module: its two sidebar links merge
/// into Invoicing's "Procurement" and "Sales" groups (see docs/modules/orders.md).
/// </summary>
public sealed class OrdersModule : IAccountixModule
{
    public string Key => Modules.Invoicing;

    public void Register(IServiceCollection services, IConfiguration config)
    {
        // Invoicing's shared document helpers; TryAdd, so whichever of the two
        // modules registers them first wins and nothing is registered twice.
        services.TryAddScoped<EditVersions>();
        services.TryAddScoped<OrderLinkage>();
        services.TryAddScoped<DocSettings>();
        services.AddScoped<OrderReads>();
        services.AddScoped<SalesOrderBook>();
        services.AddScoped<PurchaseOrderBook>();
    }

    // shared/navigation.py NAV["invoicing"]: Procurement = Suppliers, Purchase Orders,
    // Purchase Invoice, Purchase Return; Sales = Customers, Sales Orders, Sales Invoice,
    // Sales Return. Orders are the second item of each group (Order 20 of 10/20/30/40).
    public ModuleNav? Navigation => new()
    {
        Groups =
        [
            new("Procurement", "&#128230;",
            [
                new("Purchase Orders", "/inventory/purchases/list")
                {
                    Svg = "cart", Order = 20,
                    // Python: "prefix": "inv_purchases" (endpoint names).
                    ActivePrefixes = ["/inventory/purchases"],
                    Gate = (_, http) => OrderFlows.For(http).PurchaseFlow == "with_po",
                    Right = ("purchase_orders", Rights.View),
                },
            ]),
            new("Sales", "&#128176;",
            [
                new("Sales Orders", "/inventory/sales/list")
                {
                    Svg = "calendar", Order = 20,
                    // Python matches the endpoint prefix "inv_sales", which also takes in
                    // inv_sales_return.* (served at /invoicing/sales-return): kept as is.
                    ActivePrefixes = ["/inventory/sales", "/invoicing/sales-return"],
                    Gate = (_, http) => OrderFlows.For(http).SalesFlow == "with_so",
                    Right = ("sales_orders", Rights.View),
                },
            ]),
        ],
    };
}
