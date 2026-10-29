# Payroll, Provident Fund and staff loans: status

**Ported: all 30 routes** of `hr_app/routes/compensation.py` (19) and `hr_app/routes/pf.py` (11), the payroll
engine (`hr_app/payroll_engine.py`), the cash-side postings (`hr_app/payroll_posting.py`), and the 14 templates
of `hr_app/templates/compensation/*` and `pf/*`. Same URLs, ids and classes; every page fits a 320px screen.

## Routes

| Route | Methods | Status |
|---|---|---|
| `/compensation/` (admin: profiles, history, slabs; employee: own profile and slips) | GET | ✅ |
| `/compensation/tax-settings` (slabs add/delete, **Load tax year 2026 slabs**, payroll settings) | GET, POST | ✅ |
| `/compensation/create-profile` | GET, POST | ✅ |
| `/compensation/edit-profile/<uid>` (components, dated salary revision) | GET, POST | ✅ |
| `/compensation/run-payroll` (preview, run, Replace All, per-employee adjustments) | GET, POST | ✅ |
| `/compensation/payroll-preview-json` | POST | ✅ |
| `/compensation/download-template` (bulk sheet, this company's staff) | GET | ✅ |
| `/compensation/upload-bulk-data` (.xlsx / .csv / JSON) | POST | ✅ |
| `/compensation/run/<rid>` (run detail, accounting-entry panel) | GET | ✅ |
| `/compensation/run/<rid>/pay`, `/unpay` (PRL-PAY) | POST | ✅ |
| `/compensation/run/<rid>/delete` | POST | ✅ |
| `/compensation/loans` (staff loans admin) | GET | ✅ |
| `/compensation/loans/<lid>/disburse`, `/undo-disbursement` (LOAN-DSB) | POST | ✅ |
| `/compensation/slip/<sid>`, `/slip/<sid>/pdf` | GET | ✅ |
| `/compensation/export-bank-ledger/<rid>` (bank sheet) | GET | ✅ |
| `/compensation/revisions` | GET | ✅ |
| `/pf/` (admin: contributions, pending requests, profit, settlement; employee: own fund) | GET | ✅ |
| `/pf/config` | GET, POST | ✅ |
| `/pf/request-withdrawal`, `/pf/request-loan` | POST | ✅ |
| `/pf/compliance-check/<lid>` | GET (JSON) | ✅ |
| `/pf/approve-withdrawal/<wid>` (PF-WD), `/pf/approve-loan/<lid>` (PF-LOAN) | POST | ✅ |
| `/pf/distribute-profit` (PF-PROFIT) | POST | ✅ |
| `/pf/settle/<uid>` (PF-SETTLE) | POST | ✅ |
| `/pf/button-permissions` | POST (JSON) | ✅ |
| `/pf/add-ledger-entry` (PF-ADJ) | POST (JSON) | ✅ |

## How it is built (`src/Modules/Accountix.Payroll`)

- `Services/Py.cs`: **Python's float semantics, exactly.** The Python engine computes in binary floats and
  rounds with `round(x, 2)`; journals take `Decimal(str(x))`. The port does the same steps on doubles with
  Python's correctly-rounded half-even `round`, `repr`, `sum()` (Neumaier-compensated since 3.12, the venv's
  version), `f"{x:,.0f}"`, `format_amount`, `str.title()` and `json.dumps` (for `components_json`, written to
  the character). This is why every slip and journal agrees with Python to the paisa.
- `Services/PayrollMath.cs`: `payroll_engine.py` as pure functions (component amounts, slabs, surcharge,
  cumulative withholding, tax-year months, year-to-date, calendar-day proration with unpaid leave, joiners and
  leavers, overtime policy, EOBI, PF base, service years, gratuity and leave encashment, dated salary revisions).
- `Services/PayrollData.cs`: **set-based** loading of one payroll month for the whole company: one query each for
  profiles (with users), components, revisions, memberships, unpaid leave, overtime, loans, PF loans,
  year-to-date slips, leave quotas, settings, slabs and PF config. Python queried per employee.
- `Services/PayrollCalculator.cs`: `_preview_employee` (adjustments, override rules, loan cap from what is left).
- `Services/PayrollRunner.cs`: `run_payroll` (POST) and `_undo_run`; `Services/PayrollPosting.cs`:
  `payroll_posting.py`. Journals go through the real `ILedger` (Accounting); accounts are fetched in Python's
  order, so accounts a run creates get the same codes in both apps.
- `Services/PayrollExports.cs`: bank sheet and bulk template (ClosedXML) and the salary-slip PDF (PDFsharp,
  pinned **6.2.4** like Finance; Liberation Sans embedded, same face names as Finance's resolver). These three
  files are not report tables in Python either (openpyxl / reportlab layouts banks and staff rely on), so they
  keep Python's own layout instead of going through `IReportExport`; they use the same libraries and versions.
- Controllers: `CompensationController` (+ `.Run.cs`), `PfController`. Views under `Areas/Payroll/Views`.

**Rights.** As in Python, these pages are governed by role in the routes: admin of THIS company (or super
admin), or the slip's own employee; refusals use Python's words and targets ("Access denied." → `/dashboard`,
JSON 403 for the JSON routes). The Python compensation and PF routes call no `can()`: the payroll / loans / pf
sections are enforced where Python enforces them (Accounting's `/accounting/gl-entry` for PRL / PRL-PAY /
LOAN-DSB / PF-*, HR's MSS and reports). Every action carries `[AnyMember]` under `[Module(Modules.Hr)]`.
PF button permissions follow the role held in this company (`Membership.RoleId`, the 29 Oct fix).

**Sidebar.** `PayrollModule` (key `hr`) adds the **Financial** group (Compensation, Provident Fund) and the
Administration items PF Config (Order 80), Tax & Payroll Settings (90) and Staff Loans (100), slotting into HR's
group in `shared/navigation.py`'s order; active/exclude rules as Python's.

**Locks** (same keys as Python, so the two apps queue together): `serialize("payroll", company)` for run /
replace / pay / unpay / delete; `serialize("pf", company)` for withdrawals, PF loans, profit, settlement;
`serialize("first-use", "payroll_settings", company)` for the settings row; FOR NO KEY UPDATE on the run,
the staff loan, the withdrawal and the PF loan before their state changes.

## Tests (`tests/Accountix.Payroll.Tests`, `ACCOUNTIX_TEST_DB=payroll`; no Python needed)

Integration tests build fresh companies through .NET (`World.CreateAsync`: the admin and staff as HR +
Accounting logins, the company with Python's column defaults, memberships through Platform's `Memberships`
(module switches from the account, as Python's insert hook), provisioned by Platform's `CompanySetup`), so they
repeat. A Python-made and a .NET-made world were compared row by row on 29 Oct: every provisioned table equal
(chart, periods, numbers, settings, templates), users and memberships equal apart from the tagged codes. Since
E2E finding F6 .NET provisioning also seeds the CL / SL / AL leave types (Python's provisioning seeded none);
`World.LeaveTypeAsync` reuses AL and checks it is paid and carried forward, the two flags payroll reads. After
each step that posts, **Books Integrity** (Executive's port of `shared/integrity.py`) must report no failure.

| File | What | Count |
|---|---|---|
| `PyTests` | round, repr, Decimal(str), Neumaier sum, formatting, json.dumps, title, float parsing | 36 |
| `PayrollMathTests` (+ `LedgerCodeTests`) | every engine rule, unit-level (slabs, cumulative tax, bonus once, YTD, surcharge, proration, leavers, overtime, EOBI, PF base, service years, settlement, raises, components, loan due); loan account codes = the ledger's entity codes | 23 + 3 |
| `PayrollEngineTests` | `tests/e2e/test_payroll_engine.py`, all 15 steps, through the .NET routes (concrete figures) | 15 |
| `PayrollRunTests` | `test_payroll_run.py` (10 of 12; the ESS loan form and the 0007 migration are HR / Alembic) + replace, employee refusals | 12 |
| `PfFlowTests` | `test_pf_flow.py` (8 of 9; the 0006 migration is Alembic) + admin page, manual ledger entry | 10 |
| `PayrollLedgerSanityTests` | the payroll half of `test_payroll_fa_ledger_sanity.py` (p01–p09) + undo payment; loan approval and the raise go through **HR's .NET MSS routes** (HR booted with Payroll) | 10 |
| `ParityTests` | **golden** (below) | 7 |
| `ManyUsersTests` | same-app races (below) | 4 |
| `SpeedTests` | 200 staff (below) | 4 |
| `SharedAccountTests` | one company, four payroll months (leavers in July and August) and two profit distributions: one account per parked name, the same account ids in every journal | 1 |
| `PageTests` | every admin page renders in ≤ 20 queries with intact scripts (no `&quot;`/`&amp;` in `<script>`), sidebar order and active item, employee view and refusals, template → upload round trip (.xlsx and .csv), PF JSON answers | 5 |

**Golden (`ParityTests`).** The parity company (proration, joiner, leaver with gratuity + leave encashment, raise
mid-month, unpaid leave across months, overtime, staff loan with an instalment override, PF loan, bonus with a
comma, tax override, custom deduction, `salary_advance` title) is built by .NET and taken through steps S01–S07
(settings; July run; August preview; August run; salary payment + Replace All; PF withdrawal / profit /
settlement / manual entry; loan pay-out + delete run). After each step its rows are compared with
`Golden/payroll_parity.json`: **the Python app's answer for the same company and steps**, recorded on 29 Oct by
`Golden/capture/payroll_parity.py` (it builds the twin through the Python app, inserts the same seed rows column
for column, runs the steps through the Flask test client). Both sides are read with the same SQL
(`Golden/payroll_parity_views.json`, every column as Postgres text): slabs and payroll settings, slips with
`components_json` text, run totals, every journal line, PF ledger, contributions, loan repayments, loans, PF loans,
notifications, the PF settlement row; plus every figure of the preview JSON (breakdowns, loans, components) and
each route's status code. One normaliser treats both: people as positions (0 the admin), the admin's tagged name,
loan / PF loan ids, generated numbers (PF-WD-N, PF-PROFIT-yyyymm-N, PF-SETTLE-N, PF-ADJ-N, LOAN-DSB-N), entity
account codes, and the company's date of the run (documents dated "today": PF profit, manual PF entry) as TODAY.
Checked both ways: passes as is; a paisa on one basic salary fails S03–S07, and a digit changed in .NET's last row
of any view (or the preview's bonus) fails all seven. To recapture (needs the Python app, so only from the backup
zip): the command is in the script's header; it writes a new company into the payroll copy and rewrites the JSON.

**Dropped with the Python app (29 Oct):**
- the live Python side of `ParityTests` (two companies, one run by each app): replaced by the golden fixture above;
- `ManyUsersTests.Python_and_dotnet_running_the_same_month_at_the_same_instant_run_it_once` (the cross-app race):
  there is no second app; the same-app six-at-once races stay;
- `SpeedTests.S05_python_on_the_same_data_for_comparison` (live Python timings): the numbers it recorded stay in
  the Speed table below as history;
- `SharedAccountTests`' turns between the apps (Python ran July, September and the second distribution): .NET now
  runs all four months and both distributions in one company, which still proves each parked account is found again;
- Python's `integrity.failures()` after posting steps: Executive's `BooksIntegrity` (same checks) instead;
- the Python MSS routes in the sanity tests: HR's .NET routes instead.

**Result (29 Oct, `ACCOUNTIX_PYTHON` unset): 131 passed, 0 failed, 0 skipped** (about 1 min; was 8 min with the
Python worlds), three times: on the used payroll copy, on a fresh one (`tools/Accountix.RefDb --only payroll`), and
from a copy of `dotnet/` alone (no Python app above it). Nothing in the project names the Python app or its
interpreter; the only Python is the capture script, kept for the record.

## Speed (200 employees on the payroll copy; `SpeedTests`, warm, test host; machine load ~30)

The speed-test database (Samba) holds no payroll, so payroll is timed on a seeded company of 200 staff
(profiles with three components, raises, unpaid leave, overtime, loans, PF loans, EOBI, slabs). The Python column
is history: it was timed on an identical twin (Flask test client, same machine) until the Python app was removed.

| Page | .NET (full run; range over runs) | Queries | Python, same data |
|---|---|---|---|
| `GET /compensation/run-payroll` (preview of 200) | 52 ms (43–144) | 19 | 11,330 ms |
| `POST /compensation/payroll-preview-json` | 59 ms (59–184) | 19 | 8,412 ms |
| `POST /compensation/run-payroll` (200 slips, September, reads August's YTD) | **392 ms** (392–1,950 under load) | 84 | 14,404 ms |
| Replace All (undo 200 slips + run again) | 468 ms (468–1,829) | 102 | — |
| `POST /compensation/run/<id>/delete` (200 slips) | 73 ms | 32 | — |
| `GET /compensation/run/<id>` | 15 ms | 12 | 187 ms |
| `GET /compensation/export-bank-ledger/<id>` | 52 ms | 10 | 896 ms |
| `GET /compensation/` | 30 ms | 9 | 1,891 ms |
| `GET /compensation/download-template` | 45 ms | 9 | — |
| `GET /pf/` | 16 ms | 15 | 1,292 ms |
| `GET /compensation/loans`, `/revisions` | 8 ms, 7 ms | 9, 7 | — |
| `GET /compensation/slip/<id>`, `/pdf` | 7 ms, 10 ms | 10, 11 | — |

Budgets asserted: pages ≤ 800 ms and ≤ 40 queries, files ≤ 3 s, the preview ≤ 20 queries, the run ≤ 2 s and
≤ 100 queries with no statement repeated per employee. The first run of a fresh process (JIT, query
compilation, accounts created) took 745 ms–9 s and is excluded, as `MeasureAsync` excludes its warm-up.

No statement runs once per employee: the run's most repeated statement is the account lookups the ledger makes
(fixed, whatever the staff). The one N+1 the counter found (two chart lookups per recovered loan in
`EntityAccountAsync`) is now one batched query. **No index is needed** (all payroll tables are read by company
and small id lists).

## Many users (`ManyUsersTests`)

- Six admins running the same month at once: one run, one PRL journal, books tie.
- Six "Record payment" clicks: one PRL-PAY journal, Salary Payable cleared once, one audit row.
- Six different withdrawals of 3,000 approved at once against a 10,000 balance: three approved, balance 1,000,
  three PF-WD journals (the `pf` queue).
- One staff loan paid out by six admins at once: one LOAN-DSB journal, one notification.
- (Until 29 Oct also Python (3 threads) and .NET (3 requests) running the same month at the same instant: one
  run, one PRL journal, no 500 in either app. Dropped with the Python app; the locks keep Python's keys.)

## Differences from Python (deliberate)

- The salary slip's employee-details table scrolls inside its own box: in Python it pushed the page sideways at
  320px (the only payroll page that did).
- Unreadable `adjustments` / `replace_ids` JSON, a non-object salary component, and `/pf/button-permissions`
  without a role are refused with a message (Python answered 500).
- Where Python relied on Postgres heap order (`query.all()` with no ORDER BY), the port orders by id. Totals are
  rounded, so figures are unaffected; only slip insertion order could differ.
- The run creates the payroll-settings row up front (Python created it while previewing the first employee; the
  only difference is a month with nobody in payroll).
- The PF admin page does not load the lists the template never shows (profits, settlements, button permissions).

## Findings for the coordinator

1. **Fixed 29 Oct (coordinator, both apps):** codes 2125 (EOBI Payable), 5123 (EOBI Employer Expense), 5124
   (Gratuity Expense), 5125 (Leave Encashment Expense) and 5126 (PF Profit Credited to Members) have no fixed
   slot, and `get_or_create_account` never found them again, so every run and every profit distribution made a
   new set. Python's `get_or_create_account` and .NET's `Ledger.GetOrCreateAccountAsync` now find a parked
   legacy account by (catch-all parent, name, type) under `serialize("legacy-account", parent.id, name)`;
   `dotnet Accountix.Web.dll admin merge-legacy-accounts --apply` merges existing duplicates. Payroll needed no change.
   `SharedAccountTests` proved it across the apps in one company (Python ran July and September, .NET August
   and October, profit shared once by each); since the Python app was removed it runs all four months and both
   distributions in .NET. Each account name exists once, and every journal posts to the same account ids.
2. `Accountix.Web.Shared` was not touched: the Order-based merge of shared sidebar groups was already there.

## Needs from other modules

None blocking. The leaving date and the date of joining are edited on HR's user page; loan approval and
raises come from HR's MSS (the sanity tests drive HR's .NET MSS routes for them).
