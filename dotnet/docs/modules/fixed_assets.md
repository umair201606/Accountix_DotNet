# Fixed Assets: status

**Ported**: every route of `fixed_assets_app/*` (33) and `inventory_app/routes/transfers.py` (Inventory → Fixed
Assets, 5 under `/inventory`), the model logic of `fixed_assets_app/models/asset.py` and `movement.py`, the
sidebar `NAV["fixed_assets"]`, and the rights of `shared/rights_guard.py`.
FBR Digital Invoicing moved to another engineer (`Accountix.Fbr`); nothing of FBR was written here.

## Routes

| Python endpoint | URL | Status |
|---|---|---|
| fa_auth.dashboard | `GET /fixed-assets/`, `/fixed-assets/dashboard` | ✅ |
| fa_assets.list_assets | `GET /fixed-assets/assets/?q=&status=&category_id=` | ✅ |
| fa_assets.create_asset | `GET/POST /fixed-assets/assets/create` (FA-ACQ) | ✅ |
| fa_assets.view_asset | `GET /fixed-assets/assets/<id>` (planned depreciation, ledger panel) | ✅ |
| fa_assets.edit_asset | `GET/POST /fixed-assets/assets/<id>/edit` (guards, FA-ACQ restated) | ✅ |
| fa_assets.record_depreciation | `POST /fixed-assets/assets/<id>/depreciate` (manual charge, impairment) | ✅ |
| fa_assets.dispose_asset | `POST /fixed-assets/assets/<id>/dispose` (catch-up FA-DEP, FA-DISP, gain/loss) | ✅ |
| fa_assets.undo_disposal | `POST /fixed-assets/assets/<id>/undo-disposal` | ✅ |
| fa_categories.* | `GET /fixed-assets/categories/`, `GET/POST /create`, `GET/POST /<id>/edit` | ✅ |
| fa_dep.index | `GET /fixed-assets/depreciation/?as_of=` | ✅ |
| fa_dep.post_depreciation | `POST /fixed-assets/depreciation/post` (locks in id order, savepoint per asset) | ✅ |
| fa_dep.reverse_entry | `POST /fixed-assets/depreciation/entry/<id>/reverse` | ✅ |
| fa_transfers.list_transfers | `GET /fixed-assets/transfers/` | ✅ |
| fa_transfers.capitalise_from_stock | `GET/POST /fixed-assets/transfers/capitalise` (draft or approved, FA-CAP) | ✅ |
| fa_transfers.approve_capitalisation_route | `POST /fixed-assets/transfers/<id>/approve-capitalisation` | ✅ |
| fa_transfers.unapprove_capitalisation | `POST /fixed-assets/transfers/<id>/unapprove-capitalisation` | ✅ |
| fa_transfers.create_transfer | `GET/POST /fixed-assets/transfers/create?asset_id=` (asset → stock, FA-TRF) | ✅ |
| fa_transfers.edit_transfer | `GET/POST /fixed-assets/transfers/<id>/edit` | ✅ |
| fa_transfers.unapprove_transfer | `POST /fixed-assets/transfers/<id>/unapprove` | ✅ |
| fa_transfers.delete_transfer | `POST /fixed-assets/transfers/<id>/delete` (drafts take their asset) | ✅ |
| fa_reports.index | `GET /fixed-assets/reports/?format=excel\|pdf\|csv` (register + "By category" sheet) | ✅ |
| fa_reports.movement_schedule | `GET /fixed-assets/reports/movement?from=&to=&format=` | ✅ |
| inv_transfers.list_transfers | `GET /inventory/transfer-to-fa/` | ✅ |
| inv_transfers.create_transfer | `GET/POST /inventory/transfer-to-fa/create` (INV-FA drafts) | ✅ |
| inv_transfers.edit_transfer | `GET/POST /inventory/transfer-to-fa/<id>/edit` | ✅ |
| inv_transfers.unapprove_transfer | `POST /inventory/transfer-to-fa/<id>/unapprove` | ✅ |
| inv_transfers.delete_transfer | `POST /inventory/transfer-to-fa/<id>/delete` | ✅ |

Rights: `[Right]` per action as `RIGHTS` names them; `_status_form` endpoints also check `approve` in the action
when `status=approved` is posted. FA controllers carry `[Module(Modules.FixedAssets)]`, the `/inventory`
controller `[Module(Modules.Inventory)]` (section `stock`).

## Code

- `Services/AssetEngine.cs` — `AssetCalc`: `monthly_charges` (prospective; DDB switching to SL; the life's last month
  takes the remainder), `posted_depreciation`, `due_depreciation`, `depreciation_schedule`, `_months_between`,
  `_add_months`. Runs in `double` on purpose, like Python.
- `Services/Py.cs` — Python's float semantics where results are posted or printed: `round(x, n)` (correctly rounded),
  3.12's compensated `sum()`, `"{:,.Nf}"`, `repr(float)`, `Decimal(str(x))`.
- `Services/AssetBooks.cs` — live depreciation **aggregated in SQL** (one query for any number of assets),
  `recalculate`, `post_acquisition`, `post_asset_depreciation`, `reverse_asset_depreciation`, the asset's own
  accounts, `account_choices` (one query), `_mapped`, `next_code` (advisory lock `serialize("asset-code", cid)`).
- `Services/Capitalisations.cs` — `new_capitalisation`, `approve_capitalisation`, `_capitalise_from_stock`,
  `discard_draft_asset`, `_approve_transfer`, through `ILedger` and `IStockCosting`.
- `Services/Movement.cs` — the IAS 16 movement schedule (four queries).
- `Services/UpdatedAtInterceptor.cs` — `fixed_assets.updated_at` (Python's `onupdate`).

## Needs from other modules

- **Inventory**: the real `IStockCosting`. My tests use a stand-in (`tests/.../Support.cs` `FakeStock`); the
  capitalisation golden test holds what the Python costing posted for the same quantities and cost. The Inventory sidebar
  (`NAV["inventory"]`, owned by Inventory) must keep its "Transfer to FA" item:
  `new("Transfer to FA", "/inventory/transfer-to-fa/") { ActivePrefixes = ["/inventory/transfer-to-fa"], Right = ("stock", Rights.View) }`.
- **Finance**: `IReportExport` for the files. Tests check the `ReportTable` handed over (titles, periods, column
  kinds, grand total, the Excel-only "By category" sheet) with a capturing stand-in.

## Tests (`tests/Accountix.FixedAssets.Tests`, `ACCOUNTIX_TEST_DB=fixed_assets`): 62 passed, 0 skipped, no Python

Standalone since 29 Oct: nothing runs the Python app. Run on a fresh copy (`tools/Accountix.RefDb --only fixed_assets`)
and again on the used one: 62 passed both times, `ACCOUNTIX_PYTHON` unset.

- **Golden (fixed expectations captured from the Python app on 29 Oct, before it was retired)** — `Golden/<name>.json`
  is Python's answer for exactly the data the test builds through .NET, normalised by the same code the test applies
  to .NET's answer; the Python that produced it is kept in `Golden/capture/<name>.py`. `ACCOUNTIX_GOLDEN_PERTURB=1`
  changes one value of the answer: all three then fail (also checked by editing a single paisa / last binary digit in
  each fixture).
  - `EngineMonthlyCharges` (`EngineParityTests`): 120 seeded random assets (both methods, part-charged, overdue)
    through Python's own `monthly_charges`: every charge as `repr` and the rounded sum, bit for bit.
  - `AssetLifeJournals` (`JournalParityTests`): a DDB asset with awkward figures through the .NET routes — FA-ACQ,
    14 month-end runs, impairment, manual charge, its reversal, another run, disposal at a loss: the asset row,
    every depreciation row (amount, accumulated, NBV, notes, live) with its FA-DEP journal, FA-ACQ and FA-DISP, to
    the paisa (code → `{CODE}`, name → `{NAME}`, the asset's own accounts → `{A}`, voucher ids → `{V}`, the
    per-impairment account → `{IMPAIRMENT}`; dates are the test's fixed dates). Then Books Integrity passes.
  - `CapitaliseAndTransferJournals` (`JournalParityTests`): 2 units capitalised at 125,000.50 (Python's costing
    engine; .NET's stock stand-in issues at the same cost), catch-up depreciation, transfer back to stock at book
    value: both vouchers, the asset row, FA-CAP / FA-DEP / FA-TRF journals (product → `{P}`, its inventory account
    → `1-01-04-01-{P}`).
- **Engine** (`EngineTests`, port of `tests/unit/test_fixed_assets.py`'s arithmetic): even straight line, latest
  charge date, salvage floor for both methods, manual cap, reversed period chargeable again, DDB lands on salvage at
  end of life, DDB front-loads, SL to the paisa, prospective change of life, overdue residual, schedule to salvage,
  inactive assets, calendar months; Python rounding / formatting / repr / sum with concrete values.
- **Rules through the routes** (`AssetRulesTests`, port of `tests/e2e/test_fixed_asset_rules.py` + the GL tests of
  `test_fixed_assets.py`): nonsense terms refused; run + planned schedule; prospective life; edit guards; restated
  FA-ACQ; disposal guards, gain on sale, undo; manual charge / impairment / reversal; draft capitalisation not in
  service, approved later at the costing value, POST-only, delete takes the asset; inventory-side drafts (no typed
  cost), approval there, live charge blocks unapproval, cancelled-not-deleted asset; asset → stock needs a stock
  item, catch-up depreciation then one unit at book value, unapprove; movement schedule roll-forward + export.
  **Books Integrity passes** after each fixed-asset step (company B) — now Executive's `BooksIntegrity` (the port
  of `shared/integrity.py`, itself checked against Python), where it used to be Python's own.
- **Pages** (`PageTests`): every GET renders with Python's ids/classes; sidebar; 404s; inline scripts free of
  HTML-encoded text; register export table; categories; module gate (403) and section rights (refusal, sidebar hides).
- **Many users** (`ManyUsersTests`, from `tests/concurrency/test_concurrent_users.py`), N=6, .NET only: depreciation
  run posts each month once; one disposal of one asset; one capitalisation approval; six creations get six
  consecutive codes.
- **Speed** (`SpeedTests`): 300 assets with a year of monthly depreciation (company A), budgets and fixed query
  counts; the Samba `[PerfFact]`.
- **Dropped on 29 Oct**: the cross-app race (Python's 3 threads + .NET's 3 running one depreciation; it passed: charged
  once, Python's integrity clean) and the live ".NET not slower than Python" timing test (the Python times are kept
  below as history). Both need the Python app, which is being removed; the same-app races above stay.
- Setup is .NET only: the stock item for the capitalisation test is an EF insert (`AssetRulesTests.ProductAsync`),
  the stock costing is the `FakeStock` stand-in; Python's `post_opening_stock` is no longer used.

## Speed

Fastest of 3, test host, this module's copy with 300 assets × 12 charges. The Python column was measured on the same
data (Flask test client) on 29 Oct, before the Python app was retired; it is kept as history, no test measures it now:

| Page | .NET | Queries | Python |
|---|---|---|---|
| `/fixed-assets/` (dashboard) | 45 ms | 11 | 1,539 ms |
| `/fixed-assets/assets/` | 37 ms | 9 | 1,446 ms |
| `/fixed-assets/depreciation/?as_of=…` (run page) | 32 ms | 8 | 11,193 ms |
| `/fixed-assets/reports/` (register) | 45 ms | 8 | 3,604 ms |
| `/fixed-assets/reports/movement` (a year) | 43 ms | 13 | 117 ms |
| `/fixed-assets/transfers/` | 11 ms | 7 | 67 ms |
| `/inventory/transfer-to-fa/` | 10 ms | 7 | 74 ms |
| asset page | 16 ms | 12 | — |

Seven of the queries on every page are the sign-in's. No query repeats per asset (asserted). The Samba speed-test
database holds no fixed assets; the `[PerfFact]` runs the same pages there (all ≤ 13 ms, ≤ 11 queries).

**Proposed indexes** (not needed for the budgets; for companies with many thousands of charges):
`CREATE INDEX ix_asset_depreciation_asset ON asset_depreciation (asset_id, entry_date);`
`CREATE INDEX ix_asset_transfers_asset ON asset_transfers (asset_id);`

## Many users

Locks as Python: every state change loads its record with `LockAsync` (FOR NO KEY UPDATE) and re-checks;
the run locks assets in id order outside the per-asset savepoint; reversal locks the asset, then the charge;
asset codes are queued with `serialize("asset-code", company_id)` (same advisory key as Python); voucher numbers
through `IVoucherNumbers`. Tested above (4 same-app races; the cross-app race passed before Python was retired and
was then dropped).

## Responsive

No page scrolls sideways at 320px (checked in Chrome on the host, port 5307, every page incl. forms and the
movement schedule); tables scroll inside `.table-responsive` as in Python. No console errors; the accounting-entry
panel loads FA-ACQ + FA-DEP + FA-DISP.

## Differences from Python (deliberate)

- A transfer with no date falls back to the company's today (Python: the server's `date.today()`); forms always set one.
- Lists Python reads without ORDER BY (active categories) are ordered by id.
- A non-numeric `category_id` filter on the asset list is ignored (Python answered 500).

## Found in the Python app

- **Every impairment created a new "Impairment Loss on Fixed Assets" account** (`get_or_create_account("5710", …)`
  never found the parked account it made last time); the same for 2125/5123 (EOBI), 5124, 5125, 5126. Fixed by the
  coordinator in both apps (found by parent + name + type). Existing duplicates are merged by
  `dotnet Accountix.Web.dll admin merge-legacy-accounts [--apply] [company id ...]` (dry run unless `--apply`;
  the port of `tools/merge_parked_legacy_accounts.py`), tested in `tests/Accountix.Admin.Tests`
  (`MergeLegacyAccountsTests`, including row-for-row parity with the Python tool). The parity test still
  compares that account by name.
- Web.Shared: a refusal from `[Right]` (an authorization filter) adds its flash to TempData, but MVC never saves
  TempData for a short-circuited request, so "No 'x' rights here…" is not shown (the redirect is right).
- Seen while capturing the golden fixtures (29 Oct; not fixed, the Python app is being retired): on Postgres the
  Python routes `/fixed-assets/transfers/<id>/unapprove-capitalisation` and `/fixed-assets/transfers/<id>/delete` answer
  500 ("FOR NO KEY UPDATE cannot be applied to the nullable side of an outer join": the locked load eager-joins the
  asset). The .NET routes lock the transfer alone and are covered by `AssetRulesTests`.
