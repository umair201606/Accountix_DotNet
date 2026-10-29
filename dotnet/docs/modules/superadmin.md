# Super admin console: status

Owner: SuperAdmin engineer. Project `src/Modules/Accountix.SuperAdmin`, tests `tests/Accountix.SuperAdmin.Tests`
(`ACCOUNTIX_TEST_DB=superadmin`). Python: `superadmin_app/routes.py` and `superadmin_app/templates/superadmin/*`.

**Ported: all 12 routes**, same URLs, same markup (ids, classes, inline styles; the `<style>` blocks of
`base.html` and `login.html` copied verbatim by a script), the console's own layout (`_Console`, not the
module app shell, no sidebar).

| Python endpoint | Route | Status |
|---|---|---|
| `superadmin.login` | `GET/POST /superadmin/login` | ✅ super admins only; Python's throttle (429), equal-time refusal, "Those credentials do not open the super admin console." for a wrong password AND a right password without the flag; `user_sessions` row; Flask session cookie (both apps); audit `login` (company NULL) / `login_failed`; `next` followed only inside `/superadmin` |
| (logout) | link to `/auth/logout` | ✅ Python's console has no logout of its own; `/auth/logout` is Platform's |
| `superadmin.index` | `GET/POST /superadmin/` | ✅ dashboard counts + Platform (global limits: blank = default, `max(1, n)`, "Limits must be whole numbers.") |
| `superadmin.users` | `GET/POST /superadmin/users/` | ✅ list (one grouped count, no N+1); create: password policy, email checks, login-id clash, placeholder `SA####` code, HR access only, no membership |
| `superadmin.user_detail` | `GET/POST /superadmin/users/<id>/` | ✅ profile (name, quota overrides: blank = NULL, `max(0, n)`), password reset (policy; ends the person's other sessions, keeps your own), activate/deactivate with the self guard, memberships with owner pill |
| `superadmin.toggle_super` | `POST /superadmin/users/<id>/toggle-super` | ✅ self guard; confirm() text written as Jinja's `|tojson|forceescape` |
| `superadmin.companies` | `GET/POST /superadmin/companies/` | ✅ create: the named admin **owns** it (ACCESS_REVIEW #3), new admin = account with every module on (password typed or 12 random, shown once), membership through Platform's insert hooks, then `provision_company` in its own commit |
| `superadmin.company_edit` | `GET/POST /superadmin/companies/<id>/` | ✅ profile fields with Python's fallbacks, module entitlements (an absent checkbox = off), members by status/joined |
| `superadmin.member_block` | `POST /superadmin/companies/<cid>/members/<mid>/block` | ✅ `serialize("members", cid)` (same advisory key as Python), 404 for another company's membership, owner / last active admin / pending refused |
| `superadmin.my_companies` | `GET/POST /superadmin/my-companies/` | ✅ quota, slug rule, provisioning, `session["company_id"]`, → `/dashboard/`; invitations post to Settings' accept/decline |
| `superadmin.requests` | `GET /superadmin/requests/` | ✅ counts, badge; "seen" shown but not stored (see below) |
| `superadmin.request_action` | `POST /superadmin/requests/<id>/<action>` | ✅ approve (account from the request's hash, FOR UPDATE lock as Python) / block / unblock / delete / unknown |
| `superadmin.audit` | `GET /superadmin/audit/` | ✅ all companies + platform events, company / "platform" / action / search filters, 50 a page, pager links as `url_for` writes them, `decode_changes` rendered as Jinja prints Python values |

Gate (`SuperAdminOnlyAttribute`): signed out → "Please log in to access this page." and
`/superadmin/login?next=…` (exactly Flask-Login's URL); signed in without the flag → the friendly 403 page.
Missing records → the friendly 404 page.

## Services used (from Accountix.Platform, its public API in platform.md)

`SignInService` (find login, password check, `StartAsync`, `SetPasswordAsync`, `SetCompany`),
`AccountSecurity` (password policy, throttle, failed sign-ins, equal time), `AuditTrail.RecordAsync`
(+ the automatic `AuditInterceptor`: user and membership creates/updates are audited as in Python),
`CompanySetup.ProvisionAsync`, `Memberships.AddAsync` (first/further-company rules), `FirstUse.GlobalLimitsAsync`.
`SuperAdminModule.Register` registers Platform's services itself only when PlatformModule has not
(the module booted alone in tests); in the host Platform registers first (name order), so nothing doubles.

Platform had no console files when this started (it only redirects `/admin` → `/superadmin/`); nothing was copied.

## Fixed in both apps (29 Oct, found by this port)

- **320px**: the console header (brand + menu + theme + logout) was 342px wide at 320px, so every console
  page scrolled sideways. A ≤400px rule in `superadmin/base.html` now tightens the header gaps and the logout
  padding; `_Console.cshtml` carries the same style block, copied verbatim.
  Python test: `tests/e2e/test_superadmin_console_mobile.py` (8, `CLIPPED_JS` at 320px; red without the rule).
- **My Books** now queues on `serialize("own-companies", user)` in both apps, as the portal's create does, so two
  creations at once can no longer both pass the quota. Python tests: `test_my_books_enforces_the_creation_quota`
  in `tests/e2e/test_superadmin_console.py`, and the Postgres race
  `test_my_books_creations_together_never_pass_the_quota` in `tests/concurrency/test_concurrent_users.py`
  (six at once with room for two → exactly two; red without the lock).

## Deliberate differences from Python

- Antiforgery: every console page and the login carry the shared `_Antiforgery` partial (fetch/XHR header,
  hidden field on submit and on `form.submit()`); forms also get the hidden field from `asp-antiforgery`.
- Access requests: Python sets `seen` on unseen pending requests while rendering, then never commits
  (its "anything still unseen?" test runs after the loop). The .NET page does the same: shown as seen, stored unseen.
- Routes answer with or without the trailing slash (Flask 308-redirects the slashless form).
- Razor writes non-ASCII text as character references (`&#x2014;`); the browser shows the same text.

## Tests (`tests/Accountix.SuperAdmin.Tests`): 53 passed, 0 skipped, no Python (29 Oct)

Run: `ACCOUNTIX_TEST_DB=superadmin dotnet test tests/Accountix.SuperAdmin.Tests --artifacts-path <yours>`.
No Python needed: every test builds its own data through the .NET console (or EF inserts mirroring its
writes) and passes on a fresh copy (`dotnet run --project tools/Accountix.RefDb -- --only superadmin`).
`[PerfFact]` tests use the speed-test database.

**Golden (fixed expectations captured from the Python app on 29 Oct 2026, `Golden/*.json`; the Python that
produced each is in `Golden/capture/*.py`; `ACCOUNTIX_GOLDEN_PERTURB=1` makes each of them fail, checked):**
- `ConsoleCompanyBooks.json` — `CompaniesTests.A_console_company_gets_the_books_python_gave_the_same_company`:
  a company made through the console has, row for row, the books Python's console gave the same company:
  every company table (161 accounts by code and parent, 14 numbering series, 8 periods, company profile,
  fiscal rule, report / inventory settings, 6 asset categories, the 4 invoice templates), the company,
  membership and admin account rows (roles by name) and the audit events by kind. Ids and timestamps left out;
  dates and period years relative to the run's year (`Y+0-01-01`, `FY Y+0`), today's date as `TODAY`, so it
  holds in any year. (Replaces the two live parity tests, books and invoice templates.) One deliberate
  difference is left out of the comparison and asserted on its own: since E2E finding F6, .NET provisioning
  also writes the three leave types of `hr_app/seed.py` (CL 12, SL 10 / 5 carried, AL 20 / 10 carried), which
  Python's provisioning never wrote.
- `AuditPage.json` — `AuditPageTests.The_page_renders_rows_changes_and_pager_as_python_rendered_them`: 60 seeded
  rows (platform + two fresh companies, every kind of change payload, fixed times) on four filter/page
  combinations: table rows with Python's `str()` of each value, pager links, count line, action select, chosen
  company (tag → `TAG`, company ids → `NORTH` / `SOUTH`).
- `UsersList.json` — `UsersTests.The_users_table_renders_as_python_rendered_it`: five fresh accounts (the
  signed-in super admin "(you)", plain, a super admin whose name needs escaping in the revoke confirm,
  deactivated, one in two companies + a blocked membership) render as Python's rows (ids → `U0…U4`).

**Changed to .NET-only setup** (the expectations are the concrete answers Python gave):
- `AccessFixesTests` (3): the invitation and its acceptance now go through Platform's .NET Settings routes
  (`/settings/invite`, `/settings/invitations/<id>/accept`, host booted with the Platform assembly too);
  role, modules and rights are read with the .NET access check.
- `UsersTests.The_new_account_signs_in_at_the_customer_door_and_lands_on_the_portal`: was "signs in through the
  Python app"; now Platform's `/auth/login` → `/portal/`.

**Dropped** (cross-app, nothing left to compare once Python is gone):
- `LoginTests.A_dotnet_console_sign_in_is_a_sign_in_on_the_python_console_too` (sign-in itself, session row,
  cookie and audit row stay covered by `The_super_admin_enters_the_console_with_a_server_session_and_an_audit_row`).
- `MemberBlockTests.The_python_console_and_the_dotnet_console_queue_behind_each_other` (the same-app races stay).
- `SpeedTests.Python_times_for_the_same_pages` (live Python timing; the recorded times stay in the Speed table).
- `CompaniesTests.Invoice_templates_are_provisioned_like_python` (folded into `ConsoleCompanyBooks.json`).

**Kept as they were** (already .NET-only):
- **Pages** (`ConsolePageTests`, 7): every page opens in the console layout; what the Python tests look for
  (Create User, My Books nav link, no `href="/dashboard/"`); 403 for a member who is not a super admin (pages
  and writes); signed-out redirect with Flask-Login's exact `next`; 404s; scripts carry no HTML-encoded text;
  the shared antiforgery script is on every page; **query counts are fixed whatever the rows** (7–12 a page).
- **Sign-in** (`LoginTests`, 5): session row + `ax_session` cookie + audit row; `next`; refusals; inactive; throttle 429.
- **Users** (`UsersTests`, 9): `test_superadmin_console.py` Manage Users / quotas / password / deactivation /
  self guards, the customer-door sign-in, the users-table golden.
- **Companies** (`CompaniesTests`, 7): provisioning, ownership, existing admin, refusals, module entitlements,
  edit fallbacks, the books golden; **race**: six creations of one slug → one company, no stray account, no 500.
- **Member blocking** (`MemberBlockTests`, 5): round trip keeping role and code; owner / last admin / pending;
  cross-company 404; **races**: six toggles at once = six flips in turn (audit shows each), two admins blocked at
  once never leaves none.
- **My Books** (`MyCompaniesTests`, 4): create + provision + session company; quota / slug refusals; six at once
  never pass the quota; invitations and memberships listed.
- **Requests** (`RequestsTests`, 5): list, approve, existing email, block/unblock/delete/unknown, six approvals → one account.
- **Audit** (`AuditPageTests`, 4): filters; the page golden; query count fixed; `decode_changes`.
- **Access fixes** (`AccessFixesTests`, 4, console part of `test_access_fixes.py`): the console names the owner;
  a further company starts from the role (employee, manager); a first company starts from the account the console made.
- **Speed** (`SpeedTests`, 2) and the smoke test.

Browser (Chrome, 320×700 mobile, signed in through the console door): no page scrolls sideways (all 10
console pages + login), menu opens, no console errors.

## Speed (speed-test DB: Samba full year, 46k audit rows; fastest of 3, server time)

The Python column was measured on 29 Oct 2026, before the Python app was retired; it is kept as history
(the tests no longer time Python).

| Page | .NET | queries | Python |
|---|---|---|---|
| `/superadmin/audit/` | 11 ms | 10 | 29 ms |
| `/superadmin/audit/?page=200` | 16 ms | 10 | 31 ms |
| `/superadmin/audit/?q=INV` (search) | 87 ms | 10 | 119 ms |
| `/superadmin/audit/?company_id=platform` | 5 ms | 10 | 24 ms |
| `/superadmin/audit/?company_id=2&action=create&page=40` | 12 ms | 10 | 43 ms |
| `/superadmin/` | 6 ms | 11 | 42 ms |
| `/superadmin/users/` | 7 ms | 10 | 48 ms |
| `/superadmin/companies/`, `/companies/2/` | 5 / 6 ms | 9 / 11 | 31 / 32 ms |
| `/superadmin/my-companies/` | 8 ms | 12 | 48 ms |
| `/superadmin/requests/`, `/users/1/` | 3 / 6 ms | 7 / 11 | 21 / 37 ms |

Budgets (≤ 800 ms, ≤ 40 queries) met with room; never slower than Python. Lists are one query per table
(grouped counts, joins) instead of Python's per-row counts.

**Proposed index** (optional, for the audit search, which scans with ILIKE): `CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE INDEX ix_audit_logs_summary_trgm ON audit_logs USING gin (summary gin_trgm_ops);` (and on `reference`).
Not needed at today's size (87 ms on 46k rows).

## Many users

- `member_block`: `serialize("members", company_id)`, the key Python takes, in one transaction; tested .NET×6
  (the Python×3 + .NET×3 race passed on 29 Oct and was dropped with the Python app).
- Company creation: one transaction (account, company, membership); a clash on the slug / code / email is a 409 answer, never a 500.
- Request approval: `FOR UPDATE` on the request (as Python), six at once → one account.
- My Books: `serialize("own-companies", user)` around the quota check, the same key Python used; tested .NET×6 (exactly the quota).
- Password reset: the hash and the ending of the person's other sessions commit together.

## Notes for the coordinator

- If Platform is mid-edit and does not compile, this project can build against a Platform build that did:
  `-p:AxPlatformDll=<path>/Accountix.Platform.dll` (conditional reference in the csproj; the default is the
  project reference, so the solution build is unaffected).
- Platform must not add `/superadmin/*` routes (they would clash).
