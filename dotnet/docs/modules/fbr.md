# FBR Digital Invoicing: status

**Ported**: all 8 routes of `fbr_app` (blueprints `fbr_dashboard`, `fbr_settings`), the FBR client
(`fbr_client.py`), the payload mapper (`fbr_mapper.py`) with the part of `shared/invoice_totals.py` it uses
(`charge_pools_from_rows`, `sales_totals`), both templates and `NAV["fbr"]`.

Nothing was handed over from Fixed Assets: `grep -ri fbr src/Modules/Accountix.FixedAssets` (and its tests)
finds no file, so there was nothing to copy.

| Route (Python endpoint) | Method | Right (rights_guard) | Status |
|---|---|---|---|
| `/fbr/` (`fbr_dashboard.dashboard`) — submissions list, `?status=` filter, "not configured" state | GET | fbr_dashboard view | ✅ |
| `/fbr/submit/<invoice_id>` (`submit_invoice`) — Send to FBR (invoice page button) | POST | fbr_dashboard approve | ✅ |
| `/fbr/validate/<invoice_id>` (`validate_invoice`) — Validate (invoice page button) | POST | fbr_dashboard edit | ✅ |
| `/fbr/retry/<record_id>` (`retry_submission`) | POST | fbr_dashboard approve | ✅ |
| `/fbr/record/<record_id>` (`view_record`) — the JSON modal | GET | fbr_dashboard view | ✅ |
| `/fbr/status/<invoice_id>` (`check_status`) | GET | fbr_dashboard view | ✅ |
| `/fbr/settings/` (`fbr_settings.index`) — admin only | GET / POST | fbr_settings view / edit | ✅ |
| `/fbr/settings/test-connection` (`test_connection`) — admin only | POST | fbr_settings edit | ✅ |

Every JSON answer is Flask's `jsonify` byte for byte (sorted keys, compact, `ensure_ascii`, closing newline), so the
invoice page (Invoicing) and the dashboard script read the same keys: `ok`, `error`, `message`, `record_id`,
`fbr_invoice_number`, `data`. Refusal texts are Python's.

## Files

- `FbrModule.cs`: services, the named `HttpClient` "fbr" (IHttpClientFactory, 30 s timeout), sidebar (Dashboard;
  Settings gated to admins; both hidden without the view right; "doc" icon as `icons.html` falls back to).
- `Controllers/FbrDashboardController.cs`, `Controllers/FbrSettingsController.cs`.
- `Services/FbrMapper.cs`: `build_fbr_invoice` + `sales_totals`. `Build(...)` is pure (rows in, payload out).
- `Services/FbrClient.cs`: heartbeat / validate / send / status, `_body` (empty → `{}`, non-JSON gateway page →
  `{"error": "FBR answered 502: …"}`), network failures → `(False, 0, {"error": …})`, never an exception.
- `Services/FbrSubmissions.cs`: `record_submission` / `retry_submission`.
- `Services/FbrConfigs.cs`: `FBRConfig.get()` (first row by id; created under `serialize("first-use", "fbr_config", company)`
  only when settings are saved), `base_url()`.
- `Services/Py.cs`: Python's values for byte parity — `round(x, n)` on the exact binary value (half-even), float
  `repr`, `json.dumps` (indent / separators / sort_keys / ensure_ascii), `json.loads` (int vs float, key order),
  `repr()`/`str()` of what it returns, `float(Decimal)`.
- Views: `Areas/Fbr/Views/FbrDashboard/Dashboard.cshtml`, `Areas/Fbr/Views/FbrSettings/Index.cshtml` (markup, ids,
  classes and scripts as the Jinja templates; scripts contain no Razor expressions).

## How it matches Python

- **Payload**: Python computes on floats and `json.dumps` prints `0` and `0.0` differently, so the mapper carries
  Python ints and floats apart (`x or 0` gives the int 0, a Money column a float), rounds with Python's algorithm and
  prints Python's float repr. Item and charge rows are read without ORDER BY, the same query Python runs, so they come
  back in the same order.
- **Records**: on INSERT, SQLAlchemy leaves out a `None` whose column has a default, so FBR's `null` becomes `""` in a
  new record but `NULL` on a retry's UPDATE. Ported as such. `response_json` is pretty-printed for a dict, kept raw
  for anything else on the first attempt and always JSON-dumped on a retry (as Python).
- **Locks**: Send and Retry lock the invoice `FOR NO KEY UPDATE` (`DbLocks.LockAsync`, Python's
  `scoped_get_404(..., for_update=True)`, `key_share=True`) **through the call to FBR**, then re-check "already
  reported" and "approved" under the lock; Retry re-reads its record after the lock (Python's `refresh`). Same lock as
  Python's, so the two apps queue behind each other.
- **Never cancelled by the browser**: the FBR call and the record save use `CancellationToken.None`. Once FBR has the
  invoice the answer must be kept even if the person closed the tab.

## Deliberate differences

- Network errors: the message has requests' shape (`HTTPSConnectionPool(host='…', port=443): Read timed out. (read
  timeout=30)` / `…: Max retries exceeded with url: … (Caused by <.NET message>)`); the cause text is .NET's.
- Timeout: 30 s for the whole call (requests: 30 s connect and 30 s per read).
- GET calls (heartbeat, status) send no `Content-Type` header (requests sent `application/json` on a bodyless GET).
- Where Python crashed (a 500 with no record), .NET keeps going: an FBR answer that is a JSON list on success (Python:
  `list.get` AttributeError) records the attempt with empty numbers; a dict or list in `error`/`message` is stored as
  its repr (psycopg2 could not adapt it).
- A missing invoice/record answers a bare 404 (Python rendered its 404 page).
- `FBRConfig.get()` on read paths returns an unsaved default row (Python flushed one and rolled it back: same result).

## What other modules need to know

- **Invoicing** (invoice page): the "Validate FBR" / "Send to FBR" buttons POST to `/fbr/validate/<id>` and
  `/fbr/submit/<id>` with `fetch` (the shell adds the antiforgery header). The "reported to FBR cannot be unapproved"
  check stays in Invoicing: `fbr_invoice_records` row with `status = 'success'` for the invoice.
- No contract added to Core; nothing needed from other modules.

## Tests (`tests/Accountix.Fbr.Tests`, 38, `ACCOUNTIX_TEST_DB=fbr`; no Python needed)

FBR is never reached: `FakeFbrModule` (test assembly) replaces the named client's handler with `FakeFbr`, which logs
every request and answers as told. Every test builds its own data through .NET (EF inserts mirroring what the
invoice form writes, `FbrData`; product SKUs, FBR's `itemCode`, are derived from the unique invoice number).

**Golden fixtures** (`Golden/*.json`, compared by `Golden.Match`; the Python that produced each is in
`Golden/capture/*.py`). On 29 Oct 2026, before the Python app was retired, each scenario was played through the Python
app on exactly the data the .NET test builds, and Python's answer, normalised by the same C# function the test applies
to .NET's answer, was written to the fixture; .NET's answer equalled it in every case (the submission scenario was
captured twice, identically). With
`ACCOUNTIX_GOLDEN_PERTURB=1` every golden test fails (checked; a hand-edited `taxCharged` in a fixture copy fails too).

| Fixture | Test | What it pins |
|---|---|---|
| `MapperPayloads.json` | `MapperParityTests.Payloads_equal_pythons_byte_for_byte` | Six crafted invoices (combined / per-line tax, further tax, withholding, billed / absorbed / expense / zero / negative / unknown-treatment charges, fractional quantities, 2.675-style ties, empty lines, no product, NULL HS code, DN / CN numbers, non-ASCII text, and a NULL payment status → Python's error text): `request_json` (indent 2) **and** the wire body, byte for byte. Normalised: the run's tag → `TAG` |
| `MapperFullYear.json` | `MapperParityTests.Every_invoice_of_a_full_year_maps_as_in_python` ([PerfFact], read only) | All 2,760 Samba sales invoices on the speed-test DB: a 64-bit SHA-256 fingerprint of Python's two strings per invoice id |
| `SubmissionScenario.json` | `SubmissionParityTests.Send_retry_validate_and_status_match_python` | The 17-step scenario (Send failing with a JSON error, Retry against an HTML 502 and an answer without `error`, Retry succeeding, Send/Retry on a reported invoice, Status, the record's JSON, Validate ×3, a payload that cannot be built, a draft, success with a numeric number and `null` id, an empty answer, status without a record): every HTTP status and JSON body, every request sent to FBR (URL, body, token) and every `fbr_invoice_records` row. Normalised: tag → `TAG`, invoice ids → roles, record ids → `rec_<role>`, owner_b's id/name → `owner_b`, times → digit pattern |
| `SettingsScenario.json` | `SubmissionParityTests.Settings_and_test_connection_match_python` | Settings saved twice + test connection (working / broken FBR): statuses, redirects, answers, heartbeat requests, the settings row |
| `CPythonRoundRepr.json` | `PyTests.Round_and_repr_match_cpython_on_many_values` | `repr(x)`, `repr(round(x, 2))`, `repr(round(x, 0))` from CPython for 20,014 doubles (the inputs' IEEE bits are in the fixture) |
| `CPythonAnswerRoundTrips.json` | `PyTests.Answers_read_and_written_back_match_cpython` | `json.loads` → `json.dumps(indent=2)` / `repr` / sorted compact dumps of five FBR-like answers |

Also (plain .NET): `PyTests` fixed cases (2.675, 0.125, 1e16, 1e-05, -0.0, 5e-324, dumps escaping); **Pages**
(`FbrPageTests`): not-configured state + Send refused; list newest first, Retry only on failed rows, escaping, status
filter, empty state; Send uses the live/sandbox URL and token and keeps the record; network failure recorded; Retry
sends the invoice as it is now and refuses drafts and reported invoices; settings saved (admin), test connection;
non-admin with view-only rights: may look, Send refused ('approve', flash or JSON 403), settings → "Only
administrators can access FBR settings." + `/dashboard/`, test → 403 "Admin only"; no module → 403; scripts are not
HTML-encoded.

**Dropped (29 Oct, Python retired):** the race between the Python app and this one pressing Send together (cross-app,
no longer possible; the same-app six-at-once races stay); the live Python timing of `/fbr/` (its numbers stay in the
Speed table as history); `A_missing_payment_status_fails_as_in_python` (folded into `MapperPayloads`, role
`no_payment_status`).

- **320 px**: checked in a browser (Chrome, 320×700, port 5314, FBR-only host): dashboard and settings have no
  sideways scroll (`scrollWidth` 320), the table scrolls inside `.table-wrap`, the JSON modal fits (8–312 px), no
  console errors. Nothing was sent to FBR from the browser.

Result of the last full run (29 Oct, `ACCOUNTIX_PYTHON` unset, on a fresh `Accountix.RefDb --only fbr` copy and on a
used one): **38 passed, 0 failed, 0 skipped**.

## Many users

| Race | Result |
|---|---|
| Six people press Send on one invoice at once (`Race.RunAsync(6)`, FBR answering after 400 ms) | FBR called **once**, one success record, five answers 400 "Invoice already submitted successfully to FBR", no 5xx |
| Three Retries of a failed record and three Sends at once | FBR called **once**, one success record, no 5xx |

Until 29 Oct a race between the Python app (3 threads) and .NET (3 requests) also passed (FBR called once across both
apps); it was dropped when the Python app was retired.

## Speed

Python's times were measured on 29 Oct 2026 before the Python app was retired; they are kept as history (the tests
now assert the .NET budgets only).

| Page | .NET | Python | Budget |
|---|---|---|---|
| `/fbr/` with 3,000 submissions (fbr copy) | 41–55 ms, 8 queries | 250–258 ms | 800 ms / ≤ 40 queries |
| `/fbr/?status=failed` (1,000 rows) | 19–22 ms, 8 queries | — | 800 ms |
| `/fbr/settings/` | 6 ms, 7 queries | — | 800 ms |
| `/fbr/status/<id>` | 7 ms, 8 queries | — | 800 ms |
| `/fbr/`, `/fbr/settings/` on the full-year DB (no FBR settings there) | 6–8 ms, 7 queries | — | 800 ms |
| Payload of Samba's invoice with the most lines | 4.6 ms (5 queries) | — | — |

The list is one query (invoice number and submitter joined); Python's template loaded `r.invoice` and `r.submitter`
per row. Query counts do not grow with rows (asserted ≤ 12).

### Proposed indexes (for the coordinator's Alembic revision)

```sql
-- "already reported?" on every Send/Retry, and Invoicing's unapprove guard
CREATE INDEX ix_fbr_invoice_records_invoice_status ON fbr_invoice_records (inv_invoice_id, status);
-- the payload's lines and charges (also every invoice page)
CREATE INDEX ix_inv_invoice_items_invoice_id ON inv_invoice_items (invoice_id);
CREATE INDEX ix_additional_charges_doc ON additional_charges (doc_type, doc_id);
```

Today these filter the company's rows through `company_id` (the only index). Fine at today's sizes (payload 4.6 ms).

## What remains

Nothing in `fbr_app`. Out of scope here: the invoice page buttons themselves (Invoicing).
