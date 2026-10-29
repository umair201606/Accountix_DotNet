namespace Accountix.Platform.Controllers;

// My Account (settings/_account.html): save_account.
public sealed partial class SettingsController
{
    public sealed record AccountTab(User User, string Role);

    private Task<object?> AccountTabAsync(CancellationToken ct) =>
        // role_in(active company) or the account's legacy role.
        Task.FromResult<object?>(new AccountTab(Me.User, Me.RoleName ?? Me.User.Role?.Name ?? LegacyRole()));

    private string LegacyRole() =>
        db.Roles.AsNoTracking().Where(r => r.Id == Me.User.RoleId).Select(r => r.Name).FirstOrDefault() ?? "";

    [HttpPost("account")]
    public async Task<IActionResult> SaveAccount(CancellationToken ct)
    {
        var f = Request.Form;
        var user = await db.Users.FirstAsync(u => u.Id == Me.User.Id, ct);
        string fullName = f["full_name"].ToString().Trim();
        string email = f["email"].ToString().Trim().ToLowerInvariant();
        if (email.Length == 0) email = user.Email;
        string loginId = f["login_id"].ToString().Trim();
        if (loginId.Length == 0) loginId = user.LoginId ?? "";
        string newPw = f["new_password"].ToString(), confirm = f["confirm_password"].ToString();
        var back = ToTab("account");

        bool identityChanges = email != (user.Email ?? "").ToLowerInvariant() || loginId != (user.LoginId ?? "");
        if (identityChanges || newPw.Length > 0 || confirm.Length > 0)
        {
            // How this login is signed in to: a stolen session must not move it without the password.
            if (!Services.SignInService.CheckPassword(user, f["current_password"]))
            {
                this.Flash("Enter your current password to change your User ID, email or password.", "error");
                return back;
            }
        }
        if (identityChanges && await security.IdentityProblemAsync(user, loginId.Length > 0 ? loginId : email, email, ct) is { } problem)
        {
            this.Flash(problem, "error");
            return back;
        }
        if (newPw.Length > 0 || confirm.Length > 0)
        {
            if (Services.AccountSecurity.PasswordProblem(newPw) is { } pwProblem)
            {
                this.Flash(pwProblem, "error");
                return back;
            }
            if (newPw != confirm)
            {
                this.Flash("Passwords do not match — enter the new password twice.", "error");
                return back;
            }
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (fullName.Length > 0)
            user.FullName = fullName;
        if (email != (user.Email ?? "").ToLowerInvariant())
        {
            await security.MoveInvitationsAsync(user.Email, email, ct);
            user.Email = email;
        }
        if (loginId.Length > 0 && loginId != (user.LoginId ?? ""))
            user.LoginId = loginId;
        if (newPw.Length > 0)
            await signIn.SetPasswordAsync(HttpContext, user, newPw, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash("Account updated.", "success");
        return back;
    }
}
