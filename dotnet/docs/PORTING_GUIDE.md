# Porting guide: Python (Flask) → .NET, one module per project

> **Historical (29 Oct 2026).** The port is complete and the Python app has been removed. This guide is how the
> modules were ported. Its build and test rules (artifacts paths, `ACCOUNTIX_TEST_DB`, `ModuleHost`, budgets) still
> apply; everything about running Python (`PythonApp`, `[PythonFact]`, the venv, cross-app races) no longer exists.
> The reference databases are built by `tools/Accountix.RefDb`.

Goal: the complete app in .NET with **identical behaviour**. That means the
same URLs, the same pages (same HTML ids and classes, so the shared CSS/JS in
`/static` keeps working), the same database writes and the same journal
entries. It must work on phones from 320px wide. Both apps run on the same
Postgres database until cut-over, so a document saved by one app must look
exactly right to the other.

Repo root: `/Users/apple/Downloads/accountix_erp-main` (Python app). .NET: `dotnet/`.

## Layout and ownership

| Path | What | Who edits |
|---|---|---|
| `src/Accountix.Core` | Entities generated for all 122 tables (`Entities/Generated`, do not edit), `AccountixDbContext` (company filter), access rules, security, `Shared/` helpers and **contracts** | Frozen. Exception: the owner of a contract may ADD members to that interface, then build Core at once |
| `src/Accountix.Web.Shared` | Sign-in (`ax_session`), company context, `[Module]`/`[Right]`/`[AnyMember]`/`[CompanyAdmin]`/`[Shell]`, app shell `_AppShell`, flash, errors, navigation, `AccountixApp` set-up | Frozen |
| `src/Accountix.Web` | Host (`Program.cs`), `/net/access`, `/net/health` | Frozen |
| `src/Modules/Accountix.<Name>` | **Your module**: controllers, views (`Areas/<Name>/Views/...`), services, `<Name>Module.cs` | Only its owner |
| `tests/Accountix.<Name>.Tests` | Your module's tests | Only its owner |
| `docs/modules/<name>.md` | Your status notes | Only its owner |

Entity classes live in Core, and partial classes must stay in the same
assembly, so put entity behaviour in your module as extension methods or
services.

Never edit another module's files, the frozen projects, `Accountix.slnx` or
any `.csproj` except your own module and test project.

## Build and test without disturbing others

Several agents build at once. **Always** pass your own artifacts path:

```bash
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
ART=/private/tmp/claude-501/-Users-apple-Downloads-accountix-erp-main/5a911460-b143-4a75-bfdd-3af0a155b1c4/scratchpad/artifacts/<name>
cd /Users/apple/Downloads/accountix_erp-main/dotnet
dotnet build src/Modules/Accountix.<Name> --artifacts-path $ART
ACCOUNTIX_TEST_DB=<db key> dotnet test tests/Accountix.<Name>.Tests --artifacts-path $ART
```

Build only your module and your test project. Never build the whole
solution or `src/Accountix.Web`, because other modules are mid-edit. Keep
your project compiling after every step.

`ACCOUNTIX_TEST_DB` selects **your own copy** of the reference database:
`accounting`, `finance`, `executive`, `inventory`, `invoicing`, `parties`, `orders`, `returns`, `hr`,
`hr_records`, `payroll`, `fixed_assets`, `fbr`, `platform`, `superadmin` or `e2e`. Every copy is
`tests/fixtures/reference.sql.gz` (the database the Python app once built and seeded, at Alembic 0018;
company 1 "Default" holds the demo data), loaded by `tools/Accountix.RefDb`. Connection details are in
`tools/reference.json` (`databases.<key>`). Expected answers that once came from running Python are
golden values in the tests (or `tests/fixtures/reference.facts.json`).

## Writing a page

```csharp
namespace Accountix.Accounting.Controllers;

[Area("Accounting")]                     // views: Areas/Accounting/Views/<Controller>/<Action>.cshtml
[Module(Modules.Accounting)]             // company entitled + person may open the module; also picks the sidebar
[Route("accounting")]                    // the Python app's URL prefix, exactly
public sealed class VouchersController(AccountixDbContext db, ILedger ledger, IVoucherNumbers numbers,
                                       CompanyClock clock) : Controller
{
    [HttpGet("vouchers/list")]
    [Right("journal_vouchers", Rights.View)]      // or [AnyMember] when Python's rights map says OPEN
    public async Task<IActionResult> List(CancellationToken ct) { ... return View(model); }
}
```

- **URLs** are the Flask rules, character for character, including trailing
  slashes and query parameter names. Links in views are literal paths
  (`url_for('x.y')` becomes `"/x/y"`). Links to modules not yet ported still work, because
  both apps share one domain.
- **Rights.** `shared/rights_guard.py` `RIGHTS` names the (section, action)
  each endpoint needs. Put the same requirement on the action as
  `[Right(...)]`; one attribute per requirement, all must hold. Some depend on the
  request (a voucher type, approve inside a JSON save). Check those in the action
  with `HttpContext.Access()!.Can(section, action)` and refuse in the Python app's
  words. HR pages are governed by role in the routes (`is_admin`,
  `is_manager`); port those checks as written. Every action of a `[Module]`
  controller needs `[Right]` or `[AnyMember]`; a test enforces it.
- **The person**: `HttpContext.Access()` gives `EffectiveAccess` (User, Company,
  Membership, RoleName, IsAdmin, IsManager, CanOpenModule, Can). The company is
  already set for every query.
- **Views**: port each Jinja template to Razor one to one. Keep the markup, ids,
  classes and inline scripts so the shared `/static/js/*.js` and CSS behave the
  same. Layout `_AppShell` is set by `_ViewStart`. Set `ViewData["Title"]`, optionally
  `ViewData["Heading"]` and `ViewData["BackUrl"]`, and use `@section Actions {}`
  (page toolbar), `@section Head {}` and `@section Scripts {}`. Python's
  `amount_format` filter becomes `@Money.Format(v, 2, Context.Access()?.Company?.NumberFormat)`.
  The shared CSS/JS lives in `src/Accountix.Web.Shared/static/` (served at `/static`, copied next to every
  build and publish). Edit it there; `StaticFilesTests` fails when a page links a file that is not there.
- **Forms** posted to the server need the antiforgery token. A `<form method="post">`
  gets it automatically, and so do `fetch()` calls (the shell adds the header).
- **Flash**: `this.Flash("Saved.", "success")`, then redirect, as Flask's `flash()`.
- **Refusals the user should read** (closed period, bad date, negative stock,
  over-return...) throw `BusinessRuleException` (or a subclass). The middleware
  rolls back and answers JSON 409 `{ok:false,error}` or a flash plus redirect back,
  as Python's handlers do. Use the Python message text.
- **JSON endpoints** answer the same JSON shape as Python (`{ok, error, ...}`, the same keys).

## Data rules

- **Company isolation is automatic** for every table with `company_id` (except
  companies, memberships, invitations, global_limits, audit_logs). New rows
  are stamped, and querying with no company throws. Platform tables need an
  explicit company in the query.
- **Defaults**: Python's column defaults are client-side, so the database
  supplies none. Set every field Python would set (`status`, `created_at = clock.UtcNow`,
  booleans, numbers) when you insert.
- **Times**: the database holds naive UTC (`datetime.utcnow()`); the context
  converts. Document dates are midnight local dates: `DocumentDates.Parse(value, fallback, clock)`.
  The company's "today" is `clock.Today` and its "now" is `clock.Now`.
- **Money**: `decimal` everywhere. Match Python's rounding exactly where it
  posts: Python builds journal amounts with `Decimal(str(x))`, and some
  computations use `round(x, 2)` on floats (banker's rounding on the float).
  Compare your figures with Python's on the same data; the posted journal lines
  must be identical to the paisa.
- **Transactions and locks**, with the same order and names as Python (two apps
  share these locks):
  `await using var tx = await db.Database.BeginTransactionAsync(ct);` …
  `await tx.CommitAsync(ct);`. Load the record you change with
  `await db.LockAsync<T>(id, ct)` (FOR NO KEY UPDATE, then reload). Use
  `await db.SerializeAsync("scope", key...)` where Python calls
  `serialize("scope", key...)`. The lock key is identical, so Python and .NET
  queue together. The lock order everywhere is document → number series →
  order lines → products → report totals.
- **Document numbers**: `DocNumbers.Placeholder()` until the insert, then
  `DocNumbers.FromId(prefix, clock.Now, id)`, as `shared/doc_numbers.py`.
- **Edit versions** (`edit_version` on SI/PI/SO/PO/SR/PR): port
  `shared/concurrency.py` `stale_edit` / `bump_edit` in your module.

## Shared services (contracts in `Accountix.Core/Shared/Contracts.cs`)

| Contract | Owner | Python |
|---|---|---|
| `ILedger` (post, reverse, posting/entity/party accounts, cash & bank) | Accounting | `shared/ledger_utils.py`, `shared/gl_summary.py` |
| `IVoucherNumbers` | Accounting | `VoucherNumber.next` |
| `IPeriods` | Accounting | `shared/periods.py` |
| `IStockCosting` | Inventory | `shared/costing.py`, `cost_adjustment.py`, `stock_entry.py` |
| `IReportExport` | Finance | `shared/report_export.py` |
| `IAuditTrail` + automatic audit interceptor | Platform | `shared/audit.py` |

Until its owner registers the real one, a contract throws
`NotYetPortedException`. In unit tests, use a fake. The owner registers the
implementation in its `<Name>Module.Register` (registered last, so it replaces
the stub). If you need something a contract lacks, write it in your module, or
ask the owner to add it (see "Status notes").

## Sidebar

Port your module's entries from `shared/navigation.py` `NAV[<key>]` into your
`<Name>Module.Navigation`, as `ModuleNav` / `NavGroup` / `NavItem` with:
- the literal URL;
- `ActivePrefixes` for "prefix"/"exact" matching;
- `Gate` for `_admin` etc. (`(a, http) => a.IsAdmin`);
- `Right` for the rights-guard hide;
- `Svg` from `templates/partials/icons.html` (endpoint → icon name).

## Tests (in `tests/Accountix.<Name>.Tests`)

- **Unit tests** for the rules: calculations, refusals.
- **Integration tests** on your database copy: `[ReferenceFact]` (skips when
  the reference database is missing), `Reference.NewDb(companyContext)`,
  `Reference.Company("a"|"b")`, `Reference.UserId(email)`.
  The seeded admin `admin@gmail.com` (super admin) and `emp@solarkon.com` are
  in company 1 "Default" (`slug default`). Look up ids in the database.
- **Page tests**: `await using var host = await ModuleHost.StartAsync(typeof(<Name>Module).Assembly);`
  then `host.ClientFor(userId, companyId)`. This boots only your module and the
  shared core, signed in as that person.
- **Parity**: wherever you write data (journals, stock, numbers), check that .NET
  writes what Python writes for the same input on the same data. Run the Python
  route on your copy (Flask test client, `DATABASE_URL` set), read the rows, do
  the same in .NET and compare. Roll back or use distinct documents so tests can repeat.
- **Responsive**: no page may scroll sideways at 320px; tables scroll inside
  their own box, as in Python. Port the CSS as is.

## Status notes: `docs/modules/<name>.md`

Keep it current:
- what is ported (routes, with ✅);
- what isn't yet, and why;
- what you need from another module;
- the tests and their results.

The coordinator reads these files.

---

## Update 29 Oct: lessons from porting Accounting (READ THIS)

**Accounting is complete and is the reference implementation.** Copy its patterns:
`src/Modules/Accountix.Accounting` and `tests/Accountix.Accounting.Tests`, with notes in `docs/modules/accounting.md`.

### Already built (use these; do not rebuild them)
- **Ledger services** (`ILedger`, `IVoucherNumbers`, `IPeriods`): real, in Accountix.Accounting, tested for
  parity with Python. `gl_daily_balances` follows every journal save automatically (`GlSummaryInterceptor`).
  Accounting is finished, so it is stable; your **test project may reference
  `src/Modules/Accountix.Accounting`** and boot it with yours:
  `ModuleHost.StartAsync(typeof(YourModule).Assembly, typeof(Accountix.Accounting.AccountingModule).Assembly)`.
- **Report export** (`IReportExport` → `Accountix.Finance.Services.ReportExport`): Excel (ClosedXML),
  PDF (PDFsharp-MigraDoc **pinned to 6.2.4**; 6.5+ is AGPL, never upgrade) and CSV, with Python's layout.
  `format` is `"excel"|"pdf"|"csv"`; anything else throws `UnknownExportFormatException`, which answers 404.
  The Finance engineer is still editing that project, so reference it from tests only for export tests,
  and run those last.
- **Shared partials** (Web.Shared `Views/Shared`):
  - `_PeriodFilter` (model `PeriodFilterModel`): Single / Custom / Comparative + labels;
  - `_ExportButtons` (model `ExportButtons`, e.g. `ExportButtons.Default` or `new ExportButtons("excel","pdf","csv") { Param = "fmt" }`);
  - `_GlEntryButton` (model `new GlEntryButton("SI", id)`): the Accounting-entry panel; its API `/accounting/gl-entry` is ported;
  - `_PrintHeader` (model: document title string).
- **Helpers** (Web.Shared `Infrastructure`):
  - `PeriodFilters` (`ParseDate`, `AllAsync`, `DefaultAsync`, `DayBounds`) and `RegisterFilter.ResolveAsync` (month / year / custom);
  - `FormInput.Float/Int/Date/OptionalDate` (Python's `form_float` etc.; they throw `FormInputException`, flashed with a redirect back);
  - `ViewHelpers`:
    - `Html.Amount(v, decimals)`: money in the company's number format, which comes from the CompanyInfo
      profile (`access.NumberFormat`), not `Company`;
    - `this.Amount(...)` for JSON;
    - `this.RenderViewAsync(viewName, model)` for JSON that carries HTML;
    - `ViewHelpers.Title(s)` for Python's `.title()`.
- **Rights**:
  - `[Right(section, action)]` for fixed rights;
  - `[AnyRight("sec:view", "sec2:view")]` for rights_guard `AnyOf`;
  - for request-dependent rights, check in the action:
    `if (this.Deny(section, action) is { } refused) return refused;`. It refuses exactly as Python does:
    JSON 403, or a flash "No 'x' rights here. Ask an admin in Settings." plus a redirect to the referrer or the module landing;
  - `this.DenyPage(section, action)` for Python's `deny_page` (flash only; you redirect).
- **Company profile**: `HttpContext.Access().Profile` is the CompanyInfo row (company name for documents and
  exports, number format, currency symbol). Python printed `company.company_name` on documents, which was always
  blank; print `Profile.CompanyName`, falling back to `Company.Name`.
- **Contracts in `Core/Shared/Contracts.cs`**: `IPaymentTracking` (owner **Parties**; Accounting calls it after
  voucher saves and treats failure as non-fatal), `IStockCosting` (owner **Inventory**), `IAuditTrail` (owner
  **Platform**), plus `FormInputException`, `UnknownExportFormatException` and `ReportTable` options (`MonoColumn`,
  `IndentColumn`, `BoldRows`, `ColumnWidths`, `Autofilter`, `SheetTitle`, `ExtraSheets`, `FilePeriod`).

### Testing
- **Python on your database copy**: `PythonApp.Run(pythonCode, companyId)` (TestSupport) runs code inside the Python
  app on the same database your tests use (`app`, `db`, `json`, `set_current_company` are in scope, inside a request
  context), and returns stdout. Mark such tests `[PythonFact]`, and set `ACCOUNTIX_PYTHON` to the venv python:
  `/private/tmp/claude-501/-Users-apple-Downloads-accountix-erp-main/5a911460-b143-4a75-bfdd-3af0a155b1c4/scratchpad/venv/bin/python`.
  To POST to a Python route:
  `from werkzeug.datastructures import MultiDict; from shared.sessions import test_session; c = app.test_client();`
  `with c.session_transaction() as s: s.update(test_session(uid)); s["company_id"] = cid`.
  See `tests/Accountix.Accounting.Tests/VoucherPageTests.cs`
  (`Save_and_approve_posts_what_the_python_form_posts`) for a full parity test.
- **Pages**: `host.ClientFor(userId, companyId)` returns a `PageClient`. It keeps cookies, and its
  `PostFormAsync(url, fields, tokenPage)` posts with the antiforgery token taken from `tokenPage`.
  Redirects are not followed.
- **Reference users**: `Reference.Email("owner_b")`, admin of company `Reference.Company("b")`, which is
  fresh and fully provisioned; "owner_a" (an employee in B), "restricted" (a manager in B with only
  journal_vouchers view+create and chart_of_accounts view), "blocked". Company 1 ("default") holds the
  seeded demo data, and `admin@gmail.com` is the super admin.
- **One test at a time per assembly**: every test project has `AssemblyInfo.cs` with
  `DisableTestParallelization = true`, because tests of a module share one database. A parity test compared
  figures that a parallel test changed in between.
- The reference Postgres (pgserver, unix socket in `$TMPDIR/accountix_reference_pg`) must stay running.
  **Never stop it or run `tools/Accountix.RefDb` without `--prefix`/`--only`** (a full run drops every module's
  copy). Ask the coordinator instead.

### Views: how Accounting converted Jinja
- A Python script per page copied the template's `<style>` and `<script>` blocks **verbatim** (only `@` → `@@`),
  and the markup was converted by hand (ids and classes unchanged).
- `{% block extra_css %}` → `@section Head { }`, `{% block extra_scripts %}` → `@section Scripts { }`,
  `{% block title %}` → `ViewData["Title"]`, `{% block page_title %}` → `ViewData["Heading"]`.
- A standalone page (print popups) sets `Layout = null` and links `/static/css/app.css` + `ui.css` itself.
- Boolean attributes: `selected="@(cond)"`, `checked="@cond"`, `disabled="@cond"`, `readonly="@cond"`
  render only when true.

### Running the app and a browser
- **Build the full host only when you need a browser**, and use your own port:
  - accounting 5276 (coordinator);
  - finance 5301, inventory 5302, invoicing 5303, parties 5304, hr 5305, payroll 5306, fixed_assets 5307, platform 5308.
- Start it with `SECRET_KEY` and `ConnectionStrings__Accountix` from `tools/reference.json` (your `databases.<key>`).
- In the Chrome DevTools browser, always open pages with `isolatedContext: "<your module>"`, so you never share
  cookies with another engineer.
- To sign in, mint a cookie with `TestSignIn.CookieFor(userId, companyId)` (a `user_sessions` row + the signed
  value) and set it as `ax_session` (`TestSignIn.CookieName`), or sign in at `/auth/login` with the reference password.

## Update 29 Oct (later): data inside `<script>` (E2E finding F1)

Razor HTML-encodes every `@expression` that is a `string`. Inside a `<script>` block that turns
`code: "5-04"` into `code: &quot;5-04&quot;`, a syntax error that kills the page's JS. Server-side
page tests don't notice, because the text is still "there".

- In `<script>`: `@Html.Raw(JsonSerializer.Serialize(value))`, or a helper that returns
  `IHtmlContent`. `JsonSerializer`'s default encoder already escapes `<`, `>`, `&` and quotes as `\u00XX`,
  so raw output is safe there. This is Jinja's `|tojson`.
- In an HTML attribute (`onsubmit="return confirm('Delete ' + @Json(name))"`) keep the normal encoding:
  the browser decodes `&quot;` before the JS runs.
- Test it: extract the script block from the page and assert it has no `&quot;` / `&amp;`
  (`A_saved_voucher_reopens_with_its_lines_in_valid_script` in the Accounting tests).

## Update 29 Oct (later): antiforgery in every layout (E2E finding F4)

`form.submit()` fires no `submit` event, so script-submitted forms (in-page confirms) posted without
the token and were refused with 400. The token script now lives in one partial,
`Web.Shared/Views/Shared/_Antiforgery.cshtml`: fetch/XHR header, a submit listener, a wrapper of
`HTMLFormElement.prototype.submit`, same-site only. `_AppShell` and `_Layout` include it. **Any other
layout you write must include `<partial name="_Antiforgery" />` in `<head>`.** Never copy the script.
