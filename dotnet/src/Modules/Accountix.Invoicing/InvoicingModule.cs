using Accountix.Invoicing.Services;

namespace Accountix.Invoicing;

/// <summary>
/// The Invoicing module: sales and purchase invoices, the invoicing dashboard,
/// and the shared invoice logic orders, returns and registers call
/// (totals, checks, order linkage, edit versions, print). See
/// dotnet/docs/modules/invoicing.md.
/// </summary>
public sealed class InvoicingModule : IAccountixModule
{
    public string Key => Modules.Invoicing;

    public void Register(IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<DocSettings>();
        services.AddScoped<OrderLinkage>();
        services.AddScoped<EditVersions>();
        services.AddScoped<InvoicePrint>();
        services.AddScoped<TrackingSync>();
        services.AddScoped<InvoiceLookups>();
        services.AddScoped<PerformanceService>();
    }

    private static readonly object FlowKey = new();

    /// <summary>The purchase / sales flow setting (read once per request for the sidebar labels).</summary>
    private static string Flow(HttpContext http, string key)
    {
        if (http.Items[FlowKey] is not ValueTuple<string, string> flows)
        {
            var db = http.RequestServices.GetRequiredService<AccountixDbContext>();
            var s = db.Set<InventorySetting>().AsNoTracking().OrderBy(x => x.Id)
                .Select(x => new { x.PurchaseFlow, x.SalesFlow }).FirstOrDefault();
            flows = (s?.PurchaseFlow ?? "with_po", s?.SalesFlow ?? "with_so");
            http.Items[FlowKey] = flows;
        }
        return key == "purchase" ? flows.Item1 : flows.Item2;
    }

    /// <summary>
    /// NAV["invoicing"] (shared/navigation.py): Dashboard, then the invoice items of Procurement and
    /// Sales. Suppliers / Customers / Invoice Tracking (Parties), orders (Orders) and returns (Returns)
    /// come from their own modules under the same key; shared/navigation.py's order within a group is
    /// Suppliers, Purchase Orders, Purchase Invoice, Purchase Return / Customers, Sales Orders,
    /// Sales Invoice, Sales Return.
    /// </summary>
    public ModuleNav? Navigation => new()
    {
        Flat =
        [
            new("Dashboard", "/invoicing/", "&#9679;")
            {
                Svg = "grid",
                Gate = (a, _) => a.Can("sales_invoices", Rights.View) || a.Can("purchase_invoices", Rights.View),
            },
        ],
        Groups =
        [
            new("Procurement", "&#128230;",
            [
                // Order inside the group (shared/navigation.py): Suppliers 10, Purchase Orders 20, Purchase Invoice 30, Purchase Return 40.
                new("Purchase Invoice", "/inventory/purchase-invoice/list", "&#9632;")
                {
                    ActivePrefixes = ["/inventory/purchase-invoice"], Svg = "doc", Order = 30,
                    Right = ("purchase_invoices", Rights.View),
                    LabelFor = http => Flow(http, "purchase") == "with_po" ? "Invoice Against PO" : "Purchase Invoice",
                },
            ]) { Order = 10 },
            new("Sales", "&#128176;",
            [
                new("Sales Invoice", "/inventory/invoices/list", "&#9632;")
                {
                    ActivePrefixes = ["/inventory/invoices"], Svg = "doc", Order = 30,
                    Right = ("sales_invoices", Rights.View),
                    LabelFor = http => Flow(http, "sales") == "with_so" ? "Invoice Against SO" : "Sales Invoice",
                },
            ]) { Order = 20 },
        ],
    };
}
