# Invoicing: status

Scope (after the 29 Oct split): **sales invoices, purchase invoices, the invoicing dashboard**, and the shared
invoice logic that Orders (`Accountix.Orders`), Returns (`Accountix.Returns`) and Parties (registers, tracking)
call. Sales/purchase orders and returns pages belong to those modules.

## Routes

| Python endpoint | URL | Status |
|---|---|---|
| `invoicing.dashboard` | `GET /invoicing/` | ✅ |
| `inv_invoices.invoice_form` | `GET /inventory/invoices/`, `/inventory/invoices/<id>` | ✅ |
| `inv_invoices.list_invoices` | `GET /inventory/invoices/list` (+ `from`/`to`/`month`, `page`) | ✅ |
| `inv_invoices.save_invoice` | `POST /inventory/invoices/save` (JSON) | ✅ |
| `inv_invoices.unapprove_invoice` | `POST /inventory/invoices/unapprove/<id>` | ✅ |
| `inv_invoices.delete_invoice` | `POST /inventory/invoices/delete/<id>` | ✅ |
| `inv_invoices.pay_invoice` | `POST /inventory/invoices/pay/<id>` (retired: flash + tracking) | ✅ |
| `inv_invoices.api_products` / `api_customers` / `api_orders_for_customer` / `api_charge_accounts` | `GET /inventory/invoices/api/...` | ✅ |
| `inv_purchase_invoice.invoice_form` | `GET /inventory/purchase-invoice/`, `/<id>` | ✅ |
| `inv_purchase_invoice.list_invoices` | `GET /inventory/purchase-invoice/list` (+ `from`/`to`/`month`, `page`) | ✅ |
| `inv_purchase_invoice.save_invoice` | `POST /inventory/purchase-invoice/save` (JSON) | ✅ |
| `inv_purchase_invoice.unapprove_invoice` | `POST /inventory/purchase-invoice/unapprove/<id>` | ✅ |
| `inv_purchase_invoice.delete_invoice` | `POST /inventory/purchase-invoice/delete/<id>` | ✅ |
| `inv_purchase_invoice.pay_invoice` | `POST /inventory/purchase-invoice/pay/<id>` (retired) | ✅ |
| `inv_purchase_invoice.api_products` / `api_suppliers` / `api_orders_for_supplier` / `api_pi_charge_accounts` | `GET /inventory/purchase-invoice/api/...` | ✅ |
| `inv_purchase_invoice.api_new_product` | `POST /inventory/purchase-invoice/api/new-product` | ✅ |

Rights follow `shared/rights_guard.py`: editor pages `_doc_page` (create / view), JSON saves `_json_save`
(edit or create, + approve), unapprove = approve, delete = delete, lookups = view|create|edit, dashboard =
view on either invoice section. JSON endpoints refuse in JSON (403), as Python's `JSON_ENDPOINTS`.

Views: `Areas/Invoicing/Views/{SalesInvoices,PurchaseInvoices}/Form.cshtml` are converted mechanically from the
Jinja templates (style and script blocks verbatim, `@` → `@@`; every Jinja tag mapped one to one), so
`static/js/doc_form.js` + `edit_version.js` and the inline `window.DOC` run unchanged. Lists, dashboard: by hand.

## For other modules (public API of Accountix.Invoicing, stable)

All in `Accountix.Invoicing.Services` (registered by `InvoicingModule`; reference the project and boot the module).

| Class | Python | Use |
|---|---|---|
| `Py` (static) | float semantics | `Round(x, n)` (half-even on the exact binary value), `Sum(...)` (CPython 3.12 Neumaier `sum`), `Repr(x)` (`str(float)`), `Fixed(x, n)` (`f"{x:.nf}"`), `G(x)` (`f"{x:g}"`), `Dec(x)` (`Decimal(str(x))`, what Python posts/stores), `F(decimal?)` / `FN` (exact `float(Decimal)`), `Amount(v, dp, numberFormat)` (`format_amount`), `Qty` / `QtyJson` (Quantity column). |
| `PyJson` (static) | `jsonify` / `|tojson` / `json.dumps` | `Dumps(obj)` = jsonify (sorted keys, compact, ASCII, Python float repr); `Dumps(obj, htmlSafe: true)` = `|tojson` (sorted, `", "`/`": "`, `<>&'` escaped; emit with `@Html.Raw` inside `<script>`); `JsonDumps(obj)` = plain `json.dumps` (insertion order). `Result(obj, status)` → a `ContentResult`. |
| `PyData` | `request.get_json(force=True)` + `data.get(...)` | `ReadAsync(Request)`, `Get`, `Has`, `Truthy`, `Float(key, default)` (`float(data.get(k, d))`, throws `PyValueException` like Python), `FloatOr0`, `Id` (`data.get(k) or None`), `Str(key, default)` (JSON null → null), `Bool`, `List`. |
| `InvoiceTotals` (static) + `TLine`, `TCharge`, `TDoc`, `ChargePools`, `SalesTotals` | `shared/invoice_totals.py` | `ChargePoolsFromRows`, `LineValue`, `LineNet`, `Allocate`, `RevenueSplits`, `LineTaxBases`, `LineTaxAmounts`, `LineTaxes`, `OutputTaxSplits`, `Sales` (the §8 chain). `TLine.From(item, product)`, `TCharge.From(row)`, `TDoc.From(invoice)`. |
| `LabelSplit` (static) | `posting_helpers.label_weights` / `split_by_label` | `Weights(pairs)`, `Split(amount, weights)`. |
| `DocChecks` (static) | `shared/doc_checks.py` | `AmountErrors(PyData)`. |
| `OrderLinkage` (scoped) + `OrderLine`, `Billing` | `shared/order_linkage.py` | `LockOrderLinesAsync(side, ids)`, `LinesAsync`, `OrderLinesAsync`, `InvoicesDrawingOnAsync(side, orderId)` (a billed order cannot be changed), `LineBalance`, `Progress`, `PickerPayload`, `CheckOverInvoicingAsync`, `ApplyWritebackAsync(side, billings, sign)`, `Tally`, `AllocateReturn`, `ApplyReturnWritebackAsync`. `side` = `OrderLinkage.Sales` / `Purchase`. |
| `EditVersions` (scoped) | `concurrency.stale_edit` / `bump_edit` | `StaleAsync(table, id, companyId, current, sentJson, label, setVersion)` → refusal text (who/when from the audit trail) or null after bumping; `Bump(current)`; `Tracked` (table → audit entity type). |
| `DocSettings` (scoped, per request) | `InvoiceSettings/ReportSettings/InventorySettings.get()` | `InvoiceAsync`, `ReportAsync`, `InventoryAsync` (defaults when the row is missing; not created), `PartyMode(rs, "sales"|"purchase")`, `DefaultInvoiceLabelAsync`, `ActiveLabelsAsync`. |
| `InvoicePrint` (scoped) + `InvoiceTemplates` (static) | `shared/invoice_print.py`, `models/invoice_template.py` | `SalesAsync(invoice, ct, items?, products?, charges?, template?, templateGiven, company?, numberFormat?)`, `PurchaseAsync(...)` (bulk callers pass preloaded rows: no per-invoice queries), `TemplateForAsync("sales"|"purchase")`, `E(text)` (html.escape). `InvoiceTemplates.Options`, `DefaultOptions`, `BuildTotalsTable`, `ItemsTableMetrics`, `Render`. |
| `InvoiceLookups` (scoped) | the editors' `api_*` | `ProductsAsync`, `CustomersAsync`, `SuppliersAsync`, `ChargeAccountsAsync`, `SalesOrdersForPickerAsync`, `PurchaseOrdersForPickerAsync`, `CategoryRevenueAccountsAsync`, `TaxRateAccountsAsync`, `Range(from, to, month)`. |
| `TrackingSync` (scoped) | `payment_tracking.sync_invoice` / `drop_allocations_for_doc` | `SyncInvoiceAsync("SI"|"PI", id)` (after commit; non-fatal), `DropAllocationsAsync` (falls back to Python's delete until Parties registers `IPaymentTracking`). |
| `PerformanceService` (scoped) | `shared/invoicing_performance.py` | `AvailableYearsAsync`, `PerformanceAsync`, `Chart`, `ChartJson`. |

Models (`Accountix.Invoicing.Models`): `Pager` (100 rows a page, as tracking's pager) and the partial
`Areas/Invoicing/Views/Shared/_ListPager.cshtml`.

## Needs from other modules

- **Inventory** — `IStockCosting` (`LockProductsAsync`, `IssueAsync`, `RecordInAsync`, `ReverseVoucherStockAsync` (both),
  `OnHandAsync`): done, and the tests run on it (below).
- **Parties** — `IPaymentTracking.SyncInvoiceAsync` / `DropForDocAsync` (non-fatal here).
- **Shell** — Parties, Orders and Returns contribute items to the same group names ("Procurement", "Sales"); the
  sidebar must merge same-named groups in `shared/navigation.py`'s order.

## Differences from Python (deliberate)

- Settings rows missing on first use are read as defaults, not created (Python's `get()` adds them, then a GET rolls back anyway).
- Lists page at 100 rows (newest first) with optional `from`/`to`/`month` filters — the user's speed requirement
  (Python lists every invoice on one page, one customer query per row).

## Speed (Samba full year: 2,760 SI, 794 PI; busiest month 2026-05 with 318 SI)

Server time in the test host, fastest of 3 (`SpeedTests`, `[PerfFact]`); Python = the same URL through the Flask
test client on the same database (best of 3). Python's lists show every invoice on one page, so its list time is
for the whole year.

| Page | .NET | Queries | Python |
|---|---|---|---|
| SI list, busiest month (`?from=2026-05-01&to=2026-05-31`, 318 rows, 100 a page) | 9 ms | 9 | 425 ms (all 2,760) |
| SI list, month, page 4 | 8 ms | 9 | — |
| SI list, full year, page 2 | 9 ms | 9 | — |
| SI list `?status=approved` | 10 ms | 9 | — |
| PI list, month / full year page 3 | 8 / 9 ms | 9 | 178 ms (all 794) |
| Invoicing dashboard (2026 / 2025) | 31 / 27 ms | 20 | 168 ms |
| SI editor + print, biggest invoice (12 lines) | 20 ms | 16 | 126 ms |
| PI editor + print, biggest invoice (8 lines) | 15 ms | 16 | 128 ms |
| `api/products`, `api/accounts` | 5 / 6 ms | 7 / 8 | — |

Budgets (800 ms month / 1,500 ms year, ≤ 40 queries) all met; query counts do not grow with rows (lists read
one page with the party name joined; the editor loads items, products and charges in one query each, and the
print reuses them). The Python column is history: measured on 29 Oct while both apps existed (the test that timed
Python is gone with the Python app). The full-year dashboard is identical to Python's to the last digit, now
against the captured answer (`The_full_year_dashboard_shows_the_figures_python_showed`, `Golden/dashboard_perf.json`).

### Proposed indexes (for the coordinator's Alembic revision)

At this size every query is a fast scan; these keep it so as the tables grow:

```sql
CREATE INDEX ix_inv_invoice_items_invoice_id ON inv_invoice_items (invoice_id);
CREATE INDEX ix_inv_purchase_invoice_items_invoice_id ON inv_purchase_invoice_items (invoice_id);
CREATE INDEX ix_additional_charges_doc ON additional_charges (company_id, doc_type, doc_id);
CREATE INDEX ix_inv_invoices_company_date ON inv_invoices (company_id, invoice_date);
CREATE INDEX ix_inv_purchase_invoices_company_date ON inv_purchase_invoices (company_id, invoice_date);
```

## Many users

Locks as Python: the document (`DbLocks.LockAsync`, FOR NO KEY UPDATE, status re-checked after the lock) →
order lines (`OrderLinkage.LockOrderLinesAsync`, ascending) → products (`IStockCosting.LockProductsAsync`) →
report totals (Accounting's ordered upserts). Numbers: placeholder, then `SI-/PI-YYYYMM-<id>` in the same transaction.
Stale `edit_version` refused 409 `{stale: true}` with who/when from the audit trail. Conflicts answer 409, never 500.

Tests (`ManyUsersTests`, all pass, on the real costing engine):
- six people approving one SI → one journal, stock moved once (one issue per line, on hand 100 → 97); five answered
  "Cannot modify approved invoice";
- two SIs selling the last units at once (5 on hand, 4 each) → one approved, the other refused (400, not on hand),
  on hand 1, one issue row;
- six people approving one PI → one journal, one receipt per line;
- six new SIs saved at once → six distinct numbers, each ending in its own id, `invoice_number = voucher_number`;
- six SIs billing 2 each of an order line with 5 open, at once → exactly two approved, invoiced 4, order "partial";
- two saves from the same copy (same edit_version) → one saved, the other refused stale.

The race between the two apps (3 Python + 3 .NET requests approving one SI at one instant: one journal, no 500)
passed while both apps existed; it was deleted with the Python app. The locks it proved are unchanged.

## Tests (`tests/Accountix.Invoicing.Tests`, `ACCOUNTIX_TEST_DB=invoicing`): 42 pass, 0 skipped, no Python

The test host boots Invoicing + Accounting + **Inventory's real costing engine** (`Fx.HostAsync`). The old stand-in
(`ZzFakeStockModule`) is kept only behind `INVOICING_FAKE_STOCK=1`; the stock assertions need the real engine.
Every test builds its own data through .NET (fresh customer, supplier and two products per test, `Fx.PartiesAsync`;
company B's inventory settings pinned to the captured ones, `Fx.PinSettingsAsync`); nothing reads leftovers or the
company-1 demo. Passes on a copy rebuilt fresh with `tools/Accountix.RefDb --only invoicing` and again on the copy it left.

- **Unit** (`TotalsTests.cs`): Python float semantics (round half-even, Neumaier `sum`, repr, `:g`, fixed),
  money formatting, JSON as Flask writes it; `test_invoice_splits.py` (allocate, revenue splits, output tax splits,
  Example B, absorbed and by-weight charges), the sales §8 chain, label splits; `test_doc_checks.py`; `test_order_linkage.py`.
- **Golden: Python's answers, captured 29 Oct 2026** (`InvoiceGoldenTests.cs`, fixtures in `Golden/`, capture scripts
  in `Golden/capture/`, kept for the record). These were the parity tests against the Python app; the capture ran
  the same steps through the Python routes on fresh parties and wrote what Python stored, normalised exactly as
  the test normalises .NET's (`Snapshot.cs` ↔ `capture/_snapshot.py`: the same SQL; product / party / document ids
  as roles, account ids as **codes** with the test's own party accounts by name, document numbers as roles, the
  run's tag as `TAG`, exact decimals). Each fixture's `inputs` holds the payloads built for stand-in ids
  (`Fx.Sentinel`); the test checks it sends the same ones. After **every** step the step's answer and the snapshot
  must equal Python's: the documents (header, lines, charges), `stock_ledger`, `stock_layers` (order included),
  `layer_consumptions`, `stock_cost_adjustments`, product on-hand / cost, and every journal of the documents with
  its lines (CADJ included), **to the paisa**.
  - `sale_combined.json`: stock receipt, then a combined-mode SI (discount, 18% tax, further tax, WHT, billed /
    absorbed / expense charges);
  - `sale_per_line.json`: per-line SI (two rates, per-line discounts, per-item charges by weight and manual);
  - `purchase_full.json`: PI with combined discount, absorbed carriage, per-item freight, billed + expense charges,
    18% tax, WHT (landed cost, last line takes the remainder; the layers it opens);
  - `backdated_history.json`: PI on 1 May, PI on 15 May at higher prices, a sale on 20 May, a sale **back-dated**
    to 10 May (the 20 May sale re-costed, `CADJ-SI` journals), then the back-dated sale unapproved (re-costed back)
    and the 15 May purchase unapproved (its issues carried short); the unapprove messages included;
  - `dashboard_label.json`: the invoicing dashboard's performance band (KPIs, monthly table, chart geometry and
    `perf-data`) for a label of the test's own, after a receipt, the combined SI and the full PI;
  - `dashboard_perf.json` (`SpeedTests`, `[PerfFact]`): the full-year dashboard of the read-only speed-test
    database (latest year and 2025), Python's float sums to the last digit; the fixture records the database's
    document counts and the test refuses a different database by name.

  Checked both ways: all six pass as is, and all six fail with `ACCOUNTIX_GOLDEN_PERTURB=1` (one figure of the
  first line changed) and with `ACCOUNTIX_GOLDEN_PERTURB=last` (e.g. an AP credit 11000 → 12000, charges payable
  77.77 → 77.78, the chart data).
- **Dropped with the Python app**: the editor markup comparison (the whole SI/PI editor body and script blocks against
  the Jinja template) is not frozen as a fixture: it is markup, not figures, and would pin every future UI change.
  What it guarded that still matters is kept as `The_editor_carries_its_document_in_valid_script` (saved and new
  editors render; no `&quot;` / `&#x` inside `<script>`, E2E finding F1). The cross-app race (see Many users) and
  `Python_times_for_the_same_pages_are_recorded` (Python's times stay in the Speed table) are deleted.
- **Rules** (`InvoiceRuleTests.cs`): numbering and stale edits; approve locks, unapprove takes the journal and
  stock off, delete; SI unapprove refused for an approved sales return and for an FBR-reported invoice; **PI unapprove
  refused for an approved purchase return** (and allowed once that return is unapproved); validation and range
  refusals with Python's words; order billing moves the balance, refuses over-billing, unapprove restores it;
  lists / editor / dashboard query budgets; lookups; retired pay route; module refusal.
- **Speed** (`SpeedTests.cs`, Samba, `[PerfFact]`: skipped only when the speed-test database is missing): budgets above.
- **Browser**, 320 × 700 mobile (scratch host on :5303): SI and PI editors (new, draft, approved), both lists, the
  month list and the dashboard have no sideways scroll and no script errors; a `fetch` save + delete from the page passes
  the antiforgery check.

`Services/InvoiceDesigns.cs` / `InvoiceDesigns.g.cs` (template `build_body`) were added by Platform; Invoicing does not use them.
