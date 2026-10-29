-- The data of ReportGoldenTests, executed block by block by the .NET test (ReportGoldenTests.cs) and,
-- when the fixture was captured, by the Python app's capture (capture/reports.py): one text, so both apps
-- read byte-identical rows. A block starts with "-- @name"; ${name} is replaced by the value of an
-- earlier block's RETURNING id, or by ${user} (owner_b), ${tag} (the run's tag), ${slug}.
-- "-- @provision" is not SQL: each app provisions the new company its own way (CompanySetup / provision_company).
--
-- A company of its own (so every total on the pages is ours), owner_b its admin. Report rows of company B's
-- ReportFixture: products with dated receipts and issues (the ADJ keyed last but dated 1 Mar), a re-costing,
-- movements, one document of each previewable type; P2 shows 7 on hand while its ledger ends at 0.

-- @company
INSERT INTO companies (name, slug, is_active, plan_name, created_by, mod_hr_enabled, mod_inventory_enabled, mod_invoicing_enabled,
                       mod_finance_enabled, mod_accounting_enabled, mod_fbr_enabled, mod_fixed_assets_enabled, mod_executive_enabled,
                       country, timezone, created_at, updated_at)
VALUES ('Inventory Reports ${tag}', '${slug}', true, 'free', ${user}, true, true, true, true, true, true, true, true,
        'Pakistan', 'Asia/Karachi', '2026-04-02 08:00:00', '2026-04-02 08:00:00')
RETURNING id

-- @membership
INSERT INTO company_memberships (company_id, user_id, role_id, status, joined_at, module_flags)
VALUES (${company}, ${user}, (SELECT id FROM roles WHERE name = 'admin'), 'active', '2026-04-02 08:00:00',
        '{"accounting": true, "executive": true, "fbr": true, "finance": true, "fixed_assets": true, "hr": true, "inventory": true, "invoicing": true}')
RETURNING id

-- @provision

-- @category
INSERT INTO inv_categories (company_id, name, is_active, created_at)
VALUES (${company}, 'RPT Cat ${tag}', true, '2026-04-02 08:00:00')
RETURNING id

-- @p1
INSERT INTO inv_products (company_id, sku, name, category_id, unit_price, cost_price, reorder_level, current_stock, unit, hs_code, weight,
                          is_active, created_at, updated_at)
VALUES (${company}, 'RPT-${tag}-A', 'Rpt ${tag} A', ${category}, 150, 100, 10, 8.5, 'pcs', '', 0, true, '2026-04-02 08:00:00', '2026-04-02 08:00:00')
RETURNING id

-- @p2
INSERT INTO inv_products (company_id, sku, name, category_id, unit_price, cost_price, reorder_level, current_stock, unit, hs_code, weight,
                          is_active, created_at, updated_at)
VALUES (${company}, 'RPT-${tag}-B', 'Rpt ${tag} B', ${category}, 150, 100, 0, 7, 'pcs', '', 0, true, '2026-04-02 08:00:01', '2026-04-02 08:00:01')
RETURNING id

-- @p3
INSERT INTO inv_products (company_id, sku, name, category_id, unit_price, cost_price, reorder_level, current_stock, unit, hs_code, weight,
                          is_active, created_at, updated_at)
VALUES (${company}, 'RPT-${tag}-C', 'Rpt ${tag} C', ${category}, 150, 100, 0, 0, 'pcs', '', 0, false, '2026-04-02 08:00:02', '2026-04-02 08:00:02')
RETURNING id

-- @supplier
INSERT INTO inv_suppliers (company_id, name, is_active, created_at)
VALUES (${company}, 'Rpt Supplier ${tag}', true, '2026-04-02 08:00:00')
RETURNING id

-- @cons
INSERT INTO consumption_vouchers (company_id, voucher_number, date, department, reason, status, created_by, approved_by, approved_at, created_at)
VALUES (${company}, 'RPTC-${tag}', '2026-02-05 00:00:00', 'Workshop', 'Repairs', 'approved', ${user}, ${user}, '2026-02-05 10:30:00',
        '2026-04-02 08:00:00')
RETURNING id

-- @cons_item
INSERT INTO consumption_items (company_id, voucher_id, product_id, product_name, quantity, unit_cost, total_cost)
VALUES (${company}, ${cons}, ${p1}, 'Rpt ${tag} A', 4, 100, 400)
RETURNING id

-- @scrap
INSERT INTO scrap_vouchers (company_id, voucher_number, date, reason, status, created_by, created_at)
VALUES (${company}, 'RPTS-${tag}', '2026-03-15 00:00:00', NULL, 'unapproved', ${user}, '2026-04-02 08:00:00')
RETURNING id

-- @scrap_item
INSERT INTO scrap_items (company_id, voucher_id, product_id, product_name, quantity, unit_cost, total_cost)
VALUES (${company}, ${scrap}, ${p1}, 'Rpt ${tag} A', 2.5, 109.0909, 272.73)
RETURNING id

-- @adj
INSERT INTO stock_adjustment_vouchers (company_id, voucher_number, date, reason, status, created_by, approved_by, approved_at, created_at)
VALUES (${company}, 'RPTA-${tag}', '2026-03-01 00:00:00', 'Count', 'approved', ${user}, ${user}, '2026-03-01 09:00:00', '2026-04-02 08:00:00')
RETURNING id

-- @adj_item
INSERT INTO stock_adjustment_items (company_id, voucher_id, product_id, product_name, system_qty, physical_qty, difference, unit_cost, total_cost)
VALUES (${company}, ${adj}, ${p1}, 'Rpt ${tag} A', 6, 11, 5, 120, 600)
RETURNING id

-- @pi
INSERT INTO inv_purchase_invoices (company_id, invoice_number, voucher_number, invoice_date, supplier_id, net_payable, status, created_by, created_at)
VALUES (${company}, 'RPT-PI-${tag}', 'RPTPI-${tag}', '2026-01-10 00:00:00', ${supplier}, 1000.5, 'approved', ${user}, '2026-04-02 08:00:00')
RETURNING id

-- @pi_item1
INSERT INTO inv_purchase_invoice_items (company_id, invoice_id, product_id, description, quantity, unit_price, total_after_discount)
VALUES (${company}, ${pi}, ${p1}, 'Panels', 10, 100, 0)
RETURNING id

-- @pi_item2
INSERT INTO inv_purchase_invoice_items (company_id, invoice_id, product_id, description, quantity, unit_price, total_after_discount)
VALUES (${company}, ${pi}, NULL, 'Freight line', 1, 0.5, 0.5)
RETURNING id

-- @pr
INSERT INTO inv_purchase_returns (company_id, return_number, original_invoice_id, supplier_id, return_date, net_return_amount, status, created_by,
                                  created_at)
VALUES (${company}, 'RPT-DN-${tag}', ${pi}, ${supplier}, '2026-01-20 00:00:00', 200, 'new', ${user}, '2026-04-02 08:00:00')
RETURNING id

-- @pr_item
INSERT INTO inv_purchase_return_items (company_id, return_id, product_id, description, current_return_qty, unit_price, net_return_value)
VALUES (${company}, ${pr}, ${p1}, 'Panels', 2, 100, 0)
RETURNING id

-- @row_pi
INSERT INTO stock_ledger (company_id, product_id, voucher_type, voucher_id, voucher_number, transaction_type, quantity, unit_cost, total_cost,
                          running_qty, running_cost, running_avg, valuation_method, txn_date, created_at, created_by, notes)
VALUES (${company}, ${p1}, 'PI', ${pi}, 'RPTPI-${tag}', 'IN', 10, 100, 1000, 10, 1000, 100, 'weighted_average', '2026-01-10',
        '2026-04-02 08:00:00', ${user}, 'Purchase')
RETURNING id

-- @row_cons
INSERT INTO stock_ledger (company_id, product_id, voucher_type, voucher_id, voucher_number, transaction_type, quantity, unit_cost, total_cost,
                          running_qty, running_cost, running_avg, valuation_method, txn_date, created_at, created_by, notes)
VALUES (${company}, ${p1}, 'CONS', ${cons}, 'RPTC-${tag}', 'OUT', 4, 100, 400, 6, 600, 100, 'weighted_average', '2026-02-05',
        '2026-04-02 08:00:00', ${user}, 'Consumption: Repairs')
RETURNING id

-- @row_scrap
INSERT INTO stock_ledger (company_id, product_id, voucher_type, voucher_id, voucher_number, transaction_type, quantity, unit_cost, total_cost,
                          running_qty, running_cost, running_avg, valuation_method, txn_date, created_at, created_by, notes)
VALUES (${company}, ${p1}, 'SCRAP', ${scrap}, 'RPTS-${tag}', 'OUT', 2.5, 109.0909, 272.73, 8.5, 927.27, 109.0906, 'weighted_average', '2026-03-15',
        '2026-04-02 08:00:00', ${user}, '')
RETURNING id

-- @row_adj
INSERT INTO stock_ledger (company_id, product_id, voucher_type, voucher_id, voucher_number, transaction_type, quantity, unit_cost, total_cost,
                          running_qty, running_cost, running_avg, valuation_method, txn_date, created_at, created_by, notes)
VALUES (${company}, ${p1}, 'ADJ', ${adj}, 'RPTA-${tag}', 'IN', 5, 120, 600, 11, 1200, 109.0909, 'weighted_average', '2026-03-01',
        '2026-04-02 08:00:00', ${user}, 'Adjustment: Count')
RETURNING id

-- @row_open
INSERT INTO stock_ledger (company_id, product_id, voucher_type, voucher_id, voucher_number, transaction_type, quantity, unit_cost, total_cost,
                          running_qty, running_cost, running_avg, valuation_method, txn_date, created_at, created_by, notes)
VALUES (${company}, ${p2}, 'OPENING', ${p2}, 'OPEN-RPT-${tag}-B', 'IN', 3, 50, 150, 3, 150, 50, 'weighted_average', '2026-04-01',
        '2026-04-02 08:00:00', ${user}, 'Opening stock')
RETURNING id

-- @row_si
INSERT INTO stock_ledger (company_id, product_id, voucher_type, voucher_id, voucher_number, transaction_type, quantity, unit_cost, total_cost,
                          running_qty, running_cost, running_avg, valuation_method, txn_date, created_at, created_by, notes)
VALUES (${company}, ${p2}, 'SI', 999001, 'SI-RPT-${tag}', 'OUT', 3, 50, 150, 0, 0, 0, 'weighted_average', '2026-04-02',
        '2026-04-02 08:00:00', ${user}, '')
RETURNING id

-- @recost
INSERT INTO stock_cost_adjustments (company_id, product_id, ledger_id, voucher_type, voucher_id, voucher_number, old_cost, new_cost, delta,
                                    trigger, entry_date, created_at)
VALUES (${company}, ${p1}, ${row_cons}, 'CONS', ${cons}, 'RPTC-${tag}', 380, 400, 20, 'reversal of RPTX', '2026-02-05', '2026-04-02 08:00:00')
RETURNING id

-- @move_in
INSERT INTO inv_stock_movements (company_id, product_id, type, quantity, reference_type, reference_id, notes, created_by, created_at)
VALUES (${company}, ${p1}, 'adjustment_in', 5, 'stock_adjustment', ${adj}, 'Count', ${user}, '2026-03-01 09:05:00')
RETURNING id

-- @move_out
INSERT INTO inv_stock_movements (company_id, product_id, type, quantity, reference_type, reference_id, notes, created_by, created_at)
VALUES (${company}, ${p1}, 'adjustment_out', 2.5, NULL, NULL, NULL, ${user}, '2026-03-15 11:00:00')
RETURNING id
