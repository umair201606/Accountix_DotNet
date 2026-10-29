using Accountix.Core.Security;
using Accountix.Platform.Services;
using Accountix.SuperAdmin.Infrastructure;
using Accountix.SuperAdmin.Services;

namespace Accountix.SuperAdmin.Controllers;

/// <summary>
/// Manage Users (superadmin.users, user_detail, toggle_super): every account
/// on the platform, and the only place one is created. HR assigns existing
/// members; Settings invites registered emails; neither can bring a new
/// person into existence.
/// </summary>
[Area("SuperAdmin")]
[Route("superadmin")]
[SuperAdminOnly]
public sealed class UsersController(AccountixDbContext db, FirstUse firstUse, ConsoleQueries queries,
                                    ConsoleAccounts accounts, SignInService signIn) : Controller
{
    private User Me => HttpContext.Access()!.User;

    public sealed record ListModel(IReadOnlyList<ConsoleQueries.UserRow> Users, GlobalLimit Limits);

    [HttpGet("users")]
    public async Task<IActionResult> Users(CancellationToken ct)
    {
        ViewData["Nav"] = "users";
        return View("Users", new ListModel(await queries.UsersAsync(ct), await firstUse.GlobalLimitsAsync(ct)));
    }

    [HttpPost("users")]
    public async Task<IActionResult> UsersPost(CancellationToken ct)
    {
        var form = Request.Form;
        string email = Py.Strip(Py.Form(form, "email")).ToLowerInvariant();
        string name = Py.Strip(Py.Form(form, "full_name"));
        string password = Py.Strip(Py.Form(form, "password"));
        if (email.Length == 0 || password.Length == 0)
            return Refuse("Email and password are required.");
        if (AccountSecurity.PasswordProblem(password) is { } problem)
            return Refuse(problem);
        if (!email.Contains('@') || email.Any(char.IsWhiteSpace))
            return Refuse("Enter a valid email address.");
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            return Refuse($"A user with {email} already exists.");
        // login_id is globally unique and may have diverged from email on an
        // older account: an email-only check would pass and the INSERT fail.
        if (await db.Users.AnyAsync(u => u.LoginId!.ToLower() == email, ct))
            return Refuse($"The login id {email} is already taken by another account.");
        if (await accounts.RoleIdAsync(Role.Employee, ct) is not int employee)
            return Refuse("Roles are not seeded yet.");

        var u = await accounts.NewUserAsync(email, name.Length > 0 ? name : Py.Before(email, '@'), employee,
                                            PasswordHasher.Hash(password), allModules: false, ct: ct);
        await db.SaveChangesAsync(ct);
        this.Flash($"User {u.FullName} created. Invite them into a company from that company's Settings.", "success");
        return Redirect($"/superadmin/users/{u.Id}/");
    }

    private RedirectResult Refuse(string message)
    {
        this.Flash(message, "error");
        return Redirect("/superadmin/users/");
    }

    public sealed record DetailModel(User User, IReadOnlyList<ConsoleQueries.UserMembershipRow> Memberships, GlobalLimit Limits,
                                     int Owned, int EffectiveOwned, int? EffectiveJoined);

    [HttpGet("users/{userId:int}")]
    public async Task<IActionResult> UserDetail(int userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return ConsolePages.Error(404);
        var limits = await firstUse.GlobalLimitsAsync(ct);
        ViewData["Nav"] = "users";
        return View("UserDetail", new DetailModel(user,
            await queries.UserMembershipsAsync(userId, ct), limits,
            await db.Companies.CountAsync(c => c.CreatedBy == userId, ct),
            limits.CompanyLimitFor(user), limits.JoinLimitFor(user)));
    }

    [HttpPost("users/{userId:int}")]
    public async Task<IActionResult> UserDetailPost(int userId, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return ConsolePages.Error(404);
        var form = Request.Form;
        string action = Py.Has(form, "action") ? Py.Form(form, "action") : "profile";
        string back = $"/superadmin/users/{user.Id}/";
        if (action == "password")
        {
            string pw = Py.Strip(Py.Form(form, "new_password"));
            if (AccountSecurity.PasswordProblem(pw) is { } problem)
                this.Flash(problem, "error");
            else
            {
                // User.set_password: the new hash, this browser's own session
                // carries on if it is that person's, every other one ends.
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await signIn.SetPasswordAsync(HttpContext, user, pw, ct);
                user.UpdatedAt = accounts.UtcNow;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                this.Flash($"Password reset for {user.FullName}.", "success");
            }
        }
        else if (action == "active")
        {
            if (user.Id == Me.Id)
                this.Flash("You cannot deactivate your own account.", "error");
            else
            {
                user.IsActive = user.IsActive != true;
                user.UpdatedAt = accounts.UtcNow;
                await db.SaveChangesAsync(ct);
                this.Flash($"{user.FullName} is now {(user.IsActive == true ? "active" : "deactivated")}.", "success");
            }
        }
        else
        {
            // Blank means "use the global default": NULL, never 0 (which forbids everything).
            int? Limit(string field, out bool ok)
            {
                ok = true;
                string raw = Py.Strip(Py.Form(form, field));
                if (raw.Length == 0) return null;
                if (Py.TryInt(raw, out int n)) return Math.Max(0, n);
                ok = false;
                return null;
            }
            int? owned = Limit("max_companies_owned", out bool ok);
            int? joined = null;
            if (ok)
                joined = Limit("max_companies_joined", out ok);
            if (!ok)
            {
                this.Flash("Limits must be whole numbers.", "error");
                return Redirect(back);
            }
            user.MaxCompaniesOwned = owned;
            user.MaxCompaniesJoined = joined;
            if (Py.Strip(Py.Form(form, "full_name")) is { Length: > 0 } name)
                user.FullName = name;
            db.TouchIfChanged(user, () => user.UpdatedAt = accounts.UtcNow);
            await db.SaveChangesAsync(ct);
            this.Flash("User updated.", "success");
        }
        return Redirect(back);
    }

    [HttpPost("users/{userId:int}/toggle-super")]
    public async Task<IActionResult> ToggleSuper(int userId, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return ConsolePages.Error(404);
        if (user.Id == Me.Id && user.IsSuperAdmin == true)
        {
            this.Flash("You cannot remove your own super admin rights.", "error");
            return Redirect("/superadmin/users/");
        }
        user.IsSuperAdmin = user.IsSuperAdmin != true;
        user.UpdatedAt = accounts.UtcNow;
        await db.SaveChangesAsync(ct);
        string who = string.IsNullOrEmpty(user.FullName) ? user.Email : user.FullName;
        this.Flash($"{who} is {(user.IsSuperAdmin == true ? "now" : "no longer")} a super admin.", "success");
        return Redirect("/superadmin/users/");
    }
}
