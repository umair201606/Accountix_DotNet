using Accountix.Core.Entities;
using Accountix.E2E.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Accountix.E2E.Modules;

/// <summary>
/// The super admin console in the browser: its sign-in door (the Python suite's
/// admin_page fixture signs in exactly here), refusals, every console page at the four
/// widths, the theme switch on its own layout, and its POST forms carrying the
/// antiforgery token (F4: the console had its own copy of the token script).
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class SuperAdminTests(FullApp app) : IClassFixture<FullApp>
{
    private const string AdminEmail = "admin@gmail.com", AdminPassword = "admin123";   // the Python seed
    private static int JohnDoe => JohnId.Value;

    private static readonly Lazy<int> JohnId = new(() =>
    {
        using var db = TestSupport.Reference.NewDb();
        return db.Users.Where(u => u.Email == "john.doe@solarkon.com").Select(u => u.Id).First();
    });

    [E2ETheory("SuperAdmin")]
    [InlineData("desktop")]
    [InlineData("320")]
    public async Task The_console_door_signs_a_super_admin_in(string viewport)
    {
        await using var s = await app.SessionAsync(Viewport.Named(viewport), null, test: "sa_login");
        var p = s.Page;
        await s.GoAsync("/superadmin/login");
        await Expect(p.Locator("[data-theme-toggle]")).ToBeVisibleAsync();
        await p.Locator("#login").FillAsync(AdminEmail);
        await p.Locator("#password").FillAsync(AdminPassword);
        await Accounting.VoucherUi.SubmitAndWaitAsync(s, p.Locator("button[type='submit']"), @"/superadmin/$");
        await Expect(p.Locator("body")).ToContainTextAsync("Save Limits");
        var cookies = await s.Context.CookiesAsync();
        Assert.Contains(cookies, c => c.Name == Accountix.TestSupport.TestSignIn.CookieName);   // the ax_session sign-in cookie
        await s.AssertCleanAsync();
    }

    [E2EFact("SuperAdmin")]
    public async Task Wrong_credentials_and_people_without_the_flag_are_refused()
    {
        await using (var s = await app.SessionAsync(Viewport.Desktop, null, test: "sa_refused"))
        {
            var p = s.Page;
            await s.GoAsync("/superadmin/login");
            await p.Locator("#login").FillAsync(AdminEmail);
            await p.Locator("#password").FillAsync("not-the-password");
            await p.Locator("button[type='submit']").ClickAsync();
            await p.WaitForLoadStateAsync(LoadState.Load);
            await Expect(p.Locator("body")).ToContainTextAsync("Those credentials do not open the super admin console.");
            Assert.Contains("/superadmin/login", p.Url);
            s.AssertNoProblems();
            // Signed out, the console sends you to its door.
            await s.GoAsync("/superadmin/users/");
            Assert.Contains("/superadmin/login", p.Url);
            await s.AssertCleanAsync();
        }
        // Signed in without the flag: the friendly 403.
        await using (var s = await app.SessionAsync(Viewport.Desktop, E2EEnv.OwnerB, E2EEnv.CompanyB, test: "sa_forbidden"))
        {
            await s.GoAsync("/superadmin/", expectStatus: 403);
            await s.AssertCleanAsync();
        }
    }

    public static TheoryData<string, string> ConsolePages()
    {
        var data = new TheoryData<string, string>();
        foreach (string page in new[] { "/superadmin/", "/superadmin/users/", "/superadmin/users/{john}/", "/superadmin/companies/",
                                        "/superadmin/companies/1/", "/superadmin/my-companies/", "/superadmin/requests/", "/superadmin/audit/" })
            foreach (var vp in Viewport.All)
                data.Add(page, vp.Name);
        return data;
    }

    [E2ETheory("SuperAdmin")]
    [MemberData(nameof(ConsolePages))]
    public async Task Console_page_renders_cleanly_and_fits(string page, string viewport)
    {
        string path = page.Replace("{john}", JohnDoe.ToString());
        await using var s = await app.SessionAsync(Viewport.Named(viewport), E2EEnv.SuperAdmin, E2EEnv.DefaultCompany,
            test: "sa_" + new string(page.Where(char.IsLetter).ToArray()));
        await s.GoAsync(path);
        await Expect(s.Page.Locator("body")).Not.ToBeEmptyAsync();
        await s.AssertCleanAsync();
    }

    [E2EFact("SuperAdmin")]
    public async Task Console_forms_post_with_the_token_limits_and_a_users_active_switch()
    {
        await using var s = await app.SessionAsync(Viewport.Phone360, E2EEnv.SuperAdmin, E2EEnv.DefaultCompany);
        var p = s.Page;
        await s.GoAsync("/superadmin/");
        // Saving the limits unchanged: accepted (a missing token would be a 400).
        await Accounting.VoucherUi.SubmitAndWaitAsync(s, p.GetByRole(AriaRole.Button, new() { Name = "Save Limits" }), @"/superadmin/");

        // Switch John Doe off and on again from his page.
        int john = JohnDoe;
        await s.GoAsync($"/superadmin/users/{john}/");
        var active = p.Locator("form:has(input[name=action][value=active]) button");
        await Accounting.VoucherUi.SubmitAndWaitAsync(s, active, $@"/superadmin/users/{john}/");
        Assert.False(IsActive(john));
        await Accounting.VoucherUi.SubmitAndWaitAsync(s, p.Locator("form:has(input[name=action][value=active]) button"), $@"/superadmin/users/{john}/");
        Assert.True(IsActive(john));
        await s.AssertCleanAsync();
    }

    [E2EFact("SuperAdmin")]
    public async Task The_console_has_a_working_theme_switch()
    {
        await using var s = await app.SessionAsync(Viewport.Desktop, E2EEnv.SuperAdmin, E2EEnv.DefaultCompany, ColorScheme.Light);
        var p = s.Page;
        await s.GoAsync("/superadmin/");
        var btn = p.Locator("[data-theme-toggle]");
        await Expect(btn).ToHaveCountAsync(1);
        await btn.ClickAsync();
        Assert.Equal("dark", await p.EvaluateAsync<string?>("() => document.documentElement.getAttribute('data-theme-effective')"));
        await s.GoAsync("/superadmin/users/");
        Assert.Equal("dark", await p.EvaluateAsync<string?>("() => document.documentElement.getAttribute('data-theme-effective')"));
        await s.AssertCleanAsync();
    }

    private static bool IsActive(int user)
    {
        using var db = TestSupport.Reference.NewDb();
        return db.Users.AsNoTracking().Single(u => u.Id == user).IsActive == true;
    }
}
