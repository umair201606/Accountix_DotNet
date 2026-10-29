using System.Text.Json;
using Accountix.E2E.Harness;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Accountix.E2E.Accounting;

/// <summary>
/// The voucher screens in the browser: the register and its filters, the entry
/// form (lines, totals, entry preview, cash balance, in-page validation), and the
/// life of a voucher — save, approve, unapprove, delete — checked in the ledger.
/// Ports tests/e2e/test_voucher_ui.py, test_voucher_v3_and_sidebar.py and the
/// browser side of test_accounting_voucher_form.py.
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class VoucherFlowTests(AccountingApp app) : IClassFixture<AccountingApp>
{
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    // ── register (test_voucher_ui.py) ──

    [E2EFact]
    public async Task Voucher_list_has_clear_filters_and_an_empty_state()
    {
        // Company A: the suite never writes vouchers there.
        await using var s = await app.SessionAsync(Viewport.Desktop, E2EEnv.OwnerA, E2EEnv.CompanyA);
        await s.GoAsync("/accounting/vouchers/list");
        var p = s.Page;
        await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "Vouchers", Exact = true })).ToBeVisibleAsync();
        await Expect(p.GetByRole(AriaRole.Region, new() { Name = "Filter vouchers" })).ToBeVisibleAsync();
        await Expect(p.GetByLabel("Voucher type", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(p.GetByLabel("Status", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(p.GetByRole(AriaRole.Region, new() { Name = "Voucher register" })).ToBeVisibleAsync();
        await Expect(p.GetByText("No vouchers found", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(p.GetByRole(AriaRole.Link, new() { Name = "+ New Voucher", Exact = true })).ToBeVisibleAsync();
        await s.AssertCleanAsync();
    }

    [E2EFact]
    public async Task Voucher_list_filters_by_type_and_status_and_fits_a_phone()
    {
        var d = await AccountingSeed.EnsureAsync(app);
        var approved = AccountingSeed.Voucher(d.ApprovedId)!;
        await using var s = await app.SessionAsync(Viewport.Phone375, E2EEnv.OwnerB, E2EEnv.CompanyB);
        await s.GoAsync("/accounting/vouchers/list");
        var p = s.Page;
        await Expect(p.Locator(".voucher-table thead")).ToBeHiddenAsync();
        await Expect(p.GetByRole(AriaRole.Button, new() { Name = "Filter", Exact = true })).ToBeVisibleAsync();

        await s.GoAsync("/accounting/vouchers/list?vtype=JV&status=approved");
        await Expect(p.Locator(".voucher-table").GetByText(approved.VoucherNumber)).ToBeVisibleAsync();
        string text = await p.Locator(".voucher-table tbody").InnerTextAsync();
        Assert.DoesNotContain(AccountingSeed.Voucher(d.DraftId)!.VoucherNumber, text);
        await s.GoAsync("/accounting/vouchers/list?vtype=CPV&status=unapproved");
        await Expect(p.Locator(".voucher-table").GetByText(AccountingSeed.Voucher(d.DraftId)!.VoucherNumber)).ToBeVisibleAsync();
        Assert.DoesNotContain(approved.VoucherNumber, await p.Locator(".voucher-table tbody").InnerTextAsync());
        await s.AssertCleanAsync();
    }

    [E2EFact]
    public async Task Register_loads_its_rows_and_totals_for_the_month()
    {
        var d = await AccountingSeed.EnsureAsync(app);
        var approved = AccountingSeed.Voucher(d.ApprovedId)!;
        await using var s = await app.SessionAsync(Viewport.Desktop, E2EEnv.OwnerB, E2EEnv.CompanyB);
        string month = approved.VoucherDate.ToString("yyyy-MM");
        await s.GoAsync($"/accounting/registers/vouchers?mode=month&month={month}&view=1");
        await Expect(s.Page.GetByText(approved.VoucherNumber).First).ToBeVisibleAsync();
        // The register's JSON feed answers for the same filter, with the HTML rows and totals.
        var feed = await s.Page.EvaluateAsync<JsonElement>(
            $"async () => (await fetch('/accounting/registers/vouchers/data?mode=month&month={month}', {{headers: {{Accept: 'application/json'}}}})).json()");
        Assert.True(feed.GetProperty("total").GetInt32() >= 1);
        Assert.Contains(approved.VoucherNumber, feed.GetProperty("html").GetString());
        await s.AssertCleanAsync();
    }

    // ── the form (test_voucher_ui.py, test_voucher_v3_and_sidebar.py) ──

    [E2EFact]
    public async Task New_voucher_screen_exposes_type_entry_and_save()
    {
        await using var s = await app.SessionAsync(Viewport.Desktop, E2EEnv.OwnerB, E2EEnv.CompanyB);
        await s.GoAsync("/accounting/vouchers");
        var p = s.Page;
        await Expect(p.Locator("[aria-label=\"Voucher workflow\"]")).ToHaveCountAsync(0);
        await Expect(p.GetByRole(AriaRole.Button, new() { Name = "CPV Cash Payment", Exact = true })).ToBeVisibleAsync();
        await Expect(p.GetByRole(AriaRole.Button, new() { Name = "JV Journal", Exact = true })).ToBeVisibleAsync();
        await Expect(p.GetByRole(AriaRole.Textbox, new() { Name = "Cash Account" })).ToBeVisibleAsync();
        await Expect(p.GetByRole(AriaRole.Button, new() { Name = "+ Add line", Exact = true })).ToBeVisibleAsync();
        await Expect(p.GetByRole(AriaRole.Button, new() { Name = "Save & Approve", Exact = true })).ToBeVisibleAsync();
        // The type pills switch the form.
        await VoucherUi.SubmitAndWaitAsync(s, p.GetByRole(AriaRole.Button, new() { Name = "JV Journal", Exact = true }), @"type=JV");
        await Expect(p.Locator("#cbAccountWrap")).ToBeHiddenAsync();
        await Expect(p.Locator("#sumDr")).ToBeVisibleAsync();
        await s.AssertCleanAsync();
    }

    [E2EFact]
    public async Task New_voucher_entries_fit_a_phone()
    {
        await using var s = await app.SessionAsync(Viewport.Phone375, E2EEnv.OwnerB, E2EEnv.CompanyB);
        await s.GoAsync("/accounting/vouchers");
        var p = s.Page;
        await Expect(p.Locator(".vtb thead")).ToBeHiddenAsync();
        await Expect(p.GetByRole(AriaRole.Textbox, new() { Name = "Account search" })).ToBeVisibleAsync();
        await Expect(p.GetByRole(AriaRole.Textbox, new() { Name = "Description" }).First).ToBeVisibleAsync();
        await Expect(p.GetByLabel("Amount").First).ToBeVisibleAsync();
        // The action bar is pinned to the bottom of the screen on phones.
        var bar = await p.Locator(".vactions").BoundingBoxAsync();
        Assert.NotNull(bar);
        Assert.True(bar!.Y + bar.Height >= Viewport.Phone375.Height - 2 && bar.Width >= Viewport.Phone375.Width - 2,
            $"action bar not pinned to the bottom: {JsonSerializer.Serialize(bar)}");
        await s.AssertCleanAsync();
    }

    [E2ETheory]
    [InlineData("desktop")]
    [InlineData("320")]
    public async Task Lines_add_insert_remove_and_clear_and_totals_follow(string viewport)
    {
        await using var s = await app.SessionAsync(Viewport.Named(viewport), E2EEnv.OwnerB, E2EEnv.CompanyB);
        await s.GoAsync("/accounting/vouchers?type=JV");
        var p = s.Page;
        var rows = VoucherUi.Rows(p);
        await Expect(rows).ToHaveCountAsync(1);

        await VoucherUi.FillLineAsync(p, 0, "5-04-01-01-0001", debit: "100");
        await p.Locator("#addRowBtn").ClickAsync();
        await Expect(rows).ToHaveCountAsync(2);
        await VoucherUi.FillLineAsync(p, 1, "4-01-01-01-0001", credit: "60");
        await Expect(p.Locator("#sumDr")).ToHaveTextAsync("100.00");
        await Expect(p.Locator("#sumCr")).ToHaveTextAsync("60.00");
        await Expect(p.Locator("#sumDiff")).ToHaveTextAsync("40.00");
        await Expect(p.Locator("#jvBalanceChip")).ToContainTextAsync("Out by 40.00");
        await Expect(p.Locator("#approveBtn")).ToBeDisabledAsync();

        await VoucherUi.Row(p, 1).Locator(".cr-input").FillAsync("100");
        await Expect(p.Locator("#sumDiff")).ToHaveTextAsync("0.00");
        await Expect(p.Locator("#jvBalanceChip")).ToContainTextAsync("Balanced");
        await Expect(p.Locator("#approveBtn")).ToBeEnabledAsync();
        await Expect(p.Locator("#jePreview .vpre-row.dr")).ToHaveCountAsync(1);
        await Expect(p.Locator("#jePreview .vpre-row.cr")).ToHaveCountAsync(1);
        await Expect(p.Locator("#jePreview")).ToContainTextAsync("Balanced");

        // "+" on a row inserts under it; rows renumber.
        await VoucherUi.Row(p, 0).Locator(".add-after").ClickAsync();
        await Expect(rows).ToHaveCountAsync(3);
        Assert.Equal(["1", "2", "3"], await p.Locator("#entriesBody .rn").AllInnerTextsAsync());
        Assert.Equal("", await VoucherUi.Row(p, 1).Locator(".acct-input").InputValueAsync());
        Assert.Equal("100", await VoucherUi.Row(p, 2).Locator(".cr-input").InputValueAsync());
        // "−" removes it again; totals unchanged.
        await VoucherUi.Row(p, 1).Locator(".del").ClickAsync();
        await Expect(rows).ToHaveCountAsync(2);
        await Expect(p.Locator("#sumCr")).ToHaveTextAsync("100.00");
        // Enter on the last field of the last line opens a new line.
        await VoucherUi.Row(p, 1).Locator(".cr-input").FocusAsync();
        await p.Keyboard.PressAsync("Enter");
        await Expect(rows).ToHaveCountAsync(3);
        await Expect(VoucherUi.Row(p, 2).Locator(".acct-input")).ToBeFocusedAsync();

        // Clear all leaves one empty line and zero totals.
        await p.Locator("#clearRowsBtn").ClickAsync();
        await Expect(rows).ToHaveCountAsync(1);
        Assert.Equal("", await VoucherUi.Row(p, 0).Locator(".dr-input").InputValueAsync());
        await Expect(p.Locator("#sumDr")).ToHaveTextAsync("0.00");
        await Expect(p.Locator("#jePreview .vpre-empty")).ToBeVisibleAsync();
        // "−" on the only line empties it instead of removing it.
        await VoucherUi.Row(p, 0).Locator(".dr-input").FillAsync("5");
        await VoucherUi.Row(p, 0).Locator(".del").ClickAsync();
        await Expect(rows).ToHaveCountAsync(1);
        Assert.Equal("", await VoucherUi.Row(p, 0).Locator(".dr-input").InputValueAsync());
        await s.AssertFitsAsync();
        await s.AssertCleanAsync();
    }

    [E2EFact]
    public async Task Entry_preview_and_cash_balance_follow_the_lines()
    {
        await using var s = await app.SessionAsync(Viewport.Desktop, E2EEnv.OwnerB, E2EEnv.CompanyB);
        await s.GoAsync("/accounting/vouchers?type=CPV");
        var p = s.Page;
        await VoucherUi.PickAsync(p, p.Locator("#cbSearch"), "Main Cash");
        Assert.Equal("Main Cash", await p.Locator("#cbSearch").InputValueAsync());
        await VoucherUi.FillLineAsync(p, 0, "5-04-01-01-0001", amount: "1500");
        await Expect(p.Locator("#jePreview .vpre-row")).ToHaveCountAsync(2);
        await Expect(p.Locator("#jePreview .vpre-row.dr")).ToHaveCountAsync(1);
        await Expect(p.Locator("#jePreview .vpre-row.cr")).ToHaveCountAsync(1);
        await Expect(p.Locator("#jePreview .vpre-row.cr")).ToContainTextAsync("Main Cash");
        await Expect(p.Locator("#jePreview")).ToContainTextAsync("Balanced");
        await Expect(p.Locator("#sumTotal")).ToHaveTextAsync("1,500.00");
        await Expect(p.Locator("#contraAmt")).ToHaveTextAsync("1,500.00");
        // Cash balance before → after this voucher (GET /accounting/api/account-balance).
        await Expect(p.Locator("#cbBalance")).ToContainTextAsync("after this voucher");
        await s.AssertCleanAsync();
    }

    [E2EFact]
    public async Task Form_mistakes_are_shown_in_the_page_not_in_a_dialog()
    {
        await using var s = await app.SessionAsync(Viewport.Desktop, E2EEnv.OwnerB, E2EEnv.CompanyB);
        await s.GoAsync("/accounting/vouchers?type=CPV");
        var p = s.Page;
        // No line: refused in the page.
        await p.Locator("#saveBtn").ClickAsync();
        await Expect(p.Locator("#vErr")).ToBeVisibleAsync();
        await Expect(p.Locator("#vErr")).ToContainTextAsync("Add at least one line");
        // A line but no cash account.
        await VoucherUi.FillLineAsync(p, 0, "5-04-01-01-0001", amount: "10");
        await Expect(p.Locator("#vErr")).ToBeHiddenAsync();   // typing clears the message
        await p.Locator("#saveBtn").ClickAsync();
        await Expect(p.Locator("#vErr")).ToContainTextAsync("Select the Cash Account");
        Assert.EndsWith("/accounting/vouchers?type=CPV", p.Url);

        // An unbalanced journal cannot be saved.
        await s.GoAsync("/accounting/vouchers?type=JV");
        await VoucherUi.FillLineAsync(p, 0, "5-04-01-01-0001", debit: "100");
        await p.Locator("#addRowBtn").ClickAsync();
        await VoucherUi.FillLineAsync(p, 1, "4-01-01-01-0001", credit: "60");
        await p.Locator("#saveBtn").ClickAsync();
        await Expect(p.Locator("#vErr")).ToContainTextAsync("Journal must balance: Debit 100.00 vs Credit 60.00");
        Assert.Empty(s.Dialogs);
        await s.AssertCleanAsync();
    }

    // ── the life of a voucher ──

    /// <summary>
    /// Save a draft, reopen it ready to edit, approve through the in-page confirm (the
    /// journal posts), unapprove from More (the journal comes off), delete the draft.
    /// </summary>
    [E2EFact]
    public Task Cash_payment_saved_approved_unapproved_and_deleted_on_desktop() => LifecycleAsync("CPV", Viewport.Desktop);

    [E2EFact]
    public Task Journal_saved_and_approved_in_one_step_then_unapproved_and_deleted_on_a_320px_phone() => LifecycleAsync("JV", Viewport.Phone320);

    private async Task LifecycleAsync(string type, Viewport viewport)
    {
        var d = await AccountingSeed.EnsureAsync(app);
        string note = $"E2E lifecycle {type} {Tag()}";
        await using var s = await app.SessionAsync(viewport, E2EEnv.OwnerB, E2EEnv.CompanyB, test: $"lifecycle_{type}");
        await s.GoAsync($"/accounting/vouchers?type={type}");
        var p = s.Page;
        await p.Locator("#notes").FillAsync(note);
        if (type == "CPV")
        {
            await VoucherUi.PickAsync(p, p.Locator("#cbSearch"), "Main Cash");
            await VoucherUi.FillLineAsync(p, 0, "5-04-01-01-0001", amount: "700", description: "Courier");
            await VoucherUi.SubmitAndWaitAsync(s, p.Locator("#saveBtn"), @"/accounting/vouchers/\d+$");
        }
        else
        {
            await VoucherUi.FillLineAsync(p, 0, "5-04-01-01-0001", debit: "700", description: "Courier");
            await p.Locator("#addRowBtn").ClickAsync();
            await VoucherUi.FillLineAsync(p, 1, "4-01-01-01-0001", credit: "700", description: "Recharge");
            // Save & Approve on a new journal: the in-page confirm, then straight to the ledger.
            await p.Locator("#approveBtn").ClickAsync();
            string ask = await VoucherUi.ConfirmAsync(p);
            Assert.Contains("general ledger", ask);
            await p.WaitForURLAsync(new System.Text.RegularExpressions.Regex(@"/accounting/vouchers/\d+$"));
            await s.SettleAsync();
            s.AssertNoProblems("after save & approve");
        }
        int id = VoucherUi.IdFrom(p.Url);
        await Expect(p.Locator(".app-flash-success")).ToBeVisibleAsync();

        if (type == "CPV")
        {
            // A saved draft opens ready to edit: no Edit step.
            Assert.Equal("unapproved", AccountingSeed.Voucher(id)!.Status);
            Assert.Empty(AccountingSeed.PostedJournal(type, id));
            await Expect(p.Locator("#cbSearch")).ToBeEnabledAsync();
            await Expect(p.Locator(".amt-input").First).ToBeEnabledAsync();
            await Expect(p.Locator("a[href*='edit=1']")).ToHaveCountAsync(0);
            await Expect(p.Locator(".vchip")).ToHaveTextAsync("Draft");
            Assert.Equal("700", await p.Locator(".amt-input").First.InputValueAsync());
            await p.Locator("#approveBtn").ClickAsync();
            Assert.Contains("general ledger", await VoucherUi.ConfirmAsync(p));
            await p.WaitForSelectorAsync(".vlock");
            await s.SettleAsync();
            s.AssertNoProblems("after approve");
        }
        await Expect(p.Locator(".vlock")).ToBeVisibleAsync();
        await Expect(p.Locator(".vchip")).ToHaveTextAsync("Approved");
        await s.AssertFitsAsync();
        var v = AccountingSeed.Voucher(id)!;
        Assert.Equal("approved", v.Status);
        Assert.NotNull(v.ApprovedAt);
        var journal = AccountingSeed.PostedJournal(type, id);
        Assert.Equal(2, journal.Count);
        Assert.Equal(700m, journal.Sum(l => l.Dr));
        Assert.Equal(700m, journal.Sum(l => l.Cr));
        Assert.Contains(journal, l => l.Account == d.Expense && l.Dr == 700m);
        Assert.Contains(journal, l => l.Account == (type == "CPV" ? d.Cash : d.Revenue) && l.Cr == 700m);

        // Unapprove from More: confirm, the journal comes off, the voucher is a draft again.
        await p.Locator(".vmore-btn").ClickAsync();
        var unapprove = p.Locator(".vmenu button:has-text('Unapprove')");
        await Expect(unapprove).ToBeVisibleAsync();
        await unapprove.ClickAsync();
        Assert.Contains("reversed", await VoucherUi.ConfirmPostAsync(s));
        await Expect(p.Locator(".vchip")).ToHaveTextAsync("Draft");
        Assert.Equal("unapproved", AccountingSeed.Voucher(id)!.Status);
        Assert.Empty(AccountingSeed.PostedJournal(type, id));

        // Delete the draft from More.
        await p.Locator(".vmore-btn").ClickAsync();
        await p.Locator(".vmenu button:has-text('Delete draft')").ClickAsync();
        await VoucherUi.ConfirmPostAsync(s);
        Assert.EndsWith("/accounting/vouchers/list", p.Url);
        await Expect(p.Locator(".app-flash-success")).ToContainTextAsync("deleted");
        Assert.Null(AccountingSeed.Voucher(id));
        Assert.Empty(s.Dialogs);
        await s.AssertCleanAsync();
    }

    // ── writes from page scripts carry the antiforgery token (the app shell's fetch/XHR wrapper) ──

    [E2EFact]
    public async Task Fetch_and_xhr_posts_carry_the_antiforgery_header_and_a_post_without_it_is_refused()
    {
        var d = await AccountingSeed.EnsureAsync(app);
        int a = await VoucherUi.SaveDraftViaHttpAsync(app, "CPV", $"E2E fetch {Tag()}", 321m, d.Cash, d.Expense);
        int b = await VoucherUi.SaveDraftViaHttpAsync(app, "CPV", $"E2E xhr {Tag()}", 123m, d.Cash, d.Expense);
        await using var s = await app.SessionAsync(Viewport.Desktop, E2EEnv.OwnerB, E2EEnv.CompanyB);
        await s.GoAsync("/accounting/vouchers/list");

        // Signed in, same cookies, but no token: refused, nothing changes.
        var refused = await s.Context.APIRequest.PostAsync($"/accounting/vouchers/{a}/approve",
            new() { Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json", ["Accept"] = "application/json" }, Data = "{}" });
        Assert.Equal(400, refused.Status);
        Assert.Equal("unapproved", AccountingSeed.Voucher(a)!.Status);

        // fetch() from the page: JSON body, the shell adds X-CSRF-Token. Approve posts the journal...
        const string post = """
            async (url) => {
              const r = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' }, body: '{}' });
              return r.status;
            }
            """;
        Assert.Equal(200, await s.Page.EvaluateAsync<int>(post, $"/accounting/vouchers/{a}/approve"));
        Assert.Equal("approved", AccountingSeed.Voucher(a)!.Status);
        Assert.Equal(2, AccountingSeed.PostedJournal("CPV", a).Count);
        // ...unapprove takes it off, delete removes the draft.
        Assert.Equal(200, await s.Page.EvaluateAsync<int>(post, $"/accounting/vouchers/{a}/unapprove"));
        Assert.Empty(AccountingSeed.PostedJournal("CPV", a));
        Assert.Equal(200, await s.Page.EvaluateAsync<int>(post, $"/accounting/vouchers/{a}/delete"));
        Assert.Null(AccountingSeed.Voucher(a));

        // XMLHttpRequest gets the header too.
        int status = await s.Page.EvaluateAsync<int>("""
            (url) => new Promise((resolve) => {
              const x = new XMLHttpRequest();
              x.open('POST', url);
              x.setRequestHeader('Content-Type', 'application/json');
              x.onload = () => resolve(x.status);
              x.onerror = () => resolve(-1);
              x.send('{}');
            })
            """, $"/accounting/vouchers/{b}/delete");
        Assert.Equal(200, status);
        Assert.Null(AccountingSeed.Voucher(b));
        await s.AssertCleanAsync();
    }

    [E2EFact]
    public async Task A_person_without_approve_rights_cannot_approve_from_the_page()
    {
        var d = await AccountingSeed.EnsureAsync(app);
        // "restricted": journal_vouchers view + create only, in company B: no rights on cash payments.
        int jv = await VoucherUi.SaveDraftViaHttpAsync(app, "CPV", $"E2E rights {Tag()}", 50m, d.Cash, d.Expense);
        await using var s = await app.SessionAsync(Viewport.Desktop, E2EEnv.Restricted, E2EEnv.CompanyB);
        await s.GoAsync("/accounting/vouchers?type=JV");
        s.ExpectedConsoleErrors.Add("403");
        int status = await s.Page.EvaluateAsync<int>(
            "async (url) => (await fetch(url, { method: 'POST', headers: { Accept: 'application/json' } })).status",
            $"/accounting/vouchers/{jv}/approve");
        Assert.Equal(403, status);
        Assert.Equal("unapproved", AccountingSeed.Voucher(jv)!.Status);
        await s.AssertCleanAsync();
        // Tidy up as the owner.
        var c = await app.HttpAsync(E2EEnv.OwnerB, E2EEnv.CompanyB);
        await c.PostFormAsync($"/accounting/vouchers/{jv}/delete", [], "/accounting/vouchers/list");
    }
}
