namespace Accountix.Core.Entities;

/// <summary>Module keys, as stored in module_flags and the mod_*_enabled columns.</summary>
public static class Modules
{
    public const string Hr = "hr";
    public const string Inventory = "inventory";
    public const string Invoicing = "invoicing";
    public const string Finance = "finance";
    public const string Accounting = "accounting";
    public const string Fbr = "fbr";
    public const string FixedAssets = "fixed_assets";
    public const string Executive = "executive";

    public sealed record Section(string Key, string Label);
    public sealed record Module(string Key, string Label, IReadOnlyList<Section> Sections);

    /// <summary>
    /// Modules and their rights sections, in the order Settings → Rights shows
    /// them. Section keys are the <c>resource</c> stored in user_permissions —
    /// keep them identical to shared/permissions.py.
    /// </summary>
    public static readonly IReadOnlyList<Module> All =
    [
        new(Hr, "HR Management",
        [
            new("employees", "Employees / Users"), new("attendance", "Attendance"),
            new("leaves", "Leaves"), new("payroll", "Payroll & Compensation"),
            new("loans", "Loans & Advances"), new("pf", "Provident Fund"),
        ]),
        new(Inventory, "Inventory",
        [
            new("products", "Products"), new("categories", "Categories"),
            new("stock", "Stock Movements"), new("consumption_vouchers", "Consumption Vouchers"),
            new("scrap_vouchers", "Scrap Vouchers"), new("adjustment_vouchers", "Stock Adjustment Vouchers"),
            new("stock_take_vouchers", "Stock Taking Vouchers"), new("inventory_reports", "Inventory Reports"),
        ]),
        new(Invoicing, "Invoicing",
        [
            new("suppliers", "Suppliers"), new("customers", "Customers"),
            new("purchase_orders", "Purchase Orders"), new("sales_orders", "Sales Orders"),
            new("purchase_invoices", "Procurement"), new("purchase_returns", "Purchase Returns"),
            new("sales_invoices", "Sales"), new("sales_returns", "Sales Returns"),
            new("payment_tracking", "Invoice Tracking & Payment Assignment"),
        ]),
        new(Finance, "Finance", [new("financial_reports", "Financial Reports")]),
        new(Accounting, "Accounting",
        [
            new("cash_payment_vouchers", "Cash Payment Vouchers (CPV)"),
            new("cash_receipt_vouchers", "Cash Receipt Vouchers (CRV)"),
            new("bank_payment_vouchers", "Bank Payment Vouchers (BPV)"),
            new("bank_receipt_vouchers", "Bank Receipt Vouchers (BRV)"),
            new("journal_vouchers", "Journal Vouchers (JV)"),
            new("chart_of_accounts", "Chart of Accounts"),
            new("bank_reconciliation", "Bank Reconciliation"),
        ]),
        new(Fbr, "FBR Digital Invoicing", [new("fbr_dashboard", "Dashboard"), new("fbr_settings", "Settings")]),
        new(FixedAssets, "Fixed Assets Management",
        [
            new("fixed_assets_dashboard", "Dashboard"), new("fixed_assets_assets", "Assets"),
            new("fixed_assets_categories", "Categories"), new("fixed_assets_reports", "Reports"),
        ]),
        new(Executive, "Executive Reports", [new("executive_reports", "Receivables & Payables")]),
    ];

    public static readonly IReadOnlySet<string> Keys = All.Select(m => m.Key).ToHashSet();
    public static readonly IReadOnlySet<string> SectionKeys = All.SelectMany(m => m.Sections).Select(s => s.Key).ToHashSet();

    /// <summary>Accounting voucher type → rights section.</summary>
    public static readonly IReadOnlyDictionary<string, string> VoucherSection = new Dictionary<string, string>
    {
        ["CPV"] = "cash_payment_vouchers",
        ["CRV"] = "cash_receipt_vouchers",
        ["BPV"] = "bank_payment_vouchers",
        ["BRV"] = "bank_receipt_vouchers",
        ["JV"] = "journal_vouchers",
    };
}

public static class Rights
{
    public const string View = "view";
    public const string Create = "create";
    public const string Edit = "edit";
    public const string Approve = "approve";
    public const string Delete = "delete";

    public static readonly IReadOnlyList<string> Actions = [View, Create, Edit, Approve, Delete];
}
