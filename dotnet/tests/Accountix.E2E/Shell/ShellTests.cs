using Accountix.E2E.Harness;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Accountix.E2E.Shell;

/// <summary>
/// The app shell every module page renders in (Web.Shared _AppShell): the theme
/// switch (tests/e2e/test_theme_toggle.py), the sidebar on phones and desktop, the
/// company switcher and flash messages. Switching company (Platform) and the sidebar
/// accordion (needs a module with sidebar groups) are in Modules/PlatformTests and
/// Modules/InventoryTests.
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class ShellTests(AccountingApp app) : IClassFixture<AccountingApp>
{
    private static Task<string?> EffectiveAsync(IPage p) =>
        p.EvaluateAsync<string?>("() => document.documentElement.getAttribute('data-theme-effective')");

    [E2EFact]
    public async Task Theme_switch_goes_dark_and_the_choice_follows_into_the_app()
    {
        await using var s = await app.SessionAsync(Viewport.Desktop, E2EEnv.OwnerB, E2EEnv.CompanyB, ColorScheme.Light);
        var p = s.Page;
        await s.GoAsync("/accounting/");
        var btn = p.Locator("[data-theme-toggle]");
        await Expect(btn).ToHaveCountAsync(1);
        await Expect(btn).ToBeVisibleAsync();
        Assert.Equal("light", await EffectiveAsync(p));
        await Expect(btn.Locator(".t-light")).ToBeVisibleAsync();   // the moon: what you would move to
        await btn.ClickAsync();
        Assert.Equal("dark", await EffectiveAsync(p));
        Assert.Equal("dark", await p.EvaluateAsync<string?>("() => localStorage.getItem('ax-theme')"));
        Assert.Equal("true", await btn.GetAttributeAsync("aria-pressed"));
        // The body really is dark (the shell sets its own background through the tokens).
        string bg = await p.EvaluateAsync<string>("() => getComputedStyle(document.body).backgroundColor");
        Assert.True(Brightness(bg) < 100, $"body background {bg} is not dark");
        // Another page keeps it, before first paint.
        await s.GoAsync("/accounting/coa/");
        Assert.Equal("dark", await EffectiveAsync(p));
        await btn.ClickAsync();
        Assert.Equal("light", await EffectiveAsync(p));
        await s.AssertCleanAsync();
    }

    [E2EFact]
    public async Task On_an_os_dark_page_the_first_click_goes_light()
    {
        await using var s = await app.SessionAsync(Viewport.Phone360, E2EEnv.OwnerB, E2EEnv.CompanyB, ColorScheme.Dark);
        var p = s.Page;
        await s.GoAsync("/accounting/vouchers/list");
        Assert.Equal("dark", await EffectiveAsync(p));
        await Expect(p.Locator("[data-theme-toggle] .t-dark")).ToBeVisibleAsync();   // a sun on a dark page
        await p.Locator("[data-theme-toggle]").ClickAsync();
        Assert.Equal("light", await EffectiveAsync(p));
        Assert.Equal("light", await p.EvaluateAsync<string?>("() => document.documentElement.getAttribute('data-theme')"));
        await s.AssertFitsAsync();
        await s.AssertCleanAsync();
    }

    [E2ETheory]
    [InlineData("320")]
    [InlineData("375")]
    public async Task On_a_phone_the_menu_button_opens_the_sidebar_and_the_overlay_closes_it(string viewport)
    {
        var vp = Viewport.Named(viewport);
        await using var s = await app.SessionAsync(vp, E2EEnv.OwnerB, E2EEnv.CompanyB, test: "sidebar");
        var p = s.Page;
        await s.GoAsync("/accounting/vouchers/list");
        var sidebar = p.Locator("#sidebar");
        Assert.True(await sidebar.EvaluateAsync<double>("el => el.getBoundingClientRect().right") <= 4, "sidebar should start off screen");
        await p.Locator("#sidebar-toggle").ClickAsync();
        await Expect(sidebar).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bopen\b"));
        await Expect(p.Locator("#sidebar-overlay")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bshow\b"));
        // Fully on screen once the slide finishes, no wider than the phone.
        await p.WaitForFunctionAsync("() => { const r = document.getElementById('sidebar').getBoundingClientRect(); return r.left >= -1 && r.right <= innerWidth + 1; }");
        // The module's pages are there, and the current one is marked.
        var active = sidebar.Locator(".nav-item.active");
        await Expect(active).ToHaveCountAsync(1);
        await Expect(active).ToContainTextAsync("View Vouchers");
        await Expect(sidebar.Locator(".nav-item", new() { HasTextString = "Chart of Accounts" })).ToBeVisibleAsync();
        await Expect(sidebar.Locator(".sidebar-footer a[href='/auth/logout']")).ToBeVisibleAsync();
        // Tapping the dimmed page closes it (the overlay sits right of the 240px sidebar).
        await p.Mouse.ClickAsync(vp.Width - 10, vp.Height / 2f);
        await Expect(sidebar).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bopen\b"));
        await s.AssertFitsAsync();

        // A sidebar link navigates.
        await p.Locator("#sidebar-toggle").ClickAsync();
        await sidebar.Locator(".nav-item", new() { HasTextString = "Chart of Accounts" }).ClickAsync();
        await p.WaitForURLAsync("**/accounting/coa/");
        await s.SettleAsync();
        await s.AssertFitsAsync();
        await s.AssertCleanAsync();
    }

    [E2EFact]
    public async Task On_a_desktop_the_menu_button_collapses_the_sidebar()
    {
        await using var s = await app.SessionAsync(Viewport.Desktop, E2EEnv.OwnerB, E2EEnv.CompanyB);
        var p = s.Page;
        await s.GoAsync("/accounting/");
        var sidebar = p.Locator("#sidebar");
        await Expect(sidebar).ToBeVisibleAsync();
        await p.Locator("#sidebar-toggle").ClickAsync();
        await Expect(sidebar).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bcollapsed\b"));
        await p.Locator("#sidebar-toggle").ClickAsync();
        await Expect(sidebar).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bcollapsed\b"));
        await s.AssertCleanAsync();
    }

    private static readonly string[] SwitcherSizes = ["desktop", "375", "360", "320"];

    public static TheoryData<string> SwitcherWidths()
    {
        var data = new TheoryData<string>();
        foreach (string n in SwitcherSizes)
            if (KnownIssues.For($"switcher:{n}") is null)
                data.Add(n);
        return data;
    }

    public static TheoryData<string, string> BlockedSwitcherWidths()
    {
        var data = new TheoryData<string, string>();
        foreach (string n in SwitcherSizes)
            if (KnownIssues.For($"switcher:{n}") is { } f)
                data.Add(f, n);
        if (data.Count == 0)
            data.Add("none", "");
        return data;
    }

    [KnownIssueTheory]
    [MemberData(nameof(BlockedSwitcherWidths))]
    public Task Blocked_company_switcher_lists_the_persons_companies(string finding, string viewport) =>
        finding == "none" ? Task.CompletedTask : Company_switcher_lists_the_persons_companies(viewport);

    [E2ETheory]
    [MemberData(nameof(SwitcherWidths))]
    public async Task Company_switcher_lists_the_persons_companies(string viewport)
    {
        var vp = Viewport.Named(viewport);
        // owner_a: admin of A, employee in B.
        await using var s = await app.SessionAsync(vp, E2EEnv.OwnerA, E2EEnv.CompanyA, test: "switcher");
        var p = s.Page;
        await s.GoAsync("/accounting/vouchers/list");
        var menu = p.Locator("#company-switch-menu");
        await Expect(menu).ToBeHiddenAsync();
        await p.Locator("#company-switch-btn").ClickAsync();
        await Expect(menu).ToBeVisibleAsync();
        Assert.Equal("true", await p.Locator("#company-switch-btn").GetAttributeAsync("aria-expanded"));
        var links = menu.Locator("a[href^='/company/switch/']");
        await Expect(links).ToHaveCountAsync(2);
        await Expect(menu.Locator($"a.active[href^='/company/switch/{E2EEnv.CompanyA}?']")).ToHaveCountAsync(1);
        await Expect(menu.Locator($"a[href^='/company/switch/{E2EEnv.CompanyB}?']")).ToHaveCountAsync(1);
        string? href = await menu.Locator($"a[href^='/company/switch/{E2EEnv.CompanyB}?']").GetAttributeAsync("href");
        Assert.EndsWith("next=%2Faccounting%2Fvouchers%2Flist", href);   // back to this page in the other company
        await Expect(menu.Locator("a[href='/portal/']")).ToBeVisibleAsync();
        // Fully on screen, even at 320 px.
        var box = await menu.BoundingBoxAsync();
        Assert.True(box!.X >= 0 && box.X + box.Width <= vp.Width + 1, $"switcher menu off screen: x={box.X} w={box.Width}");
        if (vp.IsPhone)
            await Expect(p.Locator(".company-switch-name")).ToBeHiddenAsync();
        // A click elsewhere closes it.
        await p.Locator(".page-title").ClickAsync();
        await Expect(menu).ToBeHiddenAsync();
        await s.AssertCleanAsync();
    }

    /// <summary>
    /// F4's regression: a POST form submitted from script (form.submit(), which fires no
    /// submit event) carries the antiforgery token; a form posting to another site never does.
    /// </summary>
    [E2EFact]
    public async Task A_form_submitted_from_script_carries_the_token_and_a_foreign_form_never_does()
    {
        var d = await Accounting.AccountingSeed.EnsureAsync(app);
        int draft = await Accounting.VoucherUi.SaveDraftViaHttpAsync(app, "CPV", $"E2E script submit {Guid.NewGuid():N}", 77m, d.Cash, d.Expense);
        await using var s = await app.SessionAsync(Viewport.Desktop, E2EEnv.OwnerB, E2EEnv.CompanyB);
        var p = s.Page;
        string? foreignBody = null;
        await s.Context.RouteAsync("https://foreign.example/**", async route =>
        {
            foreignBody = route.Request.PostData ?? "";
            await route.FulfillAsync(new() { Status = 200, ContentType = "text/html", Body = "<p>foreign</p>" });
        });
        await s.GoAsync("/accounting/");

        // Same site: form.submit() → accepted (redirect to the list), the draft is gone.
        var answer = p.WaitForResponseAsync(r => r.Request.Method == "POST");
        await p.EvaluateAsync("""
            (id) => { const f = document.createElement('form'); f.method = 'post'; f.action = '/accounting/vouchers/' + id + '/delete';
                      document.body.appendChild(f); setTimeout(() => f.submit(), 0); }
            """, draft);
        var r = await answer;
        Assert.Equal(302, r.Status);
        await p.WaitForURLAsync("**/accounting/vouchers/list");
        await s.SettleAsync();
        Assert.Null(Accounting.AccountingSeed.Voucher(draft));

        // Another site: the token stays home, whichever way the form is sent.
        foreach (string how in new[] { "f.submit()", "f.requestSubmit()" })
        {
            foreignBody = null;
            await p.EvaluateAsync($$"""
                () => { const f = document.createElement('form'); f.method = 'post'; f.action = 'https://foreign.example/collect';
                        const i = document.createElement('input'); i.name = 'x'; i.value = '1'; f.appendChild(i);
                        document.body.appendChild(f); setTimeout(() => {{how}}, 0); }
                """);
            await p.WaitForURLAsync("https://foreign.example/collect");
            Assert.NotNull(foreignBody);
            Assert.DoesNotContain("__RequestVerificationToken", foreignBody);
            await s.GoAsync("/accounting/");
        }
        // fetch() to another site carries no X-CSRF-Token either.
        string? foreignHeader = "unset";
        await s.Context.RouteAsync("https://foreign.example/api", async route =>
        {
            foreignHeader = route.Request.Headers.TryGetValue("x-csrf-token", out var h) ? h : null;
            await route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = "{}", Headers = new Dictionary<string, string> { ["Access-Control-Allow-Origin"] = "*", ["Access-Control-Allow-Headers"] = "*" } });
        });
        await p.EvaluateAsync("async () => { try { await fetch('https://foreign.example/api', { method: 'POST', body: '{}' }); } catch (e) {} }");
        Assert.Null(foreignHeader);
        await s.AssertCleanAsync();
    }

    private static double Brightness(string rgb)
    {
        var m = System.Text.RegularExpressions.Regex.Matches(rgb, @"\d+(\.\d+)?");
        if (m.Count < 3) return 255;
        double r = double.Parse(m[0].Value), g = double.Parse(m[1].Value), b = double.Parse(m[2].Value);
        return 0.299 * r + 0.587 * g + 0.114 * b;
    }
}
