# Platform: status

Project `src/Modules/Accountix.Platform`, tests `tests/Accountix.Platform.Tests` (`ACCOUNTIX_TEST_DB=platform`).
Scope: landing, sign-up, sign-in / sign-out (`ax_session`), portal, hub, company switch, Settings (every tab),
company provisioning, the audit trail. The super admin console moved to `Accountix.SuperAdmin` (29 Oct); no console
page was written here.

## For other modules (stable, public API)

| Service (DI, scoped unless noted) | Python | What |
|---|---|---|
| `Audit.AuditInterceptor` (singleton `IInterceptor`) | `shared/audit.py` session hooks | Automatic: create / update (field diff) / delete / approve / unapprove / post / unpost of every TRACKED table (`Audit/AuditTables.g.cs`, generated from the models). **One** multi-row `INSERT INTO audit_logs` per SaveChanges, through EF, in the save's transaction (a save without one gets its own). Rows byte-identical to Python's (`json.dumps` format, float repr, ensure_ascii, secrets "•••"). Module code does nothing. |
| `IAuditTrail` / `Audit.AuditTrail` | `record_now` | `RecordNowAsync(...)` (contract) and `RecordAsync(..., userId, userName)` (Python's extra arguments), own connection, survives a rollback, never throws. |
| `Provisioning.CompanySetup` | `shared/company_setup.provision_company` | `ProvisionAsync(companyId)`: 161-account chart, 14 voucher series, 6 asset categories, company info, fiscal rule + periods, report / inventory settings, 4 invoice templates — in Python's insertion order. `EnsureFixedCoaAsync`, `GeneratePeriodsAsync(rule, from, to)`. |
| `Services.FirstUse` | `CompanyInfo.get()` … `GlobalLimits.get()` | `CompanyInfoAsync`, `ReportSettingsAsync`, `FiscalYearRuleAsync`, `InventorySettingsAsync`, `InvoiceSettingsAsync`, `GlobalLimitsAsync`: made on first use under `serialize("first-use", table, cid)`, read `ORDER BY id`. Call inside a transaction. |
| `Services.Memberships` | membership insert hooks | `AddAsync(companyId, userId, roleId, status, employeeCode)`: first company from the account's switches and employment details; further company from the role, rights written out. |
| `Services.AccessDefaults` (static) | `shared/access_defaults.py` | `RoleModules`, `RoleRights`, `ModulesJson`, `WriteRightsAsync`, `CurrentGridAsync`, `RoleChangedAsync`. |
| `Services.AccountSecurity` | `shared/account_security.py` | `PasswordProblem`, `ThrottledAsync(login, user)`, `RecordFailedSignInAsync`, `SpendPasswordCheck`, `IdentityProblemAsync`, `MoveInvitationsAsync`, `RegistrationEmailExistsAsync` / `RegistrationPhoneExistsAsync`, `ThrottledMessage`. |
| `Services.SignInService` | `shared/sessions.py`, `login_user` | `FindLoginAsync`, `CheckPassword`, `StartAsync(http, user)` (user_sessions row on its own connection + cookie), `EndCurrentAsync`, `EndOthersAsync`, `SetPasswordAsync` (= `User.set_password`), `SetCompany`, `Clear`. The cookie itself: `ISessionCookie` (Web.Shared). |
| `Services.AuditSearch` (static) | `audit.search`, `decode_changes`, `history_for` | Company audit query (+ members' sign-ins, minus console visits), ACTIONS / MODULES, decoded changes, CSV "Changes" text. |
| `Services.LoginChanges` (static) | `tenancy.login_change_refusal` | Why this company may not change a person's login. |
| `Services.PeriodClose` (static) | `periods.close_checklist` | Drafts in the period, depreciation due, payroll months not run. |
| `Templates.DefaultTemplates`, `Provisioning.InvoiceTemplateSeed` | `InvoiceTemplate.seed_defaults` | The four default bodies, generated from Python's builder (and equal to `InvoiceDesigns.BuildBody`'s, tested). |

Added to **Accountix.Invoicing** (public, additive; Platform references the project): `Services/InvoiceDesigns.cs`
(+ generated `InvoiceDesigns.g.cs`) = `invoice_template.py`'s designer half: `Designs`, `AccentPresets`,
`PlaceholderHelp`, `OptionGroups(doc)`, `DisplayChoices`, `StringOptions`, `Normalise`, `OptionKeys`,
**`BuildBody(design, doc, accent, options)`** (all five designs), `StripSharedSheetAssets`, `SampleItemsTable`,
`SampleContext`, `SheetCss` / `SheetFitJs` / `SheetMarker`. Byte-identical to Python for 5 designs × 2 document
types × 3 option sets, and the preview / create / edit routes write the same HTML and rows as Python's.

Platform also references **Accountix.FixedAssets**: the period-close checklist asks its `AssetCalc.DueDepreciation`
(the port of `due_depreciation` / `monthly_charges`) with live charges from `AssetBooks.LiveByAssetAsync` (one query).

## Routes

| Python | .NET | Status |
|---|---|---|
| `GET /` landing | `AuthController.Landing` | ✅ |
| `GET/POST /signup`, `/signup/check-email`, `/signup/check-phone` | `AuthController` | ✅ |
| `GET /admin`, `/admin/` | `AuthController.AdminEntry` | ✅ (redirects to the console's door) |
| `GET /company/switch/<id>` | `AuthController.CompanySwitch` | ✅ (cross-site refused) |
| `GET/POST /auth/login`, `GET /auth/logout` | `AuthController` | ✅ throttle, refusals, equal-time, user_sessions, audit rows |
| `GET /portal/`, `POST /portal/create` | `PortalController` | ✅ quota under `serialize("own-companies", uid)`, provisioning |
| `GET /dashboard/` (hub) | `HubController` at `/_platform/hub`; `HubPath` startup filter rewrites exactly `/dashboard/` (HR owns `/dashboard`) | ✅ |
| `GET /hub` (the shell's "ERP Hub" link; no Python route) | redirect to `/dashboard/` | ✅ (addition) |
| `GET /settings/` + tabs account, company, periods, reports, cash_flow, labels, inventory, purchase, sales, invoicing, templates, rights, members, invites, audit | `SettingsController` (`[Shell("settings")]`, `Require(section)` per Python's SECTIONS) | ✅ |
| `POST /settings/account`, `/company`, `/periods/rule`, `/periods/generate`, `/periods/<id>/close` (checklist + "Close anyway", row lock), `/periods/<id>/reopen`, `/reports`, `/cash-flow`, `/inventory` (incl. the method-change revaluation) | `SettingsController.Account/Finance` | ✅ |
| `GET/POST /settings/labels`, `/labels/per-line`, `/labels/<id>/rename`, `/labels/<id>/toggle`, `/labels/defaults` | `SettingsController.Finance` | ✅ |
| `POST /settings/documents/<doc>`, `/documents/<doc>/template`, `/documents/<doc>/select-template`, `/invoicing`, `/templates/delete/<id>` | `SettingsController.Invoicing` | ✅ |
| `GET/POST /settings/templates/create`, `GET/POST /templates/edit/<id>`, `POST /templates/preview` | `SettingsController.Invoicing` + `Views/Settings/TemplateForm.cshtml` (template_form.html verbatim) | ✅ build_body / sample context from `Accountix.Invoicing.Services.InvoiceDesigns` (added there, see below) |
| `GET/POST /settings/users/<uid>/rights`, `/users/<uid>/reset-rights`, `/users/<uid>/toggle-active` | `SettingsController.Admin` | ✅ |
| `GET /settings/members/`, `/members/<id>/role`, `/remove`, `/restore` | `SettingsController.Admin` (`serialize("members", cid)`) | ✅ |
| `POST /settings/invite`, `/invite/<id>/revoke`, `GET /settings/invitations/`, `/invitations/<id>/accept`, `/decline` | `SettingsController.Admin` (`serialize("members", cid)`, `serialize("joins", uid)`) | ✅ |
| `GET /settings/audit-log.csv` (`?format=excel|pdf` through IReportExport) | `SettingsController.Admin` | ✅ |

## Sign-in

One mode (29 Oct: the Flask-cookie bridge and the `Accountix:AuthMode` switch are gone).

- **Cookie** `ax_session` (`Core/Security/SessionCookie.cs`): `base64url(json).base64url(timestamp).HMAC-SHA256`,
  key = HMAC-SHA256(`SECRET_KEY`, "accountix-session"), JSON zlib-compressed when shorter (leading dot). Values:
  `_user_id = "<id>:<password fingerprint>"`, `_sid` (random token), `company_id`. `HttpOnly`, `SameSite=Lax`,
  `Secure` over HTTPS, path `/`. Readable for 13 h; the server row decides before that.
- **Server row** `user_sessions` (`token_hash` = SHA-256 of `_sid`): opened on its own connection at sign-in
  (`SignInService.StartAsync`), ended at sign-out (`signed_out`), after 2 h idle (`idle`, `SESSION_IDLE_MINUTES`) or
  12 h in all (`expired`, `SESSION_MAX_HOURS`), or by a password change elsewhere (`ended_elsewhere`).
- **Every request** (`Web.Shared/Infrastructure/SessionAuthHandler.cs`, scheme `AccountixSession`, registered by
  `AccountixApp` for every host, so module test hosts sign in the same way): valid signature, account exists and is
  switched on, fingerprint = the current password hash's, row open. Otherwise signed out: JSON 401 for `fetch()`,
  else a redirect to `/auth/login?next=…` (`/superadmin/login?next=…` under `/superadmin`).
- **Writing** (`ISessionCookie` → `SessionCookieStore`, Web.Shared): `SignIn` (fresh session), `SetCompany` (company
  switch, portal create, the console's "Open books"), `Refresh` (own password change: same row and company, new
  fingerprint; Platform's `SignInService.SetPasswordAsync`, HR's `OwnSession`), `Clear` (sign-out). No ASP.NET
  data-protection keys are involved, so any number of instances accept it.
- **Passwords** keep Werkzeug's format (`PasswordHasher`: writes `scrypt:32768:8:1$salt$hex`, reads scrypt and
  pbkdf2), so no account is rehashed.
- **`SECRET_KEY`** signs the cookie and keys the fingerprint: keep the value the deployment has. Changing it signs
  everyone out.

Switching from the Python app: its `session` cookie is not read, so **everyone signs in once** (docs/CUTOVER.md).

## Differences from Python (deliberate or known)

- Audit diffs always know the old value. Python logs `old: null` (and "update" instead of "unapprove") when code sets an
  attribute it never read after a commit expired it; real routes read first, so they agree.
- Settings with no active company: tabs that need company rows redirect to the portal ("Open a company first.");
  Python renders unsaved defaults.
- A legacy (non-segmented) chart is not migrated (`migrate_legacy_coa`); provisioning only ever sees a new company.
- Pages render `selected="selected"` / `&quot;` where Jinja printed `selected` / `&#34;`; same DOM.

## Phones (browser check, 29 Oct)

Every Platform page — landing, sign-in, sign-up, portal, hub, all 15 settings tabs, user rights, members, invitations,
template designer (new and edit) — at 320, 375 and 1280 px in Chrome (port 5308, isolated context): no sideways
scroll, nothing cut off, no console errors; the designer's live preview renders and follows a design change.
Four flaws were found, **the same in the Python app**, and fixed in both:
- Settings → Invoice Templates: the designs table sat in no scroll box (Actions ran ~60px past a 320px screen) → wrapped in `.tbl-scroll`;
- Settings → Audit Log: the four filter buttons could not wrap (CSV past the edge) → `.al-btns{flex-wrap:wrap}`;
- Settings → Invoice Defaults: the posting-map tables' `.set-table-wrap` had no CSS → `overflow-x:auto` (Python only showed it: its demo data has rows);
- Landing: brand + theme switch + two buttons overflowed at 320px → "Get started" hidden at ≤380px (it is repeated in the hero).

Python E2E: `tests/e2e/test_settings_phone_width.py` (36: every settings tab and the public pages at 320 / 375 px; red
before the fixes, green after). Related suites still green (settings cleanup / sidebar / access, portal, audit log: 57).

## Speed (perf DB: Samba, 46,321 audit rows; best of 3)

The Python column is history: measured on 29 Oct 2026, before the Python app was retired. The tests now check
only .NET's budgets (no live Python timing).

| Page | .NET | queries | Python (29 Oct, history) |
|---|---|---|---|
| Settings → Audit Log | 22–48 ms | 9 | 215 ms |
| Audit Log, filtered (`q=INV&action=create`) | 33–75 ms | 9 | 209 ms |
| Audit Log, page 50 | 24–82 ms | 9 | 165 ms |
| Audit CSV (20,000 rows max) | 490–1,370 ms | 8 | 1,730 ms |
| Portal | 20 ms | 10 | 58 ms |
| Hub | 17 ms | 6 | 100 ms |
| Settings → Members / Rights / Labels / Invoicing | 21 / 21 / 41 / 33 ms | 12 / 8 / 9 / 13 | 145 / 157 ms (members / rights) |

Every tab ≤ 40 queries (asserted on a fresh company in `SettingsFinanceTests`, `SettingsAdminTests`,
`SettingsInvoicingTests`). The audit interceptor adds exactly one INSERT per SaveChanges (asserted).

Proposed index (for the sign-in throttle, which counts `login_failed` rows of the last 15 minutes by IP / login):
`CREATE INDEX ix_audit_logs_action_created_at ON audit_logs (action, created_at);`

## Many users (Race.RunAsync(6, ...))

- Six first requests at once to a fresh company's tabs make one company_info / inventory / fiscal-rule / report row each.
- Six sign-ins at once of one person: six distinct user_sessions rows.
- Six accepts against a member limit of 3: exactly 3 active members.
- Two admins removing each other (3 + 3 requests): one admin remains.
- **Postings racing a period close** (`SettingsFinanceTests.Postings_racing_a_period_close_land_before_it_or_are_refused_and_post_nothing`,
  Platform + Accounting in one host):
  - The race: five people approve cash payments dated in last year's period through Accounting's voucher form, while a
    sixth closes that period ("Close anyway"). Two warm payments into this year's period come first, so the close
    starts about two payments in.
  - No 5xx. Each payment either committed before the close (approved; one posted journal in the period, made before
    `closed_at`; Dr = Cr = its amount) or was refused with the closed-period message and posted nothing (no journal,
    no lines, no reporting-table figure). The voucher form keeps a refused one as an unapproved draft, as Python's did.
  - One more payment after the race is refused too.
  - The period ends closed, voucher numbers are distinct, Dr = Cr over every posted line and equals
    `gl_daily_balances` and the committed total, and Books Integrity (Executive's `BooksIntegrity`) passes.
  - 10 runs in a row, all green, every one mixed: 3–4 payments posted before the close, 1–2 refused in the race.
- **The period lock without timing luck** (`…The_close_waits_for_a_posting_in_flight_and_a_posting_waits_for_a_close_in_flight`):
  - A raw connection holds the period FOR SHARE (a posting in flight): the close waits until it commits.
  - A raw connection holds the period FOR NO KEY UPDATE and marks it closed without committing (a close in flight): the
    payment waits. Once that commits, the payment re-reads the period and is refused, with nothing posted.
  - This would fail if postings stopped taking FOR SHARE, if the close stopped locking, or if the posting stopped
    reloading the period after the lock. (The race alone cannot prove the lock: a journal stamped before
    `closed_at` could still commit after the close.)
  - These two replace the race between the two apps that was dropped on 29 Oct.

## Tests (`tests/Accountix.Platform.Tests`, 45 passed, 0 failed, 0 skipped with no Python, 29 Oct)

The project no longer needs the Python app. `ACCOUNTIX_PYTHON` unset: **45 passed, 0 skipped**
(`ACCOUNTIX_TEST_DB=platform`). Every test builds what it needs through .NET: a fresh company
(`P.FreshCompanyAsync`: EF insert, `Memberships.AddAsync`, Platform's `CompanySetup.ProvisionAsync`), fresh users,
and the module's own routes.

**Golden fixtures** (`Golden/<name>.json`). Each is Python's answer captured on 29 Oct 2026 for exactly the data
the test builds. It is normalised by the same C# the test applies to .NET's answer: the run's tag → `TAG`, ids → roles
(`ADMIN`, `EMP`, `ID`, company → `C`), and years → `Y+0`/`Y-1` where the data follows today's date. The Python that
produced each fixture is in `Golden/capture/<name>.py`, kept for the record. Every golden test passes as is and
fails with `ACCOUNTIX_GOLDEN_PERTURB=1`, which changes one value of .NET's answer. While capturing, .NET's answer
equalled Python's for all nine, so no parity difference was found.

| Fixture | Test | What it pins |
|---|---|---|
| `Provisioning` | `ProvisioningTests.A_provisioned_company_gets_what_python_provisioned` | 161 accounts (parent by code), 14 number series, 6 asset categories, 8 periods (this year − 5 .. + 2), company info, fiscal rule, report / inventory settings, 4 invoice templates, and the 172 audit rows provisioning writes |
| `CompanyProfile` | `SettingsFinanceTests.Saving_the_company_profile_writes_what_python_wrote_…` | the company_info row after the profile form, and its audit row (field diff, summary) |
| `ReportStructure` | `SettingsFinanceTests.Report_structure_rows_move_rename_and_save_as_python_saved_them` | `pl_structure_json` byte for byte (row moved, renamed, empty label → key title) and `pl_detail_rows` |
| `InvoiceSettings` | `SettingsInvoicingTests.Saving_invoice_defaults_and_visibility_switches_writes_what_python_wrote` | the invoice_settings row (tax / withholding / discount defaults, tolerance, switches) |
| `Rights` | `SettingsAdminTests.Saving_rights_writes_what_python_wrote` | all 38 rights rows, the membership's module flags, the membership audit row |
| `CloseChecklist` | `PeriodChecklistTests.The_close_checklist_lists_what_python_listed_on_a_company_with_assets` | the close-checklist lines (draft voucher; depreciation due on 2 of 5 assets) |
| `VoucherAudit` | `AuditTrailTests.A_voucher_lifecycle_is_audited_exactly_as_python_audited_it` | the 5 audit rows of create / edit / approve / unapprove / delete |
| `TemplateBodies` | `InvoiceTemplateTests.Build_body_is_byte_identical_to_python_for_every_design` | `BuildBody` for every design × sales/purchase × 3 option sets, byte for byte |
| `TemplateDesigner` | `InvoiceTemplateTests.Preview_create_and_edit_write_what_python_wrote` | the designer's preview HTML (today → `TODAY`) and every template row after create + edit |

**Dropped (29 Oct)**: `SettingsFinanceTests.A_python_posting_racing_a_dotnet_close_never_lands_in_the_closed_period`
was a race between the two apps, which means nothing once Python is gone. It is replaced by the two .NET-only period-lock
tests under "Many users" (the project now also boots Accounting and references Executive for Books Integrity). The
six-at-once races inside .NET stay.
The Python sign-in test (a .NET cookie accepted by Python) became the .NET-only
`Signing_in_opens_a_server_session_and_lands_on_the_portal` with the sign-in switch.

**Unchanged, .NET only**:
- `AuditTrailTests` — Python json / float repr reproduced; one batched INSERT per save; password redacted;
  record_now survives a rollback.
- `AuthPortalTests` — landing, sign-in opening a server session (the `ax_session` cookie works in a second browser),
  unsafe / local `next`, super admin refused at the customer door, throttle (10 from one address, the password never
  logged), sign-out ends a copied session, cross-site logout refused, 6 concurrent sign-ins, portal (owned / assigned /
  case-insensitive invitations, ≤ 40 queries), company switch (own / foreign), hub tiles, companyless person, company
  creation with quota / slug / uniqueness, sign-up rules, the `ax_session` cookie end to end, antiforgery partial and
  clean scripts on every layout.
- `SettingsFinanceTests` — every tab ≤ 40 queries with the active sidebar item; employee sections and refusals; fiscal
  rule wording; period close checklist, close anyway, reopen; postings racing a close and the period lock (above);
  first-use race; account-tab password rules and session ending; labels.
- `SettingsAdminTests` — admin tabs ≤ 40 queries; employee refusals; shared-login protections; super-admin account
  unreachable; invite / accept / role change / remove / discard rules; member-limit race; last-admin race; audit log
  scoping and CSV; speed budgets.
- `SettingsInvoicingTests` — invoicing tabs; 404 on unknown documents; party-mode refusal; visibility toggles; print
  template selection; a template delete promotes the next default.
- `InvoiceTemplateTests` — seed bodies equal the builder's; custom HTML kept; editor scripts clean.
- `HubRouteTests` — `/dashboard/` is the hub and `/dashboard` HR's dashboard in one host. Whole-app `RouteTableTests` pass.

Nothing left open.
