using Accountix.Core.Security;
using Accountix.Hr.Services;

namespace Accountix.Hr.Controllers;

/// <summary>
/// HR's user pages (hr_app/routes/auth.py): the employee list, assigning a
/// member their employee details, editing one, HR rights, password reset and
/// deactivation. HR never creates logins: people join by invitation.
/// Member changes queue per company (serialize("members", company)), as
/// Settings' member changes do.
/// </summary>
[Area("Hr")]
[Module(Modules.Hr)]
[Route("auth")]
public sealed class UsersController(AccountixDbContext db, CompanyClock clock, People people, AccessDefaults defaults,
                                    OwnSession own, ILedger ledger) : HrController
{
    /// <summary>_can_manage_users: admins always; HR managers (HR switch on) with the Employees right; else refused.</summary>
    private bool CanManageUsers(string action)
    {
        if (IsAdmin) return true;
        if (IsManager && Access.ModuleFlag(Modules.Hr))
        {
            if (Access.Can("employees", action)) return true;
            this.Flash($"You do not have the Employees {action} right. Ask an admin in Settings.", "danger");
            return false;
        }
        this.Flash("Access denied.", "danger");
        return false;
    }

    public sealed record UserRow(User User, CompanyMembership Membership, string RoleName);

    public sealed class ListModel
    {
        public IReadOnlyList<UserRow> Users { get; init; } = [];
        public IReadOnlySet<int> SharedLogins { get; init; } = new HashSet<int>();
    }

    [HttpGet("users")]
    [AnyMember]   // _can_manage_users("view"), below
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!CanManageUsers(Rights.View)) return Redirect("/dashboard");
        var rows = await (await people.EmployeesAsync(Cid, ct))
            .Join(db.Roles, m => m.Membership.RoleId, r => r.Id, (m, r) => new { m.User, m.Membership, Role = r.Name })
            .OrderBy(x => x.User.FullName).AsNoTracking().ToListAsync(ct);
        var users = rows.Select(r => new UserRow(r.User, r.Membership, r.Role)).ToList();
        return View(new ListModel
        {
            Users = users,
            SharedLogins = await people.LoginsManagedElsewhereAsync(users.Select(u => u.User).ToList(), Access, ct),
        });
    }

    /// <summary>_manager_choice: the manager picked on the form, or the refusal.</summary>
    private async Task<(int? ManagerId, string? Problem)> ManagerChoiceAsync(int uid, int? current, CancellationToken ct)
    {
        int? mid = FormInt("manager_id") is int v && v != 0 ? v : null;
        if (mid is null || mid == current) return (mid, null);
        if (mid == uid) return (null, "A person cannot be their own manager.");
        if (!(await people.CompanyManagersAsync(Cid, ct)).Any(m => m.Id == mid))
            return (null, "Pick the manager from this company's managers.");
        return (mid, null);
    }

    public sealed class AssignModel
    {
        public IReadOnlyList<(User User, CompanyMembership Membership)> Pending { get; init; } = [];
        public IReadOnlyList<User> Managers { get; init; } = [];
    }

    private async Task<IActionResult> AssignPage(CancellationToken ct)
    {
        int? admin = await people.RoleIdAsync(Role.Admin, ct);
        var pending = await (from m in db.Memberships
                             where m.CompanyId == Cid && m.Status == CompanyMembership.StatusActive
                                   && (m.EmployeeCode == null || m.EmployeeCode == "") && (admin == null || m.RoleId != admin)
                             join u in db.Users on m.UserId equals u.Id
                             orderby u.FullName
                             select new { u, m }).AsNoTracking().ToListAsync(ct);
        ViewData["BackUrl"] = "/auth/users";
        return View("Assign", new AssignModel
        {
            Pending = pending.Select(x => (x.u, x.m)).ToList(),
            Managers = await people.CompanyManagersAsync(Cid, ct),
        });
    }

    [HttpGet("members/assign")]
    [AnyMember]   // _can_manage_users("create"), below
    public async Task<IActionResult> Assign(CancellationToken ct)
    {
        if (!CanManageUsers(Rights.Create)) return Redirect("/dashboard");
        return await AssignPage(ct);
    }

    [HttpPost("members/assign")]
    [AnyMember]   // _can_manage_users("create"), below
    public async Task<IActionResult> AssignPost(CancellationToken ct)
    {
        if (!CanManageUsers(Rights.Create)) return Redirect("/dashboard");
        int? uid = FormInt("user_id");
        string code = FormStr("employee_code").Trim();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SerializeAsync("members", Cid);
        var u = uid is int id && id != 0 ? await people.GetMemberAsync(id, Access, ct) : null;
        if (u is null)
        {
            this.Flash("That person is not a member of this company.", "danger");
            return await AssignPage(ct);
        }
        if (code.Length == 0)
        {
            this.Flash("Employee code is required.", "danger");
            return await AssignPage(ct);
        }
        var m = await people.ActiveMembershipAsync(u.Id, Cid, ct);
        if (m is null)
        {
            this.Flash("That person is not an active member of this company.", "danger");
            return await AssignPage(ct);
        }
        // Unique per company, not globally.
        if (await db.Memberships.AnyAsync(x => x.CompanyId == Cid && x.EmployeeCode == code && x.UserId != u.Id, ct))
        {
            this.Flash($"Employee code {code} is already used in this company.", "danger");
            return await AssignPage(ct);
        }
        var (current, _, _) = await people.EmploymentInAsync(u, Cid, ct);
        var (managerId, problem) = await ManagerChoiceAsync(u.Id, current, ct);
        if (problem is not null)
        {
            this.Flash(problem, "danger");
            return await AssignPage(ct);
        }
        m.EmployeeCode = code;
        string designation = FormStr("designation").Trim(), department = FormStr("department").Trim();
        await people.SetEmploymentAsync(u, Cid, x => { x.Designation = designation; x.Department = department; x.ManagerId = managerId; },
                                        x => { x.Designation = designation; x.Department = department; x.ManagerId = managerId; }, ct);
        string phone = FormStr("phone").Trim();
        if (phone.Length > 0) u.Phone = phone;
        if (string.IsNullOrEmpty(u.EmployeeCode)
            && !await db.Users.AnyAsync(x => x.EmployeeCode == code && x.Id != u.Id, ct))
            u.EmployeeCode = code;      // legacy global column, globally unique: only when nobody holds it
        u.DateOfJoining ??= DateOnly.FromDateTime(clock.UtcNow);
        TouchUser(u);
        await db.SaveChangesAsync(ct);
        await ledger.EntityAccountAsync("employee", u.Id, $"{u.FullName} ({code})", ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"{u.FullName} assigned as {code} — employee ledger account added.", "success");
        return Redirect("/auth/users");
    }

    /// <summary>users.updated_at follows any change to the row (onupdate=datetime.utcnow).</summary>
    private void TouchUser(User u)
    {
        db.ChangeTracker.DetectChanges();
        if (db.Entry(u).State == EntityState.Modified)
            u.UpdatedAt = clock.UtcNow;
    }

    public sealed class FormModel
    {
        public required User User { get; init; }
        public CompanyMembership? Membership { get; init; }
        public IReadOnlyList<Role> Roles { get; init; } = [];
        public IReadOnlyList<User> Managers { get; init; } = [];
        public int? ManagerId { get; init; }
        public string? Designation { get; init; }
        public string? Department { get; init; }
        public string? LoginRefusal { get; init; }
    }

    private async Task<IActionResult> FormPage(User u, CompanyMembership? membership, CancellationToken ct)
    {
        var (mid, des, dep) = await people.EmploymentInAsync(u, Cid, ct);
        ViewData["BackUrl"] = "/auth/users";
        return View("Form", new FormModel
        {
            User = u, Membership = membership, ManagerId = mid, Designation = des, Department = dep,
            Roles = await db.Roles.AsNoTracking().OrderBy(r => r.Id).ToListAsync(ct),
            Managers = (await people.CompanyManagersAsync(Cid, ct)).Where(x => x.Id != u.Id).ToList(),
            LoginRefusal = await people.LoginChangeRefusalAsync(u, Access, ct),
        });
    }

    [HttpGet("users/{uid:int}/edit")]
    [AnyMember]   // _can_manage_users("edit"), below
    public async Task<IActionResult> Edit(int uid, CancellationToken ct)
    {
        if (!CanManageUsers(Rights.Edit)) return Redirect("/dashboard");
        var u = await people.GetMemberAsync(uid, Access, ct);
        if (u is null) return NotFound();
        if (await people.IsAdminAsync(u, Cid, ct) && !IsAdmin)
        {
            this.Flash("Admin accounts are managed from ERP hub Settings.", "danger");
            return Redirect("/auth/users");
        }
        return await FormPage(u, await people.ActiveMembershipAsync(u.Id, Cid, ct), ct);
    }

    [HttpPost("users/{uid:int}/edit")]
    [AnyMember]   // _can_manage_users("edit"), below
    public async Task<IActionResult> EditPost(int uid, CancellationToken ct)
    {
        if (!CanManageUsers(Rights.Edit)) return Redirect("/dashboard");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SerializeAsync("members", Cid);
        var u = await people.GetMemberAsync(uid, Access, ct);
        if (u is null) return NotFound();
        if (await people.IsAdminAsync(u, Cid, ct) && !IsAdmin)
        {
            this.Flash("Admin accounts are managed from ERP hub Settings.", "danger");
            return Redirect("/auth/users");
        }
        var membership = await people.ActiveMembershipAsync(u.Id, Cid, ct);
        var f = Request.Form;
        string back = $"/auth/users/{uid}/edit";
        string newEmail = (f.ContainsKey("email") ? f["email"].FirstOrDefault() ?? "" : u.Email).Trim().ToLowerInvariant();
        if (newEmail.Length == 0)
        {
            this.Flash("Email is required.", "danger");
            return await FormPage(u, membership, ct);
        }
        // Another account's email OR User ID: sign-in tries the User ID first.
        if (await db.Users.AnyAsync(x => x.Id != u.Id && (x.Email.ToLower() == newEmail || (x.LoginId != null && x.LoginId.ToLower() == newEmail)), ct))
        {
            this.Flash("That email is already in use.", "danger");
            return await FormPage(u, membership, ct);
        }
        // Email, password and the on/off switch are the login in every company: changed here only
        // for a login that reaches nothing else.
        bool wantsActive = f["is_active"].FirstOrDefault() == "1";
        string password = f["password"].FirstOrDefault() ?? "";
        bool loginChanges = newEmail != (u.Email ?? "").ToLowerInvariant() || password.Length > 0 || wantsActive != (u.IsActive == true);
        string? refusal = loginChanges ? await people.LoginChangeRefusalAsync(u, Access, ct) : null;
        if (refusal is not null)
        {
            this.Flash(refusal + " Their email, password and sign-in are left as they were.", "danger");
            return Redirect(back);
        }
        var (currentManager, _, _) = await people.EmploymentInAsync(u, Cid, ct);
        var (managerId, problem) = await ManagerChoiceAsync(u.Id, currentManager, ct);
        if (problem is not null)
        {
            this.Flash(problem, "danger");
            return Redirect(back);
        }
        if (newEmail != (u.Email ?? "").ToLowerInvariant())
            await AccountRules.MoveInvitationsAsync(db, u.Email, newEmail, ct);
        u.Email = newEmail;
        u.FullName = (f.ContainsKey("full_name") ? f["full_name"].FirstOrDefault() ?? "" : u.FullName).Trim();
        int newRoleId = f.ContainsKey("role_id") ? int.Parse(f["role_id"].FirstOrDefault() ?? "") : u.RoleId;
        int? adminRole = await people.RoleIdAsync(Role.Admin, ct);
        // HR must never be able to promote anyone to admin.
        if (!IsAdmin && adminRole is not null && newRoleId == adminRole)
        {
            this.Flash("Only admin can assign the admin role.", "danger");
            return Redirect(back);
        }
        // The role is the membership's, not the global user row's.
        if (u.Id == Me && membership is not null && membership.RoleId != newRoleId)
        {
            this.Flash("You cannot change your own role.", "danger");
            return Redirect(back);
        }
        if (membership is not null)
        {
            if (Access.Company?.CreatedBy is int creator && u.Id == creator && membership.RoleId != newRoleId)
            {
                this.Flash("The company owner's role is fixed and cannot be changed.", "danger");
                return Redirect(back);
            }
            string? oldRole = await people.RoleNameAsync(membership.RoleId, ct);
            membership.RoleId = newRoleId;
            // Template rights move with the role; hand-set ones never do.
            await defaults.RoleChangedAsync(membership, oldRole, await people.RoleNameAsync(newRoleId, ct), ct);
        }
        else
            u.RoleId = newRoleId;
        string designation = FormStr("designation").Trim(), department = FormStr("department").Trim();
        await people.SetEmploymentAsync(u, Cid, x => { x.ManagerId = managerId; x.Designation = designation; x.Department = department; },
                                        x => { x.ManagerId = managerId; x.Designation = designation; x.Department = department; }, ct);
        u.Phone = FormStr("phone").Trim();
        var joined = FormInput.OptionalDate(f, "date_of_joining", "Date of joining");
        var left = FormInput.OptionalDate(f, "date_of_leaving", "Last working day");
        if (joined is not null) u.DateOfJoining = joined;
        if (membership is not null && f.ContainsKey("date_of_leaving"))
        {
            if (left is not null && u.DateOfJoining is not null && left < u.DateOfJoining)
            {
                this.Flash("The last working day cannot be before the joining date.", "danger");
                return Redirect(back);
            }
            membership.DateOfLeaving = left;
        }
        u.IsActive = wantsActive;
        if (password.Length > 0)
        {
            if (PasswordPolicy.Problem(password) is string pp)
            {
                this.Flash(pp, "danger");
                return await FormPage(u, membership, ct);
            }
            await SetPasswordAsync(u, password, ct);
        }
        TouchUser(u);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"User {u.FullName} updated.", "success");
        return Redirect("/auth/users");
    }

    /// <summary>User.set_password: new hash; this login's other sessions end; the signed-in person's own session carries on.</summary>
    private async Task SetPasswordAsync(User u, string password, CancellationToken ct)
    {
        u.PasswordHash = PasswordHasher.Hash(password);
        own.Refresh(HttpContext, u);
        await own.EndOthersAsync(HttpContext, u.Id, ct);
    }

    [HttpPost("users/{uid:int}/delete")]
    [AnyMember]   // _can_manage_users("delete"), below
    public async Task<IActionResult> Delete(int uid, CancellationToken ct)
    {
        if (!CanManageUsers(Rights.Delete)) return Redirect("/dashboard");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SerializeAsync("members", Cid);
        var u = await people.GetMemberAsync(uid, Access, ct);
        if (u is null) return NotFound();
        if (u.Id == Me)
        {
            this.Flash("Cannot delete yourself.", "danger");
            return Redirect("/auth/users");
        }
        if (await people.IsAdminAsync(u, Cid, ct))
        {
            this.Flash("Cannot delete admin users.", "danger");
            return Redirect("/auth/users");
        }
        if (await people.LoginChangeRefusalAsync(u, Access, ct) is string refusal)
        {
            this.Flash(refusal + " To stop them working here, remove them from this company in Settings → Members.", "danger");
            return Redirect("/auth/users");
        }
        u.IsActive = false;
        TouchUser(u);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"User {u.FullName} deactivated.", "success");
        return Redirect("/auth/users");
    }

    public sealed class RightsModel
    {
        public required User User { get; init; }
        public IReadOnlyList<Modules.Section> Sections { get; init; } = [];
        public IReadOnlyDictionary<string, UserPermission> Perms { get; init; } = new Dictionary<string, UserPermission>();
        public bool IsConfigured { get; init; }
    }

    private static readonly IReadOnlyList<Modules.Section> HrSections = Modules.All.First(m => m.Key == Modules.Hr).Sections;

    private async Task<(User? User, IActionResult? Refused)> RightsTargetAsync(int uid, CancellationToken ct)
    {
        var u = await people.GetMemberAsync(uid, Access, ct);
        if (u is null) return (null, NotFound());
        if (await people.IsAdminAsync(u, Cid, ct))
        {
            this.Flash("Admin accounts are managed from ERP hub Settings.", "danger");
            return (null, Redirect("/auth/users"));
        }
        if (u.Id == Me)
        {
            // An HR manager whose rights an admin narrowed could tick everything back on.
            this.Flash("You cannot change your own HR rights — ask an admin.", "danger");
            return (null, Redirect("/auth/users"));
        }
        return (u, null);
    }

    [HttpGet("users/{uid:int}/hr-rights")]
    [AnyMember]   // _can_manage_users("edit"), below
    public async Task<IActionResult> HrRights(int uid, CancellationToken ct)
    {
        if (!CanManageUsers(Rights.Edit)) return Redirect("/dashboard");
        var (u, refused) = await RightsTargetAsync(uid, ct);
        if (refused is not null) return refused;
        var perms = (await db.UserPermissions.AsNoTracking().Where(p => p.UserId == u!.Id).OrderBy(p => p.Id).ToListAsync(ct))
            .GroupBy(p => p.Resource).ToDictionary(g => g.Key, g => g.Last());
        return View("HrRights", new RightsModel
        {
            User = u!, Sections = HrSections, Perms = perms, IsConfigured = HrSections.Any(s => perms.ContainsKey(s.Key)),
        });
    }

    [HttpPost("users/{uid:int}/hr-rights")]
    [AnyMember]   // _can_manage_users("edit"), below
    public async Task<IActionResult> HrRightsPost(int uid, CancellationToken ct)
    {
        if (!CanManageUsers(Rights.Edit)) return Redirect("/dashboard");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SerializeAsync("members", Cid);
        var (u, refused) = await RightsTargetAsync(uid, ct);
        if (refused is not null) return refused;
        var keys = HrSections.Select(s => s.Key).ToList();
        await db.UserPermissions.Where(p => p.UserId == u!.Id && keys.Contains(p.Resource)).ExecuteDeleteAsync(ct);
        bool On(string section, string action) => Request.Form[$"perm_{section}_{action}"].FirstOrDefault() == "on";
        foreach (var s in HrSections)
            db.Add(new UserPermission
            {
                UserId = u!.Id, Resource = s.Key,
                CanView = On(s.Key, Rights.View), CanCreate = On(s.Key, Rights.Create), CanEdit = On(s.Key, Rights.Edit),
                CanApprove = On(s.Key, Rights.Approve), CanDelete = On(s.Key, Rights.Delete),
            });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"HR rights saved for {u!.FullName}.", "success");
        return Redirect("/auth/users");
    }

    [HttpPost("users/{uid:int}/reset-password")]
    [AnyMember]   // admins only, below
    public async Task<IActionResult> ResetPassword(int uid, CancellationToken ct)
    {
        if (!IsAdmin) return AccessDenied();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var u = await people.GetMemberAsync(uid, Access, ct);
        if (u is null) return NotFound();
        if (await people.LoginChangeRefusalAsync(u, Access, ct) is string refusal)
        {
            this.Flash(refusal, "danger");
            return Redirect("/auth/users");
        }
        string pw = FormStr("new_password");
        if (PasswordPolicy.Problem(pw) is string problem)
        {
            this.Flash(problem, "danger");
            return Redirect("/auth/users");
        }
        await SetPasswordAsync(u, pw, ct);
        TouchUser(u);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"Password reset for {u.FullName}.", "success");
        return Redirect("/auth/users");
    }
}
