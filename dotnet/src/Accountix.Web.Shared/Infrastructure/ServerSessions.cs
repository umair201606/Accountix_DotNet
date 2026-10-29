using System.Text.Json.Nodes;
using Accountix.Core.Data;
using Accountix.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Web.Infrastructure;

/// <summary>
/// The server-side session record (user_sessions): the <c>ax_session</c> cookie
/// carries a random token, the table its SHA-256. Limits: 2 hours idle
/// (SESSION_IDLE_MINUTES), 12 hours in all (SESSION_MAX_HOURS); "last seen"
/// moves at most once a minute.
/// </summary>
public sealed class ServerSessions(AccountixDbContext db, IConfiguration config, TimeProvider clock)
{
    public const string TokenKey = SessionCookie.TokenKey;
    private static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(1);

    private TimeSpan Idle => TimeSpan.FromMinutes(config.GetValue("SESSION_IDLE_MINUTES", 120));
    private TimeSpan Absolute => TimeSpan.FromHours(config.GetValue("SESSION_MAX_HOURS", 12));

    public static string Hash(string token) => SessionCookie.TokenHash(token);

    /// <summary>True while the session's server row is open (and touches it); ends it when idle or too old.</summary>
    public async Task<bool> StillOpenAsync(JsonObject session, int userId, CancellationToken ct)
    {
        if (SessionCookie.TokenOf(session) is not { } token)
            return false;
        string hash = Hash(token);
        var row = await db.UserSessions.FirstOrDefaultAsync(s => s.TokenHash == hash, ct);
        if (row is null || row.UserId != userId || row.EndedAt is not null)
            return false;
        var now = clock.GetUtcNow().UtcDateTime;
        if (now - row.CreatedAt > Absolute)
        {
            row.EndedAt = now;
            row.EndReason = "expired";
        }
        else if (now - row.LastSeenAt > Idle)
        {
            row.EndedAt = now;
            row.EndReason = "idle";
        }
        else if (now - row.LastSeenAt >= TouchEvery)
            row.LastSeenAt = now;
        else
            return true;
        await db.SaveChangesAsync(ct);
        db.Entry(row).State = EntityState.Detached;
        return row.EndedAt is null;
    }
}
