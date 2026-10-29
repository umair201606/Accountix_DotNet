-- Alembic 0018_report_indexes, ported: indexes for the big pages and the
-- per-request lookups (lines and charges by invoice, HR approval queues, the
-- sign-in throttle, the notification bell, costing history in date order).
-- Adds indexes only. As in Alembic, a missing table is skipped and an index
-- that already exists (by name) is left alone. Each CREATE INDEX briefly
-- blocks writes to its table while it builds, inside the migration's
-- transaction (seconds at today's sizes).
DO $migration$
DECLARE
    ix record;
BEGIN
    FOR ix IN
        SELECT * FROM (VALUES
            ('ix_inv_invoice_items_invoice_id', 'inv_invoice_items', '(invoice_id)', NULL),
            ('ix_inv_purchase_invoice_items_invoice_id', 'inv_purchase_invoice_items', '(invoice_id)', NULL),
            ('ix_additional_charges_doc', 'additional_charges', '(company_id, doc_type, doc_id)', NULL),
            ('ix_inv_invoices_company_date', 'inv_invoices', '(company_id, invoice_date)', NULL),
            ('ix_fbr_invoice_records_invoice_status', 'fbr_invoice_records', '(inv_invoice_id, status)', NULL),
            ('ix_attendance_company_date', 'attendance', '(company_id, date)', NULL),
            ('ix_leave_approvals_approver', 'leave_approvals', '(company_id, approver_id, status)', NULL),
            ('ix_timesheet_approvals_approver', 'timesheet_approvals', '(company_id, approver_id, status)', NULL),
            ('ix_leave_requests_user', 'leave_requests', '(company_id, user_id, status)', NULL),
            ('ix_loan_requests_user', 'loan_advance_requests', '(company_id, user_id, status)', NULL),
            ('ix_audit_logs_action_created_at', 'audit_logs', '(action, created_at)', NULL),
            ('ix_asset_depreciation_asset', 'asset_depreciation', '(asset_id, entry_date)', NULL),
            ('ix_asset_transfers_asset', 'asset_transfers', '(asset_id)', NULL),
            ('ix_notification_recipients_unread', 'notification_recipients', '(company_id, user_id, id)', 'is_read = false'),
            ('ix_stock_ledger_product_date', 'stock_ledger', '(company_id, product_id, txn_date, id)', NULL),
            ('ix_inv_stock_movements_product', 'inv_stock_movements', '(product_id, id)', NULL)
        ) AS t(name, tbl, cols, cond)
    LOOP
        IF to_regclass('public.' || quote_ident(ix.tbl)) IS NULL THEN
            CONTINUE;
        END IF;
        IF EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'public' AND tablename = ix.tbl
                                              AND indexname = ix.name) THEN
            CONTINUE;
        END IF;
        EXECUTE format('CREATE INDEX %I ON public.%I %s', ix.name, ix.tbl, ix.cols)
             || COALESCE(' WHERE ' || ix.cond, '');
    END LOOP;
END
$migration$;
