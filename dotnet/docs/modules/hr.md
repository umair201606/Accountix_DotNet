# HR: status

**Ported**: every route of `hr_app` this project owns: the HR dashboard, attendance, leave
(+ `leave_flow`), timesheets, employee self-service, manager self-service (MSS loan decisions
included) and the user / member / profile / password pages of `hr_app/routes/auth.py`.
62 Flask rules, all ✅. Same URLs, same markup (ids, classes, inline scripts), same writes.

**Not here (moved, 29 Oct):** `communication.py` (incl. the bell API `/auth/api/notifications*`),
`digital_file.py`, `workplace.py`, `reports.py` belong to **Accountix.HrRecords**. Payroll, PF and
staff loans belong to **Accountix.Payroll**. `/auth/login` and `/auth/logout` belong to **Platform**.
No files were written for the moved pages.

## Routes

| Area | Routes | Status |
|---|---|---|
| Dashboard | `GET /dashboard` (weekly attendance chart for admins / managers) | ✅ |
| Attendance | `/attendance/`, `POST clock-in`, `clock-out`, `break-start`, `break-end` (JSON), `GET live` (JSON), `GET admin`, `POST correct`, `GET/POST policies`, `GET api/logs`, `GET overview` | ✅ |
| Leave | `/leaves/`, `GET calendar`, `GET team-calendar` (JSON), `GET/POST apply`, `GET approvals`, `POST review/<aid>`, `GET/POST holidays`, `GET/POST workflows` | ✅ |
| Timesheets | `/timesheets/`, `POST add-entry`, `POST delete-entry/<eid>`, `POST submit/<wid>`, `GET approvals`, `POST review/<aid>`, `GET projects`, `POST projects/create`, `POST merge-report` | ✅ |
| ESS | `/ess/`, `POST update-profile`, `POST upload-picture`, `GET change-requests`, `POST review-change/<cid>`, `GET/POST loans`, `GET slips`, `GET avatar/<filename>`, `GET performance` | ✅ |
| MSS | `/mss/`, `GET approvals`, `POST approve-leave/<aid>`, `POST approve-timesheet/<aid>`, `POST approve-loan/<lid>`, `POST bulk-approve-timesheets`, `GET team`, `GET team-availability`, `GET team-calendar`, `GET/POST evaluate/<uid>` | ✅ |
| Users (auth.py) | `GET /auth/users`, `GET/POST members/assign`, `GET/POST users/<uid>/edit`, `POST users/<uid>/delete`, `GET/POST users/<uid>/hr-rights`, `POST users/<uid>/reset-password` | ✅ |
| Own account (auth.py) | `GET /auth/profile`, `POST /auth/account-settings`, `GET/POST /auth/change-password` | ✅ |

Guards, as Python: every page sits behind `[Module(Modules.Hr)]` (module_guard) with `[AnyMember]`, and
the role checks of the routes (`is_admin`, `is_manager`, `_can_manage_users`, `_require_manager`, the
approver's own id, a manager's own team) plus the HR section rights through `Access.Can(...)`, with
Python's words and answers (flash "Access denied." + `/dashboard`, JSON 403 / 400, `_reply` / `_answer`
JSON-or-flash). `/auth/profile`, `/auth/account-settings`, `/auth/change-password` and `/ess/avatar/*` are
module_guard's ALWAYS_OPEN: signed in only (`[SignedIn]`), so a company that drops HR keeps its people's
account pages. The 29 Oct fixes are in the user editor: the email / User-ID clash check, template rights
moving with a role change (`Services/AccessDefaults.cs` = `shared/access_defaults.role_changed`), and
`login_change_refusal` / `logins_managed_elsewhere` (`Services/People.cs`).

## Services (`src/Modules/Accountix.Hr/Services`)

- `People`: `User.employees()`, `reports_of`, `get_member`, `_company_managers`, employment per company
  (membership, account copy only for a one-company login), `role_in`, another person's `can`,
  `approver_for`, `login_change_refusal`, `logins_managed_elsewhere`.
- `LeaveFlow`: `quota_for`, `refresh`, `overlapping`, working days, `decide` (serialize("leave", user), re-read).
- `AttendanceRules`: `_get_policy`, `_late_and_half`, `_book_hours`, consecutive days, strptime-compatible correction parsing.
- `FileStore`: `shared/file_store.py` (stored_files, legacy disk fallback when `UPLOAD_FOLDER` is set, safe served types, `secure_filename`).
- `AccessDefaults`, `AccountRules` (`identity_problem`, `move_invitations`), `OwnSession` (own password change: other
  sessions end, this session's `ax_session` cookie is re-issued with the new fingerprint so it stays signed in), `Py` (Jinja's printing of Python values).

## For other modules

- **`Accountix.Hr.Services.Notify`** (public, registered by HrModule): writes a `notifications` row plus a
  `notification_recipients` row exactly as Python's `_create_notification` / `_notify` do
  (`ToAsync(userId, title, message, type, module, refId, createdBy, utcNow)`, or `CreateAsync` + `Recipient`).
  HrRecords' bell reads these rows. Leave decisions notify through it (module "leaves" from the leave page, "mss" from MSS).
- **`People`** is public too (`EmployeesAsync`, `ReportsOf`, `GetMemberAsync`, `ApproverForAsync`, ...): HrRecords'
  reports need the same "employees" and "my team" scoping.
- **Sidebar order for NAV["hr"]** (three projects share the key). Web.Shared `NavigationService` (additive, accepted
  29 Oct): groups with the same name from several projects merge into one block, `NavItem.Order` sorts inside a group
  (stable), and the coordinator's `NavGroup.Order` fixes the order of groups. Number your items so Python's order comes out:
  - Self Service: Attendance 10, Leaves 20, Timesheets 30, My Profile 40, **My Files 50 (HrRecords)**
  - FlatAfter: **Workplace (HrRecords)**
  - Management: My Team 10, Approvals 20, **Reports 30 (HrRecords)**
  - Administration: **Notifications 10 (HrRecords)**, All Attendance 20, Time Policies 30, Holidays 40, Workflows 50,
    Projects 60, **All Files 70 (HrRecords)**, **PF Config 80, Tax & Payroll Settings 90, Staff Loans 100 (Payroll)**, Users 110
  - Payroll's "Financial" group goes in `Groups` (after Self Service).
- Python's dashboard fetches `/reports/dashboard` (HrRecords); the bell lives in the shell (`_AppShell`, Web.Shared), not here.

## Tests (`tests/Accountix.Hr.Tests`, 48, all run, none skipped; `ACCOUNTIX_TEST_DB=hr`; no Python needed)

Each test builds its own company in the hr copy through .NET only (`World.cs`: EF inserts of the company, accounts and
memberships with this company's module switches and employment, as the routes write them), so they repeat and rely on
no leftovers. Since 29 Oct the project needs no Python: `PythonApp` / `[PythonFact]` are gone; what Python used to
check live is now a golden fixture (below). Last run 29 Oct: **48 passed, 0 skipped**, on the used hr copy and again
on a fresh one (`tools/Accountix.RefDb --only hr`), and built and run from a copy of `dotnet/` alone (no Python app above it).
- **Leave flow** (`test_leave_flow.py` 01–16): apply routes to the manager and holds the days (quota made from the
  type's default), MSS approve redirects and moves days to used, a second decision refused (JSON 400 "already"),
  overlap and balance refused, half day keeps its half, half day one date, no manager here → an admin decides,
  rejection needs a reason then frees the days, only the approver decides, missing dates re-show the form (200),
  ESS shows live balances; timesheets: entries validated, submit routes once and freezes, MSS approve is final,
  merge report adds approved hours. The balances asserted at each step (5 / 2 / 0.5 / 2.5, the rejected request's
  5 / 0 / 0 / 5) are the ones Python's `leave_flow.refresh` recomputed on the same rows (checked live until 29 Oct).
- **Attendance** (`test_attendance_flow.py`): clock-in on the company clock and date; corrections parse ("10:00",
  date+time) and re-derive late / half day / regular / overtime by policy (8/2, then 9/1 with a longer policy); bad
  values 400; 14:00 is a half day; the clock follows the company zone (UTC, New York, Karachi).
  **Golden** (`Corrections_re_derive_late_half_day_and_hours_as_python_does`, `Golden/attendance_rules.json`): 18
  corrections of one record through `/attendance/correct` (JSON), compared step by step with Python's answer to the
  same steps: the grace minute (09:15 not late), 13:00 sharp (late, not half) and 13:05 (half day), the overtime cap
  and double overtime, a status in any case and spacing, a half-day clock-in that keeps "absent", five refusals in
  Python's words, a policy made through `/attendance/policies` (08:30, no grace, 9 + 1 h), a logged 37-minute break,
  date+seconds and "T" forms. Each step compares the status and JSON answer and the record (times, late, half day,
  status, regular / overtime / double overtime, approved, row counts) read with one SQL text in both apps, so
  Postgres prints both sides' floats and booleans. The `attendance_corrections` rows (old → new text) are compared too.
- **Authorization** (`test_hr_authorization.py`, HR parts): loans decided only by a manager here, for their report,
  with Loans approve (ledger account opened, queued for payroll, decided once); salary revision needs Payroll edit;
  live board and corrections follow the team and the Attendance rights; HR rights never for yourself; the Employees
  rights govern HR staff; the manager must be one of this company's managers; HR never makes an admin; leave goes to
  someone who can decide it.
- **Membership / tenancy** (`test_membership_employment.py`, `test_tenancy_hr_guards.py`): each company keeps its own
  manager / title / department; the account copy untouched for a two-company person and kept in step for a one-company
  login; approver and "my team" follow the company; the manager dropdown is membership-based; the role is written on the
  membership (global column untouched) and template rights move with it while hand-set ones stay; an admin cannot demote
  themself; the owner's role is fixed; duplicate email / another account's User ID refused in words; a login reaching
  another company keeps its email, password and switch; a reset ends that person's sessions; an email change moves
  waiting invitations; member assign is per company, never takes another company's global code, opens the employee account.
- **Pages**: all 25 HTML pages render for admin / manager / employee as Python allows (others: flash + `/dashboard`), with
  Python's markers, the HR heading and sidebar, back links, no HTML-encoded text inside `<script>`; JSON endpoints keep
  Python's shapes (Flask's HTTP dates in `/leaves/calendar`); avatar through stored_files (served type from the extension,
  path tricks 404); profile edits as change requests; a loan request files the loan, its account and the manager's notice.
- **Unit**: Python float / `%g` / `.title()` printing, late / half day, strptime parsing, role templates, `secure_filename`, guards on every action.
- **Bugs fixed in both apps** (`FixedBugsTests`, 5): the user editor never prints "None" (stored "None" reads empty) and
  saves empty fields; decisions read "approved" / "rejected"; no records show 0 late / half days; "Standard" shift
  without a policy; "-" for a missing joining date.

**Golden fixtures** (`Golden/*.json`, copied to the test output): Python's answers, captured on 29 Oct 2026 by the
scripts in `Golden/capture/` (kept for the record; each header has its command line). A test takes the same steps
through .NET and compares its normalised answer (dates as DAY) with the file. Each was checked to fail when the .NET
answer is perturbed: a 38-minute break instead of 37 (two steps' overtime differ), and a refusal text with one
character changed.

**Dropped on 29 Oct** (the Python app is being retired):
- `LeaveFlowTests.Python_recomputes_the_same_balances_dotnet_stored`: the same steps and the same concrete balances are
  asserted in `Apply_routes_decides_and_keeps_the_balance` (steps 01–09).
- `AttendanceFlowTests.Python_derives_the_same_late_half_and_hours_from_the_rows_dotnet_wrote`: replaced by the golden
  test above, which covers its three corrections and fifteen more.
- `ManyUsersTests.Python_and_dotnet_applying_at_once_file_one_request`: a race between the two apps, meaningless once
  only .NET runs. The six-at-once .NET races stay.
- `SpeedTests.Dotnet_is_not_slower_than_python_on_the_same_lists`: it timed Python live. The same pages keep their
  budgets in `List_pages_cost_a_fixed_number_of_queries_for_200_people`; Python's times stay in the table below as history.

## Speed

Budgets (docs/SPEED_AND_CONCURRENCY.md): ≤ 800 ms for a month list, ≤ 40 queries per page. HR budget asserted: ≤ 25 queries.
"HR Speed Co" in the hr copy: 200 employees under one manager, a month of attendance (4,400 rows), a pending leave,
timesheet and loan each. Fastest of 3, test host (`SpeedTests`). The Python column is history: measured live on the
same data until 29 Oct, no longer run.

| Page | .NET | Queries | Python (same data, 29 Oct) |
|---|---|---|---|
| `/auth/users` (200) | 20–39 ms | 9 | 3,269 ms |
| `/attendance/admin` (month, 4,400 rows) | 182–278 ms | 10 | 553 ms |
| `/mss/team` (200) | 8–12 ms | 7–8 | 143 ms |
| `/mss/approvals` (600 items) | 27–32 ms | 9 | 112 ms |
| `/leaves/approvals` (200) | 13–16 ms | 7 | 109 ms |
| `/mss/` | 13 ms | 10–11 | |
| `/attendance/live` | 10–12 ms | 10–11 | |
| `/auth/members/assign` | 12 ms | 9 | |
| `/timesheets/approvals` | 11 ms | 7 | |
| `/mss/team-availability`, `/mss/team-calendar`, `/leaves/calendar` | 6–11 ms | 7–9 | |
| `/dashboard` (weekly chart) | 16 ms | 8 | |
| `POST /timesheets/merge-report` (200 people) | 83 ms | 9 | |

Perf database (Samba, `[PerfFact]`, read-only pages): every HR page 9–41 ms, 7–11 queries.
No per-employee queries anywhere: Python's per-row lookups (live board, availability, merge report, projects' package
counts, approvals' people and types) are one query each here.

**Proposed indexes** (Alembic owns the schema; none created from .NET):
```sql
CREATE INDEX ix_attendance_company_date ON attendance (company_id, date);           -- month register (range now)
CREATE INDEX ix_leave_approvals_approver ON leave_approvals (company_id, approver_id, status);
CREATE INDEX ix_timesheet_approvals_approver ON timesheet_approvals (company_id, approver_id, status);
CREATE INDEX ix_leave_requests_user ON leave_requests (company_id, user_id, status);
CREATE INDEX ix_loan_requests_user ON loan_advance_requests (company_id, user_id, status);
```

## Many users

Same locks and keys as Python, so both apps queue together: `serialize("leave", employee)` on apply and on every
decision (re-read after the wait), `serialize("attendance", person)` on clock-in / out / breaks, FOR NO KEY UPDATE on
timesheet weeks (submit), timesheet approvals (review, MSS, bulk), loans and change requests. Tested with
`Race.RunAsync(6, ...)` (`ManyUsersTests`), no 5xx in any:
- six Apply at once → one request, days counted once, one notice to the approver;
- six decisions on one leave (approve via MSS and reject via the leave page mixed) → one decision, balance counted once, one notice;
- six clock-ins → one attendance row and one log; six clock-outs → one overtime row;
- six submits → one approval; six approvals of one timesheet (MSS, review page, bulk mixed) → approved once;
- six HR-rights saves for one person → one clean set of rows; six edits of one person → one edit's values, whole;
- (until 29 Oct) **Python vs .NET**: 3 Python test clients and 3 .NET clients applying for the same dates at one instant →
  one request. Dropped with the Python app; the locks and keys are unchanged.

The HR user pages (assign, edit, delete, HR rights) take `serialize("members", company)`, the key Settings' member
changes use, so HR and Settings member changes queue together. Python's `hr_app/routes/auth.py` takes the same lock
since 29 Oct (`test_hr_authorization.py::test_member_changes_queue_per_company`).

## Responsive

Every page checked in Chrome at 320 px (scratch host, admin / manager / employee): no sideways scroll, no script errors.
Two pages needed a scroll box Python's markup lacks (the bare tables pushed the page 1–105 px wide): HR rights and
the profile's details table now sit in `<div class="table-wrap" style="overflow-x:auto">`.

## Differences from Python (deliberate)

- The two scroll boxes above.
- Unreadable numbers on the policy / workflow / project forms are refused in words (FormInput) instead of a 500.
- A `ChangeRequest` approval writes only the seven profile fields the ESS form files (Python `setattr`s any field name).
- `request.remote_addr` is the client address after forwarded headers (Python, without ProxyFix, records the proxy's).

## Python bugs fixed in both apps (29 Oct)

| Bug | Python | .NET | Tests |
|---|---|---|---|
| The user editor printed `None` in empty Designation / Department / Phone and a save stored the text "None" | `hr_text` filter (hr_app/app.py): None and the text "None" read as empty in the user form, user list, member assign, MSS team / index / evaluate, profile, ESS phone. Stored rows are not rewritten; the next save of the form clears them | `Py.Text` in the same views | `test_hr_authorization.py::test_the_user_form_never_prints_none_into_empty_fields`, `FixedBugsTests` |
| "Leave request rejectd." (also timesheets, change requests, loans) | approved / rejected in leave.review, mss.approve_leave, mss.approve_loan, timesheet.review, ess.review_change | `Py.Decided` | `test_leave_flow.py::test_17`, `test_hr_authorization.py::test_decisions_read_approved_and_rejected`, `FixedBugsTests` |
| The attendance page printed "None" late / half days with no records | `(stats.late_days or 0)` | `?? 0` | `test_attendance_flow.py::test_07`, `FixedBugsTests` |
| HR member changes took no lock | `serialize("members", company)` in member assign / edit / delete / HR rights (POST) | already | `test_member_changes_queue_per_company`, `ManyUsersTests` |

Python E2E after the fixes (scratchpad venv, `E2E_PORT=5062`): test_leave_flow 17, test_attendance_flow 8,
test_hr_authorization 17, test_tenancy_hr_guards 8, test_membership_employment 8, test_account_security 27,
test_hr_pipeline 16, test_login_ownership 8, test_access_fixes 13, test_storage_and_money 12 (+2 Postgres-only skipped),
test_upload_types 8, test_superadmin_console 47, test_responsive_pages + test_payroll_run 54: all passed.

Also fixed in both apps (second round, 29 Oct): a company with no time policy shows "Standard Shift: 09:00 - 18:00"
(the template's own fallback name; it printed "None Shift"), and the evaluation page shows "-" for a missing date of
joining, as its other empty fields do. Tests: `test_attendance_flow.py::test_08`,
`test_hr_authorization.py::test_evaluation_shows_a_dash_for_a_missing_joining_date`, `FixedBugsTests` (5 in all).
