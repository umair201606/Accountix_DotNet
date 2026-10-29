using Accountix.Core.Security;
using Accountix.Hr.Services;

namespace Accountix.Hr.Controllers;

/// <summary>
/// A person's own account pages of hr_app/routes/auth.py: profile, User ID /
/// email, password. Open to anyone signed in (module_guard ALWAYS_OPEN), in
/// the HR shell.
/// </summary>
[Area("Hr")]
[SignedIn]
[Shell(Modules.Hr)]
[Route("auth")]
public sealed class AccountController(AccountixDbContext db, People people, OwnSession own, CompanyClock clock) : Controller
{
    private EffectiveAccess Access => HttpContext.Access()!;

    public sealed class ProfileModel
    {
        public required User User { get; init; }
        public string? Designation { get; init; }
        public string? Department { get; init; }
    }

    [HttpGet("profile")]
    public async Task<IActionResult> Profile(CancellationToken ct)
    {
        var u = await db.Users.AsNoTracking().FirstAsync(x => x.Id == Access.User.Id, ct);
        var (_, des, dep) = await people.EmploymentInAsync(u, Access.CompanyId, ct);
        return View(new ProfileModel { User = u, Designation = des, Department = dep });
    }

    [HttpPost("account-settings")]
    public async Task<IActionResult> AccountSettings(CancellationToken ct)
    {
        var f = Request.Form;
        string loginId = (f["login_id"].FirstOrDefault() ?? "").Trim();
        string email = (f["email"].FirstOrDefault() ?? "").Trim().ToLowerInvariant();
        if (loginId.Length == 0 || email.Length == 0)
        {
            this.Flash("User ID and email are both required.", "danger");
            return Redirect("/auth/profile");
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var me = await db.Users.FirstAsync(x => x.Id == Access.User.Id, ct);
        if (loginId == (me.LoginId ?? "") && email == (me.Email ?? "").ToLowerInvariant())
        {
            this.Flash("Nothing to change.", "info");
            return Redirect("/auth/profile");
        }
        // How this login is found and signed in to: a stolen session must not move it without the password.
        if (!PasswordHasher.Verify(me.PasswordHash, f["current_password"].FirstOrDefault() ?? ""))
        {
            this.Flash("Enter your current password to change your User ID or email.", "danger");
            return Redirect("/auth/profile");
        }
        if (await AccountRules.IdentityProblemAsync(db, me, loginId, email, ct) is string problem)
        {
            this.Flash(problem, "danger");
            return Redirect("/auth/profile");
        }
        await AccountRules.MoveInvitationsAsync(db, me.Email, email, ct);
        me.LoginId = loginId;
        me.Email = email;
        me.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash("Account settings saved — sign in with your User ID from now on.", "success");
        return Redirect("/auth/profile");
    }

    [HttpGet("change-password")]
    public IActionResult ChangePassword()
    {
        ViewData["BackUrl"] = "/dashboard";
        return View();
    }

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePasswordPost(CancellationToken ct)
    {
        var f = Request.Form;
        string current = f["current_password"].FirstOrDefault() ?? "";
        string next = f["new_password"].FirstOrDefault() ?? "";
        string confirm = f["confirm_password"].FirstOrDefault() ?? "";
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var me = await db.Users.FirstAsync(x => x.Id == Access.User.Id, ct);
        if (!PasswordHasher.Verify(me.PasswordHash, current))
        {
            this.Flash("Current password is incorrect.", "danger");
            return Redirect("/auth/change-password");
        }
        if (PasswordPolicy.Problem(next) is string problem)
        {
            this.Flash(problem, "danger");
            return Redirect("/auth/change-password");
        }
        if (next != confirm)
        {
            this.Flash("New passwords do not match.", "danger");
            return Redirect("/auth/change-password");
        }
        me.PasswordHash = PasswordHasher.Hash(next);
        me.UpdatedAt = clock.UtcNow;
        own.Refresh(HttpContext, me);                     // this session stays signed in
        await own.EndOthersAsync(HttpContext, me.Id, ct);  // every other one ends
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash("Password changed successfully.", "success");
        return Redirect("/hub");
    }
}
