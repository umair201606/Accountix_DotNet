# Parties: status

Customers, suppliers, Invoice Tracking (payment assignment) and the Invoice Register: `invoicing_app/routes/customers.py`,
`suppliers.py`, `tracking.py`, `registers.py`, with `shared/payment_tracking.py`, `shared/master_data.py` (customer and
supplier rules) and `shared/registers.py`. Module key `invoicing` (the sidebar merges with the Invoicing project's).

## Routes

| Area | Routes | Status |
|---|---|---|
| Customers | `/inventory/customers/` (list, `?q=`), `/create` GET/POST, `/edit/<id>` GET/POST, `/delete/<id>` POST, `/check` | ✅ |
| Customer import | `/inventory/customers/template`, `/upload-excel` POST (review screen is the response, nothing in the cookie), `/batch` GET/POST | ✅ |
| Suppliers | the same nine routes under `/inventory/suppliers/` | ✅ |
| Payment & Receipt Feed | `/invoicing/tracking/` (snapshot + in-memory filter, tiles, stale panel, match bar, 100 a page) | ✅ |
| Assign workspace | `/invoicing/tracking/assign/<line_id>` | ✅ |
| Sales / purchase tracking | `/invoicing/tracking/sales`, `/purchases` (open by default, sort, ageing strip, waiting-money bar, paging) | ✅ |
| Invoice history | `/invoicing/tracking/invoice/<SI|PI>/<id>` (money + credit/debit notes, running balance, Apply here) | ✅ |
| Tracking JSON | `api/feed-item/<line>`, `api/auto-split/<line>`, `api/assign/<line>`, `api/auto-match`, `api/apply/<line>/<type>/<id>`, `api/force-close/<line>`, `api/release-force/<line>`, `api/clear-stale`, `api/unassign/<alloc>`, `api/open-invoices` | ✅ |
| Invoice Register | `/invoicing/registers/` (view on demand, Returned column, totals by SQL), `/data` (chunks, totals over the whole filter), `/export?fmt=excel|pdf|csv` | ✅ |
| Bulk print | `/invoicing/registers/documents` (first 20 documents with the page, `view=1` all), `/data?full=1` (100 a chunk, shared A4 assets on the first only), `/documents/export` (the invoices' workbook: Invoices / Items / Charges) | ✅ through Invoicing's `InvoicePrint` / `InvoiceTotals.LineTaxes` (`Services/BulkInvoices.cs`; `SheetAssets.cs` holds Python's `_SHEET_CSS` / `_SHEET_FIT_JS`, copied by script) |

Rights as `shared/rights_guard.py`: `customers` / `suppliers` (view, create, edit, delete; template / upload / batch need
create; `/check` open), `payment_tracking` (view reads, edit writes), the register `_register_doc` (`doc=purchase` →
purchase invoices, else sales invoices; the bare page opens for either). Sidebar items are hidden without the right.

## Shared service: `IPaymentTracking` (owner: Parties)

`Services/PaymentTracking.cs` is the whole of `shared/payment_tracking.py` and is registered as `IPaymentTracking`:
- `ReconcileVoucherAsync` (reconcile_voucher) and `DropForVoucherAsync` (accounting's `_drop_tracking_for_voucher`):
  Accounting's voucher approve / unapprove / save / delete call them (tested through Accounting's own routes).
- **Added for Invoicing:** `SyncInvoiceAsync(docType, id)` (sync_invoice: call after an SI/PI save, approve, unapprove, and
  after a return against it is approved or unapproved) and `DropForDocAsync(docType, id)` (drop_allocations_for_doc: call
  before deleting an invoice). Both save in the caller's transaction.
- Outstanding = total − assigned − returned everywhere; `paid_amount` stays money received.
- Locks as Python: the receipt line (FOR NO KEY UPDATE), then the invoices in id order; unassign locks the line;
  auto-match takes `serialize("payment-match", company_id)` (same advisory key) then the lines, then the invoices.
- Write methods run in the caller's transaction when there is one, else in their own.

Also in the module: `Services/MasterData.cs` (clean_party, imports, batch rows), `Services/InvoiceRegister.cs`,
`Services/BulkInvoices.cs` (bulk print and workbook; the module references `Accountix.Invoicing` for its print code),
`Services/TrackingRules.cs` (states, ageing, due dates, flows).

## For the Invoicing engineer
- Call `IPaymentTracking.SyncInvoiceAsync` / `DropForDocAsync` where Python calls `sync_invoice` / `drop_allocations_for_doc`
  (invoices.py, purchase_invoice.py, sales_return.py, purchase_return.py).
- The bulk print uses `InvoicePrint.SalesAsync` / `PurchaseAsync` with preloaded rows; a change to the single print shows up in
  the register's bulk print too, and `BulkPrintTests` compare it with the documents Python printed
  (`Golden/BulkPrintDocs.json`): a deliberate change to the print means re-deciding that fixture.

## Tests (`tests/Accountix.Parties.Tests`, 75 passed, 0 skipped, `ACCOUNTIX_TEST_DB=parties`, no Python needed)

Since 29 Oct the project runs without the Python app: every comparison with Python is a **golden fixture** captured from
Python before it was retired, and every test builds its own data through .NET.

- **Golden fixtures** (`Golden/<Name>.json`, compared by `Golden.Match`; the Python that produced each is in
  `Golden/capture/<Name>.py`). Each test builds the same deterministic data through .NET (EF inserts as the routes write,
  the engine, the module's routes), in a **fresh company** of its own (`Scenario.FreshCompanyAsync`: Platform's
  `CompanySetup.ProvisionAsync` + `Memberships`, owner B as admin) where company-wide figures matter, with fixed names
  and fixed dates (T = 30 Jun 2026; nothing depends on today). On 29 Oct Python ran the same steps on that data and its
  normalised answer was written as the fixture; .NET matched it then and must keep matching.
  `ACCOUNTIX_GOLDEN_PERTURB=1` changes one value of .NET's answer: all seven then fail (checked).
  - `MatchOldestFirst` (`TrackingGoldenTests`): auto-match on a messy book (a return, a hand split, a partly force-closed
    receipt, CRV / BRV / JV): the result counts and amount, every assignment written (line → invoice, amount, note) and
    each invoice's paid amount and state.
  - `OutstandingFigures`: invoice rows (settled / credited / outstanding / state / overdue days / bucket / due date /
    progress), feed rows, the invoice history with running balance, open invoices with headroom, the outstanding summary.
  - `Refusals`: the six refusal texts of assign / force-close, word for word.
  - `RegisterData` (`RegisterTests`): five `/invoicing/registers/data` chunks (sales and purchase, paging, approved /
    unapproved): counts, the nine totals, the labels and the text of every body and footer cell, on a book with tax
    columns, a credit note, a debit note, a part payment and unapproved documents.
  - `BulkPrintDocs` (`BulkPrintTests`): three `/data?full=1` chunks and two documents pages: every printed invoice
    document byte for byte (combined and per-line tax, a charge, a return, a note with `<`/`&`).
  - `BulkWorkbook`: the invoices' workbook: sheet names and every cell of Invoices / Items / Charges.
  - `CustomerForm` (`PartyPageTests`): the customer row and its ledger account (type, level, parent code, flags) the
    create form saves.
- **Dropped**: the race between the two apps on one receipt (`ManyUsersTests`; the two apps no longer share a database)
  and the live "no page slower than Python" measurement (`SpeedTests`; Python's recorded times stay in the Speed table
  below as history). The contract-hook parity test became a .NET-only test with its concrete answer
  (`The_contract_hook_refreshes_the_cache_of_every_invoice_the_voucher_settled`: both invoices back to unpaid 0.00).
- **Engine** (`TrackingEngineTests`, 29, the engine half of `tests/e2e/test_payment_tracking.py`): feed membership by direction and
  voucher type, ledger payers, ledger search, splits, re-assign replaces, refusals (money, invoice, negative, none, missing),
  unassign, force-close / reopen / partial force-close, state filters, pay / overdue / ageing filters, outstanding summary,
  history both ways, auto-split (complete split), voucher shrunk → review, voucher unapproved / line repointed / invoice
  unapproved → stale + clear, invoice total raised, returns settle (partly / fully), auto-match oldest first + idempotent +
  leaves splits and forced money alone, in-memory filters = SQL, the contract hooks, and "no journal is ever written".
- **Golden + hook** (`TrackingGoldenTests`, 4): the three tracking fixtures above and the contract hook.
- **Pages** (`TrackingPageTests`, 11 + `PartyPageTests`, 5 + `RegisterTests`, 4): every tracking page and API round trip, the
  needs-attention panel, apply here + match over HTTP, registers open on what is owed / sort / returns, rights refusals,
  query budgets (≤ 40 per page), no HTML-encoded text inside `<script>`, voucher unapprove / delete in Accounting refreshing
  the invoices; customers created with their ledger account (`CustomerForm`), duplicates / bad email / negative limit
  refused with the typed values kept, edit renames the ledger account, delete guard, check; Excel template, a 62-row upload
  flagged on the review screen, batch save keeps failed rows; suppliers; register data (`RegisterData`); exports
  (Excel / PDF / CSV, 404 otherwise).
- **Bulk print** (`BulkPrintTests`, 4): `BulkPrintDocs`, `BulkWorkbook`, the page's count / toolbar / clean script; purchase
  side; a chunk runs a fixed 15–16 queries.
- **Many users** (`ManyUsersTests`, 3, one app): six receipts assigned to one invoice at once (exactly one fits; never
  overpaid); six matches at once (each amount assigned once); detach raced with re-splits (detached once, line never over
  its amount).
- **Rules** (`RulesTests`, 6 facts + 8 theory cases) and the smoke test.
- Run: `ACCOUNTIX_TEST_DB=parties dotnet test tests/Accountix.Parties.Tests --artifacts-path <yours>` (about a minute; the
  test project references Accounting, Finance and Platform). 75 passed with `ACCOUNTIX_PYTHON` unset, on a copy rebuilt
  fresh by `tools/Accountix.RefDb --only parties` and on a copy full of earlier runs' rows.
- Browser (Chrome, 320 px, the host built with `-p:AccountixModules=Accounting+Finance+Parties` on port 5304): no page scrolls sideways (feed, `?state=`, assign, sales / purchase tracking, two invoice histories, register blank and full year, customers list / create / edit / batch, suppliers list / create); the assign drawer loads, auto-fills oldest first and saves through the page's own fetch; no console errors.
- **Speed** (`SpeedTests`, 1, Samba full year): every budget below.

## Speed (Samba, company 2: 2,760 SI, 794 PI, 3,118 vouchers; best of 3 in the test host)

The Python column was measured on 29 Oct 2026, before the Python app was retired (Flask test client, best of 3); it is
kept as history and no longer measured.

| Page | Python | .NET | Queries | Budget |
|---|---|---|---|---|
| Sales register, busiest month (2026-05, 318 SI), `view=1` | 376 ms | 61 ms | 13 | 800 ms |
| Purchase register, busiest month | 127 ms | 38 ms | 13 | 800 ms |
| Register data chunk, month | 279 ms | 37 ms | 13 | 800 ms |
| Register data chunk, full year (first / last 200) | 236 / 358 ms | 38 / 39 ms | 13 | 1,500 ms |
| Sales register, full year rendered at once (`view=1`) | 1,241 ms | 134 ms | 13 | 1,500 ms |
| Payment & Receipt Feed (default / all states) | 587 / 540 ms | 65 / 70 ms | 16 | 1,100 ms |
| Sales invoice tracking (open / all, sorted, page 3) | 1,265 / 965 ms | 118 / 101 ms | 19 | 1,500 ms |
| Purchase invoice tracking | 402 ms | 59 ms | 19 | 1,500 ms |
| Invoice history (SI / PI) | 278 / 233 ms | 50 / 41 ms | 24 | 800 ms |
| Assign workspace | 182 ms | 68 ms | 21 | 800 ms |
| Customers / suppliers list | 1,774 / 360 ms | 26 / 10 ms | 7 | 800 ms |
| Register export, month: Excel / PDF / CSV | 1,480 / – / 856 ms | 352 / 1,237 / 25 ms | 13 | 3,000 ms |
| Register export, full year Excel | 5,738 ms | 2.4–3.3 s | 13 | 3,000 ms (see below) |
| Bulk print page, busiest month (first 20 documents) | 110 ms | 25 ms | 16 | 800 ms |
| Bulk print chunk of 100 (month, offset 20 / year, offset 2,600) | 238 / 282 ms | 87 / 95 ms | 15 / 16 | 800 / 1,500 ms |
| Bulk print, the whole month at once (`view=1`, 318 documents) | 737 ms | 235 ms | 16 | 1,500 ms |
| Bulk print page, purchase month | 97 ms | 20 ms | 15 | 800 ms |
| Invoices workbook, month: sales / purchase | 2,005 / 842 ms | 603 / 262 ms | 15 | 3,000 ms |

The Samba book has no assignments yet (`payment_allocations` is empty), so the feed is timed as it is. **Auto-match**, timed on
this module's own copy: 78 receipts onto 114 invoices in 0.3 s (a fixed number of queries; the second run is a no-op).
Python's customer list ran one COUNT per row (`sales_orders.count()`); here it is one query. **Full-year register Excel:**
reading the 2,760 rows takes ~40 ms; the rest is Finance's Excel writer, which styles every cell one by one (~38k cells).
It runs 2.4–3.3 s against Python's 5.7 s, so `SpeedTests` holds that one entry to Python's recorded time (5,700 ms); styling by column in
`Accountix.Finance/Services/ReportExport.cs` would bring it under 3 s (Finance's file, reported to the coordinator).
Bulk print loads a chunk's invoices with their party in one query, then its lines, products and charges in one query each, and
renders through Invoicing's print with the template, company and settings read once: 15–16 queries for 20, 100 or 318 documents.
Browser at 320 px: the documents page does not scroll sideways (each A4 sheet scrolls inside its card, as Python's CSS
intends); 402 documents load in chunks with the shared stylesheet once; no console errors.

**Proposed indexes:** none needed at this size (every page is a fixed 7–24 queries).

## Many users
- Assign: receipt line FOR NO KEY UPDATE → invoices FOR NO KEY UPDATE in id order, then the headroom checks (Python's order); auto-match: advisory `serialize("payment-match", company)` → lines → invoices;
  unassign / force-close / reopen: the line first.
- Races tested: listed under Tests. Conflicts answer 409 through the shared middleware, refusals 400 with Python's words.
- Reads (feed, tracking pages, register, exports) run in one REPEATABLE READ snapshot.

## Differences from Python (deliberate)
- Overdue days and the register's month / year defaults use the company's clock (Python: the server's date).
- A non-numeric amount in the assign / force-close JSON is refused with the usual message (Python answered 500).
- The register footer prints the company profile's name (Python's `company.company_name` was always blank).
