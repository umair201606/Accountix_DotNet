# Accounting: status

**Ported**: all 31 routes of `finance_app/routes/accounting.py`, `coa.py` and `bank_rec.py`, plus the shared
general-ledger services.

| Area | Routes | Status |
|---|---|---|
| Dashboard | `/accounting/`, `/accounting/dashboard` | ✅ |
| Voucher form | `/accounting/vouchers`, `/accounting/vouchers/<id>` (GET/POST: save, save & approve) | ✅ |
| Voucher actions | `/vouchers/<id>/approve`, `/unapprove`, `/delete` | ✅ |
| Voucher list, print | `/vouchers/list`, `/vouchers/<id>/preview` | ✅ |
| Voucher export | `/vouchers/<id>/export?fmt=` | ✅ (needs IReportExport from Finance) |
| Voucher register | `/registers/vouchers`, `/data`, `/documents`, `/export` | ✅ (export needs IReportExport) |
| JSON | `/api/accounts`, `/api/cash-bank-accounts`, `/api/account-balance`, `/gl-entry` | ✅ |
| Chart of accounts | `/accounting/coa/`, `/add`, `/<id>/edit`, `/<id>/delete`, `/api/children`, `/api/next-code`, `/api/level-accounts` | ✅ |
| Bank reconciliation | `/accounting/bank-reconciliation`, `/new`, `/<id>`, `/<id>/save`, `/<id>/reopen`, `/<id>/delete`, `?format=` export | ✅ |

## Shared services (registered by AccountingModule)

- `ILedger` (`Services/Ledger.cs`): `post_journal_entry`, `reverse_journal_entry`, posting / entity / party
  accounts, `get_or_create_account`, cash & bank accounts, `entity_has_postings`. Extra public helpers on
  `Ledger`: `CashBankAccountAsync`, `AccountsUnderAsync`, `ByCodeAsync`. Refusals are `LedgerException`
  (a `BusinessRuleException`) or `ClosedPeriodException`.
- `gl_daily_balances` is kept by `GlSummaryInterceptor`, an EF save interceptor registered for every
  context. Every save that touches journal entries or lines updates the summary in the same transaction.
  After a bulk `ExecuteUpdate` / `ExecuteDelete` on journal tables, call `GlSummary.RebuildAsync`.
- `IVoucherNumbers`: `VoucherNumber.next`: one atomic series per prefix, Python's PREFIX-00001 format.
- `IPeriods`: `require_open_period` with FOR SHARE on the period, and Python's refusal text.
- `GlEntries`: the "Accounting entry" panel's data (`shared/gl_entry.py`).

## Tests (`tests/Accountix.Accounting.Tests`, 25, `ACCOUNTIX_TEST_DB=accounting`): 25 passed, 0 skipped, no Python

The tests need no Python. What the live parity tests compared with the Python app is now a fixed expectation:
Python's answer for the test's own data, captured once on 29 Oct (scripts in `Golden/capture/`, kept for the
record) into `Golden/*.json`. The test builds the same data through .NET only, normalises its answer the same way
(accounts as codes, generated numbers as `#` or roles) and compares (`Golden/Golden.cs`: numbers as numbers to
1e-9; `ACCOUNTIX_GOLDEN_PERTURB=1` also checks that a perturbed .NET answer fails; `ACCOUNTIX_GOLDEN_DUMP=<dir>`
writes .NET's normalised answers). Every golden test was also seen to fail when a .NET figure was changed by hand
(a journal debit +0.01, the payment 75 → 76, an account's level). Run on 29 Oct with Python unset, with
`ACCOUNTIX_GOLDEN_PERTURB=1`, twice on a copy freshly rebuilt by `dotnet run --project tools/Accountix.RefDb -- --only
accounting`, and from a copy of `dotnet/` with no Python app above it and TestSupport's `PythonApp.cs` removed: 25 passed
each time (the 2 speed tests included).

| Golden test | Fixture | What it pins |
|---|---|---|
| `LedgerTests.Accounts_resolve_as_python_resolves_them` | `LedgerAccounts.json` | On a fresh company of its own (`golden-ledger`, made and provisioned by Platform's `CompanySetup`): every posting role, the 18 `get_or_create_account` calls the modules make (legacy codes mapped or parked once and found again by name: 5710, 2125, 5123–5126), entity accounts of all 7 kinds (made, found again, renamed): code, name, type, level, parent |
| `VoucherPageTests.Save_and_approve_posts_what_the_python_form_posts` | `CpvSaveApprove.json` | The CPV form posted to Python's route: the voucher, its lines (cash line first), the journal (lines, sides, amounts, descriptions, date) |
| `CoaAndBankRecTests.A_reconciliation_reads_and_completes_as_python_does` | `BankRec.json` | A bank account of its own in company B (BRV 500, BPV 75 through the voucher form), two statements step by step: figures, open lines, ticks, the refusal at a difference, completion, cleared-before carried into the next statement, the reconciliation statement and the flashes. The fixture holds Python's pages; both apps' pages are read by the same code |

Kept as they were (no Python): closed-period refusal in Python's words, unbalanced / aggregating journals refused,
posting and un-posting keep `gl_daily_balances` equal to the lines; the pages (dashboard and form, unbalanced JV
refusal, unapprove then delete, rights, list / preview / register / data JSON / documents, the accounting-entry
panel, account pickers, script-safe JSON, antiforgery on script-submitted forms, query counts); the chart (add,
edit, delete, retire, fixed-account refusal, next code); the races and the speed tests (below).

Rewritten without Python: "voucher numbers continue across the apps" → `Voucher_numbers_are_one_gapless_series_in_pythons_format`
(PREFIX-00001… from one series); "Python's integrity check passes after both apps post to one key" →
`Two_postings_to_one_account_and_day_add_up_in_the_reporting_table` (the row adds both, un-posting takes both off).

Dropped: the cross-app race (Python's three threads and .NET's three requests approving one voucher) — there is no
second app any more; the same-app six-at-once races stay.

## Differences from Python (deliberate)

- The printed voucher and the print letterhead show the company name. Python read `company.company_name`,
  which the multi-company `Company` row does not have, so the name printed blank.
- The register's month and year defaults use the company's clock (Python used the server date).

## Speed (full-year Samba database, `tools/perf.json`; fastest of 3 after a warm-up, test host)

| Page | Time | Queries |
|---|---|---|
| Voucher register, whole year (`?view=1&mode=custom&from=2025-07-01&to=2026-06-30`) | 256 ms | 12 |
| Voucher register data JSON, whole year | 34 ms | 12 |
| Voucher list, busiest month (May 2026) | 37 ms | 8 |
| Voucher register, busiest month | 35 ms | 12 |
| Register data with full documents (100 vouchers) | 50 ms | 14 (was 212: one query pair per voucher; now batched by `DocsAsync`) |
| Printable documents for the month (all vouchers) | 94 ms | 11 (was ~600) |
| Dashboard | 15 ms | 11 |

Every page of the module stays at or under 14 queries; `Voucher_pages_run_a_fixed_number_of_queries` keeps it there.

## Many users (`ManyUsersAndSpeedTests`)

- Six people approving one draft at the same instant: one journal, status approved, no server errors.
- Six vouchers saved at the same instant: six consecutive numbers (gapless), each posted once.
- (Until 29 Oct also the Python app and the .NET app approving one voucher at the same instant: one journal. Dropped
  with the Python app.)
