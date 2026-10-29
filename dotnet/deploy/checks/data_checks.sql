-- Accountix data checks: run before and after every cut-over step
-- (docs/CUTOVER.md) and after every load test (tests/Accountix.Load runs
-- this file). Each check is a query that returns the rows that are WRONG:
-- no rows = pass. Read-only.
--
--   psql "$DATABASE_URL" -v company_id=0 -f dotnet/deploy/checks/data_checks.sql
--
-- company_id = 0 checks every company. These complement the Python app's
-- Books Integrity (Executive -> Books Integrity, shared/integrity.py), which
-- also ties stock, fixed assets, payroll and PF to the ledger.

-- check: journals_balanced | Every posted journal: debits = credits
SELECT e.company_id, e.id, e.voucher_type, e.voucher_number,
       COALESCE(SUM(l.debit), 0) AS debit, COALESCE(SUM(l.credit), 0) AS credit
  FROM journal_entries e JOIN journal_lines l ON l.journal_entry_id = e.id
 WHERE e.is_posted AND (:company_id = 0 OR e.company_id = :company_id)
 GROUP BY e.company_id, e.id, e.voucher_type, e.voucher_number
HAVING ROUND(COALESCE(SUM(l.debit), 0) - COALESCE(SUM(l.credit), 0), 2) <> 0;

-- check: trial_balance | Each company's posted debits = credits
SELECT e.company_id, COALESCE(SUM(l.debit), 0) AS debit, COALESCE(SUM(l.credit), 0) AS credit
  FROM journal_entries e JOIN journal_lines l ON l.journal_entry_id = e.id
 WHERE e.is_posted AND (:company_id = 0 OR e.company_id = :company_id)
 GROUP BY e.company_id
HAVING ROUND(COALESCE(SUM(l.debit), 0) - COALESCE(SUM(l.credit), 0), 2) <> 0;

-- check: reporting_table | gl_daily_balances = posted journal lines per (company, account, label, day)
WITH j AS (
  SELECT COALESCE(l.company_id, e.company_id) AS company_id, l.account_id,
         COALESCE(l.label_id, 0) AS label_id, CAST(e.entry_date AS DATE) AS day,
         COALESCE(SUM(l.debit), 0) AS debit, COALESCE(SUM(l.credit), 0) AS credit, COUNT(*) AS n
    FROM journal_lines l JOIN journal_entries e ON e.id = l.journal_entry_id
   WHERE e.is_posted
   GROUP BY 1, 2, 3, 4),
g AS (
  SELECT company_id, account_id, label_id, day, debit, credit, line_count AS n
    FROM gl_daily_balances
   WHERE debit <> 0 OR credit <> 0 OR line_count <> 0)
SELECT company_id, account_id, label_id, day,
       j.debit AS ledger_debit, g.debit AS table_debit, j.credit AS ledger_credit, g.credit AS table_credit,
       j.n AS ledger_lines, g.n AS table_lines
  FROM j FULL OUTER JOIN g USING (company_id, account_id, label_id, day)
 WHERE (:company_id = 0 OR company_id = :company_id)
   AND (ROUND(COALESCE(j.debit, 0) - COALESCE(g.debit, 0), 4) <> 0
        OR ROUND(COALESCE(j.credit, 0) - COALESCE(g.credit, 0), 4) <> 0
        OR COALESCE(j.n, 0) <> COALESCE(g.n, 0));

-- check: voucher_numbers_unique | No two accounting vouchers share a number
SELECT company_id, voucher_number, COUNT(*) AS n
  FROM accounting_vouchers WHERE (:company_id = 0 OR company_id = :company_id)
 GROUP BY company_id, voucher_number HAVING COUNT(*) > 1;

-- check: document_numbers_unique | No two documents of a kind share a number (SI, PI, SR, PR, SO, PO)
SELECT * FROM (
  SELECT 'inv_invoices' AS kind, company_id, invoice_number AS number, COUNT(*) AS n FROM inv_invoices GROUP BY 2, 3 HAVING COUNT(*) > 1
  UNION ALL SELECT 'inv_purchase_invoices', company_id, invoice_number, COUNT(*) FROM inv_purchase_invoices GROUP BY 2, 3 HAVING COUNT(*) > 1
  UNION ALL SELECT 'inv_sales_returns', company_id, return_number, COUNT(*) FROM inv_sales_returns GROUP BY 2, 3 HAVING COUNT(*) > 1
  UNION ALL SELECT 'inv_purchase_returns', company_id, return_number, COUNT(*) FROM inv_purchase_returns GROUP BY 2, 3 HAVING COUNT(*) > 1
  UNION ALL SELECT 'inv_sales_orders', company_id, so_number, COUNT(*) FROM inv_sales_orders GROUP BY 2, 3 HAVING COUNT(*) > 1
  UNION ALL SELECT 'inv_purchase_orders', company_id, po_number, COUNT(*) FROM inv_purchase_orders GROUP BY 2, 3 HAVING COUNT(*) > 1
) d WHERE (:company_id = 0 OR company_id = :company_id);

-- check: no_placeholder_numbers | No document left with its pre-insert placeholder number (shared/doc_numbers.py)
SELECT * FROM (
  SELECT 'inv_invoices' AS kind, company_id, id, invoice_number AS number FROM inv_invoices
  UNION ALL SELECT 'inv_purchase_invoices', company_id, id, invoice_number FROM inv_purchase_invoices
  UNION ALL SELECT 'inv_sales_orders', company_id, id, so_number FROM inv_sales_orders
  UNION ALL SELECT 'inv_purchase_orders', company_id, id, po_number FROM inv_purchase_orders
) d WHERE (:company_id = 0 OR company_id = :company_id)
   AND (number IS NULL OR number = '' OR number LIKE 'TMP-%' OR number = '(auto)');

-- check: vouchers_posted_once | An approved accounting voucher has exactly one posted journal; any other status none
SELECT v.company_id, v.id, v.voucher_number, v.status, COUNT(e.id) AS posted_journals
  FROM accounting_vouchers v
  LEFT JOIN journal_entries e ON e.voucher_type = v.voucher_type AND e.voucher_id = v.id
                             AND e.company_id = v.company_id AND e.is_posted
 WHERE (:company_id = 0 OR v.company_id = :company_id)
 GROUP BY v.company_id, v.id, v.voucher_number, v.status
HAVING (v.status = 'approved' AND COUNT(e.id) <> 1) OR (v.status <> 'approved' AND COUNT(e.id) <> 0);

-- check: voucher_series_ahead | Each voucher series' next number is beyond every number already issued
SELECT n.company_id, n.prefix, n.next_number, MAX(CAST(SUBSTRING(v.voucher_number FROM '([0-9]+)$') AS INTEGER)) AS highest_issued
  FROM voucher_numbers n
  JOIN accounting_vouchers v ON v.company_id = n.company_id AND v.voucher_type = n.prefix
 WHERE (:company_id = 0 OR n.company_id = :company_id)
 GROUP BY n.company_id, n.prefix, n.next_number
HAVING n.next_number <= MAX(CAST(SUBSTRING(v.voucher_number FROM '([0-9]+)$') AS INTEGER));

-- check: sequences_ahead | Every id sequence is past its table's highest id (a copied database must setval)
-- (A temporary function inside a transaction that is rolled back: nothing stays behind.)
BEGIN;
CREATE FUNCTION pg_temp.ax_max_id(t text, c text) RETURNS bigint LANGUAGE plpgsql AS $f$
DECLARE m bigint; BEGIN EXECUTE format('SELECT MAX(%I) FROM %I', c, t) INTO m; RETURN m; END $f$;
SELECT table_name, column_name, last_value, max_id FROM (
  SELECT c.table_name, c.column_name,
         pg_sequence_last_value(pg_get_serial_sequence(quote_ident(c.table_name), c.column_name)::regclass) AS last_value,
         pg_temp.ax_max_id(c.table_name, c.column_name) AS max_id
    FROM information_schema.columns c
   WHERE c.table_schema = 'public' AND c.column_default LIKE 'nextval(%'
) s WHERE max_id IS NOT NULL AND COALESCE(last_value, 0) < max_id;
ROLLBACK;
