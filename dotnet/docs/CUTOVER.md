# Cut-over: module by module, on one domain, then retiring Python

Status, 29 Oct 2026: **plan only, nothing switched.** Deployment is in [DEPLOY.md](DEPLOY.md).

> **Sign-in changed, 29 Oct (later): the .NET app no longer reads or writes the Python app's `session`
> cookie.** It signs people in with its own `ax_session` cookie over the same `user_sessions` table (see
> "Sign-in at the switch" below and docs/modules/platform.md, "Sign-in"). The bridge described in the ground
> rules and phases below, where both apps share one cookie, is gone: the move is one switch of every route,
> not module by module. **After the switch everyone signs in once.** Passwords are unchanged (same Werkzeug
> hashes), so nobody has to reset anything.

## Ground rules

1. **One domain.**
   - The Python app signs people in, out and between companies, and the .NET app reads the same
     `session` cookie (bridge sign-in, shared `SECRET_KEY`).
   - The cookie is host-only (no `Domain`), `HttpOnly`, `SameSite=Lax`, and `Secure` on Vercel. So
     both apps must answer on the same host name: every browser request carries the cookie, whichever
     app serves it.
2. **A module moves by URL prefixes.** The proxy sends a module's prefixes to Azure. Everything else
   keeps going to Python. The .NET URLs are Flask's, character for character.
3. **Data is compatible by construction**, which is why rollback is a routing change:
   - one database; the schema was Alembic's, and is now `src/Accountix.Migrations`' (DEPLOY.md, "Migrations");
   - the same rows, journals, document numbers, edit versions, advisory-lock keys and row-lock order
     (porting guide). Python reads what .NET wrote, and the reverse.
4. **The cross-app write rule.**
   - **A .NET page may post to a Python URL.** Python's guard (`shared/csrf_guard.py`) only checks
     that `Origin`/`Referer` is its own host, which it is.
   - **A Python page may NOT post to a .NET URL.** .NET requires an antiforgery token, and Python
     pages carry none: no meta tag, no header. The request is refused with 400.
   - So a page and every URL it POSTs to must move together, or the target must move later.
   - Cross-app GETs (JSON, images) work both ways.
5. **Flashes do not cross apps.**
   - Python keeps them in the session cookie (`_flashes`), .NET in its TempData cookie.
   - A message set by one app, followed by a redirect to the other, shows up on that app's next page,
     not at once.
   - The moves are grouped so that the common redirects stay within one app (see "Flash" below).

## Routing: Vercel rewrites (during the move), Azure Front Door (an option), DNS to Azure (at the end)

**Recommended while both apps run: keep Vercel as the front door** and proxy ported prefixes to the
App Service with external rewrites.
- The domain, its certificate and DNS stay where they are.
- Python pages keep their path: browser → Vercel → function.
- **Rollback is a Vercel redeploy, or "Instant Rollback" to the previous deployment, in seconds.**

`vercel.json` today uses the legacy `routes` list, which cannot be mixed with `rewrites`. Insert the
ported prefixes **before** the catch-all, in order. Flask's exact paths matter: `/dashboard` (HR) is
not `/dashboard/` (hub). Example after phase 1:

```json
{
  "builds": [{ "src": "api/index.py", "use": "@vercel/python" }],
  "routes": [
    { "src": "/net/(.*)",          "dest": "https://accountix-net.azurewebsites.net/net/$1" },
    { "src": "/accounting(/.*)?",  "dest": "https://accountix-net.azurewebsites.net/accounting$1" },
    { "src": "/finance(/.*)?",     "dest": "https://accountix-net.azurewebsites.net/finance$1" },
    { "src": "/executive(/.*)?",   "dest": "https://accountix-net.azurewebsites.net/executive$1" },
    { "src": "/(.*)", "dest": "api/index.py" }
  ]
}
```

What an external rewrite does, and what must be checked on a **preview deployment** before phase 1
(phase 0 below):

| Question | Expected | How phase 0 checks it |
|---|---|---|
| Browser URL | Unchanged (a rewrite, not a redirect) | Address bar |
| `Cookie` forwarded to Azure | Yes (request headers are proxied) | `/net/request` lists `session`; `/net/access` shows the person |
| `Set-Cookie` from Azure (antiforgery `ax_af`, TempData) | Passed to the browser. Without a `Domain`, the browser stores it for the public host | DevTools → Application → Cookies on the public domain |
| `Host` at Azure | The Azure host name. The public one should arrive in `X-Forwarded-Host` | `/net/request` → `host` (after forwarding) = public domain; `x_original_host` = azurewebsites name |
| Client address | Vercel adds the visitor to `X-Forwarded-For`; App Service adds Vercel's | `/net/request` → `remote_ip` = your IP with `FORWARDED_LIMIT=2` |
| Scheme | `X-Forwarded-Proto: https` | `scheme` = https, HSTS header present |
| Caching by Vercel's CDN | Vercel honours the origin's `Cache-Control` on external rewrites. Projects created on or after 6 Apr 2026 cache by default; older ones do not unless opted in. .NET pages say `private`; static files `public, no-cache` | Response headers show `x-vercel-cache: MISS`/absent on pages. Also set the response header `x-vercel-enable-rewrite-caching: 0` on the rewritten routes, to be sure |
| Compression | Brotli from .NET passed through (or re-encoded) | `content-encoding: br` on a big register |
| Query strings | Passed through | `/accounting/vouchers/list?vtype=JV` filters |
| Time limits / body size | Check Vercel's current limits for external rewrites. Exports run ≤ 3 s; uploads (HR documents) are the largest bodies | Export a year's general ledger to Excel; upload a 4 MB file once HR moves |

If `X-Forwarded-Host` does not arrive, set `FORWARDED_ALLOWED_HOSTS` to the azurewebsites name only,
and accept that links the app builds from the request host would name Azure. The .NET app builds
none today: links are literal paths, and refusals compare the referrer with the request host. So
that "back to the page you came from" falls back to the module landing page. Re-check before phase 1.

**The alternative: Azure Front Door (Standard) in front of both.**
- Two origin groups, Vercel and App Service, with route rules by path. DNS for the domain moves to
  Front Door; Front Door forwards `X-Forwarded-Host`, and its `X-Azure-FDID` lets the App Service
  accept only Front Door traffic.
- Costs: a new certificate and DNS step now, an extra paid resource, and Vercel discourages putting
  a proxy in front of it (its firewall and edge features see Front Door's addresses).
- Python's origin guard needs the public host. Send `X-Forwarded-Host`, or set
  `TRUSTED_ORIGINS=https://erp.<domain>` on Vercel.
- Worth it only if Vercel rewrites fail a phase-0 check.

**At the end: DNS points at Azure** (App Service custom domain with a managed certificate, or Front
Door). Vercel is retired.

## Module → URL prefixes (from the Flask URL map: 418 rules)

Many modules share `/inventory/…` and `/invoicing/…`, so routes are listed by **sub-prefix**, never `/inventory/*` wholesale.

| Phase | .NET module(s) | Prefixes sent to Azure | Must move together because |
|---|---|---|---|
| 1 | Accounting, Finance, Executive | `/accounting`, `/accounting/coa`, `/finance`, `/executive` | Reports are read-only, the lowest risk and the biggest speed gain. `gl_entry.js` and the invoice forms GET `/accounting/gl-entry` and `/accounting/api/accounts` cross-app (GET: fine) |
| 2 | Inventory | `/inventory/` (exact: dashboard), `/inventory/categories`, `/products`, `/units`, `/stock`, `/vouchers`, `/reports` | Products post to `/inventory/units/quick-create` (same phase) |
| 3 | Invoicing, Parties, Orders, Returns, **FBR** | `/inventory/invoices`, `/inventory/purchase-invoice`, `/inventory/sales`, `/inventory/purchases`, `/inventory/customers`, `/inventory/suppliers`, `/inventory/purchase-return`, `/invoicing` (dashboard, `sales-return`, `registers`, `tracking`), `/fbr` | The sales-invoice page POSTs to `/fbr/validate`/`submit`, so FBR cannot go first. Orders redirect into invoices, invoices into tracking |
| 4 | HR, HrRecords, Payroll | `/dashboard` (**exact, no slash**), `/attendance`, `/leaves`, `/ess`, `/mss`, `/timesheets`, `/reports` (HR's, not finance), `/workplace`, `/digital-files`, `/communications`, `/compensation`, `/pf`, `/auth/users`, `/auth/profile`, `/auth/members`, `/auth/change-password`, `/auth/account-settings` | 35 HR/payroll refusals redirect with a flash to `/dashboard`. The profile page posts to `auth.account_settings`. **Not** `/auth/login`, `/auth/logout` or `/auth/api/notifications`: every Python page's `app.js` POSTs notifications, and sign-in is phase 7. **The first .NET route that writes the session cookie:** after a password change, HR re-signs the Flask cookie with the new fingerprint (`Hr/Services/OwnSession.cs`), so the person stays signed in. Check on the preview that, after a change, Python pages still accept the cookie, and the cookie keeps `Secure` (it follows the forwarded scheme) |
| 5 | Fixed Assets | `/fixed-assets`, `/inventory/transfer-to-fa` | The transfer lives under `/inventory` in Flask but belongs to the fixed-asset module |
| 6 | Platform, SuperAdmin | `/settings`, `/portal`, `/dashboard/` (hub), `/hub`, `/superadmin` (except `/superadmin/login`), `/` (landing) | Portal and the super admin's "My companies" POST to `/settings/invitations/<id>/accept|decline`; all three move together |
| 7 | Sign-in (Platform) | `/auth/login`, `/auth/logout`, `/auth/api/notifications`, `/company/switch`, `/signup`, `/admin`, `/superadmin/login`, `/inventory/login`, `/inventory/logout` | Every route that **writes** the session cookie. See "Sign-in at the switch" |
| 8 | – | everything, `/static` | Python retired; DNS to Azure |

This follows the user's order (Accounting → Inventory → Invoicing → HR → Fixed Assets → the rest),
with two choices:
- Finance and Executive come with Accounting: read-only, and they read the ledger Accounting writes.
- FBR comes with Invoicing, because of the cross-app POST rule.

`/static` stays on Vercel until phase 8. The .NET package carries its own copy
(`src/Accountix.Web.Shared/static`, the same ten files as the Python app's `static/` on 29 Oct 2026).

## Each phase, step by step

**Before** (go/no-go):
1. The module's `docs/modules/<name>.md`:
   - every route ✅;
   - parity tests green (same journals, stock rows and numbers as Python for the same input);
   - speed budgets met;
   - its races, including one against the Python app, green.
2. `RouteTableTests` green: no two modules claim one URL.
3. Load test (DEPLOY.md) rerun with the module's writes in the mix, on a fresh copy of the speed-test
   database, and on the 4× company once seeded. Required: 0 server errors, 0 deadlocks, every check passing.
4. The release is on the production slot (healthy), and its staging-slot run used a Neon **branch**
   of production data:
   - this load tool at a small user count;
   - `data_checks.sql`;
   - Python's Books Integrity.
5. **Data checks on production**, saved with the time (they are read-only):
   ```bash
   psql "$DIRECT_DATABASE_URL" -v company_id=0 -f dotnet/deploy/checks/data_checks.sql   # every block: (0 rows)
   ```
   Also:
   - Executive → Books Integrity for each company: all pass;
   - row counts of the module's tables;
   - `SELECT version_num FROM alembic_version`.

**Switch:**
1. Add the module's routes to `vercel.json` (above the catch-all) and deploy to **preview**.
2. On the preview URL, sign in, then:
   - open each page of the module;
   - post one draft and delete it;
   - export one report;
   - check that the person in `/net/access` is right.
3. Promote to production at a quiet hour (the company clock is Asia/Karachi).

**Watch (first hour, then the first day):**
- In Log Analytics (DEPLOY.md, "Logs"):
  - 5xx = 0;
  - 400s on POST (antiforgery: a Python page posting to a moved URL, i.e. a missed cross-app write) = 0;
  - 409s and "Concurrent change refused" at a normal rate;
  - p95 per page within the speed budgets.
- In Sentry: no new issues.
- In Neon: connections and CPU.

**After (end of the first day, then weekly until phase 8):**
- `data_checks.sql`: 0 rows in every block.
- Books Integrity: all pass.
- **Parity sample:** five documents the .NET app wrote since the switch, opened in Python, should
  print and post identically. Use the Vercel deployment URL (`*.vercel.app`) after signing in there;
  no route sends it to .NET.
- Document numbers continue without gaps or duplicates across both apps (`voucher_series_ahead`,
  `document_numbers_unique`).

**Rollback** (any 5xx pattern, failed check, wrong figure):
1. Remove the module's routes and redeploy Vercel, or Instant Rollback to the previous deployment.
   Python serves the module again within a minute, on the same data.
2. Nothing to undo in the database: .NET wrote Python's rows.
3. If a figure is wrong, fix the data with the same care as a Python bug (Books Integrity names the
   check), and keep the module on Python until the parity test that would have caught it exists.

A .NET release with a bug in a module that is already live: swap the App Service slots back
(seconds), and keep the routes.

## Flash messages across the two apps

- About 59 Python routes flash and then redirect into another module; .NET's refusals do the same.
- Within one app, nothing changes.
- Across apps, the message appears on that app's next page instead:
  - Python's waits in the session cookie;
  - .NET's waits in its TempData cookie.
- The phase grouping keeps the common cases inside one app:
  - HR and payroll → `/dashboard` is phase 4;
  - orders → invoices → tracking is phase 3;
  - portal and settings is phase 6.
- What remains is minor and temporary (until phase 6): a refusal on a .NET module that sends the
  person to the Python hub.
- Not worth a cross-app flash bridge: the .NET app would have to rewrite the Flask cookie, and that
  is only done at phase 7.

## Sign-in at the switch

The .NET app signs people in on its own (`ax_session`, HMAC-SHA256 under `SECRET_KEY`, salt `accountix-session`,
over `user_sessions`); the Flask `session` cookie is neither read nor written any more. At the switch:

1. Route every page to .NET, including `/auth/*`, `/company/switch/*`, `/signup`, `/admin`, `/superadmin/login`.
2. **Everyone signs in once.** A browser still holding the Python app's `session` cookie is simply signed out (the
   cookie is ignored) and lands on `/auth/login`. Passwords keep working: `users.password_hash` stays in Werkzeug's
   format and .NET writes new ones the same way, so nothing is rehashed.
3. Optionally end the Python app's open rows at once (they would time out within 2 h anyway):
   `UPDATE user_sessions SET ended_at = now() at time zone 'utc', end_reason = 'cutover' WHERE ended_at IS NULL;`
4. Keep `SECRET_KEY` as it is: it signs `ax_session` and keys the password fingerprint. A new value signs
   everyone out once more.
5. Rollback to Python means a sign-in again there too (its cookie was never written by .NET).

## Retiring Python (phase 8)

1. Every prefix has been on .NET for a full month-end close without a rollback.
2. **Migrations.**
   - Done: the .NET app owns the schema (`src/Accountix.Migrations`). It runs at every start in
     Production (`Accountix:MigrateOnStart`) and as `dotnet Accountix.Web.dll migrate`; it adopts
     Neon at Alembic 0015 and applies the ported 0016–0018. Steps: DEPLOY.md, "Neon today → head".
   - New schema changes are .NET migrations (0019+), additive only.
3. **DNS.**
   1. Lower the record's TTL a day ahead.
   2. Add the domain to the App Service (managed certificate) or to Front Door.
   3. Switch the record.
   4. Set `FORWARDED_LIMIT=1` (or keep 2 with Front Door) and `FORWARDED_ALLOWED_HOSTS` accordingly.
   5. `/static` is now served by .NET from the copy in the package.
4. **Keep the Vercel project for 30 days**, deployable and with its environment, as the rollback.
   - Rollback: point DNS back.
   - Sessions survive both ways, since both apps read the same cookie and `user_sessions`.
   - Then delete it, and remove the `/net/*` diagnostics' proxy notes from this file.
5. Bridge-only code paths: removed 29 Oct (the Flask cookie is not read any more).

## Risks

| Risk | Mitigation |
|---|---|
| A Python page POSTs to a moved URL (400) | The table above was built from every `<form>` and `fetch()` in the templates and `static/js`. Watch POST 400s after each switch; roll back the routes if any appear |
| Vercel caches a .NET page | .NET pages are `private`, forms `no-store`; `x-vercel-enable-rewrite-caching: 0` on the rewritten routes; checked in phase 0 |
| The two apps lock differently and deadlock or double-post | Same advisory keys and lock order by design. One cross-app race test per module (Python + .NET at the same instant) before its phase |
| Connections exhausted | Both apps use Neon's pooler; .NET ≤ 20 per instance × ≤ 4 instances |
| Neon compute suspended → slow first request | `Timeout=30`; health check every minute keeps it awake; consider turning scale-to-zero off |
| Key ring missing or unreadable → every form fails | Migrations run before the warm-up; otherwise start-up stops with the reason; keys in Postgres (migration 0017); slots share them |
| Alembic revision deployed with Python breaks .NET | Additive revisions only during the move (expand, then contract after both apps stopped using the old shape); .NET maps only what it reads |
| A cookie re-signed by .NET (HR's password change, phase 4; sign-in, phase 7) is not accepted by Python or loses `Secure` | It is the same itsdangerous format, re-written whole (every key kept). Phase 0 checks the forwarded scheme; phase 4 checks a password change end to end on the preview |
| Wrong figures go unnoticed | `data_checks.sql` and Books Integrity before and after every phase, weekly until phase 8; parity sample |
