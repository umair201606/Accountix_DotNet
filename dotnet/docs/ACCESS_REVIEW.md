# Companies, users and access: review and recommended design

Reviewed on 29 Oct 2026 before moving the foundation to .NET. Every finding below
was reproduced against the running Python app through its real routes
(probe tests, kept outside the repo). The .NET foundation reproduces today's
behaviour exactly, so both apps agree while they share the database. The
fixes need to land in both apps together.

## What already works well

- **Company isolation fails closed.** A company table queried with no
  company chosen raises an error instead of returning every company's rows.
  New rows are stamped with the company, and rows can't move between companies.
- **A company opens only through an active membership of an active company.**
  Blocked members, removed members and suspended companies are refused on
  the next request.
- **Sign-in is hardened.** Passwords are stored as scrypt hashes. Repeated
  failures are throttled using the audit log. A wrong login takes as long to
  refuse as a wrong password, and the super-admin console has its own door.
  Changing a password signs out every other session.
- **Protective guards exist.** The owner and the last admin can't be removed.
  A company admin can't change a login that is also used in another company.
- **Employment details are per company.** Manager, designation and department
  live on each membership.

## Findings

| # | Severity | Finding | Where |
|---|---|---|---|
| 1 | **High** | Module access follows a person into every company | `shared/models/base.py` `module_flag` |
| 2 | **High** | Section rights are unrestricted until an admin saves them | `base.py` `can`, `shared/rights_guard.py` |
| 3 | Medium | Console-made companies are owned by the super admin, not the admin they name | `superadmin_app/routes.py` `companies()` |
| 4 | Medium | Signing out doesn't end the session; no idle timeout | Flask cookie session, `hr_app/routes/auth.py` `logout` |
| 5 | Medium | Provident Fund buttons check the account-wide role, not the role in this company | `hr_app/routes/pf.py` `_has_button_perm` |
| 6 | Medium | Email addresses are never verified, and invitations are addressed by email | `app.py` `signup`, `auth.account_settings` |
| 7 | Low | Mixed-case emails can't be invited, and their invitations never show | `shared/routes/settings.py` `invite`, `accept_invitation`; `portal.index` |
| 8 | Low | Last-admin, member-limit and company-quota checks can race | `settings.py` `member_remove` / `member_role` / `accept_invitation`, `portal.create` |
| 9 | Low | "Last login" is never saved: the column doesn't exist | `auth.py` login, `superadmin_app/routes.py` login |
| 10 | Low | The sidebar shows the account-wide role, not the role in this company | `templates/layouts/app_shell.html` |
| 11 | Low | The HR user editor checks a new email against other emails but not User IDs | `auth.py` `user_edit` |

### 1. Module access follows a person into every company
When a company has never saved someone's module access, the switches on
the person's *account* decide, and every company shares that account row.
The console turns all eight on for a company admin it creates.

**Reproduced:** Ali runs company A, which was made in the console. Company B
invites Ali as an **employee** and changes nothing else. In B, Ali can open
Accounting, Inventory, Invoicing, Finance, FBR, Fixed Assets and Executive.

### 2. Section rights are unrestricted until an admin saves them
A person with no rights rows has every right (view, create, edit, approve,
delete) in every section of every module they can open. The test suite
treats this as intended (`test_a_person_whose_rights_were_never_configured_keeps_full_access`).
Combined with #1, Ali can approve and delete vouchers in company B, holding
all 190 of 190 rights there.

### 3. Console-made companies are owned by the super admin
`created_by` is set to the super admin, who isn't a member. As a result:
- the owner protections (can't be removed, role fixed, login protected)
  guard the platform account instead of the company's admin;
- another admin of that company can demote or remove the admin the console named;
- the console marks every such company as the super admin's own.

### 4. Signing out doesn't end the session
The session is a signed cookie with no server-side record. Signing out
deletes it from that browser only, and a copied cookie keeps working for up
to 31 days. Only a password change ends it. There is also no idle timeout,
so a shared office PC stays signed in until the browser closes.

**Reproduced:** a cookie copied before sign-out still opens the portal afterwards.

### 5. Provident Fund buttons use the account-wide role
`_has_button_perm` looks up `current_user.role_id`, the role on the
account, not the one in this company. **Reproduced:** a button granted to
managers is refused to someone who is a manager in this company but an
"employee" on their account.

### 6. Email addresses are never verified
Sign-up and self-service email changes never prove the person owns the
address, and companies invite by email. Someone could register or switch
to an address before its real owner does and receive that company's
invitation. Super-admin approval of sign-ups limits this, but email changes
are self-service.

### 7. Mixed-case emails
Invitations are stored in lower case and compared exactly. An older account
whose email has capitals can't be invited ("No registered user has that
email address"), and invitations meant for it never appear.

### 8. Count-then-write races
The "last active admin", member-limit and company-quota checks count first
and write after, with no lock. Two admins acting at the same moment can
leave a company with no admin; this is reachable because of #3. The app
already has `shared/concurrency.serialize()` for exactly this.

### 9–11. Small ones
- Sign-in sets `user.last_login`, but `users` has no such column, so it is
  silently dropped. The audit log already records sign-ins.
- The sidebar shows `get_role_name()`, the account role.
- The HR user editor only checks other emails. The account settings page
  uses `identity_problem`, which also checks User IDs.

## Fixed (29 Oct 2026)

| # | Fix | Where |
|---|---|---|
| 1, 2 | A person's first company starts from their account's switches, as before. Any further company starts from the role they join as, with that role's rights written out (nothing unrestricted there). The account-wide fallback is gone, and migration 0016 wrote every existing membership's switches out unchanged. A role change moves template rights, never hand-set ones. | `shared/access_defaults.py`, `shared/models/company.py`, `shared/models/base.py` |
| 3 | The console records the admin it names as owner. Migration 0016 moves existing companies to their earliest active admin. | `superadmin_app/routes.py` |
| 4 | Server-side sessions (`user_sessions`): sign-out ends them; 2 h idle and 12 h absolute limits (`SESSION_IDLE_MINUTES`, `SESSION_MAX_HOURS`); a password change ends the other sessions; the token is stored hashed. Everyone signs in once after the upgrade. | `shared/sessions.py` |
| 5 | Provident Fund buttons use the role held in this company. | `hr_app/routes/pf.py` |
| 7 | Emails are matched case-insensitively for sign-in, invitations and the portal. Migration 0016 lower-cases stored emails and adds a unique index on `lower(email)` when no two accounts clash. | settings, portal, sign-in |
| 8 | Member, admin, limit and quota changes are queued per company and per person. | settings, portal, console |
| 9 | Removed the silent `last_login` writes; the audit log has sign-ins. | sign-in routes |
| 10 | The sidebar, hub and rights list show the role in this company. | templates |
| 11 | The HR editor refuses an email that is another account's User ID. | `hr_app/routes/auth.py` |

Not done: **#6 email verification**. It needs a mail service, and the app has none configured.

## Recommended design

1. **Access is explicit, per company.**
   - Every membership carries its own module set and section rights, written
     when the membership is created from a role template the admin picks at
     invite time. Suggested templates: *Employee* = HR self-service only;
     *Manager* = chosen modules, view and create; *Admin* = everything.
   - A one-off migration writes each existing member's **current effective
     access** onto their membership, so nobody gains or loses anything on the
     day. The account-wide fallback is then removed.
   - "Full access" becomes a tick an admin chooses, not what an empty grid means.
2. **The owner is the named admin.** The console sets `created_by` to the
   admin it names. A migration fixes existing companies whose owner is a super
   admin with no membership, moving ownership to the earliest active admin.
3. **Sessions are kept on the server.** A `user_sessions` table records user,
   company, created, last seen, IP and device. Sign-out, member removal, a
   block or a password change revokes the matching rows. Add an idle
   timeout (2 hours by default), a 12-hour absolute limit and "Sign out other devices". Both apps
   check the same table.
4. **Emails are normalised and verified.** Store emails in lower case with a
   unique index on `lower(email)`, after a backfill. Require a signed
   confirmation link before an address can receive invitations.
5. **One lock per company for member changes** (`serialize("members", company_id)`)
   around role changes, removals, blocks, invites and accepts.
6. **Roles come from the membership everywhere:** PF buttons and the
   sidebar, and no fallback to the legacy account role.
7. **Rights live next to the code.** In .NET each action declares
   `[Right(section, action)]` or `[AnyMember]`, and a test fails if a module
   action declares neither (today's `test_rights_enforced` map, kept beside the code).

## What the .NET foundation does today (bridge mode)

- Recognises the person signed in to the Python app from the same session
  cookie (shared `SECRET_KEY`). It applies the same checks as the Python app:
  valid signature, account switched on, password fingerprint still current.
- Opens the same company the Python app would (active membership of an
  active company, else the oldest one) and loads the role, company, module
  switches and rights **once per request**. The Python app re-queries them
  on every check.
- Company isolation in EF Core: a named query filter on every company table,
  fail-closed, rows stamped on insert, cross-company writes and moves refused.
- Werkzeug-compatible password hashing (scrypt and pbkdf2), verified in both directions.
- Antiforgery tokens on every form post, plus the same security headers as the Python app.
- Parity tests: for every person and company in the reference database, .NET
  gives the Python app's answer for role, modules and all section rights.
