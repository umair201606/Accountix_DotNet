using Accountix.Core.Security;
using Accountix.Platform.Audit;

namespace Accountix.Platform.Services;

/// <summary>
/// shared/account_security.py: password policy, the sign-in throttle (counted
/// from the audit log, so it works on serverless), equal-time refusals, and
/// the rules for a person's own User ID and email.
/// </summary>
public sealed class AccountSecurity(AccountixDbContext db, AuditTrail audit, IHttpContextAccessor http,
                                    TimeProvider time, IConfiguration config)
{
    public const int MinPasswordLength = 8;
    public static readonly TimeSpan ThrottleWindow = TimeSpan.FromMinutes(15);
    public const int MaxFailsPerIp = 30;
    public const int MaxFailsPerLoginFromIp = 10;
    public const int MaxFailsPerLogin = 50;
    public const string ThrottledMessage = "Too many failed sign-in attempts. Wait 15 minutes, then try again.";

    private static readonly HashSet<string> Common =
    [
        "password", "password1", "password123", "12345678", "123456789",
        "1234567890", "11111111", "00000000", "qwerty123", "qwertyui",
        "iloveyou", "admin123", "admin1234", "welcome1", "letmein1",
        "abc12345", "abcd1234", "pakistan", "pakistan1", "pakistan123",
    ];

    /// <summary>password_problem: why a NEW password is refused, or null.</summary>
    public static string? PasswordProblem(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
            return $"Password must be at least {MinPasswordLength} characters.";
        if (password.Length > 128)
            return "Password must be at most 128 characters.";
        if (PyStrip(password) != password || PyStrip(password).Length == 0)
            return "Password cannot start or end with a space.";
        if (Common.Contains(password.ToLowerInvariant()))
            return "That password is too common — choose another.";
        return null;
    }

    /// <summary>Python's str.strip() (whitespace as str.isspace sees it).</summary>
    internal static string PyStrip(string s) => s.Trim();

    /// <summary>login_key: how a typed login is filed on its failed sign-ins.</summary>
    public static string? LoginKey(string? loginId)
    {
        string k = (loginId ?? "").Trim().ToLowerInvariant();
        if (k.Length > 120) k = k[..120];
        return k.Length == 0 ? null : k;
    }

    private (int PerIp, int PerPair, int PerLogin) Limits =>
        (config.GetValue("LOGIN_MAX_FAILS_PER_IP", MaxFailsPerIp),
         config.GetValue("LOGIN_MAX_FAILS_PER_LOGIN_FROM_IP", MaxFailsPerLoginFromIp),
         config.GetValue("LOGIN_MAX_FAILS_PER_LOGIN", MaxFailsPerLogin));

    /// <summary>sign_in_throttled: whether this sign-in is refused before its password is looked at.</summary>
    public async Task<bool> ThrottledAsync(string? loginId, User? user, CancellationToken ct = default)
    {
        var (perIp, perPair, perLogin) = Limits;
        var since = time.GetUtcNow().UtcDateTime - ThrottleWindow;
        string? ip = http.HttpContext is { } h ? AuditWho.ClientIp(h) : null;
        var fails = db.AuditLogs.AsNoTracking().Where(a => a.Action == "login_failed" && a.CreatedAt >= since);
        if (ip is not null && await fails.CountAsync(a => a.IpAddress == ip, ct) >= perIp)
            return true;
        string? key = LoginKey(loginId);
        if (key is null && user is null)
            return false;
        if (user is not null)
        {
            int uid = user.Id;
            DateTime? lastOk = await db.AuditLogs.AsNoTracking()
                .Where(a => a.Action == "login" && a.UserId == uid && a.CreatedAt >= since)
                .MaxAsync(a => (DateTime?)a.CreatedAt, ct);
            if (lastOk is DateTime ok)
                fails = fails.Where(a => a.CreatedAt > ok);
        }
        int? userId = user?.Id;
        var mine = key is not null && userId is int id2
            ? fails.Where(a => a.Reference == key || a.UserId == id2)
            : key is not null ? fails.Where(a => a.Reference == key) : fails.Where(a => a.UserId == userId);
        if (await mine.CountAsync(ct) >= perLogin)
            return true;
        return ip is not null && await mine.CountAsync(a => a.IpAddress == ip, ct) >= perPair;
    }

    /// <summary>record_failed_sign_in.</summary>
    public Task RecordFailedSignInAsync(string summary, string? loginId, User? user, int? companyId = null, CancellationToken ct = default) =>
        audit.RecordAsync("login_failed", summary, reference: LoginKey(loginId), companyId: companyId,
                          userId: user?.Id, ct: ct);

    /// <summary>spend_password_check: a login that does not exist takes as long to refuse as a wrong password.</summary>
    public static bool SpendPasswordCheck(string? password)
    {
        PasswordHasher.SpendEqualTime(password ?? "");
        return false;
    }

    /// <summary>identity_problem: why this User ID / email cannot belong to <paramref name="user"/>, or null.</summary>
    public async Task<string?> IdentityProblemAsync(User user, string? loginId, string? email, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(loginId) || loginId.Any(char.IsWhiteSpace) || loginId.Length < 3)
            return "User ID must be at least 3 characters, with no spaces.";
        if (loginId.Length > 120 || (email ?? "").Length > 120)
            return "User ID and email must be at most 120 characters.";
        if (string.IsNullOrEmpty(email) || !email.Contains('@') || email.Any(char.IsWhiteSpace))
            return "Enter a valid email address.";
        string lid = loginId.ToLowerInvariant(), mail = email.ToLowerInvariant();
        var others = db.Users.AsNoTracking().Where(u => u.Id != user.Id);
        if (await others.AnyAsync(u => u.LoginId!.ToLower() == lid || u.Email.ToLower() == lid, ct))
            return "That User ID is already taken.";
        if (await others.AnyAsync(u => u.Email.ToLower() == mail || u.LoginId!.ToLower() == mail, ct))
            return "That email is already in use.";
        if (mail != (user.Email ?? "").ToLowerInvariant() && await RegistrationEmailExistsAsync(db, mail, ct))
            return "That email is already in use.";
        return null;
    }

    /// <summary>RegistrationRequest.email_exists: a user, or a pending / seen / approved request, holds this email.</summary>
    public static async Task<bool> RegistrationEmailExistsAsync(AccountixDbContext db, string email, CancellationToken ct = default)
    {
        string e = email.ToLowerInvariant();
        return await db.Users.AsNoTracking().AnyAsync(u => u.Email == e, ct)
               || await db.Set<RegistrationRequest>().AsNoTracking()
                   .AnyAsync(r => r.Email == e && (r.Status == "pending" || r.Status == "seen" || r.Status == "approved"), ct);
    }

    /// <summary>RegistrationRequest.phone_exists.</summary>
    public static async Task<bool> RegistrationPhoneExistsAsync(AccountixDbContext db, string? phone, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(phone)) return false;
        return await db.Users.AsNoTracking().AnyAsync(u => u.Phone == phone, ct)
               || await db.Set<RegistrationRequest>().AsNoTracking()
                   .AnyAsync(r => r.Phone == phone && (r.Status == "pending" || r.Status == "seen" || r.Status == "approved"), ct);
    }

    /// <summary>move_invitations: invitations waiting for this person follow them to their new email.</summary>
    public async Task MoveInvitationsAsync(string? oldEmail, string? newEmail, CancellationToken ct = default)
    {
        string old = (oldEmail ?? "").ToLowerInvariant(), neu = (newEmail ?? "").ToLowerInvariant();
        if (old.Length == 0 || old == neu) return;
        await db.Invitations
            .Where(i => i.Email.ToLower() == old && i.Status == CompanyInvitation.Sent)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Email, neu), ct);
    }
}
