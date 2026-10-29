# .NET move: progress (coordinator's file)

**Final goal (user, 29 Oct 2026):** the complete app moved from Python to
.NET, with identical behaviour. It is phased on the same database: each module
moves when ready, and the Python app serves the rest until cut-over.

## Done

- **Foundation** (coordinator):
  - entities generated for all 122 tables;
  - company isolation in EF (fail-closed filter, stamping, no cross-company writes);
  - access rules matching Python;
  - Werkzeug-compatible passwords;
  - sign-in with its own `ax_session` cookie over `user_sessions` (the Flask-cookie bridge was removed 29 Oct);
  - app shell, navigation, flash, error answers, lock timeout, antiforgery for forms and fetch;
  - shared helpers: Money, CompanyClock, DocumentDates, DocNumbers, DbLocks with Python-identical advisory keys;
  - contracts for the cross-module services, with stand-ins;
  - per-module projects and test projects, `ModuleHost` + `TestSignIn` for module page tests, a reference database built by the Python app with one copy per module.
- **Access review and fixes in the Python app** (`docs/ACCESS_REVIEW.md`; Alembic **0016**; `tests/e2e/test_access_fixes.py`, 13 tests).

## Engineers (29 Oct 2026, 16 in parallel, all Opus 5.5)

Every engineer follows `docs/PORTING_GUIDE.md` and `docs/SPEED_AND_CONCURRENCY.md` (user requirement of 29 Oct: big reports load quickly, many users in one company work smoothly without conflicts).

| Project | Scope | Status file | DB key |
|---|---|---|---|
| Accountix.Accounting | ✅ done (coordinator): vouchers, COA, bank rec, GL entry; ILedger, IVoucherNumbers, IPeriods; 27 tests incl. races, cross-app race, query budgets | modules/accounting.md | accounting |
| Accountix.Finance | ✅ financial statements, ledger, TWCF, finance dashboard; IReportExport (streaming Excel: 10k×15 in ~0.15 s; direct PDF); 52 tests (every statement equals Python's); statements 9–32 ms (Python 76–163 ms); month ledger PDF 0.3–0.4 s (Python 8–11 s); 54 tests; cash-flow comparative columns (expense refunds cut profit) and TWCF clock fixed in both apps | modules/finance.md | finance |
| Accountix.Executive | ✅ executive dashboard, aging, party ledger, Books Integrity; 72 tests (every integrity check equals Python's; passes while 6 people post); full scope: dashboard 70 ms (Python 360 ms); 3 Python bugs fixed; aging uses the company clock in both apps | modules/executive.md | executive |
| Accountix.Inventory | ✅ products, stock, vouchers, reports; IStockCosting (FIFO/average, back-dated replay, CADJ journals; parity with Python on scripted sequences); 131 tests; year valuation 13–22 ms (Python 267–300 ms); back-dated receipt re-costing 404 issues 0.5 s (Python 18.5 s); stock-take list crash, 3 phone flaws and server-date defaults fixed in both apps | modules/inventory.md | inventory |
| Accountix.Invoicing | ✅ SI, PI, invoicing dashboard; shared invoice logic, order linkage; journals equal Python's to the paisa; lists 8–10 ms vs Python 126–425 ms; 44 tests on the real stock engine (stock rows, layers, re-costing and COGS equal Python's, incl. back-dated sales) | modules/invoicing.md | invoicing |
| Accountix.Returns | ✅ sales / purchase returns; 26 tests (journals and stock equal Python's; passes with the real stock engine); pages 9–55 ms (Python 52–950 ms); draft-counts-itself bug + 2 display slips fixed in both apps | modules/returns.md | returns |
| Accountix.Orders | ✅ sales / purchase orders; 39 tests; lists 15–19 ms (Python 92 ms); 3 Python editor defects fixed in both apps (reopened SO saved totals of 0; server now recomputes totals that disagree with the lines) | modules/orders.md | orders |
| Accountix.Parties | ✅ customers, suppliers, invoice tracking, registers incl. bulk print; IPaymentTracking; 77 tests; register month 61 ms (Python 376 ms); 100 printed invoices 87 ms (Python 238 ms) | modules/parties.md | parties |
| Accountix.Hr | ✅ attendance, leave, timesheets, ESS, MSS, users, HR dashboard; 51 tests; users page 20–39 ms (Python 3.3 s); 6 Python bugs fixed in both apps | modules/hr.md | hr |
| Accountix.HrRecords | ✅ communication + bell API (bell now in the shell), digital files, workplace, HR reports; 67 tests; HR report on 200 people 13 ms (Python 3.3 s); 3 Python report bugs fixed in both apps (hours ×24 on Postgres, department list, active payroll year) | modules/hr_records.md | hr_records |
| Accountix.Payroll | ✅ compensation, payroll engine and posting, PF, staff loans, tax; 133 tests (slips and journals equal Python's to the paisa; both apps post to the same EOBI/gratuity/PF-profit accounts); payroll run of 200 employees 0.4–0.5 s (Python 14.4 s) | modules/payroll.md | payroll |
| Accountix.FixedAssets | ✅ fixed assets, inventory → FA transfers; 64 tests; pages 10–45 ms (Python run page 11.2 s); `admin merge-legacy-accounts` repairs the duplicate accounts (dry run by default) | modules/fixed_assets.md | fixed_assets |
| Accountix.Fbr | ✅ FBR digital invoicing; 40 tests; payload byte-identical to Python on all 2,760 Samba invoices | modules/fbr.md | fbr |
| Accountix.Platform | ✅ landing, sign-up, sign-in (`ax_session`), portal, hub, every settings tab incl. the invoice-template designer, provisioning (identical to Python's), audit trail (one INSERT per save); 44 tests; audit log 22–82 ms (Python 165–215 ms); 4 phone-width flaws fixed in both apps | modules/platform.md | platform |
| Accountix.SuperAdmin | ✅ super admin console; 57 tests; 2 Python console flaws fixed in both apps (320px header; My Books quota race) | modules/superadmin.md | superadmin |
| Accountix.Admin | ✅ maintenance commands `dotnet Accountix.Web.dll admin <command>`: the live-data tools ported from Python (merge-legacy-accounts, repair-opening-stock, remove-faulty-demo-records, rebuild-gl-summary), add-default-leave-types (F6) and books-integrity; dry run unless `--apply`; 17 tests, each tool leaves exactly the rows its Python tool leaves on the same input | DEPLOY.md ("Maintenance commands") | throwaway `st_admin_*` |
| tests/Accountix.E2E | browser E2E for every module, 320px+, browser timings | E2E.md, E2E_FINDINGS.md | e2e |
| deploy/, tests/Accountix.Load | ✅ Azure App Service files (Dockerfile, Bicep, az script, staging-slot workflow; nothing applied), key ring in Postgres (Alembic 0017), Neon pool settings, Brotli/gzip (ledger month 2.1 MB → 135 KB), load test, cut-over plan | DEPLOY.md, CUTOVER.md | load, load_large |

Speed-test database: `accountix_ref_perf`, a full year of Samba Private Limited (tools/seed_samba.py), described by `tools/perf.json`, read-only.
Both speed-test databases are kept as dumps; `dotnet/tools/restore_perf_db.sh` restores them and rewrites `tools/perf.json` (SPEED_AND_CONCURRENCY.md §1).

## Speed sweep: a company 4× Samba (29 Oct 2026)

`tests/Accountix.Tests/BigCompanySweepTests.cs` loads every module, as production does, on the 4× Samba
company (`accountix_ref_perf_large`: tools/seed_samba.py --scale 4, 22,786 documents posted through
the Python app, Books Integrity passing). Budgets: 800 ms for a month, 1.5 s for a year, 3 s for an
export, and 40 queries per page. Every page passes. Times are the faster of 2 runs in the test host, after
Alembic **0018_report_indexes** (which made most pages 10–50 % faster):

```
4x Samba (10405 SI, 89871 journal lines):
      42 ms  11 q 200    79 KB /inventory/invoices/list?from=2026-06-01&to=2026-06-30
      11 ms  11 q 200    79 KB /inventory/invoices/list?from=2026-06-01&to=2026-06-30&page=6
      13 ms  11 q 200    79 KB /inventory/invoices/list?from=2025-07-01&to=2026-06-30&page=50
      13 ms  11 q 200    84 KB /inventory/purchase-invoice/list?from=2026-06-01&to=2026-06-30
      81 ms  22 q 200    44 KB /invoicing/
      52 ms  16 q 200   650 KB /invoicing/registers/?view=1&mode=month&month=2026-06
      21 ms  16 q 200   209 KB /invoicing/registers/?doc=purchase&view=1&mode=month&month=2026-06
      22 ms  13 q 200   216 KB /invoicing/registers/data?mode=custom&from=2025-07-01&to=2026-06-30
     229 ms  16 q 200  5836 KB /invoicing/registers/?view=1&mode=custom&from=2025-07-01&to=2026-06-30
     194 ms  16 q 200  1285 KB /invoicing/registers/data?full=1&doc=sales&mode=month&month=2026-06
      41 ms  13 q 200 133973 B /invoicing/registers/export?mode=month&month=2026-06&fmt=excel
     196 ms  13 q 200 155233 B /invoicing/registers/export?mode=month&month=2026-06&fmt=pdf
      29 ms  13 q 200   157 KB /invoicing/registers/export?mode=month&month=2026-06&fmt=csv
     311 ms  13 q 200 1296423 B /invoicing/registers/export?mode=custom&from=2025-07-01&to=2026-06-30&fmt=excel
      54 ms  19 q 200   247 KB /invoicing/tracking/
     128 ms  22 q 200   152 KB /invoicing/tracking/sales
      30 ms  10 q 200    96 KB /inventory/customers/
      72 ms  10 q 200  1655 KB /inventory/sales/list
      12 ms  10 q 200   169 KB /invoicing/sales-return/list
      22 ms  13 q 200   115 KB /finance/trial-balance?filter_mode=custom&from=2025-07-01&to=2026-06-30
      24 ms  13 q 200    51 KB /finance/profit-loss?filter_mode=custom&from=2025-07-01&to=2026-06-30
      22 ms  12 q 200    63 KB /finance/balance-sheet?filter_mode=custom&from=2025-07-01&to=2026-06-30
      19 ms  13 q 200    49 KB /finance/socie?filter_mode=custom&from=2025-07-01&to=2026-06-30
      28 ms  16 q 200    47 KB /finance/cash-flow?filter_mode=custom&from=2025-07-01&to=2026-06-30
     130 ms  13 q 200  3420 KB /finance/ledger?filter_mode=custom&from=2026-06-01&to=2026-06-30&mode=all
      23 ms  12 q 200  18791 B /finance/trial-balance?filter_mode=custom&from=2025-07-01&to=2026-06-30&format=excel
       6 ms   7 q 200    27 KB /finance/
      48 ms  13 q 200   297 KB /accounting/registers/vouchers?view=1&mode=month&month=2026-06
     360 ms  13 q 200  2698 KB /accounting/registers/vouchers?view=1&mode=custom&from=2025-07-01&to=2026-06-30
      14 ms  12 q 200    25 KB /accounting/
      41 ms  12 q 200    50 KB /executive/
       9 ms   9 q 200    39 KB /executive/receivables
     299 ms  30 q 200    45 KB /executive/integrity?run=1
     320 ms  30 q 200   6909 B /executive/integrity?format=excel
      29 ms  13 q 200    38 KB /inventory/reports/valuation?as_of=2026-06-30
      52 ms  20 q 200    28 KB /inventory/
```

## Many users: load test (tests/Accountix.Load, 29 Oct 2026)

30 virtual users in one company, each with their own session. The mix was 70 % reads (registers,
statements, ledger, executive, Books Integrity) and 30 % writes (new CPVs, journals, and four "hot"
drafts that everyone approves, unapproves and edits at the same moment).
- Full year (2 runs, ~7,000 requests each, 39 requests/s), the 4× company (6,991 requests) and 4× limited to 1 CPU: **0 server errors, 0 deadlocks, 0 antiforgery failures**. At most 10–11 database connections were used, and at most 1 session waited on a lock.
- Hot drafts: 627 simultaneous actions. About half succeeded and the rest got Python's refusals ("Already approved."). No voucher was posted twice. 400+ new vouchers per run got gapless, unique numbers.
- Afterwards: the 9 data checks (deploy/checks/data_checks.sql) and Python's Books Integrity (11 checks) pass.
- p95 on the 4× company: month ledger 302 ms, trial balance 48 ms, P&L 48 ms, balance sheet 63 ms, sales tracking 275 ms.

## Final run: standalone .NET, Python removed (29 Oct 2026)

The Python app was moved out of the repository: backup at `~/Downloads/accountix_python_backup_2026-10-29.zip`, files as
moved in `~/Downloads/accountix_python_removed_2026-10-29/`. Then every test database was rebuilt by
`tools/Accountix.RefDb` (no Python), the whole solution was built (0 errors), and every test project was run with no
Python anywhere:

| Project | Passed | Skipped |
|---|---|---|
| Accounting | 25 | 0 |
| Finance | 54 | 0 |
| Executive | 67 | 0 |
| Inventory | 125 | 0 |
| Invoicing | 42 | 0 |
| Parties | 75 | 0 |
| Orders | 37 | 0 (3 golden tests fixed: SO and PO ids collide on a fresh database; ids now resolve by document kind) |
| Returns | 23 | 1 (on-demand browser server) |
| Hr | 48 | 0 |
| HrRecords | 67 | 0 |
| Payroll | 131 | 0 |
| FixedAssets | 62 | 0 |
| Fbr | 38 | 0 |
| Platform | 45 | 0 |
| SuperAdmin | 53 | 0 |
| Admin (maintenance commands) | 13 | 0 |
| Migrations (schema) | 31 | 0 |
| Accountix.Tests (whole app) | 84 | 0 |
| E2E (browser, every page × 1366/375/360/320) | 866 | 3 (known-issue theories, by design) |
| **Total** | **1,886** | **4** |

The last run of the Python suite, before its removal: 1,738 passed, 0 failed, 2 skipped (Postgres-only).
Parity with Python is kept as golden fixtures (`tests/*/Golden/*.json`, Python's answers recorded on 29 Oct);
`ACCOUNTIX_GOLDEN_PERTURB=1` makes every golden test fail, which proves they check.

## Coordinator to-do after the agents report

1. Read every `docs/modules/*.md`, re-launch or continue agents for what remains.
2. Whole-solution build + every test project (`dotnet test` at `dotnet/`).
3. Integration run: the full .NET host against a fresh reference DB; crawl every
   page, signed in as admin, manager and employee, at 360px and desktop.
4. Parity sweep: the same documents posted by Python and .NET produce identical rows.
5. Cut-over plan: reverse-proxy routes per module (Vercel rewrites or Azure Front
   Door), then standalone sign-in, then retire Python.

## How to rebuild the test database

`dotnet run --project dotnet/tools/Accountix.RefDb` (any Postgres 16+: `ACCOUNTIX_TEST_PG`, or the server already
named in `tools/reference.json`) loads `tests/fixtures/reference.sql.gz` into `accountix_ref` and every module copy
and writes `dotnet/tools/reference.json`. No Python and no Postgres client tools. `--only <key>` rebuilds one copy,
`--prefix t_ --out <file>` builds a side-by-side set. See README.md, "Run the tests".
