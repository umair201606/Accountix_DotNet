using Accountix.Parties.Controllers;
using Accountix.Parties.Services;

namespace Accountix.Parties;

/// <summary>
/// The Parties part of Invoicing: customers, suppliers, invoice tracking (payment assignment) and the invoice
/// register. Owns IPaymentTracking. Its sidebar entries merge into the Invoicing module's
/// (Suppliers heads Procurement, Customers heads Sales, Invoice Tracking is its own group). See docs/modules/parties.md.
/// </summary>
public sealed class PartiesModule : IAccountixModule
{
    public string Key => "invoicing";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<PaymentTracking>();
        services.AddScoped<IPaymentTracking>(sp => sp.GetRequiredService<PaymentTracking>());
        services.AddScoped<PartyPages>();
        services.AddScoped<InvoiceRegister>();
        // The register's bulk print uses Invoicing's print code (InvoicePrint, registered by InvoicingModule). A factory,
        // so a host booting Parties without Invoicing (module tests) still starts; only those three routes need it.
        services.AddScoped(sp => new BulkInvoices(sp.GetRequiredService<AccountixDbContext>(), sp.GetRequiredService<InvoiceRegister>(),
            sp.GetRequiredService<Accountix.Invoicing.Services.InvoicePrint>(), sp.GetRequiredService<PaymentTracking>()));
    }

    // shared/navigation.py NAV["invoicing"]: groups Procurement 10, Sales 20, Invoice Tracking 30; inside
    // Procurement / Sales: Suppliers / Customers 10, orders 20 (Orders), invoices 30 (Invoicing), returns 40 (Returns).
    public ModuleNav? Navigation => new()
    {
        Groups =
        [
            new("Procurement", "&#128230;",
            [
                new("Suppliers", "/inventory/suppliers/") { Svg = "building", ActivePrefixes = ["/inventory/suppliers"], Order = 10, Right = ("suppliers", Rights.View) },
            ]) { Order = 10 },
            new("Sales", "&#128176;",
            [
                new("Customers", "/inventory/customers/") { Svg = "user", ActivePrefixes = ["/inventory/customers"], Order = 10, Right = ("customers", Rights.View) },
            ]) { Order = 20 },
            new("Invoice Tracking", "&#9878;",
            [
                new("Invoice Register", "/invoicing/registers/")
                {
                    Svg = "doc", ActivePrefixes = ["/invoicing/registers"],
                    Gate = (a, _) => a.Can("sales_invoices", Rights.View) || a.Can("purchase_invoices", Rights.View),
                },
                new("Payment & Receipt Feed", "/invoicing/tracking/")
                {
                    // exact: the feed and the assign workspace
                    Right = ("payment_tracking", Rights.View), Svg = "activity", ActivePrefixes = ["/invoicing/tracking"],
                    ExcludePrefixes = ["/invoicing/tracking/sales", "/invoicing/tracking/purchases", "/invoicing/tracking/invoice", "/invoicing/tracking/api"],
                },
                new("Sales Invoice Tracking", "/invoicing/tracking/sales")
                {
                    Right = ("payment_tracking", Rights.View), Svg = "trend-up", ActivePrefixes = ["/invoicing/tracking/sales", "/invoicing/tracking/invoice/SI/"],
                },
                new("Purchase Invoice Tracking", "/invoicing/tracking/purchases")
                {
                    Right = ("payment_tracking", Rights.View), Svg = "trend-dn", ActivePrefixes = ["/invoicing/tracking/purchases", "/invoicing/tracking/invoice/PI/"],
                },
            ]) { Order = 30 },
        ],
    };
}
