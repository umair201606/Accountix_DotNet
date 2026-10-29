using Accountix.Core.Entities;
using Accountix.E2E.Harness;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Accountix.E2E.Accounting;

/// <summary>
/// The chart of accounts in the browser: the tree (search, collapse, phone layout,
/// ARIA), and an account's life through the forms — add under a parent picked level
/// by level (the code preview), edit, delete — plus the guards (fixed accounts).
/// Ports test_voucher_ui.py's COA test and the UI side of test_coa_delete_guard.py.
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class CoaTests(AccountingApp app) : IClassFixture<AccountingApp>
{
    [E2EFact]
    public async Task Coa_tree_fits_a_phone_and_its_toggles_report_their_state()
    {
        await using var s = await app.SessionAsync(Viewport.Phone375, E2EEnv.OwnerB, E2EEnv.CompanyB);
        await s.GoAsync("/accounting/coa/");
        var p = s.Page;
        await Expect(p.Locator(".tree-header")).ToBeHiddenAsync();
        await Expect(p.GetByLabel("Search accounts")).ToBeVisibleAsync();
        var toggles = p.Locator(".tree-row button.tw:not(.leafpad)");
        Assert.True(await toggles.CountAsync() > 0);
        var first = toggles.First;
        string? before = await first.GetAttributeAsync("aria-expanded");
        Assert.NotNull(before);
        int visibleBefore = await p.Locator(".tree-row:visible").CountAsync();
        await first.ClickAsync();
        Assert.NotEqual(before, await first.GetAttributeAsync("aria-expanded"));
        // Collapsing the first class hides its descendants.
        Assert.True(await p.Locator(".tree-row:visible").CountAsync() < visibleBefore);
        await first.ClickAsync();
        Assert.Equal(before, await first.GetAttributeAsync("aria-expanded"));
        await s.AssertFitsAsync();
        await s.AssertCleanAsync();
    }

    [E2EFact]
    public async Task Search_filters_the_tree_and_expand_collapse_all_work()
    {
        await using var s = await app.SessionAsync(Viewport.Desktop, E2EEnv.OwnerB, E2EEnv.CompanyB);
        await s.GoAsync("/accounting/coa/");
        var p = s.Page;
        int all = await p.Locator(".tree-row").CountAsync();
        int shown = await p.Locator(".tree-row:visible").CountAsync();
        Assert.True(all > 50, $"only {all} accounts");
        // The tree opens with posting accounts folded under their level-4 heads.
        Assert.True(shown < all, "level-5 detail should start folded");
        Assert.Equal(0, await p.Locator(".tree-row[data-level='5']:visible").CountAsync());
        await p.Locator("#coaSearch").FillAsync("Main Cash");
        await Expect(p.Locator(".tree-row:visible .acct-name", new() { HasTextString = "Main Cash" })).ToBeVisibleAsync();
        int filtered = await p.Locator(".tree-row:visible").CountAsync();
        Assert.True(filtered < all && filtered >= 1, $"search left {filtered} of {all} rows");
        await p.Locator("#coaSearch").FillAsync("1-01-01-01-0001");
        await Expect(p.Locator(".tree-row:visible .acct-code", new() { HasTextString = "1-01-01-01-0001" })).ToBeVisibleAsync();
        await p.Locator("#coaSearch").FillAsync("");
        Assert.Equal(shown, await p.Locator(".tree-row:visible").CountAsync());   // back to the tree as it was
        await p.Locator("#expandAll").ClickAsync();
        Assert.Equal(all, await p.Locator(".tree-row:visible").CountAsync());
        await p.Locator("#collapseAll").ClickAsync();
        int collapsed = await p.Locator(".tree-row:visible").CountAsync();
        Assert.True(collapsed < shown, $"collapse all left {collapsed} rows");
        await s.AssertCleanAsync();
    }

    [E2ETheory]
    [InlineData("desktop")]
    [InlineData("320")]
    public async Task An_account_is_added_level_by_level_then_edited_and_deleted(string viewport)
    {
        await TidyAsync();
        string name = $"E2E Petty {Guid.NewGuid().ToString("N")[..6]}";
        await using var s = await app.SessionAsync(Viewport.Named(viewport), E2EEnv.OwnerB, E2EEnv.CompanyB, test: "coa_add");
        s.AcceptDialogs = true;   // the tree's Delete asks with confirm(), as in Python
        var p = s.Page;
        await s.GoAsync("/accounting/coa/add");

        // Level 1 → 4 selects cascade through /accounting/coa/api/children; the code preview follows.
        await PickLevelAsync(p, "#sel1", "1 ");
        await PickLevelAsync(p, "#sel2", "1-01 ");
        await PickLevelAsync(p, "#sel3", "1-01-01 ");
        await PickLevelAsync(p, "#sel4", "1-01-01-01 ");
        await Expect(p.Locator("#codePreview")).ToBeVisibleAsync();
        await Expect(p.Locator("#cpCode")).ToContainTextAsync("1-01-01-01-");
        string code = (await p.Locator("#cpCode").InnerTextAsync()).Trim();
        await p.Locator("#acctName").FillAsync(name);
        await VoucherUi.SubmitAndWaitAsync(s, p.GetByRole(AriaRole.Button, new() { Name = "Create" }), @"/accounting/coa/$");
        await Expect(p.Locator(".app-flash-success")).ToContainTextAsync($"created as {code}");
        await s.AssertFitsAsync();   // the flash too: at the bottom on phones

        int id;
        using (var db = Db.For(E2EEnv.CompanyB))
        {
            var acct = db.Set<ChartOfAccount>().Single(a => a.Name == name);
            Assert.Equal(code, acct.Code);
            Assert.Equal(5, acct.Level);
            id = acct.Id;
        }
        // Posting accounts start folded: find it the way a person does, by searching.
        await p.Locator("#coaSearch").FillAsync(name);
        var row = p.Locator(".tree-row", new() { Has = p.Locator(".acct-name", new() { HasTextString = name }) });
        await Expect(row).ToBeVisibleAsync();

        // Edit: rename and switch it off.
        await VoucherUi.SubmitAndWaitAsync(s, row.Locator("a:has-text('Edit')"), $@"/accounting/coa/{id}/edit$");
        await p.Locator("input[name=name]").FillAsync(name + " (old)");
        await p.Locator("input[name=is_active]").UncheckAsync();
        await VoucherUi.SubmitAndWaitAsync(s, p.GetByRole(AriaRole.Button, new() { Name = "Update" }), @"/accounting/coa/$");
        await Expect(p.Locator(".app-flash-success")).ToBeVisibleAsync();
        using (var db = Db.For(E2EEnv.CompanyB))
        {
            var acct = db.Set<ChartOfAccount>().Single(a => a.Id == id);
            Assert.Equal(name + " (old)", acct.Name);
            Assert.False(acct.IsActive);
        }
        await p.Locator("#coaSearch").FillAsync(name);
        row = p.Locator(".tree-row", new() { Has = p.Locator(".acct-name", new() { HasTextString = name + " (old)" }) });
        await Expect(row.Locator(".chip-inactive")).ToBeVisibleAsync();

        // Delete (no postings, no children): asked with confirm(), then gone.
        await VoucherUi.SubmitAndWaitAsync(s, row.Locator("button.bsm-del"), @"/accounting/coa/$");
        Assert.Contains(s.Dialogs, d => d.StartsWith("confirm") && d.Contains(name));
        await Expect(p.Locator(".app-flash-success")).ToBeVisibleAsync();
        using (var db = Db.For(E2EEnv.CompanyB))
            Assert.False(db.Set<ChartOfAccount>().Any(a => a.Id == id));
        await s.AssertCleanAsync();
    }

    [E2EFact]
    public async Task A_fixed_account_cannot_be_renamed_or_deleted_from_the_page()
    {
        // Cash in Hand: part of the fixed chart.
        int fixedHead = Db.AccountId(E2EEnv.CompanyB, "1-01-01-01");
        await using var s = await app.SessionAsync(Viewport.Desktop, E2EEnv.OwnerB, E2EEnv.CompanyB);
        await s.GoAsync("/accounting/coa/");
        var row = s.Page.Locator(".tree-row[data-code='1-01-01-01']");
        await Expect(row.Locator(".chip-fixed")).ToBeVisibleAsync();
        await Expect(row.Locator("button.bsm-del")).ToHaveCountAsync(0);
        await s.GoAsync($"/accounting/coa/{fixedHead}/edit");
        Assert.NotNull(await s.Page.Locator("input[name=name]").GetAttributeAsync("readonly"));
        await s.AssertCleanAsync();
    }

    [E2EFact]
    public async Task An_empty_name_is_refused_with_a_flash_that_can_be_dismissed()
    {
        await using var s = await app.SessionAsync(Viewport.Phone320, E2EEnv.OwnerB, E2EEnv.CompanyB);
        await s.GoAsync("/accounting/coa/add");
        var p = s.Page;
        await PickLevelAsync(p, "#sel1", "1 ");
        await VoucherUi.SubmitAndWaitAsync(s, p.GetByRole(AriaRole.Button, new() { Name = "Create" }), @"/accounting/coa/add$");
        var flash = p.Locator(".app-flash-error");
        await Expect(flash).ToContainTextAsync("Account name is required.");
        // On a phone the flash sits at the bottom, inside the screen.
        var box = await flash.BoundingBoxAsync();
        Assert.True(box!.X >= 0 && box.X + box.Width <= Viewport.Phone320.Width, $"flash off screen: {box.X}+{box.Width}");
        await flash.Locator(".app-flash-x").ClickAsync();
        await Expect(p.Locator(".app-flash-error")).ToHaveCountAsync(0);
        await s.AssertCleanAsync();
    }

    /// <summary>Accounts an interrupted run left behind (no postings: deleted outright).</summary>
    private async Task TidyAsync()
    {
        List<int> left;
        using (var db = Db.For(E2EEnv.CompanyB))
            left = db.Set<ChartOfAccount>().Where(a => a.Name.StartsWith("E2E Petty ")).Select(a => a.Id).ToList();
        if (left.Count == 0)
            return;
        var c = await app.HttpAsync(E2EEnv.OwnerB, E2EEnv.CompanyB);
        foreach (int id in left)
            await c.PostFormAsync($"/accounting/coa/{id}/delete", [], "/accounting/coa/");
    }

    /// <summary>Selects the option whose label starts with a code ("1-01 — Current Assets").</summary>
    private static async Task PickLevelAsync(IPage p, string select, string codePrefix)
    {
        var sel = p.Locator(select);
        await Expect(sel).ToBeVisibleAsync();
        await Expect(sel.Locator("option", new() { HasTextRegex = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(codePrefix)) })).Not.ToHaveCountAsync(0);
        string value = await sel.EvaluateAsync<string>(
            "(el, prefix) => [...el.options].find(o => o.textContent.trim().startsWith(prefix)).value", codePrefix);
        var children = p.WaitForResponseAsync(r => r.Url.Contains("/accounting/coa/api/"));
        await sel.SelectOptionAsync(value);
        await children;
        await p.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }
}
