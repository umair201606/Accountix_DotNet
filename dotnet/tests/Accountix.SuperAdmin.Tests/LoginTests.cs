using System.Net;
using Accountix.Core.Entities;
using Accountix.Core.Security;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.SuperAdmin.Tests;

/// <summary>/superadmin/login: super admins only, the same throttle and refusals as Python, a server session.</summary>
public class LoginTests
{
    private const string AdminPassword = "admin123";

    /// <summary>A client from its own address (the throttle counts per address too).</summary>
    private static PageClient From(ModuleHost host, out string ip)
    {
        var c = host.AnonymousClient();
        ip = $"10.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}";
        c.DefaultRequestHeaders.Add("X-Forwarded-For", ip);
        return c;
    }

    private static Task<HttpResponseMessage> SignInAsync(PageClient c, string login, string password, string url = "/superadmin/login") =>
        S.PostAsync(c, url, "/superadmin/login", ("login", login), ("password", password));

    [ReferenceFact]
    public async Task The_super_admin_enters_the_console_with_a_server_session_and_an_audit_row()
    {
        await using var host = await S.HostAsync();
        var c = From(host, out string ip);
        var started = DateTime.UtcNow.AddSeconds(-5);
        var r = await SignInAsync(c, "  ADMIN@gmail.com ", AdminPassword);   // email, any case, stripped
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/superadmin/", S.Location(r));

        // The ax_session cookie is the sign-in.
        var cookie = c.Cookies.GetCookies(new Uri("http://localhost/"))[SessionCookie.CookieName];
        Assert.NotNull(cookie);
        var session = SessionCookie.Read(cookie!.Value, Reference.SecretKey, DateTimeOffset.UtcNow);
        Assert.NotNull(session);
        Assert.Null(session!["company_id"]);                          // a fresh session
        Assert.Equal(S.SuperAdmin, SessionCookie.UserOf(session)!.Value.UserId);

        string page = await S.FollowAsync(c, r);
        Assert.Contains("Welcome back,", page);
        Assert.Contains("Global Limits", page);

        await using var db = S.Db();
        var row = await db.UserSessions.AsNoTracking().Where(s => s.UserId == S.SuperAdmin && s.CreatedAt >= started)
            .OrderByDescending(s => s.Id).FirstAsync();
        Assert.Null(row.EndedAt);
        Assert.Equal(ip, row.IpAddress);
        var audit = await db.AuditLogs.AsNoTracking().Where(a => a.Action == "login" && a.UserId == S.SuperAdmin && a.CreatedAt >= started)
            .OrderByDescending(a => a.Id).FirstAsync();
        Assert.Null(audit.CompanyId);                                  // a platform event
        Assert.Equal("/superadmin/login", audit.Path);
        Assert.Equal(ip, audit.IpAddress);
        Assert.StartsWith("Super admin ", audit.Summary);
        Assert.EndsWith(" entered the console", audit.Summary);
    }

    [ReferenceFact]
    public async Task Next_is_followed_only_inside_the_console()
    {
        await using var host = await S.HostAsync();
        var c = From(host, out _);
        var r = await SignInAsync(c, "admin@gmail.com", AdminPassword, "/superadmin/login?next=%2Fsuperadmin%2Faudit%2F%3Fq%3Dx");
        Assert.Equal("/superadmin/audit/?q=x", S.Location(r));
        var c2 = From(host, out _);
        Assert.Equal("/superadmin/", S.Location(await SignInAsync(c2, "admin@gmail.com", AdminPassword, "/superadmin/login?next=/portal/")));
        var c3 = From(host, out _);
        Assert.Equal("/superadmin/", S.Location(await SignInAsync(c3, "admin@gmail.com", AdminPassword, "/superadmin/login?next=//evil.example/superadmin")));
        // Already in: the door sends the super admin on.
        Assert.Equal("/superadmin/", S.Location(await c.GetAsync("/superadmin/login")));
    }

    [ReferenceFact]
    public async Task A_correct_password_without_the_flag_is_refused_like_a_wrong_one()
    {
        await using var host = await S.HostAsync();
        string email = $"tenant.{S.Tag()}@ref.test";
        int uid = await S.NewUserAsync(email);
        var started = DateTime.UtcNow.AddSeconds(-5);
        foreach (var (login, pw) in new[] { (email, S.Pw), (email, "wrong-password"), ($"nobody.{S.Tag()}@ref.test", S.Pw) })
        {
            var c = From(host, out _);
            var r = await SignInAsync(c, login, pw);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Contains("Those credentials do not open the super admin console.", S.Text(await S.BodyAsync(r)));
            Assert.Null(c.Cookies.GetCookies(new Uri("http://localhost/"))[SessionCookie.CookieName]);
        }
        await using var db = S.Db();
        var fails = await db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == "login_failed" && a.CreatedAt >= started && a.Summary!.StartsWith("Failed console sign-in for "))
            .ToListAsync();
        Assert.Equal(2, fails.Count(a => a.Reference == email && a.UserId == uid));
        Assert.All(fails, a => Assert.Null(a.CompanyId));
    }

    [ReferenceFact]
    public async Task An_inactive_super_admin_is_refused()
    {
        await using var host = await S.HostAsync();
        string email = $"sleepy.{S.Tag()}@ref.test";
        int uid = await S.NewUserAsync(email, superAdmin: true);
        await using (var db = S.Db())
            await db.Users.Where(u => u.Id == uid).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        var r = await SignInAsync(From(host, out _), email, S.Pw);
        Assert.Contains("Those credentials do not open the super admin console.", S.Text(await S.BodyAsync(r)));
        // An active one with the flag gets in.
        string other = $"awake.{S.Tag()}@ref.test";
        await S.NewUserAsync(other, superAdmin: true);
        Assert.Equal("/superadmin/", S.Location(await SignInAsync(From(host, out _), other, S.Pw)));
    }

    [ReferenceFact]
    public async Task Too_many_failures_are_throttled_with_429_before_the_password_is_looked_at()
    {
        await using var host = await S.HostAsync();
        string email = $"target.{S.Tag()}@ref.test";
        int uid = await S.NewUserAsync(email, superAdmin: true);
        var c = From(host, out string ip);
        // Ten failures for this login from this address in the window (LOGIN_MAX_FAILS_PER_LOGIN_FROM_IP).
        for (int i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.OK, (await SignInAsync(c, email, "wrong-" + i)).StatusCode);
        var r = await SignInAsync(c, email, S.Pw);              // even the right password now
        Assert.Equal((HttpStatusCode)429, r.StatusCode);
        Assert.Contains("Too many failed sign-in attempts. Wait 15 minutes, then try again.", S.Text(await S.BodyAsync(r)));
        await using var db = S.Db();
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "login_failed" && a.Reference == email && a.IpAddress == ip
                                                     && a.Summary == $"Console sign-in throttled for '{email}'" && a.UserId == uid));
        // Another address is not held up by this one's failures.
        Assert.Equal("/superadmin/", S.Location(await SignInAsync(From(host, out _), email, S.Pw)));
    }
}
