namespace Accountix.Hr.Services;

/// <summary>
/// The rules for changing a login (shared/account_security.py): a person's
/// own User ID / email (identity_problem) and the invitations that follow an
/// email to its new address (move_invitations).
/// </summary>
public static class AccountRules
{
    /// <summary>identity_problem: why this User ID / email cannot belong to the person, or null.</summary>
    public static async Task<string?> IdentityProblemAsync(AccountixDbContext db, User user, string loginId, string email,
                                                           CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(loginId) || loginId.Any(char.IsWhiteSpace) || loginId.Length < 3)
            return "User ID must be at least 3 characters, with no spaces.";
        if (loginId.Length > 120 || (email ?? "").Length > 120)
            return "User ID and email must be at most 120 characters.";
        if (string.IsNullOrEmpty(email) || !email.Contains('@') || email.Any(char.IsWhiteSpace))
            return "Enter a valid email address.";
        string lid = loginId.ToLowerInvariant(), mail = email.ToLowerInvariant();
        var others = db.Users.Where(u => u.Id != user.Id);
        if (await others.AnyAsync(u => (u.LoginId != null && u.LoginId.ToLower() == lid) || u.Email.ToLower() == lid, ct))
            return "That User ID is already taken.";
        if (await others.AnyAsync(u => u.Email.ToLower() == mail || (u.LoginId != null && u.LoginId.ToLower() == mail), ct))
            return "That email is already in use.";
        if (mail != (user.Email ?? "").ToLowerInvariant() && await EmailExistsAsync(db, mail, ct))
            return "That email is already in use.";
        return null;
    }

    /// <summary>RegistrationRequest.email_exists: an account or a live sign-up request holds this email.</summary>
    private static async Task<bool> EmailExistsAsync(AccountixDbContext db, string email, CancellationToken ct)
    {
        string e = email.ToLowerInvariant();
        return await db.Users.AnyAsync(u => u.Email == e, ct)
               || await db.Set<RegistrationRequest>().AnyAsync(r => r.Email == e
                      && (r.Status == "pending" || r.Status == "seen" || r.Status == "approved"), ct);
    }

    /// <summary>move_invitations: invitations waiting for the old address follow the person to the new one.</summary>
    public static async Task MoveInvitationsAsync(AccountixDbContext db, string? oldEmail, string? newEmail, CancellationToken ct = default)
    {
        string old = (oldEmail ?? "").ToLowerInvariant(), neu = (newEmail ?? "").ToLowerInvariant();
        if (old.Length == 0 || old == neu) return;
        await db.Invitations.Where(i => i.Email.ToLower() == old && i.Status == CompanyInvitation.Sent)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Email, neu), ct);
    }
}
