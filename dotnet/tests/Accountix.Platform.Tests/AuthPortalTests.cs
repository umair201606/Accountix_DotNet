using System.Net;
using System.Text.RegularExpressions;
using Accountix.Core.Entities;
using Accountix.Platform.Provisioning;
using Accountix.Platform.Services;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Accountix.Platform.Tests;

/// <summary>Sign-in, sign-out, landing, sign-up, portal, hub and company switch (test_portal.py, test_account_security.py, test_access_fixes.py, test_multicompany.py).</summary>
public class AuthPortalTests
{
    internal static async Task<string> FormTokenAsync(PageClient c, string url)
    {
        string html = await (await c.GetAsync(url)).Content.ReadAsStringAsync();
        var m = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
        if (!m.Success) m = Regex.Match(html, "<meta name=\"csrf-token\" content=\"([^\"]+)\"");
        Assert.True(m.Success, $"no antiforgery token on {url}");
        return WebUtility.HtmlDecode(m.Groups[1].Value);
    }

    internal static async Task<HttpResponseMessage> LoginAsync(PageClient c, string login, string password, string url = "/auth/login", string? ip = null)
    {
        string token = await FormTokenAsync(c, url);
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent([new("login", login), new("password", password), new("__RequestVerificationToken", token)]),
        };
        if (ip is not null) req.Headers.Add("X-Forwarded-For", ip);
        return await c.SendAsync(req);
    }

    /// <summary>A new person who is a member of company B (employee), with the reference password.</summary>
    internal static async Task<(int Id, string Email)> MemberOfBAsync(ModuleHost host, string role = Role.Employee)
    {
        string email = $"m.{P.Tag()}@ref.test";
        int id = await P.NewUserAsync(email);
        var (scope, db) = P.Scope(host);
        using (scope)
        {
            int roleId = await db.Roles.Where(r => r.Name == role).Select(r => r.Id).FirstAsync();
            await scope.ServiceProvider.GetRequiredService<Memberships>().AddAsync(P.CompanyB, id, roleId);
        }
        return (id, email);
    }

    internal static async Task<int> CompanyOwnedByAsync(ModuleHost host, int ownerId, string? name = null)
    {
        var (scope, db) = P.Scope(host);
        using (scope)
        {
            string tag = P.Tag();
            var c = new Company { Name = name ?? "Co " + tag, Slug = "co-" + tag, IsActive = true, PlanName = "free", CreatedBy = ownerId, CreatedAt = DateTime.UtcNow };
            db.Companies.Add(c);
            await db.SaveChangesAsync();
            int roleId = await db.Roles.Where(r => r.Name == Role.Admin).Select(r => r.Id).FirstAsync();
            await scope.ServiceProvider.GetRequiredService<Memberships>().AddAsync(c.Id, ownerId, roleId);
            await scope.ServiceProvider.GetRequiredService<CompanySetup>().ProvisionAsync(c.Id);
            return c.Id;
        }
    }

    private static string Ip() => $"10.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}";

    [ReferenceFact]
    public async Task Landing_is_public_and_sends_signed_in_people_to_the_portal()
    {
        await using var host = await P.HostAsync();
        var anon = host.AnonymousClient();
        var r = await anon.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        string html = await r.Content.ReadAsStringAsync();
        Assert.Contains("href=\"/auth/login\"", html);
        Assert.Contains("href=\"/signup\"", html);
        Assert.DoesNotContain("/superadmin", html);           // the operator door is never linked
        var signed = host.ClientFor(P.OwnerB, P.CompanyB);
        var r2 = await signed.GetAsync("/");
        Assert.Equal("/portal/", r2.Headers.Location?.OriginalString);
        var adm = await anon.GetAsync("/admin");
        Assert.True("/superadmin/login" == adm.Headers.Location?.OriginalString, $"{(int)adm.StatusCode} {adm.Headers.Location} {(await adm.Content.ReadAsStringAsync())[..Math.Min(500, (int)(adm.Content.Headers.ContentLength ?? 0))]}");
    }

    [ReferenceFact]
    public async Task Signing_in_opens_a_server_session_and_lands_on_the_portal()
    {
        await using var host = await P.HostAsync();
        var (uid, email) = await MemberOfBAsync(host);
        var c = host.AnonymousClient();
        var r = await LoginAsync(c, email.ToUpperInvariant(), P.Password, ip: Ip());
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/portal/", r.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/portal/")).StatusCode);

        await using var db = P.Db();
        Assert.Equal(1, await db.UserSessions.CountAsync(s => s.UserId == uid && s.EndedAt == null));
        var login = await db.AuditLogs.Where(a => a.UserId == uid && a.Action == "login").SingleAsync();
        Assert.Null(login.CompanyId);
        Assert.EndsWith(" signed in", login.Summary);

        // The ax_session cookie names that row and opens the portal in a second browser too.
        string cookie = c.Cookies.GetCookies(new Uri("http://localhost/"))[TestSignIn.CookieName]!.Value;
        var copy = host.AnonymousClient();
        copy.Cookies.Add(new Uri("http://localhost/"), new Cookie(TestSignIn.CookieName, cookie));
        Assert.Equal(HttpStatusCode.OK, (await copy.GetAsync("/portal/")).StatusCode);
    }

    [ReferenceFact]
    public async Task Sign_in_never_redirects_off_site_but_follows_a_local_next()
    {
        await using var host = await P.HostAsync();
        var (_, email) = await MemberOfBAsync(host);
        var c1 = host.AnonymousClient();
        var r1 = await LoginAsync(c1, email, P.Password, "/auth/login?next=https://evil.example/x", Ip());
        Assert.Equal("/portal/", r1.Headers.Location?.OriginalString);
        var c2 = host.AnonymousClient();
        var r2 = await LoginAsync(c2, email, P.Password, "/auth/login?next=%2F%2Fevil.example", Ip());
        Assert.Equal("/portal/", r2.Headers.Location?.OriginalString);
        var c3 = host.AnonymousClient();
        var r3 = await LoginAsync(c3, email, P.Password, "/auth/login?next=%2Fsettings%2F%3Ftab%3Daccount", Ip());
        Assert.Equal("/settings/?tab=account", r3.Headers.Location?.OriginalString);
    }

    [ReferenceFact]
    public async Task A_super_admin_is_refused_at_the_customer_door_like_a_wrong_password()
    {
        await using var host = await P.HostAsync();
        string email = $"sa.{P.Tag()}@ref.test";
        int sa = await P.NewUserAsync(email, superAdmin: true);
        var r = await LoginAsync(host.AnonymousClient(), email, P.Password, ip: Ip());
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Invalid email or password.", await r.Content.ReadAsStringAsync());
        await using var db = P.Db();
        Assert.True(await db.AuditLogs.AnyAsync(a => a.UserId == sa && a.Action == "login_failed" && a.Summary!.Contains("super admin must use the console")));
        Assert.False(await db.UserSessions.AnyAsync(s => s.UserId == sa));
    }

    [ReferenceFact]
    public async Task Repeated_wrong_passwords_lock_that_login_from_that_address()
    {
        await using var host = await P.HostAsync();
        var (_, email) = await MemberOfBAsync(host);
        string ip = Ip();
        for (int i = 0; i < 10; i++)
        {
            var bad = await LoginAsync(host.AnonymousClient(), email, "wrong-password-" + i, ip: ip);
            Assert.Contains("Invalid email or password.", await bad.Content.ReadAsStringAsync());
        }
        var locked = await LoginAsync(host.AnonymousClient(), email, P.Password, ip: ip);
        Assert.Equal((HttpStatusCode)429, locked.StatusCode);
        Assert.Contains("Too many failed sign-in attempts", await locked.Content.ReadAsStringAsync());
        // Another address still gets in (the per-login limit is 50).
        var other = await LoginAsync(host.AnonymousClient(), email, P.Password, ip: Ip());
        Assert.Equal(HttpStatusCode.Redirect, other.StatusCode);
        await using var db = P.Db();
        var fails = await db.AuditLogs.Where(a => a.Reference == email.ToLowerInvariant() && a.Action == "login_failed").ToListAsync();
        Assert.Equal(11, fails.Count);
        Assert.All(fails, f => Assert.DoesNotContain("wrong-password", f.Summary + f.Changes));   // never the password
    }

    [ReferenceFact]
    public async Task Signing_out_ends_a_copied_session_too_and_a_link_on_another_site_cannot_sign_you_out()
    {
        await using var host = await P.HostAsync();
        var (uid, email) = await MemberOfBAsync(host);
        var c = host.AnonymousClient();
        await LoginAsync(c, email, P.Password, ip: Ip());
        string cookie = c.Cookies.GetCookies(new Uri("http://localhost/"))[TestSignIn.CookieName]!.Value;

        var cross = new HttpRequestMessage(HttpMethod.Get, "/auth/logout");
        cross.Headers.Add("Sec-Fetch-Site", "cross-site");
        Assert.Equal("/portal/", (await c.SendAsync(cross)).Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/portal/")).StatusCode);

        var r = await c.GetAsync("/auth/logout");
        Assert.Equal("/auth/login", r.Headers.Location?.OriginalString);
        var copy = host.AnonymousClient();
        copy.Cookies.Add(new Uri("http://localhost/"), new Cookie(TestSignIn.CookieName, cookie));
        var after = await copy.GetAsync("/portal/");
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.StartsWith("/auth/login", after.Headers.Location?.OriginalString);
        await using var db = P.Db();
        Assert.Equal("signed_out", (await db.UserSessions.SingleAsync(s => s.UserId == uid)).EndReason);
        Assert.True(await db.AuditLogs.AnyAsync(a => a.UserId == uid && a.Action == "logout"));
    }

    [ReferenceFact]
    public async Task Two_sign_ins_at_once_each_get_their_own_session_row()
    {
        await using var host = await P.HostAsync();
        var (uid, email) = await MemberOfBAsync(host);
        var clients = Enumerable.Range(0, 6).Select(_ => host.AnonymousClient()).ToArray();
        var tokens = await Task.WhenAll(clients.Select(c => FormTokenAsync(c, "/auth/login")));
        var rs = await Race.RunAsync(6, i => clients[i].SendAsync(new HttpRequestMessage(HttpMethod.Post, "/auth/login")
        {
            Content = new FormUrlEncodedContent([new("login", email), new("password", P.Password), new("__RequestVerificationToken", tokens[i])]),
        }));
        Race.NoServerErrors(rs);
        Assert.All(rs, r => Assert.Equal(HttpStatusCode.Redirect, r.StatusCode));
        await using var db = P.Db();
        var rows = await db.UserSessions.Where(s => s.UserId == uid).ToListAsync();
        Assert.Equal(6, rows.Count);
        Assert.Equal(6, rows.Select(s => s.TokenHash).Distinct().Count());
    }

    [ReferenceFact]
    public async Task Portal_shows_created_assigned_and_invitations_and_the_hub_opens_the_company()
    {
        await using var host = await P.HostAsync();
        var (uid, email) = await MemberOfBAsync(host);
        int own = await CompanyOwnedByAsync(host, uid, "Own Books " + P.Tag());
        await using (var db = P.Db())
        {
            int roleId = await db.Roles.Where(r => r.Name == Role.Manager).Select(r => r.Id).FirstAsync();
            db.Invitations.Add(new CompanyInvitation { CompanyId = P.CompanyA, Email = email.ToUpperInvariant(), RoleId = roleId, Status = "sent", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var c = host.ClientFor(uid, P.CompanyB);
        host.Queries.Reset();
        var r = await c.GetAsync("/portal/");
        int q = host.Queries.Count;
        string html = await r.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True(q <= 40, $"portal used {q} queries");
        Assert.Contains("Own Books", html);
        Assert.Contains("You own 1 of 3 companies.", html);
        Assert.Contains($"href=\"/company/switch/{own}?next=%2Fdashboard%2F\"", html);
        Assert.Contains("badge-employee", html);
        Assert.Contains("badge-manager", html);   // the invitation, matched case-insensitively
        Assert.Contains("/settings/invitations/", html);

        var sw = await c.GetAsync($"/company/switch/{own}?next=%2Fdashboard%2F");
        Assert.Equal("/dashboard/", sw.Headers.Location?.OriginalString);
        host.Queries.Reset();
        var hub = await c.GetAsync("/dashboard/");
        Assert.True(host.Queries.Count <= 40, $"hub used {host.Queries.Count} queries");
        string hh = await hub.Content.ReadAsStringAsync();
        Assert.Contains("Own Books", hh);
        Assert.Contains("8 modules", hh);
        Assert.Contains("href=\"/accounting/dashboard\"", hh);

        // A company this person is not in: refused, nothing switched.
        var foreign = await c.GetAsync($"/company/switch/{P.CompanyA}");
        Assert.Equal("/dashboard/", foreign.Headers.Location?.OriginalString);
        string hub2 = await (await c.GetAsync("/dashboard/")).Content.ReadAsStringAsync();
        Assert.Contains("Own Books", hub2);
    }

    [ReferenceFact]
    public async Task An_employee_sees_only_the_hr_tile_and_a_companyless_person_goes_to_the_portal()
    {
        await using var host = await P.HostAsync();
        var (uid, _) = await MemberOfBAsync(host);
        await using (var db = P.Db())
        {
            var m = await db.Memberships.SingleAsync(x => x.UserId == uid);
            m.SetModuleFlags(new Dictionary<string, bool> { ["hr"] = true });
            await db.SaveChangesAsync();
        }
        string hub = await (await host.ClientFor(uid, P.CompanyB).GetAsync("/dashboard/")).Content.ReadAsStringAsync();
        Assert.True(hub.Contains("People &amp; HR"), hub[..Math.Min(hub.Length, 3000)]);
        Assert.DoesNotContain("/accounting/dashboard", hub);
        Assert.Contains("Your account &amp; preferences", hub);

        int loner = await P.NewUserAsync();
        var c = host.ClientFor(loner);
        Assert.Equal("/portal/", (await c.GetAsync("/dashboard/")).Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/portal/")).StatusCode);
    }

    [ReferenceFact]
    public async Task Creating_a_company_provisions_its_books_and_enforces_quota_slug_and_uniqueness()
    {
        await using var host = await P.HostAsync();
        int uid = await P.NewUserAsync();
        await using (var db = P.Db())
        {
            var u = await db.Users.SingleAsync(x => x.Id == uid);
            u.MaxCompaniesOwned = 1;
            await db.SaveChangesAsync();
        }
        var c = host.ClientFor(uid);
        string slug = "pc-" + P.Tag();
        async Task<HttpResponseMessage> Create(string name, string s) =>
            await c.PostFormWithTokenAsync("/portal/create", [new("name", name), new("slug", s)], await FormTokenAsync(c, "/portal/"));
        Assert.Equal("/portal/", (await Create("Bad", "Bad Slug!")).Headers.Location?.OriginalString);
        var ok = await Create("Portal Made", slug);
        Assert.Equal("/dashboard/", ok.Headers.Location?.OriginalString);
        await using (var db = P.Db())
        {
            var co = await db.Companies.SingleAsync(x => x.Slug == slug);
            Assert.Equal(uid, co.CreatedBy);
            var m = await db.Memberships.Include(x => x.Role).SingleAsync(x => x.CompanyId == co.Id);
            Assert.Equal(Role.Admin, m.Role.Name);
            db.Company.Set(co.Id);
            Assert.Equal(161, await db.Set<ChartOfAccount>().CountAsync());
            Assert.Equal(14, await db.Set<VoucherNumber>().CountAsync());
            Assert.True(await db.Set<AccountingPeriod>().AnyAsync());
        }
        string hub = await (await c.GetAsync("/dashboard/")).Content.ReadAsStringAsync();
        Assert.Contains("Portal Made", hub);
        // Taken address, then the quota.
        await Create("Again", slug);
        await Create("Second", "pc2-" + P.Tag());
        await using (var db = P.Db())
            Assert.Equal(1, await db.Companies.CountAsync(x => x.CreatedBy == uid));
    }

    [ReferenceFact]
    public async Task Sign_up_files_a_request_and_refuses_duplicates_weak_passwords_and_blocked_details()
    {
        await using var host = await P.HostAsync();
        var c = host.AnonymousClient();
        string email = $"su.{P.Tag()}@ref.test";
        async Task<string> Post(string mail, string pw, string phone = "") =>
            await (await c.PostFormWithTokenAsync("/signup",
                [new("full_name", "Sign Up"), new("email", mail), new("password", pw), new("phone", phone), new("company_name", ""), new("notes", "")],
                await FormTokenAsync(c, "/signup"))).Content.ReadAsStringAsync();
        Assert.Contains("Password must be at least 8 characters.", await Post(email, "short"));
        Assert.Contains("That password is too common", await Post(email, "password123"));
        Assert.Contains("Request submitted", await Post(email, "Long-enough-9"));
        Assert.Contains("already pending review", await Post(email.ToUpperInvariant(), "Long-enough-9"));
        Assert.Contains("\"exists\":true", await (await c.GetAsync($"/signup/check-email?email={email}")).Content.ReadAsStringAsync());
        await using (var db = P.Db())
        {
            var req = await db.Set<RegistrationRequest>().SingleAsync(r => r.Email == email);
            Assert.True(Accountix.Core.Security.PasswordHasher.Verify(req.PasswordHash, "Long-enough-9"));
            req.Status = "blocked";
            await db.SaveChangesAsync();
        }
        Assert.Contains("already been reviewed", await Post(email, "Long-enough-9"));
    }

    [ReferenceFact]
    public async Task Sign_in_writes_its_own_cookie_over_the_session_table_and_an_old_flask_cookie_means_nothing()
    {
        await using var host = await P.HostAsync();
        var (uid, email) = await MemberOfBAsync(host);
        var c = host.AnonymousClient();
        var r = await LoginAsync(c, email, P.Password, ip: Ip());
        Assert.Equal("/portal/", r.Headers.Location?.OriginalString);
        var jar = c.Cookies.GetCookies(new Uri("http://localhost/"));
        Assert.NotNull(jar["ax_session"]);
        Assert.Null(jar["session"]);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/portal/")).StatusCode);
        Assert.Equal("/dashboard/", (await c.GetAsync($"/company/switch/{P.CompanyB}")).Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/dashboard/")).StatusCode);
        // The Python app's cookie ("session", itsdangerous) is not read at all: its holder signs in once.
        var old = host.AnonymousClient();
        old.Cookies.Add(new Uri("http://localhost/"), new Cookie("session",
            ".eJwlj0tLAzEURv9L1i6S3EeS2RWrDFSk4ELopuRxo7PoOJ0HgxX_u21df-fA-X7UsY4yfapmHhd5UMeuqEZ5k4XIkCPndcUUA9hUqDojWRMEx0ii2UFKJYspCFEokWCJLMBQTY4ebdaVINXosqkMLkHIQRJHS5IREQzWIDXbgj4YDGKhGMvCSV1DpnvJOr9E63ftxxKH7b59H-bXx9P5sPb77vK2drsn6C_n9nmzbTc3aZlk_L_gGus4FKKgLVkuQV_3_HUaYv99J-D3D0cWSj4.auLdXA.31RoSUD13qd_B58K0dxHpe6YRjo"));
        Assert.Equal(HttpStatusCode.Redirect, (await old.GetAsync("/portal/")).StatusCode);
        await c.GetAsync("/auth/logout");
        Assert.Equal(HttpStatusCode.Redirect, (await c.GetAsync("/portal/")).StatusCode);
        await using var db = P.Db();
        Assert.Equal("signed_out", (await db.UserSessions.Where(s => s.UserId == uid && s.EndReason != null).OrderBy(s => s.Id).LastAsync()).EndReason);
    }

    /// <summary>Every page's script blocks are real JavaScript (no HTML-encoded quotes) and every layout carries the antiforgery token.</summary>
    internal static void AssertScriptsClean(string url, string html)
    {
        foreach (Match m in Regex.Matches(html, "<script(?![^>]*src)[^>]*>(.*?)</script>", RegexOptions.Singleline))
            Assert.False(m.Groups[1].Value.Contains("&quot;") || m.Groups[1].Value.Contains("&amp;") || m.Groups[1].Value.Contains("&#x27;"),
                $"{url}: HTML-encoded text inside <script>:\n{m.Groups[1].Value[..Math.Min(400, m.Groups[1].Value.Length)]}");
        Assert.Contains("<meta name=\"csrf-token\" content=\"", html);
    }

    [ReferenceFact]
    public async Task Every_platform_layout_carries_the_antiforgery_token_and_clean_scripts()
    {
        await using var host = await P.HostAsync();
        var anon = host.AnonymousClient();
        foreach (string url in new[] { "/", "/auth/login", "/signup" })
            AssertScriptsClean(url, await (await anon.GetAsync(url)).Content.ReadAsStringAsync());
        var (cid, admin) = await P.FreshCompanyAsync(host);
        var c = host.ClientFor(admin, cid);
        var pages = new List<string> { "/portal/", "/dashboard/", $"/settings/users/{admin}/rights" };
        pages.AddRange(new[] { "account", "company", "periods", "reports", "cash_flow", "labels", "inventory", "rights", "members", "audit", "invites",
                               "purchase", "sales", "invoicing", "templates" }.Select(t => $"/settings/?tab={t}"));
        foreach (string url in pages)
        {
            var r = await c.GetAsync(url);
            Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url}: {(int)r.StatusCode}");
            AssertScriptsClean(url, await r.Content.ReadAsStringAsync());
        }
    }
}
