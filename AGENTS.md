# Development Policies

The app is **.NET 10** (`dotnet/`, ASP.NET Core MVC + EF Core on PostgreSQL / Neon). The Python/Flask
app it replaced was removed on 29 Oct 2026; its history is in `docs/HISTORY_PYTHON_ERA.md`.

## 1. Change Impact Assessment
When any new update is made, ensure all other actions/features connected to it are updated accordingly.
Check for side effects before closing a change (other modules that read the same tables, the sidebar,
exports, Books Integrity, the speed budgets).

## 2. Test Coverage
Every change must be covered by updated tests: the module's test project (`dotnet/tests/Accountix.<Module>.Tests`)
and, for any UI flow, the browser suite (`dotnet/tests/Accountix.E2E`, Playwright). Run the affected
projects and the browser suite before committing. Add new tests for any new functionality or UI flow.

## 3. Responsive UI
All UI must be responsive and optimized for mobile use. Every page must render correctly on small screens
(320px+): no sideways scroll, wide tables scroll inside their own box. The browser suite checks every page
at 1366 / 375 / 360 / 320.

## 4. Browser Tests for All Modules
The browser suite must cover all modules, at least:
- **Inventory**: Login, Dashboard, Products, Suppliers, Customers, Purchase Invoice (form, add/clear items,
  pill toggles, calculations, global inputs), Purchase Return, Logout
- **HR**: Login, Hub, Dashboard, Attendance, Leave, ESS, Profile, Logout

## 5. Speed and Many Users
Rules in `dotnet/docs/SPEED_AND_CONCURRENCY.md`. In short:
- **Budgets:** ≤ 800 ms for a month's list or report, ≤ 1.5 s for a year, ≤ 3 s for an export, and ≤ 40 queries per page with no N+1 (`host.MeasureAsync(...).Within(ms, queries)`).
- **State changes:** each one locks its record (FOR NO KEY UPDATE), in the lock order document → number series → order lines → products → report totals. Check-then-create goes through `DbLocks.SerializeAsync`.
- **Conflicts:** answered with 409 or a flash, never a 500.
- **Race tests:** each module keeps its six-at-once race tests (`Race.RunAsync`).

## 6. Schema
The schema belongs to `dotnet/src/Accountix.Migrations` (baseline + forward scripts, run by
`dotnet Accountix.Web.dll migrate` and at start in production). Every schema change is a new migration plus
a re-captured `expected_schema.json`; see `dotnet/docs/DEPLOY.md` "Migrations".

## Session Summary (29 Oct 2026) — Python → .NET 10 Complete, Python Removed

### Decisions (user)
- **Move to .NET 10** on the same Postgres (Neon). Host on Azure App Service in Neon's region.
- **Many engineers at once:** the work ran as parallel agents, Opus 5.5 only (up to 16 at once).
- **Speed and many users:** big reports (e.g. a month's sales invoices) and every financial report must load quickly, and many users in one company must work smoothly without conflicts.
- **Python removed:** after the conversion, make .NET standalone, then remove all Python files, unnecessary code and data from the app.

### What the app is now (`dotnet/`, see `dotnet/README.md`, `dotnet/docs/PROGRESS.md`)
- **Projects:**
  - `Accountix.Core`: entities for all 122 tables; EF context with a fail-closed company filter; access rules; Werkzeug-format password hashes, so existing users keep their passwords.
  - `Accountix.Web.Shared`: app shell, sign-in (`ax_session` cookie over `user_sessions`), antiforgery for forms and fetch, static CSS/JS.
  - `Accountix.Web`: the host, plus the `migrate` and `admin` commands.
  - `Accountix.Migrations`: schema ownership.
  - `Accountix.Admin`: maintenance commands.
  - 15 modules: Accounting, Finance, Executive, Inventory, Invoicing, Parties, Orders, Returns, Hr, HrRecords, Payroll, FixedAssets, Fbr, Platform, SuperAdmin.
- **Behaviour:** identical to the Python app. Same URLs, HTML ids and classes, database writes and journals, proven by parity tests against the live Python app during the port.
  - Those parity tests are now golden fixtures: Python's answers recorded on 29 Oct (`tests/*/Golden/*.json`), compared after normalising ids and dates.
  - Each fixture fails when one figure of the .NET answer is changed.
- **Speed:**
  - **4× Samba company** (22,786 documents, 10,405 sales invoices, ~90k journal lines): every big page is within budget.
    - Busiest month's sales invoice list: 42 ms.
    - Sales register for the year: 0.23 s.
    - Trial balance / P&L / balance sheet for the year: ~25 ms each.
    - Month ledger, all accounts: 0.13 s.
    - Books Integrity: 0.3 s.
    - Full-year register Excel: 0.31 s.
  - **Faster than Python on every page measured.** Examples:
    - Payroll run for 200 people: 0.4–0.5 s (Python 14.4 s).
    - Month ledger PDF: 0.3 s (Python 8–11 s).
    - Back-dated stock re-costing: 0.5 s (Python 18.5 s).
- **Many users:**
  - Six-at-once race tests in every module: one posting, gapless numbers, no overselling, no double payment, period close vs postings.
  - Load test (`tests/Accountix.Load`): 30 users in one company, ~7,000 requests a run, on the full-year and the 4× company. 0 server errors, 0 deadlocks; data checks and Books Integrity pass afterwards.
- **Deploy** (`dotnet/docs/DEPLOY.md`, `dotnet/deploy/`, nothing applied):
  - Dockerfile, Bicep + az script, a staging-slot workflow.
  - Data-protection keys in Postgres; Neon pool settings; Brotli/gzip compression.
  - Cut-over: `dotnet/docs/CUTOVER.md`. One switch now that Python is gone; everyone signs in once.

### Python-era bugs fixed on the way (in both apps before the removal)
- Duplicate "parked" accounts: every impairment, EOBI, gratuity, leave-encashment and PF-profit posting opened a new account.
- Order editors:
  - a reopened sales order saved totals of 0 (the server now recomputes totals that disagree with the lines);
  - the PO Unapprove button was dead;
  - the editors had lost their stylesheet.
- Cash-flow comparative columns: an expense refund cut profit.
- HR report hours showed 24× too high on Postgres.
- A draft return counted itself as "already returned".
- The stock-take list crashed (500).
- Executive pages printed blank labels and "1 of NaN selected".
- Company clock instead of the server's UTC: aging, profitability, TWCF, inventory defaults.
- Phone width on several pages: voucher print, company switcher, settings tabs, landing, console header, executive dashboard, inventory pages.
- HR "None" text in empty fields, and "rejectd" typos.
- Two My Books creations at once could pass the quota.
- New companies got no leave types (now provisioned; backfill with `admin add-default-leave-types`).
- 16 indexes added (Alembic 0018 then, now part of the .NET baseline).

### Verification (29 Oct 2026, with Python removed from the folder)
- Last Python suite before removal: 1,738 passed, 0 failed, 2 skipped (Postgres-only).
- .NET, with no Python present, on test databases rebuilt by `tools/Accountix.RefDb`: the whole solution builds with 0 errors. Every test project passes: **1,886 passed, 0 failed, 4 skipped** (3 known-issue browser theories, by design; 1 on-demand browser server). That includes 866 browser tests (every page at 1366/375/360/320). The table is in `dotnet/docs/PROGRESS.md` "Final run".

### For the owner (not done from this machine)
1. **Live site:** Neon and the live site still run the Python deployment. Don't push this cleaned repository to the branch Vercel deploys from until .NET is live on Azure.
2. **Neon schema:** upgrade Neon with `dotnet Accountix.Web.dll migrate --status`, then `migrate`, on a Neon branch first. Neon is most likely at Alembic 0015; the steps are in DEPLOY.md.
3. **Live-data repairs:** run on Neon in this order:
   1. `admin books-integrity`
   2. `admin remove-faulty-demo-records`
   3. `admin repair-opening-stock`
   4. `admin merge-legacy-accounts`
   5. `admin add-default-leave-types`

   Run each without `--apply` first, then with it, then `admin books-integrity` again.
4. **Not verified yet:** `az bicep build`, and Vercel/Azure header behaviour on a preview deployment.
5. **Archives:** the Python code is in `~/Downloads/accountix_python_backup_2026-10-29.zip` (taken before any removal) and `~/Downloads/accountix_python_removed_2026-10-29/` (the files as they were moved out, including the golden capture scripts). The speed-test database dumps are in `~/Downloads/accountix_data_backup_2026-10-29/` (`tools/restore_perf_db.sh`).
