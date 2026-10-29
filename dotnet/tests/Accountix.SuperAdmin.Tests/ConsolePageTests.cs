using System.Net;
using Accountix.TestSupport;

namespace Accountix.SuperAdmin.Tests;

/// <summary>Every console page: who may open it, what it shows, how many queries it costs.</summary>
public class ConsolePageTests
{
    public static readonly string[] Pages =
    [
        "/superadmin/", "/superadmin/users/", "/superadmin/companies/", "/superadmin/my-companies/",
        "/superadmin/requests/", "/superadmin/audit/",
    ];

    [ReferenceFact]
    public async Task Every_page_opens_for_the_super_admin_in_the_console_layout()
    {
        await using var host = await S.HostAsync();
        var c = S.Super(host);
        foreach (string url in Pages.Append($"/superadmin/users/{S.SuperAdmin}/").Append("/superadmin/companies/1/"))
        {
            var r = await c.GetAsync(url);
            string html = await S.BodyAsync(r);
            Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url}: {(int)r.StatusCode}\n{html[..Math.Min(html.Length, 2000)]}");
            Assert.Contains("class=\"sa-header\"", html);
            Assert.Contains("id=\"saNav\"", html);
            Assert.Contains("href=\"/auth/logout\" class=\"sa-logout-btn\"", html);
            Assert.Contains("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">", html);
            Assert.DoesNotContain("class=\"sidebar\"", html);          // not the module app shell
            // The shared antiforgery script (E2E F4: form.submit() fires no submit event).
            Assert.Contains("HTMLFormElement.prototype.submit = function", html);
        }
        string login = await host.AnonymousClient().GetStringAsync("/superadmin/login");
        Assert.Contains("HTMLFormElement.prototype.submit = function", login);
        Assert.Contains("<meta name=\"csrf-token\"", login);
    }

    [ReferenceFact]
    public async Task Pages_carry_what_the_python_tests_look_for()
    {
        await using var host = await S.HostAsync();
        var c = S.Super(host);
        string users = await c.GetStringAsync("/superadmin/users/");
        Assert.Contains("Create User", users);
        Assert.Contains("admin@gmail.com", users);
        Assert.Contains("class=\"active\">Users</a>", users);

        // My Books is a console section, linked from the console's own nav.
        string my = await c.GetStringAsync("/superadmin/my-companies/");
        Assert.Contains("Create Company", my);
        string index = await c.GetStringAsync("/superadmin/");
        Assert.Contains("href=\"/superadmin/my-companies/\"", index);
        Assert.Contains("Global Limits", index);

        // The console runs outside any company: no way into the ERP hub.
        foreach (string url in new[] { "/superadmin/", "/superadmin/users/", "/superadmin/companies/" })
            Assert.DoesNotContain("href=\"/dashboard/\"", await c.GetStringAsync(url));
    }

    /// <summary>PORTING_GUIDE "data inside &lt;script&gt;": no page's script carries HTML-encoded text.</summary>
    [ReferenceFact]
    public async Task Script_blocks_carry_no_html_encoded_text()
    {
        await using var host = await S.HostAsync();
        var c = S.Super(host);
        var pages = Pages.Append($"/superadmin/users/{S.SuperAdmin}/").Append("/superadmin/companies/1/").ToList();
        var anon = host.AnonymousClient();
        foreach (var (client, url) in pages.Select(p => (c, p)).Append((anon, "/superadmin/login")))
        {
            string html = await client.GetStringAsync(url);
            var scripts = System.Text.RegularExpressions.Regex.Matches(html, "<script>(.*?)</script>", System.Text.RegularExpressions.RegexOptions.Singleline);
            Assert.NotEmpty(scripts);
            foreach (System.Text.RegularExpressions.Match m in scripts)
            {
                Assert.DoesNotContain("&quot;", m.Groups[1].Value);
                Assert.DoesNotContain("&amp;", m.Groups[1].Value);
                Assert.DoesNotContain("&#x", m.Groups[1].Value);
            }
        }
    }

    [ReferenceFact]
    public async Task A_member_who_is_not_a_super_admin_gets_403()
    {
        await using var host = await S.HostAsync();
        var emp = host.ClientFor(S.Employee, 1);
        foreach (string url in Pages)
        {
            var r = await emp.GetAsync(url);
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Contains("You don't have permission to view this page.", S.Text(await S.BodyAsync(r)));
        }
        // Writes too (with a valid token from a page the person can open: the console door).
        string token = await emp.TokenFromAsync("/superadmin/login");
        var post = await emp.PostFormWithTokenAsync("/superadmin/users/",
            [new("email", $"x.{S.Tag()}@ref.test"), new("password", S.Pw)], token);
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
    }

    [ReferenceFact]
    public async Task A_signed_out_visit_goes_to_the_consoles_own_door_with_next()
    {
        await using var host = await S.HostAsync();
        var anon = host.AnonymousClient();
        var r = await anon.GetAsync("/superadmin/users/?x=a%20b&y=1");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        // Exactly what Flask-Login writes (python: /superadmin/login?next=%2Fsuperadmin%2Fusers%2F%3Fx%3Da%2520b%26y%3D1).
        Assert.Equal("/superadmin/login?next=%2Fsuperadmin%2Fusers%2F%3Fx%3Da%2520b%26y%3D1", S.Location(r));
        string login = S.Text(await anon.GetStringAsync(S.Location(r)));
        Assert.Contains("Please log in to access this page.", login);
        Assert.Contains("id=\"login\"", login);
        Assert.Contains("id=\"password\"", login);
    }

    [ReferenceFact]
    public async Task Missing_records_are_404()
    {
        await using var host = await S.HostAsync();
        var c = S.Super(host);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/superadmin/companies/999999/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/superadmin/users/999999/")).StatusCode);
        string token = await c.TokenFromAsync("/superadmin/");
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostFormWithTokenAsync("/superadmin/users/999999/toggle-super", [], token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostFormWithTokenAsync("/superadmin/requests/999999/approve", [], token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostFormWithTokenAsync("/superadmin/companies/1/members/999999/block", [], token)).StatusCode);
    }

    [ReferenceFact]
    public async Task Pages_run_a_fixed_number_of_queries_whatever_the_number_of_rows()
    {
        await using var host = await S.HostAsync();
        var c = S.Super(host);
        var before = new Dictionary<string, int>();
        foreach (string url in Pages.Append("/superadmin/companies/3/").Append($"/superadmin/users/{S.SuperAdmin}/"))
            before[url] = (await host.MeasureAsync(c, url, runs: 1)).Within(maxMilliseconds: 5000, maxQueries: 40).Queries;

        // More companies, members and users: the counts must not move.
        for (int i = 0; i < 3; i++)
        {
            int u = await S.NewUserAsync();
            int co = await S.NewCompanyAsync($"Count Co {S.Tag()}", createdBy: u);
            await S.AddMembershipAsync(co, u, "admin");
            await S.AddMembershipAsync(3, u);
            await S.AddMembershipAsync(co, S.SuperAdmin);
        }
        foreach (var (url, n) in before)
            Assert.Equal(n, (await host.MeasureAsync(c, url, runs: 1)).Queries);
    }
}
