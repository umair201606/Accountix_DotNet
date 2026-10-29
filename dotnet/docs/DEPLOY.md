# Deploying the .NET app (Azure App Service, next to Neon)

Status, 29 Oct 2026: **ready to deploy, not deployed.** Nothing here has contacted Azure, Vercel or Neon.
The files are in `dotnet/deploy/`. Switching traffic over module by module is described in
[CUTOVER.md](CUTOVER.md).

```
Browser ──► erp.<domain> (Vercel, as today)
              ├─ Python pages (Flask functions)            ─┐
              └─ ported prefixes ─► rewrite ─► Azure App Service (Linux) ── .NET 10 host
                                                            │   2+ instances, staging slot
                                                            ▼
                                   Neon Postgres (pooled endpoint, same region), one database
```

| File | What it is |
|---|---|
| `deploy/Dockerfile` (+ `Dockerfile.dockerignore`) | Container image: build from the repository root; `/static` comes with the publish output |
| `deploy/azure/main.bicep` | App Service plan, web app, staging slot, Key Vault, role assignments, autoscale, Log Analytics |
| `deploy/azure/provision.sh` | The same with `az` CLI commands |
| `deploy/github/dotnet-deploy.yml` | GitHub Actions: build, test, publish, deploy to staging, health check, swap, swap back on failure. **Not in `.github/`, so nothing runs** |
| `deploy/checks/data_checks.sql` | Data checks for every cut-over step and every load test (no rows = pass) |
| `tests/Accountix.Load/` | Load test: many people in one company at once (see "Load test") |
| `src/Accountix.Migrations/` | The schema: baseline at Alembic 0018, the ported 0016–0018 for databases already in production, the runner (see "Migrations") |
| `Sql/0017_dotnet_data_protection.sql` (in it) | The table for the shared Data Protection key ring (first written as Alembic 0017) |
| `Sql/0018_report_indexes.sql` (in it) | 16 indexes for the big pages, costing history, HR queues, sign-in throttle and the bell; most pages 10–50 % faster on the 4× company |

## Region

Put the App Service in the Azure region where the Neon project runs, or the nearest one. Every page
makes up to 40 queries (the speed budget), so the distance to the database is multiplied by 40.
The Neon host name tells the region: `ep-…-pooler.<region>.<cloud>.neon.tech`.

| Neon region | Azure region |
|---|---|
| `azure-eastus2` / `aws-us-east-1` (N. Virginia) | East US 2 / East US |
| `aws-us-east-2` (Ohio) | East US 2 (nearest; there is no Azure region in Ohio) |
| `azure-westus3` / `aws-us-west-2` (Oregon) | West US 3 / West US 2 |
| `azure-gwc` / `aws-eu-central-1` (Frankfurt) | Germany West Central |
| `aws-ap-southeast-1` (Singapore) | Southeast Asia |

Vercel's Python functions should run in the same area (Vercel project → Settings → Functions → Region), as they do now.

## Plan: Linux, Premium v3 P0v3, two instances

- **Linux**: the app is cross-platform, and Linux is cheaper than Windows for the same size.
- **P0v3** (1 vCPU, 4 GB) is the smallest tier that has what the cut-over needs:
  - **deployment slots**: deploy to `staging`, warm it up, swap, and swap back to roll back;
  - **autoscale**;
  - **Always On**;
  - the newer hardware.

  Basic and Standard (B1/S1) run on older hardware, and Basic has no slots.
- **Two instances**: one can restart (platform updates, a swap) while the other answers. The shared
  key ring is what makes two instances possible (see "Data Protection").
- **Autoscale**: CPU above 70% for 5 minutes adds an instance, up to 4; below 30% for 15 minutes removes one.
- **What the load test measured** (below):
  - **Memory**: about 570 MB per process, and 406 MB peak with the runtime told it has one CPU on
    the 4× company. A 16-core machine let Server GC grow to 3 GB; the runtime sizes its heaps to the instance.
  - **CPU**: about 1 CPU-second per second at 39 requests a second. That rate came from 30 virtual
    users pausing only 0.5 to 2 s between clicks, roughly what several hundred people at normal pace would make.
  - **P0v3 ×2** therefore has room for the real company sizes.
  - **When to move to P1v3** (2 vCPU, 8 GB): sustained CPU above 60%, or heavy month-end reporting.

## Settings (App Settings, read as environment variables like the Python app on Vercel)

| Setting | Value | Why |
|---|---|---|
| `SECRET_KEY` | Key Vault reference; keep the value already in use | Signs the `ax_session` sign-in cookie and keys the password fingerprint; a new value signs everyone out |
| `DATABASE_URL` | Key Vault reference; Neon **pooled** URL, `sslmode=require` | Same form Vercel and Neon hand out |
| `ASPNETCORE_ENVIRONMENT` | `Production` | Turns on everything below |
| `DATA_PROTECTION_STORE` | `postgres` (default in Production) | Shared key ring, see below |
| `DATA_PROTECTION_CERT_PATH` / `_PASSWORD` / `DATA_PROTECTION_OLD_CERT_PATHS` | optional | Encrypt the keys at rest with a certificate |
| `DB_MAX_POOL_SIZE` | 20 | Connections per instance |
| `DB_CONNECT_TIMEOUT_S`, `DB_COMMAND_TIMEOUT_S`, `DB_KEEPALIVE_S`, `DB_IDLE_LIFETIME_S` | 30, 60, 30, 60 | See "Database" |
| `DB_LOCK_TIMEOUT_MS` | 15000 | Same as Python: a request waits at most 15 s for another user's rows |
| `FORWARDED_LIMIT` | 2 behind a Vercel rewrite or Front Door; 1 for App Service alone | How many proxies append `X-Forwarded-For` |
| `FORWARDED_ALLOWED_HOSTS` | `erp.<domain>,<app>.azurewebsites.net` | Which `X-Forwarded-Host` values are believed |
| `HSTS_MAX_AGE_DAYS` / `HSTS_INCLUDE_SUBDOMAINS` | 365 / false | `Strict-Transport-Security` on HTTPS answers |
| `RESPONSE_COMPRESSION` / `RESPONSE_COMPRESSION_LEVEL` | true / `optimal` | Brotli and gzip |
| `LOG_FORMAT` / `REQUEST_LOG` | `json` / true | One JSON object per line on stdout |
| `SENTRY_DSN`, `SENTRY_ENVIRONMENT`, `SENTRY_RELEASE`, `SENTRY_TRACES_SAMPLE_RATE` | optional | Error reports, like Python's |
| `SESSION_IDLE_MINUTES` / `SESSION_MAX_HOURS` | unset (120 / 12) | Must match Python's if ever set |
| `WEBSITE_RUN_FROM_PACKAGE` | 1 | Zip deploy runs read-only from the package |
| `WEBSITE_SWAP_WARMUP_PING_PATH` / `_STATUSES` | `/net/health` / 200 | A swap waits for the new instances |
| `WEBSITE_TIME_ZONE` | UTC | The database holds naive UTC; company clocks are per company |
| `UPLOAD_FOLDER` | unset | Leave unset. Uploaded files live in the database (`stored_files`). The setting only lets HR read files from a pre-October dev disk folder (`<folder>/<company>/<key>`). The Python app's Vercel folder was `/tmp/uploads`, wiped on every cold start, so live data has nothing on disk |

### Secrets: Key Vault references

The app and its staging slot each have a system-assigned identity with **Key Vault Secrets User** on
the vault. App Settings refer to secrets as `@Microsoft.KeyVault(VaultName=<kv>;SecretName=SECRET-KEY)`;
App Service resolves them into environment variables at start.

- Secrets: `SECRET-KEY`, `DATABASE-URL` and `SENTRY-DSN` (may be empty). A person sets them; no file holds them.
- **Rotating `SECRET_KEY`** signs everyone out of both apps. Change it on Vercel and in the vault
  together, then restart the web app.
- A missing or short `SECRET_KEY` stops the app at start-up with that message.

## Database: Npgsql settings for Neon (`Infrastructure/DatabaseTuning.cs`)

Outside Development these values apply unless the connection string names them; an App Setting
overrides each one. Tests: `DatabaseTuningTests`.

- **Pooled endpoint** (`-pooler` in the host). It is the same one the Python app uses, so both apps
  together can never exhaust the compute's connections. PgBouncer runs in transaction mode, and
  the app keeps nothing that needs a session:
  - lock timeouts are `SET LOCAL`;
  - advisory locks are `pg_advisory_xact_lock`;
  - no prepared statements;
  - so `No Reset On Close=true` is set on that endpoint.
  - Use the direct endpoint only for `pg_dump`, `psql` and migrations.
- **`Maximum Pool Size=20` per instance.** With 4 instances at most, that is 80 client connections
  to the pooler. Only open transactions hold real server connections.
- **`Timeout=30`**: a suspended Neon compute takes a few seconds to wake. In production, either turn
  scale-to-zero off or accept a slow first request after a quiet spell. The health check every
  minute keeps the compute awake anyway.
- **`Command Timeout=60`**: year reports and exports run long statements. Lock waits stop much
  earlier (`DB_LOCK_TIMEOUT_MS`).
- **`Keepalive=30`, `Connection Idle Lifetime=60`**: Azure drops outbound TCP connections idle for
  4 minutes (SNAT), and Neon drops them when the compute suspends. Closing idle pooled connections
  after a minute means a dead one is never handed out.
- **`SSL Mode=Require`** for any remote host (Neon also offers `verify-full`); `Application Name=accountix-dotnet`.
- **No automatic retries.** `EnableRetryOnFailure` is not used, and must not be:
  - the modules open their own transactions (66 `BeginTransaction` calls in 22 files: lock, change,
    post, commit), and EF's retrying strategy refuses user-opened transactions;
  - replaying a write whose commit reply was lost could post it twice.

  Conflicts are answered instead: a deadlock, lock timeout, unique or foreign-key failure rolls
  back and becomes a 409 or a flash (`ConflictMessages`).
- **Neon IP allow list** (if used): add the web app's `outboundIpAddresses` (a Bicep output).

## Data Protection: one key ring for every instance

ASP.NET protects the antiforgery tokens (every form and `fetch()` POST) and the flash (TempData)
cookie with Data Protection keys. Each instance would otherwise make its own keys, and then:
- a form rendered by instance A and posted to instance B is refused (400);
- a flash set before a redirect is lost on another instance;
- every swap or restart breaks open forms.

**Decision: keep the keys in Postgres** (`dotnet_data_protection_keys`, migration **0017**), not Blob Storage.
- **Pros:**
  - Every instance and slot already reaches the database; there is nothing new to create, secure or pay for.
  - The keys follow the database to every Neon branch and copy, so a staging slot or a restored
    copy reads tokens issued against it.
  - It is the store whose availability already defines the app's: no database, no app.
  - The phased move is "one database for both apps", and this keeps it so.
- **Cons:**
  - A schema change (done: 0017; now in `src/Accountix.Migrations`).
  - Whoever can read the database can read unencrypted keys. They already hold everything those
    keys protect (the data itself), and `SECRET_KEY` is the more powerful secret. The optional
    certificate below closes even that.
- **Blob Storage plus Key Vault** (Microsoft's other documented pattern) would add a storage
  account, a managed-identity grant and a second failure point, for no gain here.

How it works (`Infrastructure/DataProtectionStore.cs`):
- `PostgresXmlRepository` keeps one row per key, `INSERT … ON CONFLICT (friendly_name) DO NOTHING`.
- The application name is fixed as `accountix` (the same on every instance and slot), and the store
  has its own two-connection pool.
- **Start-up warm-up**:
  - It loads the ring, or creates the first key, under `pg_advisory_xact_lock(815204118)`, so
    instances starting together create one key, not one each. The lock is transaction-scoped, so it
    works through the pooler.
  - The migrations run before it (`Accountix:MigrateOnStart`, see "Migrations"), so the table is
    there. With them switched off, a missing table stops the start with the fix in words
    ("`dotnet Accountix.Web.dll migrate`").
- ASP.NET rotates keys every 90 days and keeps old ones for reading. Keys are created ahead of
  activation, so every instance sees a new key before it is used.
- **Encryption at rest (optional):**
  - Upload a certificate to Key Vault and load it with `WEBSITE_LOAD_CERTIFICATES=<thumbprint>`;
    App Service puts it at `/var/ssl/private/<thumbprint>.p12`.
  - Set `DATA_PROTECTION_CERT_PATH` to that file.
  - On rotation, keep the old file in `DATA_PROTECTION_OLD_CERT_PATHS`.
  - Without a certificate, the log warns "No XML encryptor configured" at the first key.
- **Tests** (`SharedKeyRingTests`, two in-process production instances on one database):
  - a token rendered by A is accepted by B, and a forged token is refused (400);
  - a flash set by A is shown by B;
  - a database without the table stops the start.
- **Python tests**: `tests/e2e/test_storage_and_money.py`, with the head assertions moved to 0017 and
  a new test showing the revision is idempotent and the key names unique. Results: 12 passed and
  2 skipped (Postgres-only) on SQLite; 14 passed on Postgres.
- With the key ring shared, **ARR affinity is off**: any instance can answer any request.

## HTTP edge (`Infrastructure/Production.cs`, `AccountixHost.cs`)

- **Forwarded headers**: scheme, host and client address come from `X-Forwarded-*`:
  - `FORWARDED_LIMIT` proxies are trusted;
  - only `FORWARDED_ALLOWED_HOSTS` are believed as `X-Forwarded-Host`; a spoofed host is ignored;
  - this matters because the "back to the referring page" refusals compare the referrer with the public host.
  - `/net/request` (signed in) shows what the app sees; it is phase 0 of the cut-over.
  - With `FORWARDED_LIMIT=2` someone calling `*.azurewebsites.net` directly can choose the logged
    client address, but nothing else. Access restrictions to Vercel's or Front Door's addresses would
    stop that; Front Door makes this exact with the `X-Azure-FDID` header.
- **HTTPS**:
  - App Service "HTTPS Only" redirects at the platform's front end.
  - The app adds `Strict-Transport-Security: max-age=31536000` to HTTPS answers. There is no
    `includeSubDomains` and no preload, since the domain is shared with Python and Vercel already
    enforces HTTPS.
  - No HTTPS redirect in the app: it would loop behind a proxy that does not forward the scheme.
- **Compression**:
  - Brotli and gzip for HTML, JSON, CSS, JS, SVG and CSV, also over HTTPS. Exports (xlsx/pdf) are
    already compressed, so they are left alone.
  - BREACH: the antiforgery token is re-encrypted on every render, and the session cookie is never
    in a body, so there is no stable secret in a compressible page to guess at.
  - Level `optimal`, measured on the load copy (Brotli; gzip is close):

    | Page | Plain | Brotli `optimal` | Brotli `fastest` |
    |---|---:|---:|---:|
    | General ledger, all accounts, busiest month | 2,145 KB | 135 KB | – |
    | Voucher register, busiest month | 422 KB | 33 KB | 50 KB |
    | Sales-order list | 455 KB | 15 KB | 35 KB |
    | Chart of accounts | 292 KB | 20 KB | 33 KB |
    | Payment & receipt feed | 255 KB | 28 KB | 45 KB |
    | `/static/css/app.css` | 29 KB | 6.5 KB | 8.5 KB |

    Compressing the 455 KB page costs 3–5 ms of CPU (measured on the page bodies).
    `RESPONSE_COMPRESSION_LEVEL=fastest` trades that for size. Inside the middleware, which writes in
    chunks, Brotli `fastest` was sometimes larger than gzip.
- **Caching**:
  - The files under `/static` carry no version in their names, so they are served
    `public, no-cache` with ETag and Last-Modified (a 304 when unchanged; a deployment is seen at once).
  - `?v=` URLs are served `immutable` for a year; fonts and images for a day.
  - Pages and JSON that set no caching get `private, no-cache`, so no CDN in front can keep one
    person's ledger for the next. Pages with a form token already say `no-store`.
- **Headers** as Python's (`X-Frame-Options`, `nosniff`, `Referrer-Policy`, `frame-ancestors`).
- **Routes: Flask's strict slashes.** `/dashboard` (HR) and `/dashboard/` (hub) are two pages in
  Flask; ASP.NET ignores the slash, and the combined host answered 500 for both.
  `TrailingSlashPolicy` picks by the request's slash where two routes differ only by it, and
  changes nothing elsewhere. `RouteTableTests` fails if two actions claim one URL and method; it
  found `/executive/*` defined by both Finance and Executive (since resolved by their owners).

## Health, logs, errors

- **`/net/health`**: 200 when the database answers, 503 otherwise. It is the App Service health check
  (an unhealthy instance is taken out of rotation) and the swap warm-up path.
- **Logs**: JSON on stdout, collected by App Service as `AppServiceConsoleLogs` and sent to Log
  Analytics (Bicep). Each request writes one line:
  `HTTP {Method} {Path} {Status} {ElapsedMs} ms company={CompanyId} user={UserId}`.
  - There is no query string (it can hold names and search terms), no names and no emails.
  - Static files and the health check log at Debug.
  - Example queries:
    ```kusto
    AppServiceConsoleLogs | extend j = parse_json(ResultDescription)
    | where j.Category == "Accountix.Web.Infrastructure.RequestLogMiddleware"
    | summarize n = count(), p95 = percentile(todouble(j.State.ElapsedMs), 95), errors = countif(toint(j.State.Status) >= 500)
        by tostring(j.State.Path) | order by n desc
    // conflicts answered: j.Category == "Accountix.Web.Infrastructure.ErrorAnswersMiddleware" and j.Message startswith "Concurrent change refused"
    ```
- **Sentry**: only when `SENTRY_DSN` is set, and with the same rules as Python:
  - `SendDefaultPii=false`;
  - no request bodies;
  - cookies, query string and user cleared in `BeforeSend`;
  - breadcrumbs from warnings up.

  Test: `Sentry_is_installed_only_with_a_dsn_and_never_sends_bodies_or_people`.

## Build and deploy

**Zip deploy is the default** (what the workflow does). `dotnet publish` for Linux x64 with
ReadyToRun (new instances answer at once, with no JIT warm-up). The publish output carries
`static/` at its root (the build copies `src/Accountix.Web.Shared/static` there). App Service runs
`dotnet Accountix.Web.dll` from `/home/site/wwwroot`; the host serves `/static` from `static/` next to it.

The layout was checked on this machine:
- portable publish (the Linux ReadyToRun image cannot load on macOS);
- `/static` served, with revalidating headers;
- an unsigned request redirected to Python's `/auth/login`;
- the general ledger compressed from 2.1 MB to 135 KB.

**The container** (`deploy/Dockerfile`) is the same build. It is for a container registry or any
other host: it runs as the image's unprivileged user and listens on 8080 (`WEBSITES_PORT=8080` on
App Service).

**Workflow** (`deploy/github/dotnet-deploy.yml`, move it to `.github/workflows/` to switch it on):
1. Build and test the solution (tests that need the reference database skip on CI).
2. Publish and upload `app.zip`.
3. Under the GitHub environment `production` (reviewers required), sign in to Azure with OIDC (no
   stored password).
4. Deploy to `staging` and wait for `/net/health`.
5. Swap, then health-check production; **swap back** if it fails.

**Rollback** of a .NET release: swap the slots back (seconds). Rollback of a module's traffic: [CUTOVER.md](CUTOVER.md).

## Migrations (`src/Accountix.Migrations`; the schema is owned by .NET)

Alembic and the Python app are retired. The .NET app creates and changes the schema itself, and
there is no "deploy Python first" step any more.

**What is in it**
- **Baseline**: the whole schema at Alembic head **0018** (`Sql/baseline_schema.sql`, pg_dump of a
  database the Python app built: create_all + every revision), plus the rows a new installation
  needs (`Sql/baseline_seed.sql`: the 3 roles, their 54 permissions, the global limits row, and
  `alembic_version` = 0018).
- **Forward migrations** (`MigrationCatalog.Forward`), for databases already in production:
  - **0016** sessions table, module switches written out per membership, console companies handed
    to their admin, emails lower-cased: `AccessPerCompany.cs`, set-based SQL;
  - **0017** key ring table and **0018** indexes: `Sql/*.sql`.
  - New changes are appended here as 0019, 0020… Each must be additive and harmless when re-run.
    To add one: write `Sql/0019_<what>.sql` (guarded: `IF NOT EXISTS`, `to_regclass`, only rows
    still in the old shape), add `new SqlMigration("0019_<what>", "…")` at the end of `Forward`,
    then re-capture the expected schema from a database the migrator built fresh:
    `dotnet Accountix.Web.dll migrate && dotnet Accountix.Web.dll migrate --describe > src/Accountix.Migrations/Schema/expected_schema.json`.
    Never edit a released migration (the runner warns when a recorded checksum changes). The
    golden comparisons with Python stop at 0018 (`StopAfter`), so they keep passing.
- **The runner** (`Migrator.cs`):
  - one transaction holding `pg_advisory_xact_lock(815204117)` (Python's key) with
    `SET LOCAL lock_timeout = 0`: instances starting together queue, and the later ones find
    nothing to do. It works through the pooled endpoint;
  - any failure rolls everything back;
  - `schema_migrations` records each version, how it got there (`baseline`, `alembic`, `dotnet`),
    a checksum and the time;
  - **adopts** a database the Python app built: everything up to its `alembic_version` counts as
    applied, written down together with the first change. A database already at 0018 is left
    completely untouched;
  - **refuses** a database below 0015 ("upgrade it with the Python app from the backup first"),
    an unknown Alembic revision, or tables with no record at all;
  - keeps `alembic_version` in step for 0016–0018, so the Python app from the backup would still
    read the database correctly.
- **First super admin**: an empty database has no account. Set `Accountix:FirstAdmin:Email` and
  `Accountix:FirstAdmin:Password` (App Settings `Accountix__FirstAdmin__Email` / `__Password`;
  optional `__Name`). The account is created only while the database has no super admin at all,
  with the password policy applied. There is no built-in account and no default password. Remove
  the settings once the account exists.

**When it runs**
- **At every start in Production** (`Accountix:MigrateOnStart`, default true in Production, false
  elsewhere), before the key ring warm-up and before any request, as the Python app migrated at
  every boot. A refusal stops the start with its reason in the log.
- **By hand**, with the same configuration as the app:

  ```bash
  dotnet Accountix.Web.dll migrate              # apply (and create the first super admin when configured)
  dotnet Accountix.Web.dll migrate --status     # what would run; changes nothing
  dotnet Accountix.Web.dll migrate --check      # compare the schema with what this build expects (exit 2 if not)
  dotnet Accountix.Web.dll migrate --describe   # the schema as JSON (re-capture Schema/expected_schema.json)
  ```

  It reads `MIGRATIONS_DATABASE_URL` (use Neon's **direct** endpoint here), else `DATABASE_URL`,
  else `ConnectionStrings:Accountix`.
- With the staging slot on the production database, the slot's start migrates **before the swap**.
  That is why every migration must be additive (expand now, contract in a later release): the
  release still on production runs on the new schema until the swap. A swap back (rollback) starts
  the older build on the newer schema; it logs the versions it does not know and carries on.

**Neon today → head (once, with the first .NET deploy)**
1. `psql "$DIRECT_DATABASE_URL" -c "SELECT version_num FROM alembic_version"`. Expected
   `0015_membership_employment` (unverified since 20 Oct). Below 0015: run the backup's Python
   `tools/db_migrate.py upgrade` once, which goes straight to 0018.
2. Make a Neon **branch** of production (pg_dump 16 cannot dump Neon's Postgres 18), then on the
   branch: `MIGRATIONS_DATABASE_URL=<branch direct URL> dotnet Accountix.Web.dll migrate --status`
   (expected: `0016_access_per_company, 0017_dotnet_data_protection, 0018_report_indexes`),
   `migrate`, then `migrate --check` ("The schema is the one this build expects.").
   Read every `Warning:` line: 0016 names any two accounts whose addresses differ only in letter
   case; those stay as they are, and the unique index on lower(email) is not created until they are fixed.
3. Run `data_checks.sql` and Books Integrity on the branch, then repeat step 2 on production (direct
   endpoint) at a quiet hour. `MigrateOnStart` would do the same at the first start; running it by
   hand first means the output is read before any instance starts.
4. Afterwards: `alembic_version` = `0018_report_indexes`, `schema_migrations` has 18 rows
   (0001–0015 `alembic`, 0016–0018 `dotnet`).
5. 0016 starts server-side sessions: everyone signs in once more after it.
6. Retire the Vercel Python deployment at the same time. Its 20 Oct code knows Alembic only up to
   0015: on Neon at 0018 its boot logs "Can't locate revision" and carries on, without migrating.

**Tests** (`tests/Accountix.Migrations.Tests`; 35 tests, 4 of them need the Python app and skip
without it):
- a fresh database equals a Python-built 0018 one (tables, columns, types, defaults, nullability,
  order, constraints, indexes, sequences: `Schema/describe_schema.sql`, compared with
  `Schema/expected_schema.json`);
- the seed rows are the Python app's;
- a database the 0015 Python app built (`Fixtures/db_0015.sql.gz`), upgraded here, equals the same
  database upgraded by Python's Alembic: the schema, and every row of every table by digest
  (`Fixtures/upgrade_0015.json`);
- runs twice are no-ops; two instances at once (the second waits on the advisory lock, then has
  nothing to do); below 0015 refused; a failure rolls everything back; the host migrates before
  the key ring at start; the CLI.
- The golden files were captured once with `tools/make_migration_fixtures.py` while Python
  existed, from the 20 Oct code (Neon's) and today's. The tests need only a Postgres server
  (`ACCOUNTIX_MIGRATIONS_PG`, or the one in `tools/reference.json`).

**Building while modules are mid-edit**: `src/Accountix.Web` takes `-p:AccountixModules=Accounting+Finance+…`
(default `all`). `tests/Accountix.Load/build-host.sh [Release|Debug] [project]`:
- builds every module that compiles right now;
- builds the host (or `tests/Accountix.Tests`) with those;
- deletes stale module DLLs, which the host would otherwise load from disk.

## Maintenance commands (`src/Accountix.Admin`, `Hosting/AdminHost.cs`)

The live-data tools that were Python scripts under `tools/` are commands of the app. They run on the
database the app is configured with (`DATABASE_URL`, or `ConnectionStrings__Accountix`), in the App
Service SSH console or on any machine with the publish output and the .NET 10 runtime. `SECRET_KEY` is
not needed. **Every command is a dry run unless `--apply`**: it does the work in a transaction, prints
what it found and what Books Integrity says afterwards, and rolls back. Company ids are bare numbers;
none means every company. stdout is the report, logs go to stderr, and the exit code is 0 for done,
1 for refused or aborted (nothing written), and 2 for a usage error.

```bash
cd /home/site/wwwroot                          # or the publish folder; locally: dotnet run --project src/Accountix.Web -- admin ...
dotnet Accountix.Web.dll admin                 # lists the commands
dotnet Accountix.Web.dll admin books-integrity [company id ...]                 # read-only: every check, each company
dotnet Accountix.Web.dll admin merge-legacy-accounts [--apply] [company id ...]   # was tools/merge_parked_legacy_accounts.py
dotnet Accountix.Web.dll admin repair-opening-stock [--apply] [company id ...]    # was tools/repair_opening_stock.py
dotnet Accountix.Web.dll admin remove-faulty-demo-records [--apply]              # was tools/remove_faulty_demo_records.py (company 1 only)
dotnet Accountix.Web.dll admin rebuild-gl-summary [--apply] [company id ...]      # was tools/rebuild_gl_summary.py (it had no dry run)
dotnet Accountix.Web.dll admin add-default-leave-types [--apply] [company id ...] # F6: Casual / Sick / Annual Leave where a company has none
```

| Command | What it does | Refuses / aborts when |
|---|---|---|
| `merge-legacy-accounts` | Merges the duplicate accounts the old `get_or_create_account` parked for 5710 / 2125 / 5123–5126 into the lowest id: repoints every column holding a `chart_of_accounts` id (database FKs, EF model FKs, integer `*account_id` columns), drops a row whose key the kept account already holds, deletes the duplicates, rebuilds `gl_daily_balances`. One transaction for all companies | Books Integrity fails before (fix that first), or after; the posted totals of a merged name change |
| `repair-opening-stock` | Posts Dr stock / Cr Opening Balance Equity, on the row's own date, for each OPENING stock row with no journal (`StockEntry.JournalOpeningRowAsync`, created_by 1). One transaction per company | A closed period covers a row's date (reopen it, repair, close again); a check that passed before would fail after |
| `remove-faulty-demo-records` | Deletes the three July-2026 demo records of Default Company (BRV-00001, DN-202607-0001, PINV-202607-0001), recomputes their products' on-hand and rebuilds company 1's report totals. Raw SQL, as Python's | Any record is not exactly as found on 15 Oct (each check named in the `ABORT:` line); a second run aborts (`BRV-00001 journal: []`) |
| `rebuild-gl-summary` | Rebuilds `gl_daily_balances` from the posted ledger; shows "Reporting table matches the ledger" before and after for each company | An unknown company id |
| `add-default-leave-types` | Gives each company with **no** leave types the three of `hr_app/seed.py` (CL 12 days; SL 10, carry 5; AL 20, carry 10; paid, approval). A company with leave types of its own is left alone | – |
| `books-integrity` | Executive → Books Integrity for each company, read-only (exit 1 if a check fails) | – |

**Still to run on Neon** (from the Python sessions): `remove-faulty-demo-records`, `repair-opening-stock`,
`merge-legacy-accounts` and `add-default-leave-types` (existing companies have no leave types). Suggested order:
`books-integrity` → each command without `--apply` → read its report → the same with `--apply` → `books-integrity`.

Tests: `tests/Accountix.Admin.Tests` (17). Each test works in its own throwaway database (`st_admin_*`, a copy
of `st_admin_tpl`, which is copied from the reference database once). While the Python app existed, each of the
four ported commands was run beside its Python tool on two clones of the same input, and the two databases
compared table by table, row by row (ids included; not timestamps, sign-in sessions, and two `audit_logs` columns
excluded): identical. Those comparison tests were removed with the Python app on 29 Oct 2026; the 13 behaviour tests remain.

## Load test (`tests/Accountix.Load`)

Many people in **one** company at once, against the real host on Kestrel (Production mode, all
production settings, 20-connection pool). The database is a writable copy of the full-year
speed-test database: Samba Private Limited, 2,760 sales invoices, 3,118 vouchers, 26,833 journal lines.

```bash
S=<scratch>; PG=<pgserver bin>; H=<socket dir>
$PG/pg_dump -h $H -U postgres -Fc -f $S/perf.dump accountix_ref_perf
$PG/psql -h $H -U postgres -c "CREATE DATABASE accountix_ref_load" && $PG/pg_restore -h $H -U postgres -d accountix_ref_load --no-owner $S/perf.dump
ART=<artifacts> dotnet/tests/Accountix.Load/build-host.sh Release && dotnet build dotnet/tests/Accountix.Load -c Release --artifacts-path <artifacts>
DATABASE_URL="postgresql://postgres:@/accountix_ref_load?host=$H" dotnet <artifacts>/bin/Accountix.Web/release/Accountix.Web.dll migrate   # → head (the host below would do it at start too)
dotnet <artifacts>/bin/Accountix.Load/release/Accountix.Load.dll seed-users --db "Host=$H;Database=accountix_ref_load;Username=postgres" \
  --company 2 --count 29 --admins 9                                                            # 29 more members (ids as JSON)
ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=http://127.0.0.1:5321 SECRET_KEY=... DB_MAX_POOL_SIZE=20 \
  ConnectionStrings__Accountix="Host=$H;Database=accountix_ref_load;Username=postgres" dotnet <artifacts>/bin/Accountix.Web/release/Accountix.Web.dll
dotnet <artifacts>/bin/Accountix.Load/release/Accountix.Load.dll run --base http://127.0.0.1:5321 \
  --db "Host=$H;Database=accountix_ref_load;Username=postgres;Maximum Pool Size=4" --secret <ref key> \
  --company 2 --users 1,6-34 --vus 30 --seconds 180 --hot 4 --out $S/run
dotnet <artifacts>/bin/Accountix.Load/release/Accountix.Load.dll check --db "Host=$H;Database=accountix_ref_load;Username=postgres" --company 2   # checks only
```

No Python is needed: `seed-users` writes the people `seed_load_users.py` used to (accounts `load01@load.test` …,
password `Load-pass-123!`, first-company memberships made by Platform's `Memberships`, so the account's module
switches are copied onto them as Python's insert hook did; re-running reuses them), and refuses the speed-test
database itself.

What each virtual user does:
- It is a separate browser (its own session, cookies and connections), signed in as one of 30
  members: 10 admins and 20 managers.
- Between pauses of 0.5–2 s:
  - **70% reads**: registers, lists, statements, ledger, executive pages, books integrity;
  - **10%**: a new cash payment, saved and approved;
  - **5%**: a new journal saved as a draft, then approved;
  - **15% hot vouchers**: four drafts shared by everyone, which people approve, unapprove and edit at
    the same moment.
- POSTs carry the antiforgery token from the page, and redirects are followed like a browser. The
  next page's flash tells success, refusal or conflict.
- Postings are dated in the busiest month, so they hit its busiest `gl_daily_balances` rows.

The run also records:
- the database's deadlock counter;
- how many connections the host held;
- how many sessions waited on a lock.

Afterwards it runs `deploy/checks/data_checks.sql` (before and after the run) and, after the run, Books Integrity
on the same database: Executive's `BooksIntegrity`, the 11 checks of the Executive page (the port of
`shared/integrity.py`, whose findings matched Python's word for word; it replaced the run of the Python app's
own check, `--python`, which is gone).

**Results, 29 Oct 2026** (this machine, shared with fifteen engineers' builds, so the times are upper bounds).
Runs 1–2 used the full-year company: 2,760 sales invoices, 26,833 journal lines.
Runs 3–4 used the **4× company** (`accountix_ref_perf_large` copied to `accountix_ref_load_large`):
10,405 sales invoices, about 90k journal lines, busiest month 2026-06 with 1,108 sales invoices.

| | Run 1: 12–15 modules, no Finance statements yet | Run 2: all 15 modules | Run 3: all, 4× company | Run 4: all, 4× company, runtime told 1 CPU |
|---|---|---|---|---|
| Duration, virtual users, people | 182 s, 30, 30 | 182 s, 30, 30 | 182 s, 30, 30 | 122 s, 30, 30 |
| Requests | 7,100 (39.1/s) | 7,006 (38.6/s) | 6,991 (38.4/s) | 4,748 (39.0/s) |
| Server errors (5xx or no answer) | **0** | **0** | **0** | **0** |
| Antiforgery refusals (400) | **0** | **0** | **0** | **0** |
| 409 answers / conflicts shown | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 |
| Deadlocks (`pg_stat_database`) | **0** | **0** | **0** | **0** |
| Most connections held by the host (pool 20) | 7 | 10 | 10 | 11 |
| Most sessions waiting on a lock at once | 1 | 1 | 1 | 0 |
| Vouchers made: CPV / JV, numbers gapless and unique | 415 / 216 | 409 / 214 | 408 / 213 | 292 / 151 |
| Data checks (9) + Books Integrity (11; Python's, as these runs used) | all pass | all pass | all pass | all pass |
| Host CPU | 0.66 CPU-s per s | 1.02 CPU-s per s | 0.88 CPU-s per s | 0.98 CPU-s per s |
| Host memory | 536 MB | 568 MB | 3.0 GB (see below) | **406 MB peak** |

On memory: this machine has 16 cores, so Server GC kept 16 heaps and grew to 3 GB on the 4× company.
Told it has one CPU (`DOTNET_PROCESSOR_COUNT=1`, as on a P0v3), the same load peaked at 406 MB. The
runtime sizes its heaps from the instance it is on, so 4 GB (P0v3) leaves ample room.

Per page, ms (run 2: full year / run 3: 4× company):

| Page | p50 | p95 | p99 | p95, 4× | p99, 4× |
|---|---:|---:|---:|---:|---:|
| POST new cash payment (save & approve) | 25 | 62 | 123 | 63 | 122 |
| POST approve / unapprove / edit (hot, same 4 vouchers) | 15 | 38 | 62 | 40 | 102 |
| Voucher form | 19 | 36 | 59 | 41 | 72 |
| Voucher register, busiest month | 61 | 145 | 181 | 100 | 184 |
| Sales invoice list, busiest month | 14 | 31 | 47 | 34 | 66 |
| Sales register, busiest month | 10 | 27 | 44 | 20 | 76 |
| General ledger, all accounts, busiest month (1.9 MB / 3.7 MB of HTML) | 97 | 186 | 266 | 302 | 581 |
| Trial balance, year | 24 | 46 | 75 | 48 | 79 |
| Profit & loss, year | 21 | 42 | 82 | 48 | 128 |
| Balance sheet | 20 | 41 | 51 | 63 | 199 |
| Cash flow statement, year | 34 | 53 | 100 | 85 | 179 |
| Executive dashboard | 35 | 63 | 96 | 74 | 153 |
| Payment & receipt feed | 49 | 114 | 198 | 141 | 186 |
| Sales tracking | 60 | 143 | 167 | 275 | 481 |
| Books integrity (run all checks) | 182 | 373 | 493 | 1,264 | 1,293 |

Under 30 users at once on the 4× company, every page stays within the single-user budgets
(month ≤ 800 ms; year and ledger ≤ 1,500 ms) at p95. At p99 the slowest are:
- the month general ledger, 581 ms;
- sales tracking, 481 ms;
- Books Integrity, about 1.3 s: an on-demand check over the whole ledger.

What the runs show:
- **Hot vouchers**: of 627 simultaneous approve, unapprove and edit requests on the same four
  vouchers, 316 succeeded and 311 got the Python app's own refusals ("Already approved.",
  "Voucher is not approved.", "…approved and cannot be edited"). The row locks serialise them:
  - no voucher was posted twice;
  - no lock wait timed out;
  - no deadlock;
  - `gl_daily_balances` still equals the journal lines to the cent.
- **New vouchers**: 1,244 from 30 people drew gapless, unique numbers from the shared series.
- **Not yet in the load**: invoice writes (sales and purchase invoices, with stock costing). Add
  them once those modules have their parity notes, and rerun before Invoicing's cut-over (see
  CUTOVER.md, phase 3).
- **Azure numbers**: App Service to Neon in one region adds roughly 1 ms a query (here it was a unix
  socket), so 40 queries add up to about 40 ms on the heaviest pages. Re-measure on the staging slot
  with this tool pointed at its URL and a Neon branch.

## Open items

- `main.bicep` has not been compiled here: no Azure tooling on this machine, and running a
  downloaded Bicep CLI was refused. `az bicep build -f dotnet/deploy/azure/main.bicep` before use.
  The workflow YAML parses.
- First deploy: Neon goes from Alembic 0015 to head through the .NET migrator ("Migrations",
  "Neon today → head"). The migrator has been proven on Postgres 16 here; Neon runs 18, so run it on
  a Neon branch first.
- Pick the region from the Neon host name. Create the Entra app and federated credential for the
  workflow, and the Key Vault secrets.
