using Accountix.Core.Security;
using Accountix.Platform.Audit;
using Npgsql;

namespace Accountix.Platform.Services;

/// <summary>
/// Signing in and out (/auth/login, /auth/logout, the console's login): finds
/// the login, opens the user_sessions row on its own connection, writes the
/// ax_session cookie (ISessionCookie), ends sessions.
/// </summary>
public sealed class SignInService(AccountixDbContext db, ISessionCookie cookie, AppSettings settings, TimeProvider time)
{
    /// <summary>User ID first, then the email (both case-insensitive).</summary>
    public async Task<User?> FindLoginAsync(string loginId, CancellationToken ct = default)
    {
        string l = loginId.ToLowerInvariant();
        return await db.Users.AsNoTracking().Where(u => u.LoginId!.ToLower() == l).OrderBy(u => u.Id).FirstOrDefaultAsync(ct)
               ?? await db.Users.AsNoTracking().Where(u => u.Email.ToLower() == l).OrderBy(u => u.Id).FirstOrDefaultAsync(ct);
    }

    public static bool CheckPassword(User user, string? password) => PasswordHasher.Verify(user.PasswordHash, password ?? "");

    /// <summary>A fresh session: the user_sessions row, then the cookie (nothing from before sign-in carries over).</summary>
    public async Task<string> StartAsync(HttpContext http, User user, CancellationToken ct = default)
    {
        string token = SessionCookie.NewToken();
        var now = time.GetUtcNow().UtcDateTime;
        string? ip = AuditWho.ClientIp(http);
        string agent = http.Request.Headers.UserAgent.ToString();
        if (agent.Length > 200) agent = agent[..200];
        await using (var conn = new NpgsqlConnection(db.Database.GetConnectionString()))
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO user_sessions (token_hash, user_id, created_at, last_seen_at, ip_address, user_agent) " +
                              "VALUES (@h, @u, @c, @c, @ip, @ua)";
            cmd.Parameters.AddWithValue("h", ServerSessions.Hash(token));
            cmd.Parameters.AddWithValue("u", user.Id);
            cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlTypes.NpgsqlDbType.Timestamp) { Value = DateTime.SpecifyKind(now, DateTimeKind.Unspecified) });
            cmd.Parameters.Add(new NpgsqlParameter("ip", NpgsqlTypes.NpgsqlDbType.Varchar) { Value = (object?)ip ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("ua", NpgsqlTypes.NpgsqlDbType.Varchar) { Value = agent.Length == 0 ? DBNull.Value : agent });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        cookie.SignIn(http, user.Id, SessionFingerprint.For(user.PasswordHash, settings.SecretKey), token);
        http.Items[AuditTrail.SignedInKey] = (user.Id, string.IsNullOrEmpty(user.FullName) ? user.Email : user.FullName);
        return token;
    }

    /// <summary>This request's session ends (sign-out), on its own connection.</summary>
    public async Task EndCurrentAsync(HttpContext http, string reason = "signed_out", CancellationToken ct = default)
    {
        string? token = CurrentToken(http);
        if (string.IsNullOrEmpty(token)) return;
        await using var conn = new NpgsqlConnection(db.Database.GetConnectionString());
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE user_sessions SET ended_at = @n, end_reason = @r WHERE token_hash = @h AND ended_at IS NULL";
        cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlTypes.NpgsqlDbType.Timestamp) { Value = DateTime.SpecifyKind(time.GetUtcNow().UtcDateTime, DateTimeKind.Unspecified) });
        cmd.Parameters.AddWithValue("r", reason);
        cmd.Parameters.AddWithValue("h", ServerSessions.Hash(token));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Every other open session of this person ends, in the caller's transaction.</summary>
    public async Task EndOthersAsync(HttpContext? http, int userId, string reason = "ended_elsewhere", CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        string? token = http is null ? null : CurrentToken(http);
        var q = db.UserSessions.Where(s => s.UserId == userId && s.EndedAt == null);
        if (!string.IsNullOrEmpty(token))
        {
            string hash = ServerSessions.Hash(token);
            q = q.Where(s => s.TokenHash != hash);
        }
        await q.ExecuteUpdateAsync(s => s.SetProperty(x => x.EndedAt, now).SetProperty(x => x.EndReason, reason), ct);
    }

    /// <summary>
    /// A new password (same hash format as every stored one): this request's own
    /// session keeps going (it carries the new fingerprint), every other session ends.
    /// </summary>
    public async Task SetPasswordAsync(HttpContext? http, User user, string password, CancellationToken ct = default)
    {
        user.PasswordHash = PasswordHasher.Hash(password);
        if (http is not null)
            cookie.Refresh(http, user.Id, SessionFingerprint.For(user.PasswordHash, settings.SecretKey));
        await EndOthersAsync(http, user.Id, ct: ct);
    }

    public string? CurrentToken(HttpContext http) => SessionCookie.TokenOf(cookie.Read(http));

    /// <summary>Keeps the company choice when the session is re-written (password change keeps the person signed in here).</summary>
    public void SetCompany(HttpContext http, int companyId) => cookie.SetCompany(http, companyId);

    public void Clear(HttpContext http) => cookie.Clear(http);
}
