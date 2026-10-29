# Returns: status (sales returns / credit notes, purchase returns / debit notes)

Project `src/Modules/Accountix.Returns` (module key `invoicing`; its sidebar items merge into NAV["invoicing"]),
tests `tests/Accountix.Returns.Tests` (`ACCOUNTIX_TEST_DB=returns`). Ported from `invoicing_app/routes/sales_return.py`,
`purchase_return.py` and their four templates. **All 12 routes are ported.**

| Route | Rights (rights_guard) | Status |
|---|---|---|
| `GET /invoicing/sales-return/`, `/invoicing/sales-return/<id>` (form) | `_doc_page`: create (blank) / view (saved) | ✅ |
| `GET /invoicing/sales-return/api/invoice/<invoice_id>[?return_id=]` | `_use`: view, create or edit | ✅ JSON byte-identical to Python |
| `GET /invoicing/sales-return/api/invoices?q=` | `_use` | ✅ JSON byte-identical |
| `POST /invoicing/sales-return/save` (save / approve, JSON) | `_json_save` + route's `deny_json` | ✅ parity to the paisa |
| `POST /invoicing/sales-return/unapprove/<id>` (JSON) | approve | ✅ parity |
| `GET /invoicing/sales-return/list` | view | ✅ |
| `GET /inventory/purchase-return/`, `/inventory/purchase-return/<id>` | `_doc_page` | ✅ |
| `GET /inventory/purchase-return/api/invoice/<invoice_id>[?return_id=]` | `_use` | ✅ JSON byte-identical |
| `GET /inventory/purchase-return/api/invoices?q=` | `_use` | ✅ JSON byte-identical |
| `POST /inventory/purchase-return/save` | `_json_save` + `deny_json` | ✅ parity to the paisa |
| `POST /inventory/purchase-return/unapprove/<id>` | approve | ✅ parity |
| `GET /inventory/purchase-return/list` | view | ✅ |

Sidebar: "Purchase Return" (Procurement) and "Sales Return" (Sales), `Order = 40` (last in each group, after
Parties 10 / Orders 20 / Invoicing 30), hidden without the section's view right, svg `return`.

## How it is built

- `Controllers/SalesReturnsController.cs`, `Controllers/PurchaseReturnsController.cs`: the routes, line by line as Python
  (float arithmetic via Invoicing's `Py`; stored and posted as `Decimal(str(x))` via `Py.Dec`).
- `Services/ReturnRules.cs`: `validate_return`, `_over_returned` (server cap), lock_products' id reading.
- `Services/ReturnSupport.cs`: Python-shaped JSON (`PyJson`, sorted keys), the rights of a JSON save, and app.py's
  handlers for these JSON endpoints: a costing / period / date refusal or a clash with another user is rolled back and
  answered `409 {"ok": false, "error"}` **whatever the Accept header** (the unapprove `fetch` sends none), plus the
  "refused" audit row (`IAuditTrail.RecordNowAsync`, Python's class names).
- Views `Areas/Returns/Views/Returns/{Sales,Purchase}{Form,List}.cshtml`: style and script blocks copied verbatim by a
  script (`@` → `@@`), markup converted by hand, ids/classes unchanged, so the page JS runs unchanged. The "auto-load if
  editing" block is built by `ReturnFormModel.InitScript()` and written raw (nothing HTML-encoded inside `<script>`;
  tested).
- **Locks, in Python's order:** the return (FOR NO KEY UPDATE, then re-check status / `edit_version`), the invoice
  (FOR NO KEY UPDATE, *before* the over-return check), the source order lines (`OrderLinkage.LockOrderLinesAsync`), the
  products (`IStockCosting.LockProductsAsync`), then report totals (GlSummary). One transaction; invoice tracking
  (`TrackingSync` → `IPaymentTracking.SyncInvoiceAsync`) after commit, non-fatal, as Python's `sync_invoice`.
- Numbers: placeholder, then `CN-` / `DN-YYYYMM-<id>` by the company clock (`DocNumbers`). `edit_version`: Invoicing's
  `EditVersions.StaleAsync` on save, `Bump` on unapprove.
- Stock: `RecordInAsync("SRV", …, unit = original_issue_cost)` / `IssueAsync("PRV", …, unit = original_receipt_cost,
  prefer = ("PI", invoice))`, `ReverseVoucherStockAsync` on unapprove; `inv_stock_movements` rows as Python.

### Taken from Accountix.Invoicing (referenced, not copied; nothing return-specific existed there)
`Py`, `PyJson`, `PyData`, `PyValueException` (Python float/JSON semantics), `EditVersions`, `TrackingSync`,
`OrderLinkage` (`LockOrderLinesAsync`, `ApplyReturnWritebackAsync`, `Tally`). `ReturnsModule` `TryAdd`s the three
services so the module also runs without `InvoicingModule`.

## Fixed in both apps (29 Oct, coordinator's decision)

- **A return being edited no longer counts itself as "previously returned".** The form's lookup
  (`/api/invoice/<id>`) summed every draft and approved return of the invoice, the open draft included, so a reopened
  draft returning everything still returnable showed max 0 and its approval was refused ("exceeds max returnable
  (0.0)"). The form now sends `?return_id=<its id>` (blank for a new return) and the lookup leaves that one return out;
  every other draft or approved return keeps counting exactly as before. The server-side cap (`_over_returned`) was
  already right and is unchanged: it counts approved returns only and already excludes the return being approved; a
  plain save has no cap, as before. Python: `invoicing_app/routes/{sales,purchase}_return.py` (`api_invoice_detail`)
  and both `form_return.html` (`loadInvoiceData`); .NET: the same in both controllers and views (views regenerated from
  the fixed templates).
- **Sales return list** (Python template): `(x or 0)|amount_format(0)` instead of `x or 0|amount_format(0)`, which printed
  the raw float for every non-zero return. **Mojibake** "â€”" → "—" in both Python form templates.
- Python E2E (`tests/e2e/test_sales_return.py`, +3, Playwright through the server's routes): a reopened sales draft
  returning everything shows previously 0 / max 5 in the form and approves through the page's own buttons, a new return
  still sees the draft, the approved return then caps a further one; the same for a purchase draft; the list prints
  formatted amounts. Red without the fix, green with it. With the other return-related E2E files
  (`test_books_integrity`, `test_payment_tracking`, `test_inventory_pipeline`, `test_responsive_pages`):
  **159 passed** (`E2E_PORT=5064`).

## Tests (`tests/Accountix.Returns.Tests`): **23 passed, 1 skipped** (the browser server, run on demand), no Python

Run: `ACCOUNTIX_TEST_DB=returns dotnet test tests/Accountix.Returns.Tests --artifacts-path <yours>`. Nothing else is needed:
no Python, no build switches. The tests boot the real services the return routes call (Inventory's `StockCosting`,
Parties' `PaymentTracking`, the general ledger) plus Invoicing and Orders, whose own routes build the documents to return
against (`World.CreateAsync`: products, customer and supplier inserted with the Python models' defaults, then purchase and
sales invoices approved through `/inventory/purchase-invoice/save` and `/inventory/invoices/save`). Every test builds its
own world, so nothing depends on what earlier runs left in the copy; verified on a copy rebuilt fresh with
`dotnet run --project tools/Accountix.RefDb -- --only returns` (company B empty) and again on top of that run's leftovers.
The old `-p:RealStock` / `-p:RealTracking` switches and the recording stand-ins (`FakeServices.cs`) are gone: the real
engines are the only way now.

**Golden fixtures** (captured from the Python app on 29 Oct 2026, before it was removed; `Golden/*.json`, written by
`Golden/capture/*.py`, kept for the record). Each capture script ran the same steps through the Python routes on its own
fresh world (`Golden/capture/_world.py` is `World.cs` + `Payloads.cs` + `Dump.cs` in Python), read the rows with the same
SQL text (`Dump.cs`) and wrote them in the same canonical form: decimals without trailing zeros, ids → roles (P0, P1,
CUSTOMER, SUPPLIER, OWNER, SI/PI/CN/DN), the world's tag → TAG, document numbers → `<SI>`/`<PI>`/`<CN>`/`<DN>`, entity
accounts → parent code + party name, a purchase invoice's `created_at` date → `<created>`. Each fixture also holds the
payloads the capture sent (`inputs`), and the test first checks it sends the same ones (`Golden.SameInputs`).

| Test | Fixture | What it holds Python's answer for |
|---|---|---|
| `SalesReturnTests.Approve_and_unapprove_write_what_python_wrote` | `sales_return_approve_unapprove.json` | The form's two lookups (invoice detail with cost bases, search); then after draft save, approve and unapprove: the JSON answer, the return row and every item column, the journal (Sales Returns, output tax, AR on the customer's own account, Inventory, COGS; 1.25 × 99.99 rounded on its way to the journal, gross 3,226.12 posted 3,226.13), the `SRV` stock rows (qty, cost, running figures), the products' layers and figures, the stock movements, the invoice's `payment_status` / `paid_amount` (partial after the credit note, unpaid again after unapprove) |
| `PurchaseReturnTests.Approve_and_unapprove_write_what_python_wrote` | `purchase_return_approve_unapprove.json` | The same for a debit note with tax 17%, WHT 4.5% and freight not credited back: AP, WHT reversal, Inventory at landed cost, input tax reversal, price difference; the `PRV` issue drawn from the invoice's own layers |
| `ReopenedDraftTests` (sales, purchase) | `reopened_draft_sales.json`, `reopened_draft_purchase.json` | The lookup for a new return (the draft returning all 5 counts: previously 5 / max 0) and for the reopened draft itself (`?return_id=`: 0 / 5), byte for byte after normalising |

Checked both ways: each golden test passes as is, and fails with `ACCOUNTIX_GOLDEN_PERTURB=1` (one figure of .NET's
answer changed) and when a figure of a later step (the approved journal's Inventory debit, the debit note's stock
quantity) is changed in the fixture.

**Kept, .NET only, concrete expectations in Python's words** (they were `[PythonFact]` only because the world was built
through Python): refusals (browser max not trusted, row validation, date before the invoice, malformed date 409, nothing
to return, approved return cannot be changed, second return capped, unknown invoice 404 JSON, unapprove twice); stale copy
(409 with `stale: true`, who/when); debit note over the purchase; closed period (409, nothing saved); order balances (a
credit note on an invoice billed from a sales order made through Orders' route: 5 → 3 `partial`, unapprove → `invoiced`);
rights (the restricted manager's grid written as Platform's Settings → Rights form writes it: approve and unapprove 403
JSON in Python's words, draft save allowed, purchase returns hidden and refused; without the Invoicing module, 403);
pages (markup, ids, query counts, scripts not HTML-encoded; the saved-return page now always checked on a draft of its
own), the sidebar; the reopened-draft approval and cap; unit `ReturnRulesTests` (5). After each golden approve and
unapprove the engine's own `AssertInvariantAsync` must hold for the products (was Python's `costing.assert_invariant`).

**Dropped:** the two cross-app races (Python and .NET users at one instant: there is one app now) and
`Dotnet_is_not_slower_than_python_on_the_return_pages` (live Python timing; the Python column below stays as the record).
The .NET speed budgets (`Return_pages_on_a_full_year_are_quick`) stay.

- **320px** (Chrome, `BrowserHost.Serve_for_the_browser` on port 5310, `RETURNS_SERVE=<seconds>`): both lists, both new
  forms (pick an invoice, Return All, save through the page's own fetch), approved sales/purchase returns reopened (lines
  auto-loaded), the Accounting-entry panel: no page wider than 320, tables scroll in their box, no console errors.

## Many users (all pass; `Race.RunAsync(6, …)`, real engine, no Python)

- Six people each credit 4 of 10 sold at once → exactly two credit notes, 8 returned, two journals (the invoice lock
  serialises the cap); the other four refused "… remain returnable on the invoice", no 500.
- Six people approve one sales-return draft → posted once, one stock receipt. Same for a purchase-return draft.
- The Python-vs-.NET races (3 + 3 crediting 4 of 10; 3 + 3 approving one draft) passed while both apps ran; they were
  removed with the Python app.

## Speed (Samba full year, `[PerfFact]`, fastest of 3; Python = same URL through the Flask test client, recorded 29 Oct while it still ran)

The return lists have no date filter (Python's neither): they show every return of the year (68 SR, 34 PR), so the year
budget (1,500 ms) applies; the forms' pickers list every approved invoice of the year (2,760 SI / 794 PI).

| Page | .NET | Queries | Python |
|---|---|---|---|
| `/invoicing/sales-return/list` | 13–17 ms | 7 | 52–122 ms |
| `/inventory/purchase-return/list` | 9–13 ms | 7 | 56–104 ms |
| `/invoicing/sales-return/` (2,760-invoice picker) | 33–55 ms | 7 | 253–950 ms |
| `/invoicing/sales-return/<id>` | 28–51 ms | 10 | 228–614 ms |
| `/inventory/purchase-return/` | 16–23 ms | 7 | 103–290 ms |
| `/inventory/purchase-return/<id>` | 16–22 ms | 10 | 104–223 ms |
| `/invoicing/sales-return/api/invoice/<busiest month's longest SI>` | 17–20 ms | 11 | 128–221 ms |
| `/invoicing/sales-return/api/invoices` (+ `?q=`) | 8–15 ms | 7 | 91–171 ms |
| `/inventory/purchase-return/api/invoice/<longest PI>` | 11–12 ms | 10 | 72–101 ms |
| `/inventory/purchase-return/api/invoices` (+ `?q=`) | 8–10 ms | 7 | 43–73 ms |

Every page is a fixed number of queries (no N+1: the lists and pickers are one joined query each; the invoice lookup
reads lines, returned quantities, SKUs and cost bases in one query each). Proposed indexes: none needed.

## Differences from Python (deliberate)

- An unbalanced journal (only reachable through a hand-made payload) answers 409 JSON; Python raised ValueError → 500.
- 403 JSON from the shared rights refusal has its keys in another order (`{"ok":false,"error":…}`); same text.

## Python behaviour kept identical

- Purchase-return picker and lookup date the invoice by `created_at`, not `invoice_date`.
- A sales return credits the customer's own ledger even when the invoice posted to an override party account
  (`inv_sales_returns.party_account_id` is never set); a purchase return uses the invoice's override.
- A new return saved with nothing to return against (no `original_invoice_id`) is a 500, as in Python.

## Needs from other modules

- **Invoicing**: "an invoice with approved returns cannot be unapproved" lives in the SI/PI unapprove routes
  (Invoicing's). The Returns side needs nothing more from Invoicing.
- **Inventory** (`IStockCosting`) and **Parties** (`IPaymentTracking.SyncInvoiceAsync`): done; the tests always run with
  the real services.
- **Platform** (`IAuditTrail`): used for the "refused" row; skipped silently until registered.
