# Inventory: status

**Ported:** every route of `inventory_app/routes/*` except `transfers.py`, which the Fixed Assets module owns. That is 56 routes. Also ported: the costing engine (`shared/costing.py`, `shared/cost_adjustment.py`, `shared/stock_entry.py`) as the real `IStockCosting`, the product and unit rules of `shared/master_data.py`, and the sidebar.

Tests: `tests/Accountix.Inventory.Tests`, run with `ACCOUNTIX_TEST_DB=inventory`; no Python needed. **125 tests: 125 passed, 0 skipped** (29 Oct, after the move off Python; see [Tests](#tests)).

## Routes

| Area | Routes | Status |
|---|---|---|
| Dashboard | `/inventory/`, `/inventory/dashboard` | ✅ |
| Legacy sign-in door | `/inventory/login` → `/auth/login`, `/inventory/logout` → `/auth/logout` (no `[Module]`; rights_guard lists them OPEN) | ✅ |
| Products | `/inventory/products/`, `create`, `edit/<id>`, `delete/<id>`, `adjust-stock/<id>`, `check`, `template`, `upload-excel`, `batch` | ✅ |
| Units | `/inventory/units/`, `create`, `edit/<id>`, `delete/<id>`, `quick-create`, `check`, `template`, `upload-excel`, `batch` | ✅ |
| Categories | `/inventory/categories/`, `create`, `edit/<id>`, `delete/<id>` | ✅ |
| Stock | `/inventory/stock/`, `/inventory/stock/movements` | ✅ |
| Consumption | `/inventory/vouchers/consumption[/<id>]` (GET/POST: save, approve), `/list`, `/<id>/delete`, `/<id>/unapprove` | ✅ |
| Scrap | `/inventory/vouchers/scrap[/<id>]`, `/list`, `/<id>/delete`, `/<id>/unapprove` | ✅ |
| Stock adjustment | `/inventory/vouchers/adjustment[/<id>]`, `/list`, `/<id>/delete`, `/<id>/unapprove` | ✅ |
| Stock take | `/inventory/vouchers/stock-take[/<id>]`, `/list`, `/<id>/delete` | ✅ |
| Voucher lookups | `/inventory/vouchers/api/products`, `/api/product-stock/<pid>` | ✅ |
| Print preview | `/inventory/vouchers/preview/<vtype>/<vid>` (CONS, SCRAP, ADJ, PI, PR) | ✅ |
| Product ledgers | `/inventory/vouchers/product-ledger` (+ `format=`, Excel adds the re-costing sheet), `/product-ledger/list` (+ `format=`) | ✅ |
| Reports | `/inventory/reports/stock-ledger` (+ `format=`), `/valuation` (as of any date, GL reconciliation, + `format=`), `/low-stock` (+ `format=`), `/api/stock-ledger-json` | ✅ |
| Sidebar | `NAV["inventory"]`: the gates and rights hides of rights_guard. "Transfer to FA" links to `/inventory/transfer-to-fa/` (Fixed Assets) | ✅ |

**Rights** are rights_guard's, as attributes:
- `_crud` / `_imports` on the master data;
- `adjust-stock` needs `adjustment_vouchers:approve`;
- the `check` endpoints are `[AnyMember]`.

`_status_form` for the four voucher forms depends on the request, so it is checked in the action (`this.Deny`):
- GET: `create` for a blank form, `view` for a saved one;
- POST: `create` or `edit`, plus `approve` when `status=approved`.

The voucher lookups take `[AnyRight]` over the 12 voucher view/create/edit pairs. The preview checks the section of its `vtype`. A test checks that every action declares a right.

**Exports** go through `IReportExport` (Finance), with the same `ReportTable` as Python's `export_table` / `build_excel`. Tests assert the tables through a fake exporter. Finance is still being edited, so the real Excel/PDF files are not tested here.

## IStockCosting: what other modules rely on

Registered by `InventoryModule` as `StockCosting` (scoped). Invoicing, Returns and Fixed Assets call it.

**Calling it**
- **Transaction.** Every call works inside the caller's transaction. Open one (`BeginTransactionAsync`) before the first stock call and commit at the end. Nothing is committed here.
- **Lock order.** Lock the document first (`db.LockAsync`), then take numbers (`IVoucherNumbers`), then lock the products, all at once (`LockProductsAsync`, ascending ids, FOR NO KEY UPDATE, the same lock Python takes). `Receive`/`Issue`/`Reverse` lock on their own as a fallback.
- **Dates.** Pass the document date as `txnDate` (midnight `DateTime`). Layers are consumed in (txn_date, id) order. A back-dated receipt or issue replays the product's history: later issues are re-costed, and each re-costed issue gets a `CADJ-<type>` journal for the difference. Leaving `txnDate` out means the company's today.

**Receiving and issuing**
- `ReceiveAsync(...) → StockLedger` is `record_in`. Post the row's `TotalCost`. Pass `totalCost` with a purchase line's exact landed total, so the receipt carries what the journal debits. `RecordInAsync` returns the same total.
- `IssueAsync(...) → (UnitCost, TotalCost)` is `record_out`. Post `TotalCost`.
  - `unitCost`: an explicit basis, e.g. a purchase return at `OriginalReceiptCostAsync`.
  - `preferVoucher: ("PI", invoiceId)`: a purchase return draws from its own invoice's layers first.
  - Sales returns receive at `OriginalIssueCostAsync("SI", invoice, product)`.
  - `CostOfIssueAsync` is the pure preview of what an issue would cost.

**Reversing**
- `ReverseVoucherStockAsync(type, id, allowVariance)` is `reverse_voucher_stock`. It deletes the voucher's rows, gives back what they consumed, re-costs later issues, and un-posts this voucher's own `CADJ-<type>` journals. It returns `{productId: moved}`.
  - With `allowVariance: false`, a receipt whose stock was issued since is refused (`ConsumedLayerException`, naming the issues).
  - `allowVariance: true` is for "correct an earlier purchase" (PI unapprove). Issues that drew on it are carried short until the corrected document is re-approved on its own date.
- Un-post your own journal with `ILedger.ReverseAsync` before or after this; the order does not matter.

**Refusals.** All are `BusinessRuleException` subclasses, answered as a flash or JSON 409, with Python's words:
- `NegativeStockException`: not on hand, not on hand at that date, or it would strip a later issue;
- `ConsumedLayerException`;
- `StockQuantityException`: a receipt of qty ≤ 0, which is a `ValueError` in Python;
- `ClosedPeriodException`, from re-costing journals.

**Re-costing journals**
- `CADJ-<type>` / `<voucher id>`, numbered `CADJ-<number>`.
- Each issue's adjustment charges the account the issue originally hit: COGS with the invoice line's label for SI, the charge account for CONS/SCRAP, 5900 for ADJ, the asset for FA-CAP.
- It is dated on the issue's date, or today when that period is closed.
- It also updates the source document's item cost (CONS/SCRAP/ADJ items; FA-CAP asset and transfer, with `recalculate`).

**Other methods**
- `OnHandAsync(product, at)` (`on_hand` / `on_hand_at`), `ValueAtAsync`, `StockValueAsync`, `CurrentUnitCostAsync`.
- `ConsumersOfVoucherAsync`.
- `RevalueForMethodChangeAsync(newMethod)`: Settings calls it when the valuation method changes.
- `AssertInvariantAsync(product)`: for Books Integrity.

**Product figures.** The product's `current_stock` and `cost_price` (and `updated_at`) follow every movement, as `_sync_product_stock`.

**`StockEntry`** (not part of the contract):
- `PostOpeningStockAsync`: an OPENING receipt, then Dr the product's stock account / Cr Opening Balance Equity;
- `QuickAdjustmentAsync`: an approved ADJ voucher;
- `UnjournaledOpeningsAsync` / `JournalOpeningRowAsync`.

**How it is built**
- The arithmetic is Python's Decimal: scales are kept, quantize is half-even, and quantities and values are carried exactly as in `costing.py`.
- A replay rebuilds the product's layers in memory. It then writes them with one INSERT per table (`unnest`, ids taken in creation order, so the id order is the FIFO order) and one UPDATE for the re-costed rows.
- The re-costing journals go through EF in one save, so `gl_daily_balances` and the audit trail follow them. Their `stock_cost_adjustments` rows go in one INSERT.
- After each call the engine forgets the stock rows it tracked, as SQLAlchemy's weak identity map does. Refusal texts and short-layer notes therefore print stored quantities ("5.0000") exactly as Python prints them.

**Not ported:** the boot-time maintenance functions `ensure_opening_balances`, `backfill_layers` and `backfill_layer_values`. They run in the Python app's boot and `tools/`.

## Tests

`ACCOUNTIX_TEST_DB=inventory dotnet test tests/Accountix.Inventory.Tests --artifacts-path <yours>`. No Python: the
project has no `PythonApp` / `[PythonFact]`, and it builds and passes from a copy of `dotnet/` alone (no Python app,
no `/static`, `PythonApp.cs` deleted from TestSupport).

**Results (29 Oct):** 125 passed, 0 skipped, on the old copy, on a copy rebuilt fresh with
`dotnet run --project tools/Accountix.RefDb -- --only inventory`, on that run's leftovers, and from the Python-free copy.
Before the move: 131 passed with Python. The six fewer tests are the cross-app race, the Python timing test, and four
report parity tests that are now steps of one golden test.

`CostingSpeedTests` has a time bound with headroom (2.5 s). Once, in a full run on a loaded machine, FIFO took 2.7 s;
alone it takes 1.7 s. Its statement count (30) is the stable measure.

### Golden tests (Python's answers captured on 29 Oct, before Python was removed)

These were parity tests that ran the Python app next to .NET. They are now fixed expectations.
- Each test builds its own data through .NET only: the engine, the app's own forms, or SQL/EF inserts.
- `Golden/<name>.json` holds the answer the Python app gave for the same steps on its own fresh data.
- The capture scripts are kept under `Golden/capture/` for the record: `_capture.py` boots the app, and `_dump.py` is
  the Python twin of `GoldenDump.cs`, using the same SQL.
- Both sides are normalised the same way:
  - ids become roles: products P/Q, their own ledger accounts `1-01-04-01-<P>`, stock rows `<row_pi>`, the approver `<owner_b>`;
  - generated numbers become N/R, and the run's tag becomes TAG;
  - dates become days from the test's own date (`D-2`).
- Each fixture stores the inputs it was captured with, and `Golden.SameInputs` checks the test still sends them.
- With `ACCOUNTIX_GOLDEN_PERTURB=1` (or `=last`), one figure of every .NET answer is changed, and all 12 golden tests
  then fail. A figure changed in the middle of a fixture copy also failed: a costing journal line and the report test's
  last step.

| Test | Fixture | What it protects |
|---|---|---|
| `CostingGoldenTests` (4): FIFO back-dated + reversals, WA re-averaging + landed total, negative stock, method switch + explicit basis | `costing_*.json` (`capture/costing.py`) | Every `stock_ledger`, `stock_layers`, `layer_consumptions` and `stock_cost_adjustments` row; the CADJ journals and lines; the product's figures; each refusal's words |
| `VoucherPageTests`: consumption, scrap (charge account + label), adjustment (system qty from the ledger), stock take | `voucher_*.json` (`capture/vouchers.py`) | What the form posts: voucher, items, stock rows, journal lines to the paisa, redirect |
| `ProductPageTests`: product create (awkward figures), opening stock | `product_create.json`, `product_opening_stock.json` (`capture/products.py`) | The product row, its ledger account, opening stock rows, layer and OPENING journal |
| `MasterDataTests.Number_and_text_read_odd_inputs_as_python_did` | `master_data_parsing.json` (`capture/master_data.py`) | `number` / `text` / `single_line` on 26 awkward inputs |
| `ReportGoldenTests` (one test, 25 steps) | `reports.json` + `report_data.sql` (`capture/reports.py`) | See below |

**`ReportGoldenTests` in detail.**
- It runs on a company of its own: rows from `Golden/report_data.sql`, provisioned with Platform's `CompanySetup.ProvisionAsync`.
- The capture ran the same SQL file. Python's model defaults would otherwise have made the two apps' rows differ.
- It compares, in a fixed order:
  - the export tables: valuation over two ranges with the GL reconciliation lines, low stock, sub-ledgers, and the stock and product ledgers of two products;
  - the stock-ledger JSON;
  - nine pages' table text;
  - five print previews;
  - the dashboard's figures.
- Numbers are compared by value to 10 decimals. A quotient's 28th digit differs between Python's `Decimal` and .NET's `decimal`.

### Deleted, and why

- `The_python_app_and_the_dotnet_app_issuing_one_product_together_never_oversell` (`StockRaceTests`): a race between the two apps. The four same-app six-at-once races stay and need no Python.
- `ReportSpeedTests.Dotnet_is_not_slower_than_python_on_the_same_pages`, and the live Python timing inside `VoucherSpeedTests` and `ProductPageTests.The_product_list_is_quick_on_a_full_year`: the .NET budgets stay. Python's times are kept in the Speed table below as recorded history.
- The demo-company (company 1) comparisons of the old report parity tests: they read seeded data the tests did not build. The pages still render on the demo company in `ReportPageTests` (.NET only), and the figures are covered by `ReportGoldenTests` on data of its own.
- Helpers: `ReportKit.PythonGet` / `PythonCsv` / `SameTable` / `SameCell`, `VoucherKit.PythonPost` / `PostedAsync`, `EnginePythonFactAttribute`, `PerfTarget.DatabaseUrl`.

### Kept as they were (.NET only)

- The 61 ported unit cases of `test_costing.py` / `test_costing_backdated.py` (`CostingTests`, `CostingBackdatedTests`).
- `CostingSpeedTests`, and the page, rights and query-count tests of products, units, categories, vouchers and reports.
- The four `StockRaceTests` races, the speed budgets and the smoke test.

## Speed

Full Samba year (`accountix_ref_perf`, company 2), server time, fastest of 3, measured 29 Oct. Python was timed with the Flask test client on the same data. The Python column is recorded history: the tests no longer time Python.

| Page | .NET | Python |
|---|---|---|
| Stock valuation, whole year | 13–22 ms, 12 queries | 267–300 ms |
| Stock ledger, busiest product (#37) | 12–29 ms, 9 q | 82–90 ms |
| Product ledger, busiest product | 12–29 ms, 10 q | 66–81 ms |
| Product sub-ledgers | 14–21 ms, 8 q | 65–96 ms |
| Low stock | 3–8 ms, 7 q | 27–29 ms |
| Stock list / movements | 3–10 ms, 7–8 q | 22–34 ms |
| Dashboard | 16–26 ms, 19 q | 129–143 ms |
| Products / categories / units lists | 17–24 ms, 7–8 q | 64–77 ms |
| Consumption / scrap / adjustment / stock-take lists | 8–10 ms, 7 q | 22–85 ms |
| Voucher forms (new, saved) | 8–11 ms, 9–11 q | 25–35 ms |
| Voucher lookups (`api/products`, `api/product-stock`) | 5–6 ms, 7–8 q | 5–8 ms |
| Exports (table building; the file itself is Finance's) | 5–18 ms, 8–12 q | — |

**Costing.** A back-dated receipt on a product with a year of history (1,460 rows: a receipt and three issues every day) replays the year and re-costs every later issue, with journals:
- **Weighted average:** 404 issues re-costed. .NET takes 0.45–0.6 s in **27 statements**; Python takes 18.5 s.
- **FIFO:** 1,087 issues re-costed. .NET takes about 1.1 s in **30 statements**; Python takes 29.4 s.
- The replay itself is 0.1–0.2 s. The rest is the 400–1,100 `CADJ` journals that Python posts one by one.
- The statement count does not grow with history. The test asserts at most 40 statements, plus a time bound with headroom, because the machine is shared (load average 18–34 during the runs).

**Proposed indexes:** not needed at this volume. They are for the coordinator's Alembic revision.
- `CREATE INDEX ix_stock_ledger_company_product_date ON stock_ledger (company_id, product_id, txn_date DESC, id DESC);` This index lets the latest-balance-per-product query (dashboard, sub-ledgers) and every per-product ledger read the index in order.
- `CREATE INDEX ix_inv_stock_movements_product_id ON inv_stock_movements (product_id, id);`

## Many users

All of these run with `Race.RunAsync(6, ...)` against the real host and database.
- **Six people approving one consumption draft:** stock moves once, with one journal and one stock row.
- **Six people each consuming 4 of 10 on hand:** exactly two vouchers go through. The rest are refused in Python's words and leave no voucher. On hand ends at 2 and the layers tie.
- **Six scrap vouchers listing two products in opposite orders:** none deadlocks, all post, and both products tie. Products are locked ascending after the document and its number.
- **A stock take approved while five consumptions race:** its system quantity is what was on hand when it got the product lock, and the final on-hand equals the count less the consumptions after it.
- **Cross-app race** (three Python and three .NET users consuming 4 of the same 10): passed while both apps ran; deleted with the Python app.
- **Locks are Python's.** The document is locked FOR NO KEY UPDATE and its status re-checked; then the number series; then the products, ascending, FOR NO KEY UPDATE; then the report totals through `GlSummary`. Periods are taken FOR SHARE: re-costing journals lock all their periods at once, in id order.

## Fixed in both apps

These were bugs in the Python app, found while porting. They are fixed in Python and .NET alike, so the two apps show the same pages.

- **Stock-take list.** Python's page raised a 500 as soon as any stock take existed: `st.items|length` was called on a dynamic relationship. Both apps now show the item count (`st.items.count()` in Python).
- **320px.** Three pages scrolled sideways on a phone. All three now fit in both apps:
  - the products list search box and the stock movements product picker get `max-width:100%`;
  - the print preview's lines table scrolls in its own box at ≤480px (same CSS in both apps).
- **Company clock.** "Today" is the company's today (`shared/clock.local_today`, `CompanyClock.Today`), not the server date. This covers:
  - the costing fallback date (`as_txn_date(None)`);
  - the adjustment date when the issue's period is closed;
  - the undated opening stock and quick adjustment (`stock_entry`), now dated midnight of the company's today;
  - the voucher forms' default date;
  - the valuation report's default dates;
  - the low-stock and sub-ledger export periods and file names.
- **Python tests.** `tests/e2e/test_inventory_phone_and_stock_take.py` adds 5 tests:
  - the stock-take list with a stock take renders;
  - the products list and stock movements fit 320px (`CLIPPED_JS` and no sideways scroll);
  - the print preview fits at 320 and 375px.

  Against the old templates, the four phone tests fail and the stock-take test errors (its list 500s); with the fixes all pass. `tests/unit/test_costing_backdated.py` now expects the company's today for a closed-period adjustment.
- **Python results.**
  - Unit `test_costing.py` and `test_costing_backdated.py`: **65 passed**.
  - E2E `test_inventory*.py` and `test_responsive_pages.py` (`E2E_PORT=5069`): **66 passed**.
  - E2E `test_books_integrity.py` and `test_order_actions.py`, which go through the changed `costing` / `stock_entry`: **41 passed**.
- **.NET** after the matching `StockEntry` change: **131 passed, 0 skipped**.

## Differences from Python (deliberate)

- **Unreadable quantities:** a voucher line whose quantity is not a number is skipped. Python answered 500 (`float("")`).
- **`record_in` with qty ≤ 0** is a readable refusal, not a 500.
- **Print preview:** it prints the company profile name, as the porting guide says. Python printed it blank.
- **Ordering:** where Python's order was unspecified (category pickers, first matching item), .NET orders by id.

## Changes outside this module

- **`Accountix.Core/Shared/Contracts.cs`:** members added to `IStockCosting` (additive). They are `ReceiveAsync`, `IssueAsync`, `CostOfIssueAsync`, `ValueAtAsync`, `OriginalReceiptCostAsync`, `ConsumersOfVoucherAsync`, `ReverseVoucherStockAsync(…, allowVariance, …)`, `RevalueForMethodChangeAsync` and `AssertInvariantAsync`. Core was built right after.

## For the coordinator / other modules

- **The `IStockCosting` stand-in is gone.** Any test of another module that relied on it throwing `NotYetPortedException` now gets the real engine once Inventory is booted.
- **Indexes:** the two proposed indexes above go into Alembic 0018 (coordinator).
- **Rights flash:** the lost "No 'x' rights here" flash on `[Right]` refusals is fixed in Web.Shared (coordinator).
