using Accountix.E2E.Harness;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Accountix.E2E.Accounting;

/// <summary>
/// Every Accounting page at 1366, 375, 360 and 320 px: it loads, nothing errors
/// in the console or on the server, nothing is cut off or scrolls sideways, and
/// its main content is there.
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class AccountingPagesTests(AccountingApp app) : IClassFixture<AccountingApp>
{
    private static readonly string Month = AccountingSeed.CompanyNow.ToString("yyyy-MM");

    /// <summary>Page key → (path, a selector that must be visible).</summary>
    private static (string Path, string Visible) Resolve(string key, AccountingSeed.Data d) => key switch
    {
        "dashboard" => ("/accounting/", ".content-area"),
        "dashboard-alias" => ("/accounting/dashboard", ".content-area"),
        "new-cpv" => ("/accounting/vouchers?type=CPV", "#cbSearch"),
        "new-brv" => ("/accounting/vouchers?type=BRV", "#cbSearch"),
        "new-jv" => ("/accounting/vouchers?type=JV", "#sumDr"),
        "draft-voucher" => ($"/accounting/vouchers/{d.DraftId}", "#approveBtn"),
        "approved-voucher" => ($"/accounting/vouchers/{d.ApprovedId}", ".vlock"),
        "voucher-list" => ("/accounting/vouchers/list", ".voucher-table"),
        "voucher-preview" => ($"/accounting/vouchers/{d.ApprovedId}/preview", "body"),
        "register" => ("/accounting/registers/vouchers", ".content-area"),
        "register-month" => ($"/accounting/registers/vouchers?mode=month&month={Month}&view=1", ".content-area"),
        "register-documents" => ($"/accounting/registers/vouchers/documents?mode=month&month={Month}&view=1", "body"),
        "coa" => ("/accounting/coa/", "#coaSearch"),
        "coa-add" => ("/accounting/coa/add", "#acctName"),
        "coa-edit" => ($"/accounting/coa/{d.Bank}/edit", "input[name=name]"),
        "bank-rec-list" => ("/accounting/bank-reconciliation", "select[name=account_id]"),
        "bank-rec" => ($"/accounting/bank-reconciliation/{d.RecId}", "#br-diff"),
        _ => throw new ArgumentOutOfRangeException(nameof(key), key),
    };

    public static readonly string[] Pages =
    [
        "dashboard", "dashboard-alias", "new-cpv", "new-brv", "new-jv", "draft-voucher", "approved-voucher", "voucher-list",
        "voucher-preview", "register", "register-month", "register-documents", "coa", "coa-add", "coa-edit",
        "bank-rec-list", "bank-rec",
    ];

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (string page in Pages)
            foreach (var vp in Viewport.All)
                if (KnownIssues.For($"{page}:{vp.Name}") is null)
                    data.Add(page, vp.Name);
        return data;
    }

    /// <summary>The cases an open finding blocks (finding id first).</summary>
    public static TheoryData<string, string, string> Blocked()
    {
        var data = new TheoryData<string, string, string>();
        foreach (string page in Pages)
            foreach (var vp in Viewport.All)
                if (KnownIssues.For($"{page}:{vp.Name}") is { } f)
                    data.Add(f, page, vp.Name);
        if (data.Count == 0)
            data.Add("none", "", "");   // a theory needs a row; nothing is blocked
        return data;
    }

    [KnownIssueTheory]
    [MemberData(nameof(Blocked))]
    public Task Blocked_page_renders_cleanly_and_fits(string finding, string page, string viewport) =>
        finding == "none" ? Task.CompletedTask : Page_renders_cleanly_and_fits(page, viewport);

    [E2ETheory]
    [MemberData(nameof(Cases))]
    public async Task Page_renders_cleanly_and_fits(string page, string viewport)
    {
        var d = await AccountingSeed.EnsureAsync(app);
        var (path, visible) = Resolve(page, d);
        await using var s = await app.SessionAsync(Viewport.Named(viewport), E2EEnv.OwnerB, E2EEnv.CompanyB, test: "page_" + page);
        await s.GoAsync(path);
        await Expect(s.Page.Locator(visible).First).ToBeVisibleAsync();
        if (s.Viewport.IsPhone && path.StartsWith("/accounting/") && !path.EndsWith("/preview") && !path.Contains("/documents"))
        {
            // The shell on a phone: the sidebar is off screen until the menu button opens it.
            await Expect(s.Page.Locator("#sidebar-toggle")).ToBeVisibleAsync();
            Assert.False(await s.Page.Locator("#sidebar").EvaluateAsync<bool>("el => el.getBoundingClientRect().right > 4"),
                "the sidebar covers the page on a phone");
        }
        await s.AssertCleanAsync();
    }
}
