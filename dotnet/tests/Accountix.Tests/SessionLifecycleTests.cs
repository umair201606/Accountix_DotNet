using System.Net;
using Accountix.Core.Security;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Tests;

/// <summary>
/// The ax_session cookie through a password change in HR (OwnSession): this
/// browser stays signed in with a re-issued cookie (new fingerprint, same
/// server row and company), every other session of the login ends.
/// </summary>
public class SessionLifecycleTests
{
    [ReferenceFact]
    public async Task Changing_your_own_password_keeps_this_session_and_ends_the_others()
    {
        int ownerA = Reference.UserId(Reference.Email("owner_a"));
        int companyA = Reference.Company("a");
        string original;
        await using (var db = Reference.NewDb())
            original = (await db.Users.AsNoTracking().SingleAsync(u => u.Id == ownerA)).PasswordHash;
        await using var host = await ModuleHost.StartAsync(typeof(Accountix.Hr.HrModule).Assembly);
        try
        {
            var me = host.ClientFor(ownerA, companyA);
            var other = host.ClientFor(ownerA, companyA);
            string before = me.Cookies.GetCookies(new Uri("http://localhost/"))[TestSignIn.CookieName]!.Value;
            Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/auth/change-password")).StatusCode);

            var r = await me.PostFormAsync("/auth/change-password",
                [new("current_password", Reference.Json.GetProperty("password").GetString()!),
                 new("new_password", "Another-pass-77"), new("confirm_password", "Another-pass-77")],
                "/auth/change-password");
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Equal("/hub", r.Headers.Location?.OriginalString);

            string after = me.Cookies.GetCookies(new Uri("http://localhost/"))[TestSignIn.CookieName]!.Value;
            Assert.NotEqual(before, after);
            var was = SessionCookie.Read(before, Reference.SecretKey, DateTimeOffset.UtcNow)!;
            var now = SessionCookie.Read(after, Reference.SecretKey, DateTimeOffset.UtcNow)!;
            Assert.Equal(SessionCookie.TokenOf(was), SessionCookie.TokenOf(now));       // the same server row
            Assert.Equal(companyA, SessionCookie.CompanyOf(now));
            string newHash;
            await using (var db = Reference.NewDb())
                newHash = (await db.Users.AsNoTracking().SingleAsync(u => u.Id == ownerA)).PasswordHash;
            Assert.Equal(SessionFingerprint.For(newHash, Reference.SecretKey), SessionCookie.UserOf(now)!.Value.Fingerprint);

            Assert.Equal(HttpStatusCode.OK, (await me.GetAsync("/auth/change-password")).StatusCode);
            var refused = await other.GetAsync("/auth/change-password");
            Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
            Assert.StartsWith("/auth/login?next=", refused.Headers.Location!.OriginalString);
            string otherHash = SessionCookie.TokenHash(SessionCookie.TokenOf(SessionCookie.Read(
                other.Cookies.GetCookies(new Uri("http://localhost/"))[TestSignIn.CookieName]!.Value, Reference.SecretKey, DateTimeOffset.UtcNow))!);
            await using (var db = Reference.NewDb())
                Assert.Equal("ended_elsewhere", (await db.UserSessions.AsNoTracking().SingleAsync(s => s.TokenHash == otherHash)).EndReason);
        }
        finally
        {
            await using var db = Reference.NewDb();
            await db.Users.Where(u => u.Id == ownerA).ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, original));
        }
    }
}
