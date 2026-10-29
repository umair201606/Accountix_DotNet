using Accountix.E2E.Harness;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Accountix.E2E.Modules;

/// <summary>
/// The rest of AGENTS.md's lists. Platform (done 29 Oct): sign-in → hub → HR → sign-out,
/// public pages' theme switch, the hub on a phone, switching company
/// (tests/e2e/test_hr_pipeline.py, test_inventory_pipeline.py, test_theme_toggle.py).
/// Inventory (not done yet: these skip with the reason): dashboard, products, the stock
/// list, the sidebar accordion (test_voucher_v3_and_sidebar.py). Returns: the purchase /
/// sales return forms and lists.
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class PlatformInventoryReturnsTests(FullApp app) : IClassFixture<FullApp>
{
    private const string EmployeeEmail = "emp@solarkon.com", EmployeePassword = "emp123";   // the Python seed

    // ── Platform ──

    [E2ETheory("Platform")]
    [InlineData("/auth/login")]
    [InlineData("/")]
    public async Task Public_pages_load_with_a_floating_theme_switch(string path)
    {
        await using var s = await app.SessionAsync(Viewport.Phone320, null, colorScheme: ColorScheme.Light, test: "public");
        var p = s.Page;
        await s.GoAsync(path);
        var btn = p.Locator("[data-theme-toggle]");
        await Expect(btn).ToHaveCountAsync(1);
        await btn.ClickAsync();
        Assert.Equal("dark", await p.EvaluateAsync<string?>("() => document.documentElement.getAttribute('data-theme-effective')"));
        Assert.Equal("dark", await p.EvaluateAsync<string?>("() => localStorage.getItem('ax-theme')"));
        if (path == "/auth/login")
        {
            await Expect(p.Locator("#login")).ToBeVisibleAsync();
            await Expect(p.Locator("#password")).ToBeVisibleAsync();
            Assert.Equal("fixed", await btn.EvaluateAsync<string>("el => getComputedStyle(el).position"));
        }
        await s.AssertCleanAsync();
    }

    [E2EFact("Platform", "Hr")]
    public async Task An_employee_signs_in_reaches_the_hub_and_hr_and_signs_out()
    {
        await using var s = await app.SessionAsync(Viewport.Desktop, null, test: "signin");
        var p = s.Page;
        await s.GoAsync("/auth/login");
        await p.Locator("#login").FillAsync(EmployeeEmail);
        await p.Locator("#password").FillAsync(EmployeePassword);
        await p.Locator("button[type='submit']").ClickAsync();
        // One company and no invitations: straight to the hub; otherwise the portal first.
        await p.WaitForURLAsync(new System.Text.RegularExpressions.Regex(@"/(dashboard|portal)/"));
        await s.SettleAsync();
        s.AssertNoProblems("after sign-in");
        if (p.Url.Contains("/portal/"))
        {
            await p.Locator("a.enter").First.ClickAsync();
            await p.WaitForURLAsync("**/dashboard/**");
            await s.SettleAsync();
        }
        Assert.Contains("HR", await p.Locator("body").InnerTextAsync(), StringComparison.OrdinalIgnoreCase);
        await s.AssertFitsAsync();
        await s.GoAsync("/attendance/");
        await s.GoAsync("/auth/logout");
        Assert.Contains("/auth/login", p.Url);
        // The session is over: a module page sends you to sign in again.
        await s.GoAsync("/attendance/");
        Assert.Contains("/auth/login", p.Url);
        await s.AssertCleanAsync();
    }

    [E2EFact("Platform", "Hr")]
    public async Task Hub_shows_the_modules_and_fits_a_phone()
    {
        await using var s = await app.SessionAsync(Viewport.Phone375, E2EEnv.SuperAdmin, E2EEnv.DefaultCompany, test: "hub");
        await s.GoAsync("/dashboard/");
        Assert.Contains("HR", await s.Page.Locator("body").InnerTextAsync());
        await Expect(s.Page.Locator("[data-theme-toggle]")).ToHaveCountAsync(1);
        await s.AssertCleanAsync();
    }

    /// <summary>
    /// owner_a: admin of A (every module), employee in B (HR only). Switching on an HR page
    /// opens the same page in B's books; switching on an Accounting page lands on B's
    /// refusal, because B never gave her Accounting (per-company module access, Alembic 0016).
    /// </summary>
    [E2EFact("Platform", "Hr")]
    public async Task Switching_company_opens_the_same_page_in_the_other_companys_books()
    {
        await using var s = await app.SessionAsync(Viewport.Phone360, E2EEnv.OwnerA, E2EEnv.CompanyA, test: "switch");
        var p = s.Page;
        await s.GoAsync("/attendance/");
        await SwitchToAsync(p, E2EEnv.CompanyB);
        await p.WaitForURLAsync("**/attendance/");
        await s.SettleAsync();
        s.AssertNoProblems("after switching to B");
        await p.Locator("#company-switch-btn").ClickAsync();
        await Expect(p.Locator($"#company-switch-menu a.active[href^='/company/switch/{E2EEnv.CompanyB}?']")).ToHaveCountAsync(1);
        await p.Locator(".page-title").ClickAsync();   // a click elsewhere closes the menu
        await Expect(p.Locator("#company-switch-menu")).ToBeHiddenAsync();

        // Back to A on an Accounting page, then to B: B refuses Accounting.
        await SwitchToAsync(p, E2EEnv.CompanyA);
        await p.WaitForURLAsync("**/attendance/");
        await s.SettleAsync();
        await s.GoAsync("/accounting/vouchers/list");
        s.ExpectedConsoleErrors.Add("status of 403");
        await SwitchToAsync(p, E2EEnv.CompanyB);
        await p.WaitForURLAsync("**/accounting/vouchers/list");
        await s.SettleAsync();
        await Expect(p.Locator("body")).ToContainTextAsync("access to this module");
        await s.AssertCleanAsync();
    }

    private static async Task SwitchToAsync(IPage p, int company)
    {
        if (!await p.Locator("#company-switch-menu").IsVisibleAsync())
            await p.Locator("#company-switch-btn").ClickAsync();
        await p.Locator($"#company-switch-menu a[href^='/company/switch/{company}?']").ClickAsync();
    }

    // ── Inventory ──

    [E2ETheory("Inventory")]
    [InlineData("/inventory/dashboard", "desktop")]
    [InlineData("/inventory/dashboard", "375")]
    [InlineData("/inventory/products/", "desktop")]
    [InlineData("/inventory/products/", "375")]
    [InlineData("/inventory/stock/", "360")]
    public async Task Inventory_page_loads_and_fits(string path, string viewport)
    {
        await using var s = await app.SessionAsync(Viewport.Named(viewport), E2EEnv.SuperAdmin, E2EEnv.DefaultCompany, test: "inv");
        await s.GoAsync(path);
        Assert.NotEmpty(await s.Page.Locator("body").InnerTextAsync());
        await s.AssertCleanAsync();
    }

    /// <summary>The stock list's Value is a number, not "28,000" repeated qty times (Jinja filter precedence, fixed in Python).</summary>
    [E2EFact("Inventory")]
    public async Task Stock_value_is_a_number_not_a_repeated_string()
    {
        await using var s = await app.SessionAsync(Viewport.Desktop, E2EEnv.SuperAdmin, E2EEnv.DefaultCompany);
        await s.GoAsync("/inventory/stock/");
        var cells = await s.Page.Locator("table.table tbody tr td:nth-child(6)").AllInnerTextsAsync();
        Assert.NotEmpty(cells);
        foreach (string c in cells)
        {
            Assert.Matches(@"^-?[\d,]+(\.\d+)?$", c.Trim());
            Assert.True(c.Trim().Length < 20, c);
        }
        await s.AssertCleanAsync();
    }

    [E2EFact("Inventory")]
    public async Task Sidebar_groups_are_an_accordion()
    {
        await using var s = await app.SessionAsync(Viewport.Desktop, E2EEnv.SuperAdmin, E2EEnv.DefaultCompany);
        var p = s.Page;
        await s.GoAsync("/inventory/dashboard");
        await p.EvaluateAsync("() => { localStorage.setItem('sidebar_group_procurement','expanded'); localStorage.setItem('sidebar_group_sales','expanded'); }");
        await s.GoAsync("/inventory/customers/");
        var groups = p.Locator(".sidebar .nav-group");
        Assert.True(await groups.CountAsync() >= 2);
        // Old stored states open nothing: at most the active page's group is open.
        Assert.True(await p.Locator(".sidebar .nav-group.expanded").CountAsync() <= 1);
        var first = groups.Nth(0);
        var second = groups.Nth(1);
        await first.Locator(".nav-group-header").ClickAsync();
        if (!((await first.GetAttributeAsync("class")) ?? "").Contains("expanded"))
            await first.Locator(".nav-group-header").ClickAsync();
        await second.Locator(".nav-group-header").ClickAsync();
        Assert.Contains("expanded", await second.GetAttributeAsync("class") ?? "");
        Assert.DoesNotContain("expanded", await first.GetAttributeAsync("class") ?? "");
        Assert.Equal(1, await p.Locator(".sidebar .nav-group.expanded").CountAsync());
        Assert.Equal(0, await p.EvaluateAsync<int>("() => Object.keys(localStorage).filter(k => k.startsWith('sidebar_group_')).length"));
        await s.AssertCleanAsync();
    }

    // ── Returns ──

    [E2ETheory("Returns")]
    [InlineData("/inventory/purchase-return/", "desktop")]
    [InlineData("/inventory/purchase-return/", "320")]
    [InlineData("/inventory/purchase-return/list", "375")]
    [InlineData("/invoicing/sales-return/", "desktop")]
    [InlineData("/invoicing/sales-return/list", "360")]
    public async Task Return_page_loads_and_fits(string path, string viewport)
    {
        await using var s = await app.SessionAsync(Viewport.Named(viewport), E2EEnv.SuperAdmin, E2EEnv.DefaultCompany, test: "ret");
        await s.GoAsync(path);
        Assert.Contains("return", s.Page.Url, StringComparison.OrdinalIgnoreCase);
        await s.AssertCleanAsync();
    }
}
