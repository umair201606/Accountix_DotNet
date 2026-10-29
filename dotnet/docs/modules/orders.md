# Orders (sales orders, purchase orders): status

Project `src/Modules/Accountix.Orders` (module key `invoicing`; its links merge into the Invoicing sidebar),
tests `tests/Accountix.Orders.Tests` (`ACCOUNTIX_TEST_DB=orders`).

**Ported**: all 24 routes of `invoicing_app/routes/sales.py` (blueprint `inv_sales`) and `purchases.py`
(`inv_purchases`), both templates per side, and the two sidebar links.

## Routes

| Route (sales: `/inventory/sales`, purchase: `/inventory/purchases`) | Right (rights_guard) | Status |
|---|---|---|
| `GET /` (new order editor) | create | ✅ |
| `GET /<id>` (editor; an unknown id opens a blank order, as `scoped_get`) | view | ✅ |
| `POST /save` (JSON: save, save & approve, edit) | `_json_save`: new → create (+ approve); existing → edit or approve; JSON 403 | ✅ |
| `POST /unapprove/<id>` (JSON) | approve, JSON 403 | ✅ |
| `POST /delete/<id>` (JSON) | delete, JSON 403 | ✅ |
| `GET /list[?status=]` | view | ✅ |
| `GET /deliver/<id>` (SO) · `GET /receive/<id>` (PO): retired, flash + redirect to bill the order | view | ✅ |
| `POST /cancel/<id>` (refused unless unapproved / approved / new) | edit (flash + redirect) | ✅ |
| `GET /api/products?q=` | view \| create \| edit (`_use`), JSON 403 | ✅ |
| `GET /api/customers?q=` (SO) · `GET /api/suppliers?q=` (PO) | `_use` | ✅ |
| `GET /api/accounts?q=` | `_use` | ✅ |
| `GET /api/orders/<customer_id>` (SO) · `/api/orders/<supplier_id>` (PO) | `_use` | ✅ |

Sidebar (`OrdersModule.Navigation`): "Purchase Orders" in **Procurement** and "Sales Orders" in **Sales**, `Order = 20`
(the second item; the other projects filling those groups should use 10 = Suppliers/Customers, 30 = Purchase/Sales
Invoice, 40 = Purchase/Sales Return). Gated on `inventory_settings.purchase_flow == "with_po"` /
`sales_flow == "with_so"` (`OrderFlows`, one read per request; no row → the column defaults), hidden without
`view`. Icons `cart` / `calendar` (templates/partials/icons.html).

## How it is built

- `Services/SalesOrderBook.cs`, `PurchaseOrderBook.cs`: save / approve / unapprove / delete / cancel, one per side
  as Python has one file per side. Every state change: transaction → `LockAsync` (FOR NO KEY UPDATE) → status check
  after the lock → `edit_version` check (save) → billed check, which locks the order's lines
  (`OrderLinkage.InvoicesDrawingOnAsync`). Lock order: document → order lines.
- New orders: placeholder number, insert, then `SO|PO-YYYYMM-<id:0000>` by the company clock (`DocNumbers`), in the
  same transaction; a number sent by a JSON client (anything but blank / `(auto)`) is kept, as Python keeps it.
  Python's column defaults are set on insert: `status=unapproved`, `fulfilment_status=open`, `edit_version=0`,
  `created_at`, line `invoiced_qty=0`.
- `Services/OrderSupport.cs` (`OrderInput`): the JSON read exactly as the routes read it — `float(...)` (a
  ValueError is the 500 page), `strptime("%Y-%m-%d")` (one-digit month/day accepted, bad date = 500), ids handed to
  the INTEGER column as Postgres casts them (`"12"`, `12.0`; `None` = NOT NULL = 500; `0` = FK = 409 "removed by
  another user"), text columns storing what the browser sends (its `g()` sends `0` for an empty field, so an empty
  note or driver name is stored as `"0"`, as Python stores it). Values go through `Py.Dec` (`Decimal(str(float))`).
- `Services/OrderPages.cs`: the editor model (every `{{ ... }}` as Jinja prints it: `None`, `17.0`, the dropped
  discount/withholding columns as `""`), product / account lookups, the editor's inline JSON (`|tojson`, sorted keys,
  HTML-safe, via `PyJson.Dumps(htmlSafe: true)` + `Html.Raw`).
- Views (`Areas/Orders/Views/{SalesOrders,PurchaseOrders}/{List,Form}.cshtml`): the editors were generated from the
  Jinja templates (script kept in the session scratchpad, `orders_work/convert_order_forms.py`): `<style>` and
  `<script>` verbatim, markup expressions replaced one by one; ids, classes and the inline script unchanged (the
  golden editor test holds the inline script's code as a SHA-256 of Python's page, so it stays byte for byte).
- Queries: no `ORDER BY` where Python has none (`order.items.all()`, `/api/orders`): both apps then return the
  scan's order, which after updates is not id order (a test caught it).

### From Accountix.Invoicing (referenced, not copied)

`EditVersions` (`stale_edit` / `bump_edit`, who/when from the audit log), `OrderLinkage.InvoicesDrawingOnAsync`
(+ `lock_order_lines`), `DocSettings` (ReportSettings `party_mode`), `Py` (float rounding/repr, `Dec`, `FN`,
`QtyJson`, `Amount`), `PyJson`, `PyData`. Registered with `TryAddScoped` by `OrdersModule` (so it runs without
InvoicingModule in tests; whichever registers first wins). Invoicing had no order pages yet, so nothing was copied.

## Fixed in both apps (29 Oct, coordinator's request)

Three defects of the Python order editors, found while porting, are fixed in the Python app; the .NET pages are
generated from the fixed templates, so both apps serve the same page again:

1. **Editor stylesheet.** The order templates had lost the editor design system the invoice templates carry
   (`.modal-o`, `.side-panel`, `.tb-w`, toasts, autocomplete…): every modal and the settings panel rendered inline
   under the form, and a phone got a squeezed header. Now `static/css/order_editor.css` (the purchase invoice
   editor's rules, verbatim; a superset of the sales invoice editor's), linked by both order templates before their
   own `<style>`, plus one phone rule in the templates (the paired date / driver fields stack under 480px). Both apps
   link the same file from `/static`.
2. **Purchase editor script**: the two unguarded `chargesCount` lookups threw on an approved PO, the script stopped,
   and the Unapprove button did nothing. Guarded.
3. **Sales editor script**: a reopened SO showed 0.00 everywhere (its lines' amounts were never computed on load), and
   saving it unedited stored totals of 0. The load now runs `recalcRow` on each line, as the purchase editor does.
   **Server guard** (`shared/order_totals.py`, ported as `OrderTotals` in `Services/OrderSupport.cs`): the request's
   `subtotal` / `total_tax` / `total_amount` and each line's `total_before_discount` / `total_price` are kept only
   while they agree with the lines to the paisa (amount = qty × rate with the editors' `Math.round(x*100)/100`,
   plain running subtotal, tax on the subtotal or per line, no discount/charges on an order); otherwise the figures
   the lines give are stored. A request that agrees is stored exactly as sent, so nothing changes for a working page.

Python E2E (port 5063: Chrome refuses 5061, a SIP port): `tests/e2e/test_order_editors.py` (5: a reopened SO shows
its totals and saving it unedited, then approving, keeps them; the server recomputes totals of 0 and keeps figures
within a paisa; an approved PO is unapproved from its editor; both editors at 320px and 1280px with nothing cut off,
no script errors, modals and panel closed until opened), the two order lists added to `test_responsive_pages.py`.
Also run: `test_order_actions.py`, `test_line_entry.py` (39 passed together), order-linkage unit tests (20),
`tests/concurrency -k order` (2).

## Deliberate differences from Python

1. A save without an order date uses the company's today (Python: `date.today()`, the server's date).
2. Delete's except-clause answers the database's message (`MessageText` + DETAIL) instead of SQLAlchemy's `str(e)`.
3. Deleting an SO sets `inv_invoices.sales_order_id` to NULL first (what SQLAlchemy's `invoices` relationship does);
   a PO has no such relationship, so a purchase invoice header still naming it makes the delete fail, as in Python.

Python quirks kept as they are: the list's Delete is a native form POST to a JSON endpoint (the browser shows the
JSON); the "Sales Orders" link is also lit on `/invoicing/sales-return/*` (Python matches the endpoint prefix
`inv_sales`, which takes in `inv_sales_return`); `GET /<unknown id>` opens a blank order.

## Web.Shared change (tiny, additive)

`Refusal.Right` (AccessPipeline.cs) now calls `tempData.Save()` after adding its flash. A `[Right]` refusal
short-circuits at the authorization filter, before the filter that saves TempData runs, so **the "No 'x' rights
here" flash was lost on every `[Right]`-refused page in every module** (found by
`Each_action_needs_its_right_pages_refuse_with_a_flash_and_json_with_403`). `ErrorAnswersMiddleware` already did this.

## Tests (37, all passing with no Python: `ACCOUNTIX_TEST_DB=orders`, perf DB present)

Independent of the Python app since 29 Oct 2026. Every test builds its own data through .NET: `OrdersWorld.cs`
makes a company of owner_b's (the portal's insert, then Platform's `CompanySetup.ProvisionAsync`) with two
customers, a supplier and three products of its own (EF inserts with the master-data forms' values). Most tests
share one such world per run; the list, editor and lookup tests make a fresh one each, so a page shows only what the
test made. Nothing reads company 1's demo data or what earlier runs left. Runs: 37 passed, 0 skipped, on the old copy
and again on a copy rebuilt fresh with `tools/Accountix.RefDb --only orders`.

- **Golden** (`Golden/*.json`, captured from the Python app on 29 Oct 2026 by `Golden/capture/<name>.py`, kept for the
  record; `_world.py` there is `OrdersWorld.cs`'s twin: same company set-up, payloads and line formats). The capture
  ran the same steps through the Python routes on its own fresh company; ids, order numbers, the admin's name and
  approval times are written as roles (`SO1`, `<SO1>`, `C1`, `P2`, `<admin>`, `<at>`), and each test also checks its
  payloads are the ones the fixture was captured for (`Golden.SameInputs`).
  - `so_saved_then_approved` (`A_sales_order_saved_then_approved_writes_what_the_python_app_wrote`): the answers and
    the order + line rows of two drafts (one with `notes: 0`, stored `"0"`) and the approval (subtotal, tax, total,
    quantities, prices, statuses, versions, who, number format);
  - `po_saved_then_approved`: the same for a PO (logistics fields sent as 0, `expected_date: 0` → NULL, totals
    recomputed from the lines on approval);
  - `order_answers_and_refusals`: status codes and JSON of edit / delete while approved, two unapprovals, a stale
    save, delete, delete of a missing order: every refusal text word for word (who/when aside);
  - `totals_recomputed`: totals of 0 for real lines recomputed, figures within a paisa kept as sent (plus the
    concrete figures asserted in the test);
  - `order_editors`: new, draft and approved editors of both kinds: every id, value, class, disabled/checked, the
    textareas, the inline data (`allProducts`, `existingOrderItems`), buttons, approval banner, pills, column
    styles, and a SHA-256 of the inline script's code; the test also asserts the `order_editor.css` link and no
    `&quot;`/`&amp;`/`&#` in the data lines;
  - `order_lists`: both lists and their status filters (an order dated `2026-1-5`, an odd total, a cancelled PO);
  - `order_lookups`: `/api/products`, `/customers`, `/suppliers`, `/accounts`, `/orders/<party>` JSON.

  Every golden test fails with `ACCOUNTIX_GOLDEN_PERTURB=1` (one figure of .NET's answer changed), and a changed
  figure in a row line fails too (checked by editing a fixture copy).
- **Unit** (`OrderInputTests`, 13): dates as strptime, ids/text/float as Python hands them to the columns, numbers
  (placeholder, `(auto)`, `PREFIX-YYYYMM-id`), items shape, template values as Jinja prints them.
- **Flows**: numbering from the row id; unapprove bumps the version, then delete; billed orders (a draft SI with
  lines drawing on two orders, inserted as the invoice editor's Import from order leaves them) refuse unapprove /
  edit / delete with Python's words; a stale copy is refused 409 `stale` "…was changed by <admin> at HH:MM on DD
  Mon…" after a second browser saved (the audit row the full app's audit interceptor writes is inserted by the test,
  as this host boots Orders alone); Cancel POST-only, refuses a cancelled order; Deliver/Receive only redirect (stock
  untouched, 404 for unknown); rights per action (page flash + redirect, JSON 403, sidebar hides refused links);
  bad figures = 500 and nothing saved, customer 0 = 409; sidebar follows `sales_flow`.
- **Many users** (`ManyUsersAndSpeedTests`, N = 6, .NET only): six new orders at once → six distinct numbers, all
  200; six approvals of one draft → one 200, five "Cannot modify approved order", lines replaced once; six
  unapprovals → one; six saves from copies at the same version → one saved, five 409 `stale`. No 5xx anywhere.
- **Speed** (`[PerfFact]`, read-only Samba DB): the .NET budgets below.
- **Dropped with the Python app** (29 Oct 2026):
  - the cross-app race (3 Python threads + 3 .NET clients approving one order: approved once): it needs both apps;
    the same-app six-at-once races above stay;
  - `Dotnet_is_not_slower_than_python_on_the_order_pages` (live Python timing on the perf DB): Python's times stay
    in the Speed table as recorded history.
  - The parity tests that read the demo company (company 1) are now the golden tests above on data of their own.

## Speed (Samba, company 2: 391 SO, 288 PO; busiest month 2026-05; fastest of 3, server time)

The Python column was measured on 29 Oct 2026 (Flask test client, same data) and is kept as history; the live
comparison test went with the Python app.

| Page | .NET | Queries | Python | Budget |
|---|---|---|---|---|
| `/inventory/sales/list` (all 391 orders: the list is not paged, as in Python) | 19 ms | 8 | 92 ms | 1,500 ms |
| `/inventory/purchases/list` (all 288) | 15 ms | 8 | 94 ms | 1,500 ms |
| `/inventory/sales/list?status=approved` · purchases | 18 ms · 18 ms | 8 | | 800 ms |
| SO editor, largest order of the busiest month | 12 ms | 12 | 72 ms | 800 ms |
| PO editor, largest order of the busiest month | 10 ms | 12 | | 800 ms |
| New SO editor (all active products inline) | 7 ms | 9 | | 800 ms |
| `/inventory/sales/api/orders/<busiest customer>` · purchases | 8 ms · 9 ms | 9 | 277 ms | 800 ms |
| `/api/products?q=` · `/api/customers?q=` | 6 ms · 5 ms | 7 | | 800 ms |

Query counts do not grow with rows (the list joins the party name in the one list query; editor lines and picker
lines read their products in one query by id); the reference-DB test pins every order page at ≤ 15 queries.
Proposed indexes: none needed.

## Remaining / for other modules

- Nothing outstanding in this module. Audit rows for .NET saves come from Platform's audit interceptor (the stale-edit
  "who/when" reads them); this project's host boots Orders alone, so the stale-copy test inserts that row itself.
- Invoicing: billing an order from SI/PI (`order_linkage` write-back) stays with Accountix.Invoicing.
