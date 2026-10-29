// Generated once from the former Python app (29 Oct 2026): the tables the audit trail tracks.
// The generator went with the Python app, so this file is now maintained by hand.
#nullable enable
namespace Accountix.Platform.Audit;

internal static partial class AuditTables
{
    /// <summary>shared/audit.py TRACKED: table -> (module, label, reference attributes that exist on the model, columns in the model's order).</summary>
    internal static readonly IReadOnlyDictionary<string, Tracked> All = new Dictionary<string, Tracked>
    {
        ["inv_invoices"] = new("invoicing", "Sales invoice", ["invoice_number"], false,
            ["id", "company_id", "invoice_number", "voucher_number", "sales_order_id", "customer_id", "party_account_id", "label_id", "invoice_date", "due_date", "voucher_status", "payment_status", "discount_mode", "charges_mode", "tax_mode", "global_discount_pct", "global_discount_value", "global_delivery", "global_installation", "global_sales_tax_pct", "further_tax_pct", "apply_further_tax", "withholding_tax_pct", "apply_withholding_tax", "subtotal", "total_discount", "total_charges", "total_tax", "total_further_tax", "total_withholding_tax", "total_amount", "paid_amount", "notes", "created_by", "approved_by", "approved_at", "edit_version", "created_at"]),
        ["inv_purchase_invoices"] = new("invoicing", "Purchase invoice", ["invoice_number"], false,
            ["id", "company_id", "invoice_number", "voucher_number", "invoice_date", "supplier_id", "party_account_id", "label_id", "driver_name", "driver_contact", "vehicle_number", "gate_pass", "discount_mode", "expenses_mode", "tax_mode", "global_discount_pct", "global_discount_value", "global_commission", "global_freight", "global_loading", "global_sales_tax_pct", "global_withholding_tax_pct", "further_tax_pct", "apply_further_tax", "apply_withholding_tax", "subtotal", "total_discount", "total_expenses", "total_tax", "total_further_tax", "total_withholding_tax", "net_payable", "total_amount", "paid_amount", "payment_status", "purchase_order_id", "notes", "status", "created_by", "approved_by", "approved_at", "edit_version", "created_at"]),
        ["inv_sales_returns"] = new("invoicing", "Sales return", ["return_number"], false,
            ["id", "company_id", "return_number", "original_invoice_id", "customer_id", "party_account_id", "return_date", "notes", "gross_return_value", "total_discount", "total_charges", "total_tax", "net_return_amount", "total_cost_returned", "reverse_charges", "status", "created_by", "approved_by", "approved_at", "edit_version", "created_at"]),
        ["inv_purchase_returns"] = new("invoicing", "Purchase return", ["return_number"], false,
            ["id", "company_id", "return_number", "original_invoice_id", "supplier_id", "return_date", "notes", "gross_return_value", "total_discount", "total_expenses", "total_tax", "net_return_amount", "reverse_expenses", "status", "created_by", "approved_by", "approved_at", "edit_version", "created_at"]),
        ["inv_sales_orders"] = new("invoicing", "Sales order", ["id"], false,
            ["id", "company_id", "so_number", "customer_id", "party_account_id", "order_date", "expected_date", "status", "fulfilment_status", "tax_mode", "global_sales_tax_pct", "subtotal", "total_tax", "total_amount", "notes", "created_by", "approved_by", "approved_at", "edit_version", "created_at"]),
        ["inv_purchase_orders"] = new("invoicing", "Purchase order", ["id"], false,
            ["id", "company_id", "po_number", "supplier_id", "party_account_id", "order_date", "expected_date", "status", "fulfilment_status", "tax_mode", "global_sales_tax_pct", "subtotal", "total_tax", "total_amount", "notes", "driver_name", "driver_contact", "vehicle_number", "gate_pass", "created_by", "approved_by", "approved_at", "edit_version", "created_at"]),
        ["inv_customers"] = new("invoicing", "Customer", ["name"], false,
            ["id", "company_id", "name", "contact_person", "email", "phone", "mobile", "address", "city", "tax_id", "payment_terms", "credit_limit", "website", "notes", "is_active", "created_at"]),
        ["inv_suppliers"] = new("invoicing", "Supplier", ["name"], false,
            ["id", "company_id", "name", "contact_person", "email", "phone", "mobile", "address", "city", "tax_id", "payment_terms", "website", "notes", "is_active", "created_at"]),
        ["inv_products"] = new("inventory", "Product", ["sku", "name"], false,
            ["id", "company_id", "sku", "name", "description", "category_id", "unit_price", "cost_price", "reorder_level", "current_stock", "unit", "hs_code", "weight", "is_active", "created_at", "updated_at"]),
        ["consumption_vouchers"] = new("inventory", "Consumption voucher", ["voucher_number"], false,
            ["id", "company_id", "voucher_number", "date", "department", "reason", "charge_account_id", "label_id", "status", "created_by", "approved_by", "created_at", "approved_at"]),
        ["scrap_vouchers"] = new("inventory", "Scrap voucher", ["voucher_number"], false,
            ["id", "company_id", "voucher_number", "date", "reason", "charge_account_id", "label_id", "status", "created_by", "approved_by", "created_at", "approved_at"]),
        ["stock_adjustment_vouchers"] = new("inventory", "Stock adjustment", ["voucher_number"], false,
            ["id", "company_id", "voucher_number", "date", "reason", "label_id", "status", "created_by", "approved_by", "created_at", "approved_at"]),
        ["stock_takes"] = new("inventory", "Stock take", ["reference"], false,
            ["id", "company_id", "reference", "date", "location", "status", "created_by", "approved_by", "created_at", "approved_at", "adjustment_voucher_id"]),
        ["inventory_settings"] = new("inventory", "Inventory settings", ["id"], false,
            ["id", "company_id", "valuation_method", "allow_negative_stock", "decimal_places", "auto_generate_vouchers", "purchase_flow", "sales_flow", "default_cogs_account_id", "default_inventory_account_id", "default_return_account_id", "per_line_labeling_voucher", "per_line_labeling_invoice", "default_voucher_label_id", "default_invoice_label_id"]),
        ["accounting_vouchers"] = new("accounting", "Accounting voucher", ["voucher_number"], false,
            ["id", "company_id", "voucher_type", "voucher_number", "voucher_date", "cash_bank_account_id", "label_id", "notes", "status", "created_by", "approved_by", "created_at", "approved_at"]),
        ["journal_entries"] = new("accounting", "Journal entry", ["voucher_number"], false,
            ["id", "company_id", "voucher_type", "voucher_id", "voucher_number", "description", "entry_date", "created_by", "created_at", "is_posted"]),
        ["chart_of_accounts"] = new("accounting", "Account", ["code", "name"], false,
            ["id", "company_id", "code", "name", "type", "parent_id", "level", "is_fixed", "is_active", "description", "cash_flow_activity", "pl_section", "created_at"]),
        ["accounting_periods"] = new("accounting", "Accounting period", ["period_name"], false,
            ["id", "company_id", "fiscal_year", "period_name", "start_date", "end_date", "is_open", "is_closed", "is_active", "created_at", "closed_at"]),
        ["project_labels"] = new("accounting", "Project label", ["name"], false,
            ["id", "company_id", "name", "is_active", "created_at"]),
        ["company_info"] = new("settings", "Company profile", ["company_name", "id"], false,
            ["id", "company_id", "company_name", "address", "city", "state", "country", "phone", "email", "website", "tax_id", "registration_number", "logo_url", "bank_name", "bank_account_title", "bank_account_number", "fiscal_year_start_month", "currency", "currency_symbol", "date_format", "number_format", "timezone", "created_at", "updated_at"]),
        ["report_settings"] = new("settings", "Report settings", ["id"], false,
            ["id", "company_id", "pl_structure_json", "pl_detail_rows", "invoice_party_mode", "purchase_party_mode", "sales_party_mode", "purchase_template_text", "sales_template_text", "purchase_template_id", "sales_template_id", "cash_flow_method", "twcf_cash_floor", "updated_at"]),
        ["invoice_settings"] = new("settings", "Invoice settings", ["id"], false,
            ["id", "company_id", "default_sales_tax_pct", "default_further_tax_pct", "default_withholding_tax_pct", "default_discount_pct", "default_charges_mode", "default_discount_mode", "default_tax_mode", "show_discount_column", "show_charges_column", "show_tax_column", "auto_add_line", "require_approval", "allow_partial_payment", "default_party_mode", "over_invoice_tolerance_pct", "withholding_base", "show_further_tax", "show_withholding_tax", "show_transport_block", "create_from_orders_enabled", "per_line_discount_enabled", "per_line_tax_enabled"]),
        ["fixed_assets"] = new("fixed_assets", "Fixed asset", ["asset_code", "name"], false,
            ["id", "company_id", "asset_code", "name", "description", "category_id", "purchase_date", "purchase_cost", "useful_life", "depreciation_method", "salvage_value", "accumulated_depreciation", "current_book_value", "status", "location", "assigned_to", "vendor", "serial_number", "fixed_asset_account_id", "accum_dep_account_id", "dep_expense_account_id", "acquisition_credit_account_id", "notes", "label_id", "is_active", "created_at", "updated_at"]),
        ["asset_transfers"] = new("fixed_assets", "Asset transfer", ["voucher_number"], false,
            ["id", "company_id", "voucher_number", "direction", "asset_id", "product_id", "new_product_name", "source_product_id", "transfer_amount", "quantity", "description", "transfer_date", "status", "created_by", "approved_by", "created_at", "approved_at"]),
        ["asset_depreciation"] = new("fixed_assets", "Depreciation", ["id"], false,
            ["id", "company_id", "asset_id", "entry_date", "amount", "accumulated_after", "net_book_value_after", "journal_entry_id", "notes", "created_at"]),
        ["payroll_runs"] = new("hr", "Payroll run", ["month", "year"], true,
            ["id", "company_id", "month", "year", "run_date", "status", "total_gross", "total_deductions", "total_net", "employee_count", "processed_by", "approved_by", "approved_at", "notes", "paid_at"]),
        ["payroll_profiles"] = new("hr", "Payroll profile", ["user_id"], false,
            ["id", "company_id", "user_id", "basic_salary", "effective_from", "payment_method", "created_at", "updated_at"]),
        ["users"] = new("admin", "User", ["email"], false,
            ["id", "employee_code", "email", "login_id", "password_hash", "full_name", "role_id", "manager_id", "designation", "department", "date_of_birth", "date_of_joining", "phone", "cnic", "bank_name", "bank_account_title", "bank_account_number", "emergency_contact", "emergency_phone", "address", "profile_image", "is_active", "has_hr_access", "has_inventory_access", "has_invoicing_access", "has_finance_access", "has_accounting_access", "has_fbr_access", "has_fixed_assets_access", "has_executive_access", "is_super_admin", "max_companies_owned", "max_companies_joined", "created_at", "updated_at"]),
        ["company_memberships"] = new("admin", "Membership", ["user_id"], false,
            ["id", "company_id", "user_id", "role_id", "status", "employee_code", "joined_at", "date_of_leaving", "module_flags", "manager_id", "designation", "department"]),
    };

    /// <summary>Quantity columns (Python reads whole numbers back as int, others as float).</summary>
    internal static readonly IReadOnlySet<string> QuantityColumns = new HashSet<string> { "inv_products.current_stock" };

    /// <summary>Columns whose model attribute is a property, not the column (never in an update's diff).</summary>
    internal static readonly IReadOnlySet<string> PropertyColumns = new HashSet<string> { "users.manager_id", "users.designation", "users.department" };
}
