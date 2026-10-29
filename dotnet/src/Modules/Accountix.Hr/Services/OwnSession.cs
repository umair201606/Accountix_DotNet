using Accountix.Core.Security;

namespace Accountix.Hr.Services;

/// <summary>
/// After a password change (User.set_password): every OTHER session of the
/// login ends on the server, and when it is the signed-in person's own
/// password, this session's ax_session cookie carries the new password
/// fingerprint so it stays signed in.
/// </summary>
public sealed class OwnSession(AccountixDbContext db, ISessionCookie cookie, AppSettings settings, TimeProvider time)
{
    public async Task EndOthersAsync(HttpContext http, int userId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        string? token = SessionCookie.TokenOf(cookie.Read(http));
        string? hash = token is null ? null : SessionCookie.TokenHash(token);
        await db.UserSessions
            .Where(s => s.UserId == userId && s.EndedAt == null && (hash == null || s.TokenHash != hash))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.EndedAt, now).SetProperty(x => x.EndReason, "ended_elsewhere"), ct);
    }

    /// <summary>Re-issue the session cookie with the new fingerprint (only when it is this person's own session).</summary>
    public void Refresh(HttpContext http, User user) =>
        cookie.Refresh(http, user.Id, SessionFingerprint.For(user.PasswordHash, settings.SecretKey));
}
