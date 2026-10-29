# HR records: status

`src/Modules/Accountix.HrRecords` (key `hr`: its sidebar links merge into HR's) ports
`hr_app/routes/communication.py`, `digital_file.py`, `workplace.py`, `reports.py`, the bell API
of `hr_app/routes/auth.py` and their templates. Tests: `tests/Accountix.HrRecords.Tests`,
`ACCOUNTIX_TEST_DB=hr_records`, **67 passed, none skipped, with no Python app** (29 Oct; see Tests).

Nothing of this scope existed in Accountix.Hr. Copied from it: `Services/FileStore.cs`
(`shared/file_store.py`: stored_files, legacy upload-folder fallback, safe content types), unchanged
apart from the namespace. Hr's `Notify` service writes the notifications other modules send; the
bell here reads them.

## Routes

| Python endpoint | URL | Status |
|---|---|---|
| communications.index | GET `/communications/` (admin: sent + e-mail log; others: own notifications) | ✅ |
| communications.send_notification | POST `/communications/send-notification` (all / by role held in THIS company) | ✅ |
| communications.send_email | POST `/communications/send-email` (logged `not_sent`, module `manual`) | ✅ |
| auth.api_notifications | GET `/auth/api/notifications` (bell list, Flask jsonify bytes) | ✅ |
| auth.mark_notification_read | POST `/auth/api/notifications/<id>/read` (csrf-exempt + origin guard) | ✅ |
| auth.mark_all_read | POST `/auth/api/notifications/read-all` (csrf-exempt + origin guard) | ✅ |
| digital_files.index | GET `/digital-files/` | ✅ |
| digital_files.upload | POST `/digital-files/upload` (stored_files key `<user_id>/<uuid>.<ext>`) | ✅ |
| digital_files.download | GET `/digital-files/download/<id>` (owner or admin; legacy disk files read) | ✅ |
| digital_files.delete | POST `/digital-files/delete/<id>` (owner or admin) | ✅ |
| digital_files.admin | GET `/digital-files/admin` | ✅ |
| digital_files.verify | POST `/digital-files/verify/<id>` | ✅ |
| digital_files.check_expiry | GET `/digital-files/check-expiry` | ✅ |
| digital_files.profile | GET `/digital-files/profile/<uid>` | ✅ |
| workplace.index / announcements / events / kanban | GET `/workplace/`, `/announcements`, `/events`, `/kanban` | ✅ |
| workplace.create_announcement / create_event | POST `/workplace/announcements/create`, `/workplace/events/create` | ✅ |
| workplace.create_board / add_task / board_tasks / update_task / delete_task | `/workplace/kanban/...` (JSON) | ✅ |
| reports.index | GET `/reports/` (leads: builder; others: the HR dashboard's employee form) | ✅ |
| reports.dashboard | GET `/reports/dashboard` (the HR dashboard's figures) | ✅ |
| reports.attendance_chart | GET `/reports/attendance-chart?year=` | ✅ |
| reports.query | POST `/reports/query` (report builder) | ✅ |
| reports.export_excel | POST `/reports/export-excel` (excel / pdf / csv through IReportExport) | ✅ |
| reports.export_attendance / export_leaves | GET `/reports/export-attendance`, `/reports/export-leaves` | ✅ |

Sidebar (NAV["hr"]): My Files (Self Service, 50), Workplace (flat after), Reports (Management 30, admin
or manager), Notifications (Administration 10, admin), All Files (Administration 70, admin). All
controllers are `[Module(Modules.Hr)]` + `[AnyMember]`, with the role checks in the actions as
Python writes them (`is_admin`, `is_manager`, owner/assignee, `can(section, "view")` for report
columns and downloads). The bell controller has no `[Module]` (module_guard.ALWAYS_OPEN): signed in
with a company open is enough.

## The bell in the app shell ✅

`Accountix.HrRecords.Components.NotificationBellViewComponent` renders Python's `#notif-dropdown` block
(unread count, the five newest unread, "Mark all read"); the shell (Web.Shared) calls it between the module
badge and the user menu whenever HrRecords is loaded. One or two queries per page (the list only when
something is unread). Tested: the count and items equal Python's shell on the same data (golden), and in
Chromium (Playwright for .NET) clicking an item and "Mark all read" (static/js/app.js) update the bell and the records.

Web.Shared now writes UTF-8 like Jinja (no `&#x…;`), so pages compare as Python's without decoding.

## Fixed in both apps (29 Oct, coordinator's request)

| Bug | Cause | Fix (Python `hr_app/routes/reports.py` + `reports/index.html`, and .NET) |
|---|---|---|
| Report builder "Total Hours" 24 times too many on Postgres (8 h read 192) | `round(float(totals) * 24, 2)` applied to both databases; the × 24 only turns SQLite's `julianday()` days into hours, Postgres' `EXTRACT(epoch …) / 3600.0` is already hours | × 24 moved into the SQLite expression; the figure is the summed hours on either database. .NET: no × 24 |
| Department picker hard-coded (Engineering, HR, Finance, Marketing) | template literal | the departments of the people the person reports on, as kept on this company's memberships (active staff; blank, blocked, switched-off and other companies left out), sorted; "No departments recorded" when there are none |
| Dashboard "active payroll" counted an unapproved run of this month in any year | `extract("month", run_date)` only | also `extract("year", run_date) == today.year` |

Tests: Python `tests/e2e/test_hr_reports.py` (4, new): red against the old code, green on SQLite and on a fresh
Postgres database (`TEST_DATABASE_URL`); with `test_hr_authorization.py`, `test_responsive_pages.py` and
`test_rights_enforced.py`: 113 passed (E2E_PORT=5067). .NET `ReportFixesTests` (3): each fix in .NET, the hours
report equal to Python's answer (Golden/report_fixes.json); the 15 report-builder scenarios still byte-equal (golden).

## Still worth knowing

- **Finance's CSV writes a double 0.0 as `0`** (Python `0.0`). This project passes floats as decimals carrying
  Python's repr, so its CSVs equal Python's; other modules may want Finance to write Python's repr for doubles.
- The department report with no department chosen still reports on the whole company (Python's
  `_get_scope_users` falls back), now reachable only when a company has no departments.

## Differences from Python (deliberate)

- "Today" and the default year/month (`/reports/`, `/reports/dashboard`, attendance chart and downloads,
  expiry checks, upcoming events) use the company's clock; Python used the server's date (UTC on Vercel, so
  it lagged between 00:00 and 05:00 in Pakistan). Same choice as Accounting.
- Lists Python leaves unordered are ordered: the builder's people and the employee picker by user id
  (Python: the database's physical order), task ties by id, categories by id.
- Bad input that made Python crash answers instead of a 500: `/reports/query` with a non-object body or an
  unreadable date / user id → 400; downloads with month outside 1–12 → 400; an upload's `category_id=0` → none.
- Deleting a file, verifying it and moving / deleting a task lock the row first (Python did not): two at once
  act once, the second gets 404, never a 500.

## Tests (67, all passed; no Python needed)

Every test builds its own data through .NET (the app's routes, or EF inserts mirroring what the routes write:
`World.cs`, `BigOffice.cs`) and runs with no Python app and no `ACCOUNTIX_PYTHON`. Where a test used to compare
.NET with the Python app on the same data, Python's answer was **captured once (29 Oct 2026) into a golden
fixture**, and the test compares .NET's normalised answer with it.

### Golden fixtures (`Golden/*.json`, copied to the test output)

Captured by the scripts in `Golden/capture/` (kept for the record; they need the Python app, so they cannot run
once it is gone). Each runs the same steps through Python's routes (Flask test client) on its own company in the
hr_records copy, normalises what changes from run to run, and prints the fixture:

```bash
cd <repo> && DATABASE_URL='postgresql://postgres:@/accountix_ref_hr_records?host=<socket dir from tools/reference.json>' \
  SECRET_KEY='<reference.json secret_key>' $PY -I dotnet/tests/Accountix.HrRecords.Tests/Golden/capture/<name>.py \
  > dotnet/tests/Accountix.HrRecords.Tests/Golden/<name>.json
```

| Fixture (script) | Tests | What Python answered | Normalised |
|---|---|---|---|
| `big_office` | `ReportsTests.The_report_builder_answers_what_python_answers` (15 scenarios), `ReportsTests.The_attendance_chart_answers_what_python_answers`, `ExportTests.Attendance_and_leave_csv_equal_pythons`, `ExportTests.The_report_builder_download_equals_pythons` | the report builder's status + JSON bytes for 15 scenarios (admin company / default columns / dates / department / no department / one person / outsider 404; manager team / report / someone else's 403; restricted manager's column-right 403s; employee 403) on ~200 employees; the attendance chart for four roles and two years; the attendance and leave CSVs (admin, manager, four periods); the report builder's CSV and file names | ids → the world's keys (`admin`, `mgr`, `rmgr`, `e0`…`e199`); file names after the company part, the day as TODAY. Runs on the newest BigOffice a .NET test run left in the copy (its data is a seeded `Random` placed around the fixed `BigOffice.Anchor`, 15 Oct 2026, so the figures are the same on any day) |
| `dashboard` | `ReportsTests.Dashboard_figures_answer_what_python_answers` | `/reports/dashboard` for admin, manager and three employees (clocked in / done / not clocked; blocked and switched-off members; leave in every state; this month's unapproved run) | none: dates are each run's own today |
| `report_fixes` | `ReportFixesTests.Total_hours_are_the_hours_worked` | the hours report (real hours, open shifts, blank departments) | ids → keys |
| `bell` | `BellTests.The_shell_shows_the_unread_count_and_the_newest_five_as_python_does`, `CommunicationsTests.The_bell_answers_byte_for_byte_what_python_answers_and_marks_read_alike` | the shell's bell block (count, newest five, text); `/auth/api/notifications` bytes after 23 sends through the form, after reading two, after read-all; the read answers | recipient ids → `n<i>` (the notification's number in its title); `created_at` digits → 0 |
| `files` | `DigitalFilesTests.Expiry_check_and_profile_match_python` | `/digital-files/check-expiry` bytes for six documents around today | ids → `doc<n>`, dates → `today±days` |
| `kanban` | `WorkplaceTests.Task_boards_answer_what_python_answers_and_guard_who_may_change_them` | create-board and move answers; the task list for owner, assignee and manager | task ids → `a` / `b`, the board's → `board` |
| `pages` | `PageParityTests.Each_page_has_pythons_ids_classes_and_text` | the content (page header to `</main>`) of 15 page/role combinations after the same data through Python's forms; the test compares element ids, classes, visible text and the report's employee picker | picker values → `USER_<key>`, employee codes / e-mails → `CODE_<key>` / `EMAIL_<key>`; on both sides `dd-Mon-yyyy hh:mm` stamps → DATE, today's ISO date → TODAY, picker years → offsets from this year |
| `formatting` | `FormattingTests.Floats_rounding_and_json_match_python_on_many_values` | `repr`, `round(v, 2)`, `round(v * 24, 2)`, `round(v, 1)`, `%g` of 400 floats of four kinds, and `jsonify` bytes | none (the inputs are in the fixture) |

Each golden test was checked to fail when .NET's answer is perturbed (one PF amount, one attendance flag or
clock-out, a leave length, a dropped attendance row, a renamed announcement, a moved expiry or due date, a
changed notification type or message, a department's spacing, a float nudged by 1e-12; the six error-answer
report scenarios with their text altered), and to pass as is.

### Made .NET-only (the Python half dropped; the concrete expectations stay)

- `CommunicationsTests.A_notification_by_role_reaches_the_role_held_here` (was `…the_same_people_in_both_apps`):
  the recipient lists per target are asserted as concrete sets (the ones Python's send wrote, 29 Oct).
- `DigitalFilesTests.An_upload_lands_in_stored_files_and_is_served_back_byte_for_byte` (was `…both_apps_serve_the_same_bytes`):
  Python reading .NET's stored file and .NET serving Python's upload were cross-app checks; the stored_files row
  (key, type, size, company, bytes) is asserted as before and a second upload (PNG) is served with its type.
- `ReportFixesTests.The_department_picker_lists_the_companys_departments` and
  `ReportFixesTests.Active_payroll_is_a_run_of_this_month_of_this_year` (`…_in_both_apps`): concrete values only.

### Dropped

- `CommunicationsTests.Python_and_dotnet_sending_together_each_reach_everyone_once` (a race between the two apps):
  there is one app now. The same-app race `Six_sends_at_once_make_six_notifications_with_each_person_once` stays.
- The live Python timings in `ReportsTests.The_report_on_200_people_runs_a_fixed_number_of_queries` (written to
  `ACCOUNTIX_TIMINGS` as `PYTHON …`); the recorded numbers stay in the Speed table below as history.

### Browser tests

`BellTests.Clicking_an_item_and_mark_all_read_in_the_browser_update_the_bell_and_the_records` and
`NarrowScreenTests.Pages_fit_phones_from_320px_and_raise_no_script_errors` drove the Python venv's Playwright; they
now use **Microsoft.Playwright 1.64.1** (as `tests/Accountix.E2E`: its Chromium, chromium-1248, or the installed
Chrome) against `LiveApp` on a free local port.

### By area

- **Formatting** (19): Python's float repr, `round()`, `%g` and Flask's `jsonify` bytes (golden, 400 values).
- **Communications** (5): "by role" = the role held here (blocked members and switched-off accounts out);
  flashes and refusals; e-mail logged `not_sent`; the bell's JSON byte-equal to Python's (golden), read / read-all,
  someone else's id changes nothing, a foreign Origin refused; bell **1 query of its own**; six sends at once.
- **Digital files** (6): upload → stored_files row and file, served back byte for byte; owner/admin access,
  refusals, verify, delete; bad uploads in Python's words; a pre-database file read from UPLOAD_FOLDER;
  check-expiry JSON equal to Python's (golden); profile page; two deletes at once.
- **Workplace** (2): who may post (role here), the event/announcement rows, kanban JSON equal to Python's (golden)
  for owner / assignee / manager, permission refusals, status/position rules.
- **Reports** (21): 15 report-builder scenarios (golden); attendance chart (golden) and dashboard figures (golden);
  scope by role; query budget; pages per role (the year pickers read from their own selects: the employee
  picker's values are user ids, which once read like a year); Samba speed (PerfFact).
- **Exports** (3, need Accountix.Finance; `-p:WithFinance=false` leaves them out): attendance and leave CSV (golden,
  line for line), report-builder CSV (golden) with Excel/PDF built, file names as Python's, refusals.
- **Bell** (2): the shell's bell equals Python's (golden); read-all through the shell empties it; in Chromium an
  item click and "Mark all read" update the badge and the records.
- **Report fixes** (3): real hours (golden), the company's departments, this month of this year.
- **Pages** (6): ids, classes and visible text of all 15 page/role combinations equal Python's (golden); sidebar
  per role; every action declares `[AnyMember]`; script blocks carry no HTML-encoded data; real Chromium at
  320 / 360 / 1366 px: no sideways scroll, no script error on 11 pages.

## Speed (best of 3, server time, test host; page counts include the bell)

The Python column was measured on the same data on 29 Oct, before the Python app was retired; it is kept as
history (the tests no longer time Python).

| Page | .NET | queries | Python (29 Oct) |
|---|---|---|---|
| POST `/reports/query`, 200 people, all 9 columns | 13 ms | 14 | 3,275 ms |
| `/reports/` (admin, 200 in the picker) | 6 ms | 10 | 83 ms |
| `/reports/` (manager) | 6 ms | 11 | 92 ms |
| `/reports/dashboard` (admin / employee) | 5 / 3 ms | 12 / 9 | 32 / 24 ms |
| `/reports/attendance-chart` | 4 ms | 8 | 11 ms |
| `/digital-files/admin` | 4 ms | 9 | 60 ms |
| `/communications/` (admin) | 4 ms | 9 | 69 ms |
| `/workplace/` | 7 ms | 11 | 109 ms |
| `/auth/api/notifications` | 5 ms | 7 (6 sign-in + 1) | |
| Samba (perf DB): every page above, `/workplace/*`, check-expiry | 4–7 ms | 7–12 | |
| Samba: POST `/reports/query` for the year | 6 ms | 8 | |

Queries do not grow with people: the builder runs one grouped query per column family (hours; overtime;
leave + sick; late + present; PF; loans) over everyone in scope, where Python ran ~9 per person.

## Many users

- Six admins sending at once: six notifications, each person once in each. Python did not serialize sends, so
  neither does .NET (each send is its own row).
- Two deletes of one file at once: removed once, no server error.
- (History: a race of Python's and .NET's sends together, six notifications each with exactly this company's
  employees, passed on 29 Oct; dropped with the Python app.)

## Proposed indexes

- The bell runs on every page in both apps and filters `notification_recipients` by company only:
  `CREATE INDEX ix_notification_recipients_unread ON notification_recipients (company_id, user_id, id DESC) WHERE is_read = false;`
- Optional (small tables today): `digital_files (company_id, user_id)`, `leave_requests (company_id, user_id)`.
