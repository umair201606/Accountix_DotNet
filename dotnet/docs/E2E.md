# Browser E2E suite for the .NET app (`tests/Accountix.E2E`)

Owner: the E2E engineer. The .NET counterpart of the Python Playwright suite (`tests/e2e/`), run against the
real .NET app in a real browser. AGENTS.md rules 2–4: every module and UI flow covered, every page right from
320 px. Bugs found in other people's modules go to [E2E_FINDINGS.md](E2E_FINDINGS.md) (the suite never fixes
module code).

## Run it

```bash
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
ART=/private/tmp/claude-501/-Users-apple-Downloads-accountix-erp-main/5a911460-b143-4a75-bfdd-3af0a155b1c4/scratchpad/artifacts/e2e
cd /Users/apple/Downloads/accountix_erp-main/dotnet
dotnet build tests/Accountix.E2E --artifacts-path $ART
dotnet test  tests/Accountix.E2E --artifacts-path $ART --no-build                     # everything (~10 min)
dotnet test  tests/Accountix.E2E --artifacts-path $ART --no-build --filter "FullyQualifiedName~VoucherFlowTests"
```

| Variable | Effect |
|---|---|
| `ACCOUNTIX_TEST_DB` | database copy; the suite sets `e2e` (`accountix_ref_e2e`) when unset |
| `E2E_KNOWN=run` | also run the cases blocked by open findings (`Harness/KnownIssues.cs`) to check a fix |
| `E2E_FORCE_MODULES=Inventory,Hr` | run a module's tests although its docs don't say done yet |
| `E2E_HEADED=1` | watch the browser |
| `E2E_BROWSER_CHANNEL=chrome` | use the installed Google Chrome first |
| `E2E_SCREENSHOTS=<dir>` | where failure screenshots go (default `<scratchpad>/e2e-screens`) |
| `-p:E2EModules="Accounting;Hr;..."` | which module projects are compiled in (default in the `.csproj`) |

Prerequisites: the reference Postgres (pgserver) running with `tools/reference.json` listing `e2e`, and, for the
speed tests, `tools/perf.json`. Without them the tests skip with that reason. No Python: since 29 Oct the suite
needs nothing of the Python app (it used to find `docs/` and `/static` through it). `docs/` is the folder next to `Accountix.slnx`
(`E2EEnv.DocsDirectory` = TestSupport's `Reference.DotnetRoot` + `docs`), and `/static` ships from
`src/Accountix.Web.Shared/static/`, copied next to the binaries (`AccountixApp.StaticFolder()`). Set-up goes through the app's routes, as before; a fresh e2e copy
comes from `dotnet run --project tools/Accountix.RefDb -- --only e2e`.

## Browser

**Microsoft.Playwright 1.64.1** (NuGet), which drives **chromium-1248** — exactly the revision the Python suite's
playwright 1.64.1 installed in `~/Library/Caches/ms-playwright` (`chromium-1248`,
`chromium_headless_shell-1248`). Nothing extra to install on this machine. `Harness/Browsers.cs` tries, in order:
`E2E_BROWSER_CHANNEL`, Playwright's own Chromium, then the installed Google Chrome (`Channel = "chrome"`). On a
machine without either: `pwsh $ART/bin/Accountix.E2E/debug/playwright.ps1 install chromium`. One headless
browser for the whole run (collection fixture), a fresh context per test (own cookies, viewport, colour scheme).

## The harness (`Harness/`)

- **`AppServer`**: the real app on **Kestrel on a real port** (not TestServer) with Core + Web.Shared + the module
  assemblies named, on the e2e copy, set up exactly as the host (`AccountixApp.ConfigureServices` /
  `ConfigurePipeline`, `/static` from the build output, Development environment).
  - **Owns its port**: before starting, it fails fast when anything accepts connections on, or cannot bind, the
    port (the Python suite once ran for hours against a stale server, AGENTS.md Aug 2026).
  - **Stays alive and is the one we started**: every response carries `X-E2E-Instance: <guid>`;
    `/__e2e/ping` answers the guid; each test checks it before starting, and every page the browser opens must
    carry this run's id.
  - Records every unhandled exception (with stack) and every 5xx on the server side; sessions report them.
- **Ports** (this suite's range 5315–5320): 5315 Accounting alone (`AccountingApp`), 5316 Accounting on the
  speed-test DB, 5317 the integrated app (`FullApp`: every module of the build), 5318 the integrated app on the
  speed-test DB. Each server lives for one test class and is disposed after it.
- **Sign-in** (`UiSession.OpenAsync`): as `TestSupport.TestSignIn` does — a `user_sessions` row plus the
  `ax_session` cookie signed with `SECRET_KEY`, added to the browser context for `127.0.0.1`. The console's own
  doors (`/auth/login`, `/superadmin/login`) are tested with the seeded passwords.
- **Guards on every page** (`UiSession`): any console error, uncaught script error, 5xx answer (page, fetch or
  asset), refused form POST (4xx, e.g. a missing antiforgery token), or page served by another server fails the
  test; so does **horizontal overflow** — `documentElement.scrollWidth > innerWidth` **and** any element past the
  right edge outside a scroll box or fixed overlay (the Python suite's 360 px check: the shell sets
  `body{overflow-x:hidden}` on phones, so a too-wide page is cut off, not scrollable, and scrollWidth alone misses it).
  A test ends with `AssertCleanAsync()`; a failed test leaves a full-page screenshot.
  A call to a route of a module that is **not in the run** (e.g. the HR dashboard's `/reports/dashboard`,
  HrRecords) is a 404 here but answered by Python in production: it is printed as a `[note]`, not failed
  (`Harness/RouteOwners.cs`); a 404 from a module that *is* in the run fails.
- **Viewports**: desktop 1366×768, 375×812, 360×780, 320×640.
- Web fonts and the Font Awesome CSS from CDNs are answered empty (no network dependency, honest timings).
- **Module readiness** (`Harness/ModuleStatus.cs`): a module's tests run when its project is in the build
  (`E2EModules`) and its `docs/modules/<key>.md` says done (a status line, "Ported: all/every …", or a route table
  of ✅ rows with nothing in progress) or PROGRESS.md marks it ✅ done. Otherwise every test of it is **skipped
  with that reason** — never a silent pass.
- **Known issues** (`Harness/KnownIssues.cs`): a case blocked by an open finding is skipped naming the finding
  (`E2E_KNOWN=run` runs it). None open at the moment: F1–F6 are fixed; F7–F8 don't fail a test.
- Data set-up goes through the app's own routes over HTTP (`AppFixture.HttpAsync`, `Accounting/AccountingSeed.cs`),
  found again on later runs; flows clean up after themselves (vouchers unapproved and deleted, reconciliations
  reopened and deleted, accounts and parties deleted), so the e2e copy can be reused run after run.
  Database reads are for assertions only, with two set-up exceptions on the e2e copy: today's attendance of the
  employee is removed before the clock-in test, and the three leave types of `hr_app/seed.py` are added when missing
  (neither app has a page that creates leave types).

## Last full run (29 Oct, every module in the build)

**869 tests: 866 passed, 0 failed, 3 skipped** in 12.9 min (`dotnet test tests/Accountix.E2E`). The 3 skipped are the
`Blocked_*` theories that re-run cases of open findings; no finding blocks a case now (they skip until `E2E_KNOWN=run`).

Again later on 29 Oct with no Python (`ACCOUNTIX_PYTHON` unset, `PythonApp` gone from the suite): **866 passed, 0 failed,
3 skipped** in 14.1 min; on a freshly rebuilt e2e copy 856 passed, and the 10 Shell tests that failed only because a second
E2E run started on port 5315 meanwhile (the harness refuses a port it does not own) passed when re-run (10 of 10). A copy
of `dotnet/` with no Python app above it and TestSupport's `PythonApp.cs` removed builds, and its Harness, VoucherFlow,
Finance/Executive pages and BrowserSpeed tests (110) pass. Nothing is skipped for lack of Python.

| Suite | Passed |
|---|---|
| `ModulePagesTests` (every parameter-free page of 14 modules × 4 widths) | 668 — Platform 108, Hr 136, Inventory 104, Finance 64, Fixed assets 52, Parties 48, HR records 44, Payroll 32, Executive 20, Invoicing 20, Orders 16, Returns 16, FBR 8 |
| `AccountingPagesTests` (17 pages × 4 widths) | 68 |
| `SuperAdminTests` | 37 |
| `InvoicingTests` | 19 |
| `PlatformInventoryReturnsTests` | 17 |
| `VoucherFlowTests` | 13 |
| `BrowserSpeedTests` | 11 |
| `ShellTests` | 10 |
| `HrTests` | 7 |
| `CoaTests`, `PartiesTests` | 6 each |
| `HarnessTests`, `BankRecTests` | 2 each |

## What is covered (29 Oct)

People: `owner_b` (admin of fresh company B) for Accounting writes; `owner_a` (admin of A, employee in B) for the
switcher and empty lists; `restricted` for rights; the super admin and `emp@solarkon.com` in company 1 "Default"
(seeded demo data) for the other modules, as the Python suite does.

| Area | Tests | What |
|---|---|---|
| Harness | `HarnessTests` | second server on the port refused; instance header; guards catch console errors, script errors, overflow |
| Accounting pages | `AccountingPagesTests` | 17 pages × 4 widths: dashboard (+alias), new CPV/BRV/JV, saved draft, approved voucher, list, print preview, register (empty, month), print-all, COA tree/add/edit, bank-rec list/view |
| Voucher flows | `VoucherFlowTests` | list filters + empty state (port of test_voucher_ui), filters by type/status at 375, register + its JSON feed, new-voucher screen and type pills, phone layout (pinned action bar), lines add / insert / remove / clear / Enter-adds-line with totals, balance chip and approve button (desktop + 320), entry preview + cash balance before → after (test_voucher_v3), in-page validation (no line, no cash account, unbalanced JV; no browser dialogs), **life of a voucher**: CPV save draft → reopen editable → approve via in-page confirm → journal posted (exact lines) → More → Unapprove → journal off → More → Delete (desktop) and JV Save & Approve → unapprove → delete at 320; fetch/XHR JSON posts carry the antiforgery header, a POST without it is refused (400), rights refusal (403) |
| COA | `CoaTests` | tree at 375 (header hidden, search, aria-expanded toggles), search / expand / collapse, add level by level with the code preview → edit (rename, deactivate) → delete with confirm (desktop + 320), fixed account guarded, empty name refused with a dismissible flash at 320 |
| Bank reconciliation | `BankRecTests` | E2E Bank: two posted vouchers, start a statement, live cleared/difference, Complete refused with a difference, completed at zero, statement figures, reopen, delete (desktop + 360) |
| Shell | `ShellTests` | theme switch (dark, persists across pages, body really dark; OS-dark first click goes light), phone sidebar (menu opens, overlay closes, active item, navigation) at 320/375, desktop collapse, company switcher (lists the person's companies, `next=` back to the page, on screen at 1366/375/360/320, closes on outside click), `form.submit()` carries the token while forms/fetch to another site never get it |
| HR | `HrTests`, `ModulePagesTests.Hr_page` | dashboard (admin, employee), **attendance**: clock in → break → clock out through the page buttons (desktop + 320), **leave**: apply through the form at 375 → admin rejects with a reason on the approvals page, **ESS**, **profile** + change password; 34 HR pages × 4 widths |
| Invoicing | `InvoicingTests`, `Invoicing_page` | **purchase invoice**: form, add line, clear items (in-page confirm), scope pill toggles, summary calculation, global discount → net payable, phone (375, 320); discount scope × method on both editors (test_discount_modes); sales invoice draft: customer + product by code, Ctrl+S save (JSON + antiforgery), reopen, delete; Accounting-entry panel "Nothing posted yet"; 5 pages × 4 widths |
| Parties | `PartiesTests`, `Parties_page` | customer / supplier lists with demo parties and search, create → edit → delete through the forms (desktop, 320), double-submit guard, invoice register on demand (375); 12 pages × 4 widths |
| Orders, Fixed Assets, FBR, Returns, Executive, Payroll, HR records | `ModulePagesTests` | every parameter-free page × 4 widths (4 + 13 + 2 + 4 + 5 + 8 + 11 pages) |
| Platform | `ModulePagesTests.Platform_page`, `PlatformInventoryReturnsTests` | landing, sign-in, sign-up (anonymous), portal, hub (admin, employee), Settings and all its tabs, members, invitations, labels × 4 widths; **sign-in with a password → hub → HR → sign-out**, the session really ends; public pages' theme switch (floating on sign-in); hub on a phone; switching company keeps the page (HR) and refuses a module the other company did not give (Accounting) |
| Super admin console | `SuperAdminTests` | sign-in door (desktop, 320), wrong password and no-flag refusals, 8 console pages × 4 widths, POST forms with the token (limits, a user's active switch), theme switch on the console layout |
| Speed | `BrowserSpeedTests` | see below |
| Inventory, Finance | `ModulePagesTests.Inventory_page` / `Finance_page`, `PlatformInventoryReturnsTests`, `InvoicingTests` | 26 inventory pages and 16 finance pages (empty and with a year's figures) × 4 widths; dashboard, products, stock value is a number, **sidebar accordion**; sales invoice **save & approve** (stock moves) → the Accounting-entry panel lists the journal, balanced, accounts link to the ledger, then unapprove and delete; the Accounting-entry button on every posting form; a **stale tab** is told and overwrites nothing (test_edit_conflicts_ui) |

AGENTS.md rule 4 minimum lists, as of 29 Oct:
- **Inventory**: Login ✅ (`/auth/login` and the super admin door), Dashboard ✅, Products ✅, Suppliers ✅, Customers ✅, Purchase Invoice ✅ (form, add/clear items, pill toggles,
  calculations, global inputs, phone), Purchase Return ✅ (form and list at 4 widths), Logout ✅.
- **HR**: Login ✅, Hub ✅, Dashboard ✅, Attendance ✅, Leave ✅, ESS ✅, Profile ✅, Logout ✅.

## Speed in the browser (full-year DB, desktop, best of 3 after a warm-up, budget 2 s)

Navigation start → load event, on `tools/perf.json` (Samba, company 2; busiest month 2026-05):

| Page | Load | DOM ready | First byte | Size |
|---|---|---|---|---|
| Voucher register, busiest month (`?mode=month&month=2026-05&view=1`) | 106 ms | 105 ms | 27 ms | 161 KB |
| Voucher register, **whole year** by its URL (`?mode=year&period_id=…&view=1`) | 801 ms (runs 1,049 / 847 / 801) | 799 ms | 85 ms | 1.3 MB |
| Voucher register, whole year built by pressing **View** (3,118 rows in chunks of 200) | 822 ms to the last row | | | |
| Voucher list | 26 ms | 22 ms | 10 ms | 37 KB |
| Accounting dashboard | 30 ms | 30 ms | 13 ms | 25 KB |
| Chart of accounts | 60 ms | 59 ms | 17 ms | 285 KB |
| Sales invoice register, busiest month (318 SI; Parties, integrated app) | 110 ms | 110 ms | 10 ms | 210 KB |
| Trial balance, full year | 478 ms | 477 ms | 24 ms | 115 KB |
| Profit & loss, full year | 398 ms | 398 ms | 14 ms | 51 KB |
| Balance sheet, full year | 697 ms | 697 ms | 11 ms | 63 KB |
| General ledger, all accounts, busiest month | 785 ms | 784 ms | 31 ms | 1.3 MB |

The finance pages answer in 10–30 ms; most of their load time is the Tailwind CDN script `finance_app` pages use
(it compiles styles in the browser), not the server.

Numbers from a quiet machine. With the other engineers' builds running, the whole-year register by URL measured
1,937 ms (runs 2,312 / 1,937 / 2,033): it is the one page near the budget, because `view=1` renders the whole year
into one 1.3 MB page and the browser lays it all out (E2E_FINDINGS.md F7). Everything else is far inside.

## Not covered yet / next

- Every module is in the build and marked done (29 Oct evening): all 15 module projects are booted together in
  `FullApp`, as the real host loads them.
- Deeper flows still to port for the modules that landed today (pages are swept at 4 widths, the AGENTS.md lists are
  covered): test_invoice_form_ux.py's approved-state bar and keyboard shortcuts, test_payroll_run.py's
  360 px run page with a real run, test_payment_tracking.py's assign workspace, fixed-asset create → depreciate,
  order → invoice import, returns against an approved invoice, settings tabs' saves, the template designer,
  standalone sign-in mode, the HR records bell in the shell.
- Exports (Excel/PDF/CSV buttons) are not clicked yet: the export tests of each module cover the files.
