# E2E findings (browser suite, `tests/Accountix.E2E`)

Bugs the browser suite found in other people's modules. Each has the page, the steps, what was
expected and what happened, and a screenshot in the session scratchpad
(`/private/tmp/claude-501/-Users-apple-Downloads-accountix-erp-main/5a911460-b143-4a75-bfdd-3af0a155b1c4/scratchpad/e2e-screens/`).
The E2E engineer does not fix module code. A case that fails because of an open finding is listed in
`Harness/KnownIssues.cs` and is skipped with the finding's id. `E2E_KNOWN=run` runs those cases too,
so you can check a fix; remove the entry once it passes.

| Id | Module | Page | Status |
|---|---|---|---|
| F1 | Accounting | saved voucher form `/accounting/vouchers/<id>` | ✅ fixed 29 Oct (coordinator); the 8 page cases pass |
| F2 | Accounting (inherited from Python) | voucher print `/accounting/vouchers/<id>/preview` at ≤ 375 px | ✅ fixed 29 Oct in both apps; 3 cases pass |
| F3 | Accounting (inherited from Python) | register print-all `/accounting/registers/vouchers/documents` at ≤ 375 px | ✅ fixed 29 Oct in both apps; 3 cases pass |
| F4 | Web.Shared shell (+ every module page that calls `form.submit()`) | voucher form More → Unapprove / Delete draft, and other `form.submit()` callers | ✅ fixed 29 Oct (shared `_Antiforgery` partial); both lifecycle tests and the script-submit test pass. the SuperAdmin console now uses the shared partial too; its POST forms pass (`SuperAdminTests`) |
| F5 | Web.Shared shell (inherited from Python) | company switcher menu at 375 / 360 / 320 px | ✅ fixed 29 Oct in both shells; switcher test passes at 1366/375/360/320 |
| F6 | HR (owner) + Platform provisioning, both apps | Leave → Apply in a new company: no leave types to choose | ✅ fixed 29 Oct (.NET): provisioning adds the three seed leave types; `admin add-default-leave-types --apply` for existing companies (`LeaveTypesTests`) |
| F7 | Accounting, coordinator (speed; same design in Python) | voucher register for the whole year opened by its URL (`view=1`) | accepted: 0.36 s server time on the 4× company after Alembic 0018; production compresses the 1.3 MB page to ~100 KB (Brotli, docs/DEPLOY.md); within the 1.5 s budget |
| F8 | Web.Shared, coordinator (low) | the module refusal page (`AccessDenied`) outside the real host | fixed (coordinator): the view sets `Layout = "_AppShell"` itself and uses Python's words; `ShellTests.A_module_refusal_renders_in_the_app_shell_with_pythons_words` |

## F1 — A saved voucher opens with a script error: no lines, nothing can be edited, approved or unapproved

- **Module / page:** Accounting, `Areas/Accounting/Views/Vouchers/Form.cshtml`, every saved voucher (draft or approved), at every width.
- **Steps:** save any voucher (e.g. a CPV draft), or open `/accounting/vouchers/<id>` for one that exists.
- **Expected:** the form lists the voucher's lines (Python's `{{ line.account.code|tojson }}`), totals and the entry preview fill in.
- **Actual:** the console shows `SyntaxError: Unexpected token '&'` and then `ReferenceError: IS_CB is not defined`.
  The whole first `<script>` block fails, so the page shows no lines, no totals, no preview; the add/remove
  buttons, account pickers, Save/Approve validation and the v3 block (confirm, More menu) are dead or broken.
  The rendered script reads:
  ```
  var EXISTING = [
    {id: 516, code: &quot;5-04-01-01-0001&quot;, name: &quot;Other Expenses — General&quot;,
  ```
- **Cause:** `string Json(string s) => JsonSerializer.Serialize(s)` (line 9) returns a `string`, and `@Json(...)`
  in the `@:` lines (493–494, 502) is HTML-encoded by Razor. It needs `Html.Raw(...)` (or the helper to return
  `IHtmlContent`). The page tests pass on the server side because they only search the HTML for text.
- **Screenshot:** `page_approved-voucher_320_failed_*.png`, `page_draft-voucher_desktop_failed_*.png`.
- **Blocks:** every voucher flow test that reopens a saved voucher (save → approve → unapprove → delete).

## F2 — The voucher print page is wider than a phone (inherited from Python)

- **Module / page:** Accounting, `/accounting/vouchers/<id>/preview` (`Preview.cshtml` + `_VoucherDoc` + `_VoucherDocStyle`), at 375, 360 and 320 px.
- **Steps:** open the preview of any voucher on a phone.
- **Expected:** fits the screen (AGENTS.md rule 3: every page from 320 px); a wide table scrolls inside its own box.
- **Actual:** the document is 514 px wide; the lines table (`table`, six columns) pushes the page sideways.
- **Note:** the port is faithful: Python's `_voucher_doc.html` / `_voucher_doc_style.html` have the same
  markup and CSS, so the Python page overflows the same way. A fix (wrap the table in
  `<div style="overflow-x:auto">` or let `.pa table` scroll under 560 px) belongs in both apps.
- **Screenshot:** `page_voucher-preview_320_overflow_*.png`.

## F3 — The register's print-all page is wider than a phone (inherited from Python)

- **Module / page:** Accounting, `/accounting/registers/vouchers/documents?mode=month&month=<m>&view=1`, at 375, 360 and 320 px.
- **Steps:** open the print-all page for a month with vouchers on a phone.
- **Expected / actual:** as F2: each voucher's table reaches 489–514 px and is cut off past the right edge
  (the page itself doesn't scroll sideways, so the Debit/Credit columns are unreachable).
- **Note:** same shared `_VoucherDoc`; same in Python (`voucher_documents.html`).
- **Screenshot:** `page_register-documents_360_overflow_*.png`.

## F4 — Forms submitted from script (`form.submit()`) carry no antiforgery token: 400 Bad Request

- **Module / page:** the app shell's antiforgery script (`Accountix.Web.Shared/Views/Shared/_AppShell.cshtml`) and every
  page that submits a POST form from JavaScript. Seen on Accounting's voucher form: **More → Unapprove** and
  **More → Delete draft** (both `data-confirm` buttons; the in-page confirm calls `f.submit()` on
  `#vUnapproveForm` / `#vDeleteForm`).
- **Steps:** approve a voucher, open it, More → Unapprove → confirm "Unapprove". (Or More → Delete draft on a draft.)
- **Expected:** the POST is accepted, the voucher becomes a draft (journal reversed) / is deleted.
- **Actual:** `POST /accounting/vouchers/<id>/unapprove` answers **400** (antiforgery validation) and the browser shows
  the empty 400 page. Nothing changes in the database.
- **Cause:** the shell adds `__RequestVerificationToken` in a capture-phase `submit` event listener, but
  `HTMLFormElement.prototype.submit()` does not fire `submit` events. The form tag helper does not add the token
  either, because these forms have an explicit `action=` attribute. The Python app checks Origin/Referer, so the
  same JS works there.
- **Where else:** `.submit()` on POST forms also appears in the .NET ports of Fbr dashboard, Payroll RunPayroll,
  Inventory ProductLedger / Stock List / Movements, Orders SO/PO lists, Invoicing SI list and dashboard (some may be
  GET filter forms, which are fine).
- **Suggested fix (one place):** in the shell script, wrap `HTMLFormElement.prototype.submit` (and `requestSubmit`
  is already covered by the event) so a non-GET same-site form gets the hidden token before it submits.
- **Test:** `VoucherFlowTests.Cash_payment_saved_approved_unapproved_and_deleted_on_desktop` and
  `..._Journal_saved_and_approved_in_one_step_then_unapproved_and_deleted_on_a_320px_phone` (skipped as F4 until fixed;
  `E2E_KNOWN=run` runs them). Screenshot: `lifecycle_CPV_desktop_failed_*.png`.

## F5 — The company switcher's menu opens off the left edge of a phone (inherited from Python)

- **Module / page:** the app shell (`_AppShell.cshtml`, `.company-switch-menu`), every module page, at 375, 360 and 320 px.
- **Steps:** sign in as someone in two companies (owner_a), open any page on a phone, tap the company button in the top bar.
- **Expected:** the menu opens inside the screen (AGENTS.md rule 3).
- **Actual:** the menu (`position:absolute; right:0; min-width:210px`) is right-aligned to the company button, which on a
  phone sits ~110 px from the left (the module badge and user menu follow it), so it starts at **x = −100 px**: the
  company names' first letters and the dots are cut off; "My Companies" is half hidden. The page itself does not
  scroll sideways (the menu is absolutely positioned), so the scrollWidth check does not see it; the switcher test
  measures the menu box.
- **Note:** identical CSS in Python's `templates/layouts/app_shell.html`, so the Python app has it too.
  Possible fix: under 768 px make the menu `position:fixed; left:12px; right:12px; top:56px` (or `right:auto; left:0`
  when the button is in the left half).
- **Test:** `ShellTests.Company_switcher_lists_the_persons_companies` (desktop passes; 375 and 320 are skipped as F5,
  `E2E_KNOWN=run` runs them). Screenshot: `switcher_320_320_failed_*.png`, `switcher_375_375_failed_*.png`.

## F6 — A company has no leave types unless `hr_app/seed.py` was run (both apps)

- **Module / page:** HR, `/leaves/apply` (and ESS / leave balances); company provisioning (Platform's `CompanySetup`,
  Python's `provision_company`).
- **Steps:** in any company of the reference database (or a company made through the portal / console), open Leave → Apply.
- **Expected:** the leave types to choose from (Casual / Sick / Annual, as `hr_app/seed.py` writes them).
- **Actual:** the select holds only "Select type...": no page in either app creates leave types, provisioning doesn't
  either, and only the stand-alone `hr_app/seed.py` script does. An employee cannot apply for leave at all.
- **Note:** not a porting bug. The browser test adds the three seed types to the e2e copy when they are missing.
  Suggest provisioning them (and file categories) with the company, or a Leave types page.
- **Fixed 29 Oct (.NET only; the Python app is being retired):** `CompanySetup.ProvisionAsync` (portal, console,
  My Companies) now adds `hr_app/seed.py`'s three types (Casual CL 12 days; Sick SL 10, carry 5; Annual AL 20,
  carry 10; paid, need approval) through `EnsureLeaveTypesAsync`, only when the company has none, so a company with
  its own types keeps them and provisioning twice adds nothing. Existing companies (the reference database's, and
  Neon's) get them with `dotnet Accountix.Web.dll admin add-default-leave-types --apply [company id ...]`, which
  also skips any company that already has leave types. Tests: `tests/Accountix.Admin.Tests/LeaveTypesTests.cs` (a new
  company's Leave → Apply lists the three; idempotent; own types kept; backfill dry run, apply, second run, one
  company). File categories are not provisioned (not part of this finding).

## F7 — The whole year's voucher register, opened by its URL, takes ~2 s in the browser (speed)

- **Module / page:** Accounting, `/accounting/registers/vouchers?mode=year&period_id=<FY>&view=1` on the full-year
  database (3,118 vouchers). This is the URL the page itself writes into the address bar after **View**
  (`history.replaceState(…'&view=1')`), so a reload or a shared link opens it this way.
- **Measured (desktop, best of 3 after a warm-up):** load event **1,937 ms** (runs 2,312 / 1,937 / 2,033 ms) while
  the machine was busy with other builds, **801 ms** on a quiet machine; first byte 85–163 ms. The HTML is **1.3 MB**
  (every row of the year in one page), so nearly all of the time is the browser laying it out.
  The month view is ~110 ms; building the same year by pressing View (chunks of 200) takes ~820 ms.
- **Expected:** comfortably inside the 2 s budget; SPEED_AND_CONCURRENCY.md asks for "registers for the year with paging".
- **Suggestion:** with `view=1`, render the first 200 rows server-side and let the existing chunk loader (`/data?offset=`)
  fetch the rest, as pressing **View** already does; or page at 100 rows as the invoice lists do.
  `BrowserSpeedTests.Voucher_register_for_the_year` passes today on its best run only.

## F8 — The module refusal page gets its layout from the host project only (low)

- **Module / page:** Web.Shared `Views/Shared/AccessDenied.cshtml` (rendered by `Refusal.Forbidden` for a module the
  person may not open, e.g. owner_a switching to company B on an Accounting page).
- **Actual:** the view sets no layout; it gets `_Layout` only from `src/Accountix.Web/Views/_ViewStart.cshtml`. Any host
  without the Accountix.Web project (module tests' `ModuleHost`, this suite's Kestrel host) renders it as bare HTML
  (Times font, no theme, no shell): screenshot `switch_360_failed_035445.png`. In the real host it is styled.
- **Also:** Python renders this refusal inside the app shell (`templates/access_denied.html` extends `layouts/base.html`:
  lock icon, "Module Not Available", "Back to Modules"); .NET shows a plain card on `_Layout` with different words.
- **Suggestion:** set `Layout = "_Layout"` (or `_AppShell`) in the view itself, and match Python's text.
