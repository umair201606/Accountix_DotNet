# Finance: status

**Ported**: every route of `finance_app/routes/reports.py` (prefix `/finance`), the 13-week cash flow
(`shared/twcf_reports.py`, `models/twcf.py`), NAV["finance"], and the report export pipeline
(`shared/report_export.py`) that every module uses.

Executive moved to its own engineer (`src/Modules/Accountix.Executive`). The copies this project had were
deleted on the coordinator's request (29 Oct): `Controllers/ExecutiveController.cs`, `Areas/Executive/**`,
`Services/ExecutiveReports.cs`, `Services/BooksIntegrity.cs`, the `ExecutiveModule` class, and the tests
`ExecutiveTests.cs`, `BooksIntegrityTests.cs` and `ExecutiveSeed.cs`. Accountix.Executive.Tests uses only `ReportExport`.

| Python route | .NET | Status |
|---|---|---|
| `GET /finance/` dashboard | `ReportsController.Dashboard` | ✅ |
| `GET /finance/ledger` (all / select: custom accounts or heads; labels; `?format=excel\|pdf`) | `Ledger` | ✅ |
| `GET /finance/trial-balance` (opening → movement → closing, class subtotals, comparative; excel/pdf) | `TrialBalance` | ✅ |
| `GET /finance/profit-loss` (structure from Report Settings, "Others (N accounts)", comparative; excel/pdf) | `ProfitLoss` | ✅ |
| `GET /finance/label-pl` (a column per label + Unlabelled; excel/pdf/csv) | `LabelPl` | ✅ |
| `GET /finance/balance-sheet` (net income in equity, comparative merged by code; excel/pdf) | `BalanceSheet` | ✅ |
| `GET /finance/socie` (IFRS roll-forward matrix, comparative periods chained; excel/pdf) | `Socie` | ✅ |
| `GET /finance/cash-flow` (indirect or direct per Report Settings, comparative; excel/pdf) | `CashFlow` | ✅ |
| `GET /finance/twcf` (bare open → load prompt; `?start=`, `?edit=`, `?format=excel\|pdf`) | `TwcfController.Index` | ✅ |
| `POST /finance/twcf` (`action=add\|edit\|delete`) | `TwcfController.Post` | ✅ |

- **Same behaviour as Python:** URLs and query names (`filter_mode`, `period_id`, `from`, `to`, `comp_mode`,
  `comp_period_ids`, `label_ids`, `format`, `mode`, `selection_mode`, `account_ids`, `head_ids`, `label_id`, `run`, `start`, `edit`),
  `_resolve_period` (tested against Python on 8 query strings), empty states until View (an explicit export always runs),
  the export file names (`Company_Trial_Balance_2026-01-01_to_2026-12-31.xlsx`, `..._as_at_...`), the hand-built
  workbooks (P&L, balance sheet, cash flow, the ledger's one sheet per account with a linked Summary sheet) and the
  PDF row kinds.
- **Rights:** `[Right("financial_reports", "view")]` on every statement. TWCF follows `rights_guard._twcf`: add needs create,
  delete needs delete, any other POST needs edit. The sidebar hides links the person may not open.
- **Snapshot:** `[ConsistentSnapshot]` (Services/FinanceData.cs) puts each statement in one REPEATABLE READ transaction, as
  `consistent_snapshot`. TWCF has none, as in Python.
- **Views:** the eight templates were ported one to one, with the same ids, classes and inline scripts, and the style
  blocks copied verbatim. The finance layout CSS (Tailwind + Font Awesome + statement styles) is in
  `Areas/Finance/Views/Shared/_FinanceHead.cshtml`. The period filter is the shared `_PeriodFilter`.

## Deliberate differences

- **The letterhead prints the company name.** Python printed `company.company_name` from the multi-company `Company`
  row, which has no such column, so it was always blank (as in Accounting).
- **Bare open shows "Single Period" selected.** The shared `_PeriodFilter` does this. Python's empty state showed no mode
  selected and hid the period select until clicked.
- **P&L by Label reads once, not twice per label.** Every label column comes from one grouped read by
  (label, account), where Python ran two queries per label. The figures are the same (tested).
- **Ties are ordered by account id, then code.** This applies where Python's order depends on the database's unordered
  GROUP BY: P&L accounts with equal amounts, and accounts inside a cash-flow head.
- **Fixed in both apps (30 Oct, coordinator):**
  - The cash-flow comparative columns used Python's old expense formula (`dr - cr if cr > dr else cr - dr`, always −abs), so an expense refund cut profit in every column but the first. Now every column takes credit − debit, exactly as the period's own column. `finance_app/routes/reports.py` and `FinanceStatements.IndirectAsync` were changed.
  - TWCF's "today" (which weeks count as finished) was the UTC date. It is now the company's date: `local_today` in `shared/twcf_reports.py`, and `CompanyClock.Today` in `TwcfController`.
- **TWCF (fork) — Python would 500 on these:**
  - an unreadable `?start=` opens the current week;
  - a NaN or Infinity amount is refused;
  - an empty day field falls back to 0 or 1.
- **Exports:**
  - Python's PDFs come from reportlab and its Excel files from openpyxl. Here Excel is written by our own streaming
    writer (see Speed) or by ClosedXML for the hand-built sheets, and PDFs are drawn with PdfSharp.
  - The layout, figures, formats and file names are the same. Byte-level output differs.
  - The PDF footer is clipped to the column width, not to 110 / 90 characters.

## For other modules (public API; kept stable)

- **`IReportExport`** (Core contract) → `Services/ReportExport.cs`:
  - `ExportTableAsync(fmt, ReportTable)`: Python's `export_table`.
  - `Send(fmt, table, fileTitle, filePeriod)`: build_excel / build_pdf named as `send_export`.
  - `File(fmt, bytes, title, period)`: name a file you built yourself.
  - `BuildExcel` / `BuildPdf` / `BuildCsv` (static), `FileName`.
  - For hand-built sheets: `NewWorkbook`, `AddSheet`, `WriteHeading`, `StyleHeader`, `StyleRow(kind, negative)`,
    `Put(ws, row, col, value, bold, border)`, `FinishSheet` (formats, widths, frozen and repeated header, print setup),
    `SetupSheet`, `Save`.
  - `PyFloat(double)`: Python's `str(float)`.
- **`Services/FinanceData`** (scoped; `new FinanceData(db)` also works). All balances come from `gl_daily_balances`:
  - `ChartAsync()`: the chart in memory, with `CashFlowActivity` / `PlSection` (inherited), `CashIds()`, `L3Head`, `Subtree`;
  - `AllAccountBalancesAsync(asOf, types, labelIds)`, `PeriodMovementsAsync(from, to, types, labelIds)`,
    `SumBalancesAsync(ids, asOf, labelIds)`, `NetIncomeAsync`;
  - `PlBySectionAsync` (= `_pl_by_section`), `PlRowsAsync` (= `_pl_rows`), `PlLookupAsync`, `PlCompAmount`,
    `ParseStructure` (Report Settings P&L structure), `SettingsAsync`.
- **`Services/ReportPeriod`**:
  - `ResolveAsync(request, db, today)` = `_resolve_period` + `_resolve_labels`;
  - `ParseDate`, `ColLabel`, `PeriodLine`, `FilePeriod`, `ExportDatesAsync`, `FilterModel(loaded)`, `ExportQuery`.
- **`ConsistentSnapshotAttribute`**: put it on any report action.
- **`TwcfReports`**: static helpers `F` (= `float(Decimal)`), `Round2` (= Python's `round(x, 2)`), `PySum` (= Python 3.12's
  compensated `sum`), and `Display` (= `format_amount` for floats).
- **`FinanceStatements`**: `TrialBalanceAsync`, `ProfitLossAsync`, `LabelPlAsync`, `BalanceSheetAsync`, `SocieAsync`,
  `CashFlowAsync` (indirect and direct), `LedgerSectionsAsync`, `LeafDescendants`.

## Tests (`tests/Accountix.Finance.Tests`, `ACCOUNTIX_TEST_DB=finance`): 54 passed, 0 failed, 0 skipped, no Python

The tests build their own data through .NET only and run without the Python app (`ACCOUNTIX_PYTHON` is not read).
29 Oct, Python unset: **54 passed, 0 failed, 0 skipped** (37 statements and the rest, 17 TWCF; speed-test DB present);
the same with `ACCOUNTIX_GOLDEN_PERTURB=1`; the same twice in a row on a copy freshly rebuilt by
`dotnet run --project tools/Accountix.RefDb -- --only finance` (the first run makes the golden companies, the second
reuses them), and from a copy of `dotnet/` with no Python app above it and TestSupport's `PythonApp.cs` removed.

**Data.** The statements read a company of their own, `golden-finstmt` ("Golden Statements"), made once and found again
by its slug (`StatementSeed.EnsureAsync`):
- created as the portal creates a company and provisioned by Platform's `CompanySetup` (chart, periods FY 2021–2028,
  settings), owner_b its admin (`World.CompanyAsync`);
- the 22 `FINSTMT` journals of the old Python seed, posted through Accounting's `Ledger` (so `gl_daily_balances` follows):
  two years with labels (one archived), `pl_detail_rows = 4` so "Others" folds, dividends, OCI, a transfer out of retained
  earnings, a three-way cash receipt (direct-method weights), an expense refund. Checked once: the journal lines .NET
  posts hash equal to the ones Python's `post_journal_entry` posted for the same seed;
- a member without the `financial_reports` view right (`fin.noview.golden@ref.test`, EF inserts as the HR form writes them).
Nothing else posts there, so the figures are fixed. The race test and the TWCF clock test use company B.

**Golden fixtures** (`Golden/*.json`, compared by `Golden.Equal`, numbers to 1e-9 relative). Each is the Python app's answer
for exactly these books, captured once on 29 Oct 2026 by `Golden/capture/*.py` (kept for the record): the Python routes ran
on the .NET-built company with `render_template` recorded and "today" stopped at 29 Oct 2026; queries are stored with
symbols (`{FY 2026}`, `{Proj Alpha}`, `{head:5-02}`), accounts as codes, periods and labels as names.

| Test | Fixture | What is pinned |
|---|---|---|
| `StatementGoldenTests.Trial_balance_is_the_python_trial_balance` | `TrialBalance.json` | 5 queries (single period, labelled custom, 3-column comparative, 2-year span, one month): every row (code, name, type, subtotal, six figures, comparative closings), all six totals, comparative class totals |
| `…Profit_and_loss_is_the_python_profit_and_loss` | `ProfitLoss.json` | rows, kinds, "Others", comparative amounts, net profit |
| `…Pl_by_label_is_the_python_pl_by_label` | `LabelPl.json` | labels shown, each label's column, Unlabelled, nets (all labels, one picked, a half year) |
| `…Balance_sheet_is_the_python_balance_sheet` | `BalanceSheet.json` | sections, totals, merged comparative rows, comparative equity |
| `…Socie_is_the_python_socie` | `Socie.json` | columns (an account's column `a<id>` written `a:<code>`), rows, kinds, indents, every value including the dashes |
| `…Cash_flow_indirect_and_direct_are_the_python_cash_flow` | `CashFlow.json` | both methods × 5 queries: items, comparative maps, nets, opening / closing / movement |
| `…Ledger_sections_are_the_python_ledger` | `LedgerSections.json` | all accounts in a window, with a label, all time, a head's leaves: opening, closing, sides, totals, every line with its running balance |
| `…Period_resolution_is_the_python_resolution` | `PeriodResolution.json` | `_resolve_period` on 8 query strings (dates, selected period, modes, raw strings, comparative periods), today = 29 Oct 2026 |
| `StatementPageTests.Every_figure_on_every_statement_page_is_the_python_figure` | `StatementPages.json` `figures` | every `<td class="num">` on 20 pages (5 statements × 3 queries, ledger all / custom / head, label P&L ×2) |
| `StatementPageTests.Bare_opens_wait_for_view_as_python_did` | `StatementPages.json` `bare` | 8 pages opened without a period: their figure cells and the prompt Python showed |
| `StatementPageTests.Exports_are_files_named_as_python_named_them` | `StatementPages.json` `exports` | 27 export file names (dates that come from "today" stored as `{today}` / `{period_start}` / `{period_end}` and filled from the company's own day); the files open |
| `StatementPageTests.Without_the_view_right_every_statement_is_refused_as_python_refused` | `StatementPages.json` `no_view` | the status of 9 statement URLs for a member without the view right (Python: 302 each) |
| `StatementSpeedTests.Direct_cash_flow_for_the_year_is_quick` | `CashFlowDirectPerf.json` | the direct-method engine on the full Samba year (read only): every item, opening and closing, to 1e-12 |
| `ReportExportSpeedTests.Csv_writes_floats_as_python_str_does` | `CsvFloats.json` | `str(float)` of 21 awkward doubles |

Verified: each golden test passes as is and passes with `ACCOUNTIX_GOLDEN_PERTURB=1` (the perturbed .NET answer — first
and last figure moved — is caught); by hand, adding 0.005 to the cash flow's net change turned
`Cash_flow_indirect_and_direct_are_the_python_cash_flow` red in all 10 query/method pairs.

**Kept as they were (.NET-only, now on the golden books):** the workbook layout test, the 40-query budget on every
statement and export, the comparative cash-flow columns test (`ClockAndComparativeTests`), the TWCF Lahore-midnight
clock test (company B), the export pipeline tests (layout, extra sheets, linear growth), the many-users tests.

**Dropped:**
- the live Python calls (`PythonApp.Run`, `[PythonFact]`): replaced by the fixtures above;
- the seed through Python's `post_journal_entry`: replaced by the same journals through .NET's `Ledger`;
- the live Python timing in the speed tests (`StatementSpeedTests` asserted ".NET is not slower than Python" on every URL,
  and timed `_cash_flow_direct`): the .NET budgets stay; the Python times measured on 29 Oct stay in the Speed table below
  as history.

**TWCF** (`TwcfTests`, 17, no Python): data built through .NET only in its own company `golden-twcf` (`TwcfWorld.cs`:
15 journals posted by Accounting's `Ledger`, with entity accounts for customers 1–3 and suppliers 1–2; an approved
payroll run with its PRL journal; six forecast lines of every frequency; a view-only member `twcf.viewer@ref.test`;
existing rows reused on later runs, stray non-seed forecast lines removed before each golden test). The page tests sign
in as owner_b in that company. Fixed expectations captured from the Python app on 29 Oct on exactly that company
(scripts `Golden/capture/twcf_*.py`):

| Test | Fixture | What is pinned |
|---|---|---|
| `The_matrix_equals_python_build_matrix_cell_for_cell` | `TwcfMatrices.json` | `build_matrix` for four windows (today given), every cell exact |
| `A_cash_floor_adds_the_floor_and_headroom_rows_as_python_does` | `TwcfMatrices.json` (floor) | the same with a 2,000.50 cash floor: floor and headroom rows |
| `The_page_shows_the_same_matrix_as_the_python_page` | `TwcfPage.json` | Python's page; both pages read the same way (matrix cells and classes, KPIs, lines table) |
| `The_excel_sheet_matches_the_python_sheet` | `Twcf.xlsx`, `TwcfExports.json` | Python's file, compared cell by cell (values, formats, bold/italic, alignment, merges, freeze, widths); name and type |
| `The_pdf_is_landscape_and_named_as_python_names_it` | `TwcfExports.json` | name, type and the table Python handed its PDF builder (title, window line, headers, `format_amount` cells, row kinds) |
| `Adding_editing_and_deleting_a_line_writes_what_python_writes` | `TwcfLines.json` | add / edit / delete answers (redirect, flash) and the stored lines (day fields reset or clamped as Python did) |
| `Round2_agrees_with_python_round_on_float_sums`, `PySum_agrees_with_python_builtin_sum` | `TwcfNumbers.json` | 1,610 stored floats under `round(x, 2)` and 62 stored lists under `sum()`, exact |
| `On_a_full_year_the_matrix_equals_python_build_matrix` | `TwcfPerfMatrices.json` | `build_matrix` for three windows on the full-year Samba books (read only) |

The page and the files use a window wholly in the past (start 2026-07-06), so they do not depend on today. Rights,
refusals, the query count and the six-at-once race are .NET only. Dropped: the live Python half of the viewer-refusal
test (the .NET refusal is still asserted) and the live Python timing in `Thirteen_weeks_on_a_full_year_are_quick` (Python's
times stay in the Speed table as history). Verified with `ACCOUNTIX_GOLDEN_PERTURB=1`, by changing one forecast line's
amount in the database (matrix, floor, page, Excel and PDF tests failed) and by changing .NET figures by hand.

- **Many users** (2): see below.
- **Speed** (`[PerfFact]`, 10): see below.

## Speed (full-year Samba DB, fastest of 3, server time)

The Python column is history: measured on 29 Oct 2026 through the Python app's Flask test client on the same data, before
the Python app was retired. The tests now check the .NET budgets only.

| Page | .NET | queries | Python | budget |
|---|---|---|---|---|
| Trial balance: month / year / comparative (3 cols) | 17 / 13 / 23 ms | 12–14 | 115 / 84 / 132 ms | 800 / 1,500 / 1,500 |
| TB Excel / PDF: year, comparative | 35 / 81, 70 / 127 ms | 12–14 | 151 / 311, 313 / 443 ms | 3,000 |
| P&L: month / year / comparative | 9 / 12 / 18 ms | 12–14 | 76 / 81 / 119 ms | |
| P&L Excel / PDF: year, comparative | 18 / 18, 27 / 29 ms | | 71 / 102, 120 / 125 ms | |
| Balance sheet: month / year / comparative | 14 / 17 / 27 ms | 11–13 | 82 / 89 / 163 ms | |
| BS Excel / PDF: year, comparative | 33 / 33, 56 / 59 ms | | 99 / 157, 180 / 268 ms | |
| SOCIE: month / year / comparative | 24 / 15 / 21 ms | 12–13 | 100 / 96 / 131 ms | |
| SOCIE Excel / PDF: year, comparative | 20 / 18, 26 / 29 ms | | 89 / 80, 111 / 94 ms | |
| Cash flow (indirect): month / year / comparative | 11 / 19 / 32 ms | 15–23 | 80 / 89 / 127 ms | |
| CF Excel / PDF: year, comparative | 22 / 23, 38 / 40 ms | | 72 / 71, 114 / 110 ms | |
| Cash flow, direct method, year (engine) | 55 ms | | 704 ms | 1,500 |
| P&L by label: month / year; Excel / PDF | 16 / 12 ms; 20 / 22 ms | 11 | 119 / 157; 172 / 150 ms | |
| Finance dashboard | 4 ms | 6 | 30 ms | 800 |
| **General ledger, all accounts, busiest month** | **89 ms** | 12 | 348 ms | 1,500 |
| Ledger Excel (one sheet per account + Summary) / PDF | 949–1,329 / 312–383 ms | 12 | 2,014–2,492 / 7,911–10,932 ms | 3,000 |
| 13-week cash flow page / Excel / PDF (fork) | 19 / 30 / 22 ms | 14 | 670 / 610 / 588 ms | 1,500 / 3,000 |

**The export pipeline on big tables (ReportExport):**
- **Excel:** `BuildExcel` (every flat-table export in the app) now streams the .xlsx with `Services/XlsxStream.cs`. Each
  cell is written once, cells that look alike share one style entry, and column widths are measured from the values in the
  same pass. Times:
  - 10,000 × 15 columns: 113–184 ms (ClosedXML needed 1.3 s at best);
  - 10,000 × 6: 95 ms (was 1,965 ms);
  - the full-year invoice register (2,760 × 14, read from Samba): 24 ms.
  - The layout is unchanged: tested by reading the workbook back, and the Parties register export tests pass (10/10).
- **PDF:** `BuildPdf` draws the table straight onto the pages with `Services/PdfTableWriter.cs` (PdfSharp; no MigraDoc
  layout engine) and measures each distinct text once.
  - 10,000 × 6: 221–805 ms (was 9,201 ms); 10,000 × 15: 472–509 ms.
  - The ledger month PDF fell from 2,488 to 383 ms.
- **CSV** is streamed through a writer: 10,000 rows in 17–41 ms.
- **Linear growth:** 4× the rows costs at most ~6× the time (tested).
- **Hand-built workbooks:** these statements (and the ledger's one sheet per account) still use ClosedXML through the
  helpers.

No missing index found: the slowest statement query runs in about 10 ms on the full year. **Proposed indexes: none.**

## Many users

- **Reports read one snapshot.** `A_report_reads_one_snapshot_even_when_another_user_commits_mid_report` is
  deterministic: another connection commits a row between two reads in one request. With `[ConsistentSnapshot]` the
  second read matches the first; without it, the new row appears halfway through.
- **Under load.** In `Statements_stay_consistent_while_six_people_post_vouchers`, six clients post 3 rounds of CPVs
  (18 vouchers) through the Accounting routes in company B, after posting one voucher of its own first (an empty year
  shows no "IN BALANCE" verdict, so on a freshly rebuilt copy the test used to fail; it relied on earlier runs'
  vouchers). Meanwhile the cash flow, trial balance and balance sheet are read 81 times.
  - Every cash flow reconciles opening + change = closing, and every TB and BS is in balance.
  - The 18 vouchers posted exactly once.
- **TWCF lines.** Python takes no lock on forecast lines, and neither does .NET. Concurrent adds all land, and an edit
  racing a delete is answered, never a 500.

## Changes outside this project

- **Python app (coordinator's request, 30 Oct):**
  - `finance_app/routes/reports.py`: the cash-flow comparative uses credit − debit for every type.
  - `shared/twcf_reports.py`: the default "today" is `local_today()`.
  - `tests/e2e/test_cash_flow_statement.py` (new) and `tests/e2e/test_twcf_reports.py`.
- No change to Core or Web.Shared.
