# Executive: status

**Ported**: every route of `executive_app/routes/reports.py`, all of `shared/executive_reports.py`
(scope, registers, party ledger, FIFO aging, party aging, liquidity, profitability + chart geometry,
settings) and every check of `shared/integrity.py`, with their exports. Project
`src/Modules/Accountix.Executive`, tests `tests/Accountix.Executive.Tests` (DB key `executive`).

| Python route | .NET | Status |
|---|---|---|
| `GET /executive/` (dashboard: profitability + 12-month chart, position, liquidity, aging profile, largest exposures; `?as_of=`) | `ExecutiveController.Dashboard` | ✅ |
| `GET /executive/receivables`, `/executive/payables` (`q`, `group`, `as_of`; `format=excel\|pdf\|csv`: aged register with FIFO buckets) | `Receivables`, `Payables` | ✅ |
| `GET /executive/party/<int:account_id>` (`as_of`; `format=`: every posting, running balance) | `Party` | ✅ |
| `GET/POST /executive/settings` (tree picker, replace-not-merge selection) | `Settings`, `SaveSettings` | ✅ |
| `GET /executive/integrity` (`run=1`; `format=excel` with the re-costings sheet, `pdf`, `csv`) | `Integrity` | ✅ |

- Rights as `shared/rights_guard.py`: every page `executive_reports:view`; settings POST `executive_reports:edit`
  (plus the route's own view check, flash + `/hub`, as Python's `deny_page`).
- Sidebar `NAV["executive"]` in `ExecutiveModule.Navigation` (Settings gated to admin or manager, every link hidden without view).
- Every report action runs in one REPEATABLE READ snapshot (`Services/ConsistentSnapshotAttribute.cs`, Python's
  `consistent_snapshot`); settings are not wrapped, as in Python.
- Views port the Jinja templates one to one (ids, classes, inline script and `_style.html` copied verbatim,
  `@` → `@@`); the settings tree's data goes into the script with `Html.Raw(JsonSerializer.Serialize(...))`.

## Code

- `Services/ExecutiveReports.cs`: `shared/executive_reports.py`. The chart is read once per request (code order);
  balances come from `gl_daily_balances`; FIFO aging reads ONE grouped query (account × day) for every party in
  scope; profitability reads ONE grouped query (account × month) for the 13 months (Python runs 13).
- `Services/ProfitChart.cs`: `profit_chart` / `_nice_step`, in doubles, printed with Python's `repr`.
- `Services/BooksIntegrity.cs`: the 11 checks of `shared/integrity.py`, read-only, set-based: posted lines are
  summed once by (account, label) and that one read gives the trial balance, every `_gl_balance` and the
  reporting-table comparison; stock by (product, type) once; documents with an `EXISTS` for their journal
  (7 queries, no per-document query); payroll / loans / PF one grouped query each (Python ran 1–2 per run and
  per loan). A savepoint before each check, so one that fails ("Could not run: …") cannot abort the snapshot.
- `Services/Py.cs`: Python's number semantics where figures pass through floats: `float(Decimal)`,
  `Decimal(str(float))`, exact `round(x, n)`, `repr(float)`, `{:,.2f}`, `str(Decimal.normalize())`, the
  `Quantity` column type. Checked against the Python interpreter's answers for 300 values (Golden/UnitRepr.json).

## Fixed in both apps (29 Oct, coordinator's follow-up)

Found while porting; fixed in the Python app too, so the two render the same:
1. **Register and party labels.** `register.html` / `party.html` read `meta`, which the app shell's own
   `{% set meta = nav_meta(module_key) %}` hid: blank page title, "Total " with no noun, empty "owes us",
   blank `data-label`, "Nothing " in the empty state, a blank total label and "← Back to " on the party page.
   The route now passes `side_meta` (`executive_app/routes/reports.py`, both templates).
2. **Settings "1 of NaN selected".** The counter script reads `data-total` from `.exr-section-head`; only the count
   span carried it. The head button now carries it too (`settings.html`; script unchanged).
3. **320px.** `.exr-charts` had a 340px minimum column (page 360px wide on a 320px phone) and an opened "Table view"
   pushed it to 409px. Now `minmax(min(340px,100%),1fr)`, `.exr-chart{min-width:0}`, `.exr-table-view{overflow-x:auto}`
   (`_style.html`; the .NET `_Style.cshtml` is regenerated from it, byte for byte).
4. **Company clock.** With no as-of date, aging (`_aged_parties`) and profitability ran to the server's UTC date
   (`datetime.utcnow()`), yesterday in Lahore until 05:00. Both apps now use the company's today
   (`shared/clock.local_today` / `CompanyClock.Today`), as every other report since 13 Oct.

Python E2E (`E2E_PORT=5065 … -m pytest tests/e2e/test_executive_reports.py tests/e2e/test_executive_mobile.py`):
**38 passed**. New: `test_register_and_party_pages_name_their_side`,
`test_settings_section_head_carries_the_total_its_script_reads`, `test_aging_and_profitability_default_to_the_company_clock`
(UTC+14 vs UTC-11 company zones: ages and the chart's month follow the zone), and `tests/e2e/test_executive_mobile.py`
(browser at 320px: every executive page, configured, with the table views opened, via `CLIPPED_JS`; the settings
counter reads "N of M selected"). The 320px test fails on the old CSS ("page is 360px wide on a 320px phone").
The existing aging tests now post relative to the company clock (`local_now()`), since ages run to the company's day.

## Differences from Python (deliberate)

1. **Selection save.** Account ids that are not in this company's chart are ignored (Python would insert them and
   fail on the foreign key, or point at another company's account; the flash still counts what was posted).
   Two .NET saves at once queue on `serialize("exec_selection", company)` so the last one wins whole
   (Python takes no lock; a Python save racing a .NET one can still interleave, as two Python saves can).
2. **Ties.** Where Python's order is undefined (rows of equal amount, journal lines of one entry), .NET orders by
   id, so results are deterministic.

## Finance's earlier copy

Before the split, the Finance project carried its own executive port. Compared with this project: same design, no
integrity checks; nothing needed copying. The coordinator has removed it (RouteTableTests pass in the whole-app tests).
This project's tests still register Finance's `ReportExport` directly (`ExportPipelineForTests`) rather than loading
Finance's assembly as a module.

## Tests (`tests/Accountix.Executive.Tests`, `ACCOUNTIX_TEST_DB=executive`): **67 passed, 0 failed, 0 skipped**, no Python

Independent of the Python app since 29 Oct (the parity tests became fixed expectations; nothing runs Python).

**Data.** The golden tests read their own company, `golden-exec` ("Golden Executive"): made as the portal makes
one and provisioned by Platform's `CompanySetup` (`World.CompanyAsync`, owner_b its admin, found again by slug), then
seeded once through .NET only (`Exec.SeedAsync`): customers C1..C7 and suppliers S1..S4 through `Ledger`
(entity accounts 9001.., posting roles) — invoices, receipts, an advance before its invoice (C3: 350 then 1,000), a
party that flips side, a settled party, a posting after the as-of day, a reversed (unposted) posting, a sales return,
P&L activity over 13 months — and opening stock of two products through Inventory's `StockEntry` (stock ledger,
layers, on-hand, journal), so liquidity has inventory and the stock checks have rows. Nothing else writes there.
Rights tests and the races use reference company B (the races post vouchers).

**Clock.** Figures without an as-of date run to a fixed instant, 29 Oct 2026 12:00 Lahore (`Exec.Now`): `Exec.Reports`
gets it, and every test host of this project runs the company clock at it (`FixedClockForTests` re-registers
`CompanyClock`; sign-in keeps the real clock). The captures stopped Python's `local_now` / `local_today` at the same instant.

**Golden (Golden/*.json, captured from the Python app on 29 Oct by `Golden/capture/*.py`; accounts by code, links
`/executive/party/<id>` and `group=<id>` as `{code}`, product ids in findings as `{sku}`):**

| Test | Fixture | What Python's answer was |
|---|---|---|
| `Aging_is_what_python_ages` (4 dates incl. none) | `Aging` | `er.aging()`: totals, parties, avg/oldest days and party, buckets (amount, share, width), top 5, peak |
| `Party_aging_and_registers_are_what_python_gives` (4) | `PartyAging` | `party_aging()`, `party_rows()` (all; receivable search), `groups()`, `both_sides()`; rows by code (Python's order of equal amounts is open), amounts in order |
| `One_group_filter_and_a_parent_without_its_children_match_python`, `Settings_replace_the_selection_and_python_would_read_the_same_scope` | `Scope` | `scope()` for three selections, `party_rows(group_id=)` |
| `Liquidity_is_what_python_derives` (none, 30 Jun 2026, 30 Sep 2025) | `Liquidity` | `liquidity()` with stock: net assets, current/quick ratio (none where liabilities are 0) |
| `Profitability_and_its_chart_are_what_python_draws` (3) | `Profitability` | `profitability()` months/totals/prior/deltas/peak/trough/margin, `profit_chart()` down to the SVG `points` / `area` text |
| `Party_ledger_lists_what_python_lists` | `Ledger` | `account_ledger()` whole, and as of with a limit |
| `Every_integrity_check_gives_what_python_gives` | `Integrity` | `integrity.run_all()`: every key, label, ok, expected, actual, detail |
| `Failing_checks_say_what_python_says` | `IntegrityBroken` | `run_all()` on the same books broken on purpose (debit line +12.5, reporting row +1, on-hand +2.5, layer value +7.25; restored) |
| `Pages_show_what_the_python_pages_show` | `Pages` | Python's dashboard (with and without as-of), registers, party ledger, integrity (run and not): texts of every class, every SVG/bar/link attribute, titles |
| `Register_party_and_integrity_csv_files_carry_what_python_writes` | `Exports` | the six CSV files (file names and cells) |
| `On_a_full_year_of_books_every_check_matches_python`, `On_a_full_year_aging_registers_and_liquidity_match_python` | `FullYear` | the same on the full-year Samba books (`accountix_ref_perf`, read only) |
| `UnitTests.Repr_and_round_agree_with_python_on_chart_like_values` | `UnitRepr` | `repr(v)`, `repr(round(v, 2))`, `round(v)` for 300 values (inputs stored as repr text) |

One deliberate difference is normalised: the reporting-table finding names the .NET repair command
(`dotnet Accountix.Web.dll admin rebuild-gl-summary --apply`) where Python named `tools/rebuild_gl_summary.py`.
Each golden test passes as is, passes with `ACCOUNTIX_GOLDEN_PERTURB=1` (every comparison also run on a perturbed
.NET answer must fail), and failed when a .NET figure was changed by hand (total +0.01, oldest day +1, ratio, margin,
ledger debit, check value, a scope account dropped, a page amount, a CSV cell, a rounding) — 22 of 22 golden cases.

**Dropped, and why:**
- `Books_integrity_in_dotnet_passes_while_the_python_app_posts` — a race between the two apps; there is one app now.
  The same-app race (`Books_integrity_still_passes_while_six_people_post_vouchers`) stays.
- `ClockTests.Both_apps_age_to_the_same_company_day` — "both apps, now"; the fixed-instant clock test pins the same
  values in .NET and the goldens without an as-of date carry Python's answer at a fixed instant.
- Company 1 ("Default", demo data) cases of liquidity and integrity: replaced by the golden company, which now
  carries stock of its own (the demo data is not this project's to rely on).
- The two page-parity tests (dashboard, registers) are one golden test now.

**Kept, .NET only:** FIFO ages as documented (C1 600 at 45 days, C3 650 at 20 days, C4 on the payable side, C6 not
there yet); company clock at fixed instants (1 Jul 01:30 vs 30 Jun 23:00 Lahore); ≤ 40 queries on every page; no
selection → "Select accounts" and the party redirect; settings save replaces the selection; script data has no HTML
entities; module off → 403; view right withdrawn → refused; view without edit → read-only, save refused; sidebar
links; Excel/PDF files open with their sheets and headings; unknown format → 404; Books Integrity run 9 times while 6
people approve vouchers (every run passes); 6 admins saving the selection at once leave one whole selection; unit
tests (FIFO, buckets, `repr` / `round` / `normalize` / `{:,.2f}`, `_delta`, `_nice_step`, dates). Speed tests (below)
measure .NET only.

Run: `ACCOUNTIX_TEST_DB=executive dotnet test tests/Accountix.Executive.Tests --artifacts-path <art>`.
While Finance is mid-edit and does not compile, add `-p:NoFinance=true` (the export tests drop out; everything else runs).

## Speed (full-year Samba: 26,833 journal lines, 11,830 reporting rows; fastest of 3)

Samba's selection is saved on the speed-test database as Settings saves it (`er.set_selection`, Trade Debtors
`1-01-02-01` + Trade Creditors `2-01-01-01` with sub-accounts, created_by 1; rows left in place for the other engineers
and the load test): 132 accounts in scope, 110 receivable and 12 payable parties. Server time in the test host; the
machine's load average was 12–25 (16 engineers). The Python column is history: measured on 29 Oct through its Flask
test client on the same database, same method, before the Python app was retired; the tests now time .NET only.

| Page | Budget | .NET | Queries | Python |
|---|---|---|---|---|
| `/executive/` (dashboard, full scope) | 1,500 | 70 ms | 13 | 360 ms |
| `/executive/?as_of=2026-06-30` | 1,500 | 66 ms | 12 | 330 ms |
| `/executive/receivables` (110 parties) | 1,500 | 16 ms | 9 | 112 ms |
| `/executive/receivables?as_of=2026-06-30` | 1,500 | 16 ms | 9 | 106 ms |
| `/executive/payables` | 1,500 | 15 ms | 9 | 116 ms |
| `/executive/party/<biggest customer>` | 800 | 17 ms | 10 | 111 ms |
| `/executive/settings` | 800 | 14 ms | 8 | 81 ms |
| `/executive/integrity` (not run) | 800 | 9 ms | 6 | 56 ms |
| `/executive/integrity?run=1` (all 11 checks) | 1,500 | 136 ms | 28 | 682 ms |

| Export | Budget | Excel | PDF | CSV | Queries | Python (Excel / PDF / CSV) |
|---|---|---|---|---|---|---|
| Aged receivables (110 parties, FIFO buckets) | 3,000 | 125 ms | 185 ms | 47 ms | 11 | 236 / 315 / 158 ms |
| Aged payables, as of year end | 3,000 | 57 ms | 65 ms | 44 ms | 11 | 176 / 168 / 148 ms |
| Party ledger, biggest customer | 3,000 | 34 ms | 47 ms | 18 ms | 11 | 99 / 126 / 60 ms |
| Books Integrity | 3,000 | 139 ms | 149 ms | 156 ms | 29 | 677 / 608 / 675 ms |

Under heavier load (average ~47) the same pages took about 2× these numbers, still within budget; one loaded run
put the receivables PDF at 392 ms (Python 315 ms quiet), re-measured at 185–189 ms twice.

The figures behind the pages, each one query-bounded (`Each_figure_is_a_fixed_handful_of_queries_with_every_party_in_scope`):
aging 30 ms / 3 queries (Python 84 ms), dashboard figures 56 ms / 7 (Python 286 ms), receivables register 8 ms / 3
(21 ms), party aging 26 ms / 3 (87 ms), party ledger 1 ms / 1 (5 ms).

### Proposed indexes

None needed. EXPLAIN ANALYZE on the speed-test books: party ledger uses `ix_journal_lines_account_entry`, aging and
balances `ix_gl_daily_balances_account_day` / `uq_gl_daily_balances_key`, the documents check
`ix_journal_entries_voucher`. The only sequential scans are the whole-ledger aggregates Books Integrity needs by
definition (posted lines by account/label: 38 ms for 26,833 lines; by journal for the balance check).

## Many users

Read-only module apart from the selection: every report is one REPEATABLE READ snapshot, so a posting landing
mid-report cannot make Books Integrity disagree with itself (tested with six people posting at once; the race
against the Python app posting was dropped with the Python app). The selection save
serializes on `("exec_selection", company_id)`.

## Needs from others

Nothing.

## Python files changed (29 Oct follow-up)

`executive_app/routes/reports.py`, `executive_app/templates/executive/{register,party,settings,_style}.html`,
`shared/executive_reports.py` (company clock), `tests/e2e/test_executive_reports.py`, `tests/e2e/test_executive_mobile.py` (new).

## Core / Web.Shared changes

None.
