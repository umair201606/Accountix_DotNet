# Speed and many users: rules for every module (29 Oct 2026)

User requirement, 29 Oct: pages and reports must load quickly on big data, and many users working in ONE company at once must work smoothly, with no conflicts.

The user's words: "make sure the reports especially bigger reports like sales invoice in a month loads quickly. and also other reports in financial module loads quickly. and upon multiple users on single company it operates smoothly without conflicts."

## 1. Speed: budgets and how to prove them
**Speed-test database.** A full financial year of Samba Private Limited (made by the former Python seeder; restore it with tools/restore_perf_db.sh), about 7,500 documents (2,700 SI, 650 PI, 1,500 receipts, ...) posted through the Python app. It is being seeded now into `accountix_ref_perf`. `dotnet/tools/perf.json` appears when it is ready, and `[PerfFact]` tests skip until then.
- It is READ-ONLY for you: time pages and exports, never post to it (sign-in session rows excepted).
- Write your speed tests now; they run once it's there.
- **On another machine (no Python):** both speed-test databases are kept as `pg_dump -Fc` files at Alembic
  0018 in `~/Downloads/accountix_data_backup_2026-10-29/` (`accountix_ref_perf.dump` 4.2 MB,
  `accountix_ref_perf_large.dump` 11.6 MB; not in the repository). `dotnet/tools/restore_perf_db.sh
  [--host H --port P --user U] [--replace]` restores them (Postgres 16+ client tools on PATH or `PG_BIN`)
  and writes `dotnet/tools/perf.json` from the restored data: company, busiest month, year, row counts.
  `[PerfFact]` / `[PerfLargeFact]` also need `tools/reference.json` next to it. `--help` lists the options.

**New in TestSupport** (already built; just use it):
- `await using var host = await ModuleHost.StartOnAsync(Perf.ConnectionString, typeof(YourModule).Assembly /*, deps */);`
- `var c = host.ClientFor(Perf.AdminUserId, Perf.CompanyId);`
- `(await host.MeasureAsync(c, url)).Within(maxMilliseconds, maxQueries);`
  - It warms up once, then takes the best of 3 runs.
  - On failure it prints the time, the query count and the most repeated SQL ("240× SELECT … WHERE id = ?" is an N+1).
- `host.Queries` (Reset / Count / Repeated()) works on every ModuleHost, including your normal page tests. Add query-count asserts on list pages so an N+1 can never come back.
- `Perf.BusiestMonthStart` / `BusiestMonthEnd` / `BusiestMonth` ("yyyy-MM", the month with the most sales invoices), `Perf.YearStart` / `YearEnd`, `Perf.Rows("inv_invoices")`.
- `ACCOUNTIX_TIMINGS=<file>` (environment, optional): every `MeasureAsync` appends "time, queries, status, url" to that file. Copy those numbers into your "Speed" table.
- The speed-test DB is READY: Samba, company 2, admin user 1, busiest month 2026-05 (318 sales invoices); 2,760 SI, 794 PI, 3,118 vouchers, 6,809 journals / 26,833 lines, 10,198 stock-ledger rows, 46,321 audit rows. A 4× larger company is being seeded into a second database for the final checks.
- Example results (Accounting): the voucher register for the year takes 256 ms with 12 queries; the month takes 35 ms. The counter found a 212-query N+1 in Accounting's register "full documents" view; it is fixed with a batched loader (`DocsAsync`: one split query per 500 ids).

**Budgets** (server time in the test host, fastest of 3, on the speed-test DB):
- Any list or report for **one month** (the busiest), e.g. the sales invoice list or register for a month: **≤ 800 ms**.
- Full **year** reports (TB, P&L, BS, SOCIE, cash flow, registers for the year with paging, stock valuation, aging): **≤ 1,500 ms**.
- General ledger, all accounts, one month: **≤ 1,500 ms**.
- Excel/PDF exports of those: **≤ 3,000 ms**.
- Queries per page must not grow with the number of rows: **≤ 40** for any page. There is no N+1 anywhere.
- .NET must never be slower than Python on the same page. Record both times in a "Speed" table in your docs/modules/<name>.md.

**How:**
- `AsNoTracking()` for reads, and `Select` only the columns the page shows. Load related rows in ONE query by a list of ids, then use dictionaries; never query per row and never lazy-load.
- Aggregate in SQL (GroupBy/Sum) or read `gl_daily_balances` for account balances. Never pull a year of journal lines into C# to add them up.
- Page lists the way Python does (100 rows), with totals computed by SQL over the whole filter.
- Reports read one snapshot: a REPEATABLE READ read-only transaction, as Python's `consistent_snapshot`.
- Look at EXPLAIN (ANALYZE) for every big query. If an index is missing, do NOT create it from module code: put the CREATE INDEX in your docs under "Proposed indexes" and tell the coordinator in your final report. The coordinator adds them as one migration in `src/Accountix.Migrations` (appended to `MigrationCatalog.Forward`, written to be harmless when re-run; docs/DEPLOY.md, "Migrations").
- No static mutable caches shared across requests or companies (the app runs on several instances). Per-request caches are fine.

## 2. Many users in one company: no conflicts
Both apps will write the same database at the same moment during the phased move, so the locking must be IDENTICAL to Python's (same advisory keys, same row locks, same lock order). Otherwise the two apps won't block each other. Read `shared/concurrency.py`, the 15 Oct "Many users at once" section of AGENTS.md, and `tests/concurrency/test_concurrent_users.py`.
- **Locks on state changes.** Every save, approve, unapprove, delete or cancel loads its record with `DbLocks.LockAsync` (SELECT … FOR NO KEY UPDATE, then reload) and re-checks the status after the lock.
- **Lock order** everywhere: document → number series → order lines → products (ascending id) → report totals (GlSummary already does sorted ON CONFLICT upserts).
- **Check-then-create** is guarded by `DbLocks.SerializeAsync(scope, key)`, with Python's exact scope string and key, so the advisory key is the same in both apps.
- **Numbers:** voucher numbers through IVoucherNumbers (atomic); document numbers through `DocNumbers` (placeholder, then PREFIX-YYYYMM-id).
- **Stale edits:** `edit_version` on SI/PI/SO/PO/SR/PR. A save carrying an older version is refused with who and when.
- **Conflicts are answered, never a 500:** deadlock / lock timeout / unique / FK give 409 JSON or a flash through `ConflictMessages`, and the whole transaction rolls back. Postings take the period FOR SHARE through IPeriods.

**Tests.** Port every scenario of `tests/concurrency/test_concurrent_users.py` that touches your module:
1. `var clients = host.ClientsFor(uid, cid, 6);`
2. Fetch the tokens BEFORE the race (`await clients[i].TokenFromAsync(page)`).
3. `var rs = await Race.RunAsync(6, i => clients[i].PostFormWithTokenAsync(url, fields, tokens[i]));` (or `PostJsonWithTokenAsync`).
4. `Race.NoServerErrors(rs);`
5. Assert the database: posted exactly once, distinct gapless numbers, stock not oversold, balances not double-counted, and so on.

(During the port, each module also raced the Python app against the .NET app on the same record; those tests were
removed with the Python app on 29 Oct 2026. Their results are in each docs/modules/<name>.md "Many users".)

Postgres max_connections is 100, shared by all eight of you: keep races at N=6 and dispose hosts.

Add a "Speed" and a "Many users" section to docs/modules/<name>.md listing the budgets met (with numbers), the races tested, and anything left.
