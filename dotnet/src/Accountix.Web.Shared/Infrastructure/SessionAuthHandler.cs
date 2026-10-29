using System.Security.Claims;
using System.Text.Encodings.Web;
using Accountix.Core.Data;
using Accountix.Core.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Accountix.Web.Infrastructure;

/// <summary>
/// Signs the request in from the <c>ax_session</c> cookie (<see cref="SessionCookie"/>).
/// The checks: valid signature under SECRET_KEY, the account exists and is
/// switched on, the password fingerprint is current (a password change signs
/// out every other session), and the server's user_sessions row is still open
/// (signed out, 2 h idle, 12 h in all, or ended elsewhere = signed out).
/// </summary>
public sealed class SessionAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AccountixDbContext db,
    AppSettings settings,
    ServerSessions sessions,
    ISessionCookie cookie)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AccountixSession";

    /// <summary>HttpContext.Items key: the company the session names (CompanyContextMiddleware opens it).</summary>
    public const string SessionCompanyKey = "accountix.session_company_id";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var session = cookie.Read(Context);
        if (session is null || SessionCookie.UserOf(session) is not var (userId, mark))
            return AuthenticateResult.NoResult();

        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Id, u.FullName, u.Email, u.PasswordHash, u.IsActive })
            .FirstOrDefaultAsync(Context.RequestAborted);
        if (user is null || user.IsActive == false)
            return AuthenticateResult.NoResult();
        if (mark is null || !SessionFingerprint.Matches(mark, user.PasswordHash, settings.SecretKey))
            return AuthenticateResult.NoResult();
        if (!await sessions.StillOpenAsync(session, user.Id, Context.RequestAborted))
            return AuthenticateResult.NoResult();

        Context.Items[SessionCompanyKey] = SessionCookie.CompanyOf(session);
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, string.IsNullOrWhiteSpace(user.FullName) ? user.Email : user.FullName),
        ], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Request.WantsJson())
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Response.WriteAsJsonAsync(new { ok = false, error = "Please sign in again." });
        }
        string next = Request.PathBase + Request.Path + Request.QueryString;
        // A signed-out visit to a console page goes to the console's own door.
        string door = Request.Path.StartsWithSegments("/superadmin") ? "/superadmin/login" : settings.LoginUrl;
        Response.Redirect($"{door}?next={Uri.EscapeDataString(next)}");
        return Task.CompletedTask;
    }
}
