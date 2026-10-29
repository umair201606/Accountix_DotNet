# Accountix on .NET

The phased move of Accountix from Python (Flask) to .NET 10. Both apps run
on **the same Postgres database** and, in production, behind one domain. Each
module moves over once it is ready, while the Python app keeps serving the rest.

- **Schema:** owned by this app, `src/Accountix.Migrations`: a baseline at
  Alembic head 0018 (generated from a database the Python app built), the
  ported 0016–0018 for databases already in production, and new changes as
  0019+. It runs at every start in Production and as
  `dotnet Accountix.Web.dll migrate`. See docs/DEPLOY.md, "Migrations".
- **Sign-in:** the app's own `ax_session` cookie (signed with `SECRET_KEY`)
  over the `user_sessions` table; passwords stay in the Werkzeug hash format
  every account already has. See docs/modules/platform.md, "Sign-in".
- **Order:** foundation (done) → access fixes in both apps → Accounting →
  Inventory → Invoicing → HR & Payroll → Fixed Assets → Executive, FBR, Super admin.

See [docs/ACCESS_REVIEW.md](docs/ACCESS_REVIEW.md) for the review of
companies, users and access, and the recommended design.

## Status and documents

- `docs/PROGRESS.md`: every module's status, the speed sweep on a 4× company, the many-users load test.
- `docs/SPEED_AND_CONCURRENCY.md`: speed budgets and many-users rules every module follows.
- `docs/PORTING_GUIDE.md`, `docs/modules/*.md`: how modules were ported; per-module routes, tests, speed tables.
- `docs/DEPLOY.md`, `docs/CUTOVER.md`, `deploy/`: Azure App Service set-up and the module-by-module switch from Python.
- `docs/ACCESS_REVIEW.md`: the companies / users / access review and its fixes.

## Layout

```
src/Accountix.Core/     entities, EF Core context + company isolation, access rules, password hashing
src/Accountix.Web/      ASP.NET Core MVC host; sign-in, company context, [Module]/[Right] checks live in Web.Shared
src/Accountix.Migrations/  the schema: baseline (Alembic 0018), ported 0016-0018, the runner; `migrate` CLI
tests/Accountix.Migrations.Tests/  fresh = Python-built schema, 0015 upgrade = Python's, concurrency (golden files)
tests/Accountix.Tests/  unit tests + integration tests against the reference database
tests/fixtures/         reference.sql.gz (the reference database, plain INSERTs) + reference.facts.json
tools/Accountix.RefDb/  builds the reference database and its per-module copies from the fixture, writes tools/reference.json
```

## Run the tests

```bash
cd dotnet
# 1. Any Postgres 16+ (local, Docker postgres:16, ...). Build accountix_ref and one
#    copy per test project (accountix_ref_<module>) from tests/fixtures/reference.sql.gz,
#    and write tools/reference.json. ~10 s; drops and re-creates those databases.
ACCOUNTIX_TEST_PG="Host=localhost;Username=postgres;Password=..." \
  dotnet run --project tools/Accountix.RefDb
#    One copy again:            ... dotnet run --project tools/Accountix.RefDb -- --only hr
#    Side by side (t_accountix_ref…, own reference.json; tests: ACCOUNTIX_REFERENCE=<that file>):
#                               ... dotnet run --project tools/Accountix.RefDb -- --prefix t_ --out /tmp/t_reference.json
#    and remove that set again: ... dotnet run --project tools/Accountix.RefDb -- --prefix t_ --drop
#    After loading, this build's migrations (src/Accountix.Migrations) run on it (--no-migrate to skip).

# 2. The tests (a project's own copy: ACCOUNTIX_TEST_DB=<module key>).
dotnet test tests/Accountix.Tests
ACCOUNTIX_TEST_DB=hr dotnet test tests/Accountix.Hr.Tests
```

Without `tools/reference.json` (or with its server down), the integration tests are skipped and the unit tests
still run. Without `ACCOUNTIX_TEST_PG` the tool reuses the server named in an existing `tools/reference.json`.

The fixture was captured 29 Oct 2026 from the database the Python app built and seeded (Alembic 0018), with
`pg_dump --inserts --no-owner --no-privileges --exclude-table-data=user_sessions --exclude-table-data=dotnet_data_protection_keys accountix_ref | gzip -9n`.
Re-capture it the same way only on purpose (tests rely on its ids and on `tests/fixtures/reference.facts.json`);
new schema changes reach the test databases through the migrations instead.

## Run the web app locally

```bash
cd dotnet
SECRET_KEY=<16+ characters, the deployment's own> DATABASE_URL=postgresql://... \
  dotnet run --project src/Accountix.Web
```

- `/auth/login` signs you in (the `ax_session` cookie). `/net/access` shows who
  you are signed in as, the company, and what you may open.
- `/net/health` answers when the app reaches the database.
- `/static` (the pages' CSS and JS) is `src/Accountix.Web.Shared/static`, copied next to the build and publish output.
- Maintenance commands (the old Python `tools/` scripts): `dotnet run --project src/Accountix.Web -- admin`
  lists them; each is a dry run unless `--apply` (docs/DEPLOY.md, "Maintenance commands").
- Speed-test databases: `tools/restore_perf_db.sh` restores them from their dumps and writes `tools/perf.json`.

## Rules for module code

- Each module controller carries `[Module("accounting")]`.
- Each action carries `[Right("journal_vouchers", Rights.Approve)]` (one or
  more) or `[AnyMember]`. `RightsCoverageTests` fails otherwise.
- Company tables implement `ITenantScoped`, which brings filtering, stamping
  and move protection automatically. Deliberate platform-wide reads use
  `CompanyContext.Unscoped()` or `IgnoreQueryFilters([AccountixDbContext.TenantFilter])`.
- Money is `decimal`, never `double`.
