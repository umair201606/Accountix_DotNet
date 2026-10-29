using System.Text.Json.Nodes;
using Accountix.Core.Security;

namespace Accountix.Web.Infrastructure;

/// <summary>
/// The browser's half of a signed-in session: the <c>ax_session</c> cookie
/// (<see cref="SessionCookie"/>). It carries the user id with the password
/// fingerprint, the server session token (<c>_sid</c>, whose SHA-256 names the
/// user_sessions row) and the chosen company. A value written during this
/// request is what later reads in the same request see.
/// </summary>
public interface ISessionCookie
{
    /// <summary>The session's values (null when there is none or it does not verify).</summary>
    JsonObject? Read(HttpContext http);

    /// <summary>A fresh session for this person: nothing set before sign-in carries over.</summary>
    void SignIn(HttpContext http, int userId, string fingerprint, string token);

    /// <summary>The company the person works in from now on.</summary>
    void SetCompany(HttpContext http, int companyId);

    /// <summary>
    /// After this person's own password change: the same session (token, company)
    /// with the new fingerprint, so it stays signed in. False when the session is
    /// not this person's (an admin setting someone else's password).
    /// </summary>
    bool Refresh(HttpContext http, int userId, string fingerprint);

    /// <summary>Sign-out: the cookie is removed.</summary>
    void Clear(HttpContext http);
}

public sealed class SessionCookieStore(AppSettings settings, TimeProvider time) : ISessionCookie
{
    private static readonly object WrittenKey = new();

    public JsonObject? Read(HttpContext http)
    {
        if (http.Items.TryGetValue(WrittenKey, out var w))
            return (w as JsonObject)?.DeepClone() as JsonObject;
        return SessionCookie.Read(http.Request.Cookies[SessionCookie.CookieName], settings.SecretKey, time.GetUtcNow());
    }

    public void SignIn(HttpContext http, int userId, string fingerprint, string token) =>
        Write(http, SessionCookie.NewSession(userId, fingerprint, token));

    public void SetCompany(HttpContext http, int companyId)
    {
        var s = Read(http) ?? new JsonObject();
        s[SessionCookie.CompanyKey] = companyId;
        Write(http, s);
    }

    public bool Refresh(HttpContext http, int userId, string fingerprint)
    {
        var s = Read(http);
        if (s is null || SessionCookie.UserOf(s) is not var (uid, _) || uid != userId)
            return false;
        s[SessionCookie.UserKey] = $"{userId}:{fingerprint}";
        Write(http, s);
        return true;
    }

    public void Clear(HttpContext http)
    {
        http.Items[WrittenKey] = null;
        http.Response.Cookies.Delete(SessionCookie.CookieName, new CookieOptions { Path = "/" });
    }

    private void Write(HttpContext http, JsonObject s)
    {
        http.Items[WrittenKey] = s.DeepClone();
        http.Response.Cookies.Append(SessionCookie.CookieName, SessionCookie.Sign(s, settings.SecretKey, time.GetUtcNow()),
            new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = http.Request.IsHttps,
                Path = "/",
                IsEssential = true,
            });
    }
}
