using System.Globalization;
using System.Text;
using Accountix.Platform.Services;

namespace Accountix.Platform.Controllers;

// Administration tabs: rights & access, members & roles, invitations, audit log (+ CSV / Excel / PDF).
public sealed partial class SettingsController
{
    public sealed record RightsRow(User User, string Role, bool IsAdmin);
    public sealed record RightsTab(IReadOnlyList<RightsRow> Users, IReadOnlySet<int> Configured, int MeId);
    public sealed record UserRightsPage(User U, string? Role, bool IsAdmin, bool Configured, IReadOnlyDictionary<string, UserPermission> Perms,
                                        IReadOnlyDictionary<string, bool> Flags, string? LoginRefusal);
    public sealed record MembersTab(Company? Company, IReadOnlyList<(CompanyMembership M, User? U, string Role)> Members, int ActiveCount,
                                    int MemberLimit, int ActiveAdmins, int? AdminRoleId, int? CreatedBy, IReadOnlyList<string> Roles,
                                    IReadOnlyList<(CompanyInvitation Inv, string Role, string? Inviter)> Invitations, int MeId);

    public sealed class AuditTab(IQueryCollection args)
    {
        public required IReadOnlyList<AuditLog> Rows { get; init; }
        public required int Total { get; init; }
        public required int Page { get; init; }
        public required int Pages { get; init; }
        public required IReadOnlyDictionary<int, string> Members { get; init; }

        public string Arg(string key) => args[key].FirstOrDefault() ?? "";

        /// <summary>url_for(endpoint, extra, **args.to_dict()): format goes first; page replaces in place or goes last.</summary>
        public string Url(string path, (string Key, string Value)? extra = null)
        {
            var items = args.Select(kv => (kv.Key, Value: kv.Value.FirstOrDefault() ?? "")).ToList();
            if (extra is { } e)
            {
                int at = items.FindIndex(i => i.Key == e.Key);
                if (at >= 0) items[at] = (e.Key, e.Value);
                else if (e.Key == "format") items.Insert(0, (e.Key, e.Value));
                else items.Add((e.Key, e.Value));
            }
            return items.Count == 0 ? path
                : path + "?" + string.Join("&", items.Select(i => Uri.EscapeDataString(i.Key) + "=" + Uri.EscapeDataString(i.Value)));
        }
    }

    private static readonly string[] MemberRoles = [Role.Admin, Role.Manager, Role.Employee];

    private int Cid => Me.CompanyId ?? throw new NoActiveCompanyException("No active company.");

    // ── Rights & access ──────────────────────────────────────────────────

    private async Task<object?> RightsTabAsync(CancellationToken ct)
    {
        int cid = Cid;
        var rows = await db.Memberships.AsNoTracking().Where(m => m.CompanyId == cid && m.Status == CompanyMembership.StatusActive)
            .Join(db.Users.AsNoTracking(), m => m.UserId, u => u.Id, (m, u) => new { u, Role = m.Role.Name })
            .OrderBy(x => x.u.FullName).ToListAsync(ct);
        var configured = (await db.UserPermissions.AsNoTracking().Select(p => p.UserId).Distinct().ToListAsync(ct)).ToHashSet();
        return new RightsTab(rows.Select(x => new RightsRow(x.u, x.Role, x.u.SuperAdmin || x.Role == Role.Admin)).ToList(), configured, Me.User.Id);
    }

    /// <summary>_managed_user_or_404: an active member of this company; a platform account only for a super admin.</summary>
    private async Task<(User? User, IActionResult? Refusal)> ManagedUserAsync(int uid, CancellationToken ct)
    {
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == uid, ct);
        if (u is null || Me.CompanyId is not int cid
            || !await db.Memberships.AnyAsync(m => m.CompanyId == cid && m.UserId == uid && m.Status == CompanyMembership.StatusActive, ct))
            return (null, NotFound());
        if (u.SuperAdmin && !Me.IsSuperAdmin)
            return (null, StatusCode(403));
        return (u, null);
    }

    [HttpGet("users/{uid:int}/rights")]
    public async Task<IActionResult> UserRights(int uid, CancellationToken ct)
    {
        if (Require("rights") is { } denied) return denied;
        var (u, refusal) = await ManagedUserAsync(uid, ct);
        if (refusal is not null) return refusal;
        int cid = Cid;
        var m = await db.Memberships.AsNoTracking().Include(x => x.Role).FirstAsync(x => x.CompanyId == cid && x.UserId == uid && x.Status == "active", ct);
        var perms = await db.UserPermissions.AsNoTracking().Where(p => p.UserId == uid).ToDictionaryAsync(p => p.Resource, ct);
        var flags = Modules.All.ToDictionary(x => x.Key, x => m.ModuleFlag(x.Key) == true);
        return View("UserRights", new UserRightsPage(u!, m.Role?.Name, u!.SuperAdmin || m.Role?.Name == Role.Admin, perms.Count > 0, perms, flags,
                                                     await LoginChanges.RefusalAsync(db, u, cid, Me.User.Id, ct)));
    }

    [HttpPost("users/{uid:int}/rights")]
    public async Task<IActionResult> SaveUserRights(int uid, CancellationToken ct)
    {
        if (Require("rights") is { } denied) return denied;
        var (u, refusal) = await ManagedUserAsync(uid, ct);
        if (refusal is not null) return refusal;
        int cid = Cid;
        var f = Request.Form;
        string newPw = f["new_password"].ToString(), confirm = f["confirm_password"].ToString();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (newPw.Length > 0 || confirm.Length > 0)
        {
            if (await LoginChanges.RefusalAsync(db, u, cid, Me.User.Id, ct) is { } no)
            {
                this.Flash(no, "error");
                return Redirect($"/settings/users/{uid}/rights");
            }
            if (AccountSecurity.PasswordProblem(newPw) is { } problem)
            {
                this.Flash(problem, "error");
                return Redirect($"/settings/users/{uid}/rights");
            }
            if (newPw != confirm)
            {
                this.Flash("Passwords do not match — enter the new password twice.", "error");
                return Redirect($"/settings/users/{uid}/rights");
            }
            await signIn.SetPasswordAsync(HttpContext, u!, newPw, ct);
            u!.UpdatedAt = UtcNowMicro;
        }
        // Module access is set for THIS company (the account's switches are shared by every company).
        var membership = await db.Memberships.FirstAsync(x => x.CompanyId == cid && x.UserId == uid && x.Status == "active", ct);
        membership.SetModuleFlags(Modules.All.ToDictionary(x => x.Key, x => f[$"module_{x.Key}"] == "on"));
        // The rows are rewritten from the grid, so stored state is exactly what the admin saw.
        await db.UserPermissions.Where(p => p.UserId == uid && p.CompanyId == cid).ExecuteDeleteAsync(ct);
        foreach (var mod in Modules.All)
            foreach (var sec in mod.Sections)
                db.UserPermissions.Add(new UserPermission
                {
                    UserId = uid, Resource = sec.Key,
                    CanView = f[$"perm_{sec.Key}_view"] == "on", CanCreate = f[$"perm_{sec.Key}_create"] == "on",
                    CanEdit = f[$"perm_{sec.Key}_edit"] == "on", CanApprove = f[$"perm_{sec.Key}_approve"] == "on",
                    CanDelete = f[$"perm_{sec.Key}_delete"] == "on",
                });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"Access rights saved for {u!.FullName}.", "success");
        return ToTab("rights");
    }

    [HttpPost("users/{uid:int}/reset-rights")]
    public async Task<IActionResult> ResetRights(int uid, CancellationToken ct)
    {
        if (Require("rights") is { } denied) return denied;
        var (u, refusal) = await ManagedUserAsync(uid, ct);
        if (refusal is not null) return refusal;
        int cid = Cid;
        await db.UserPermissions.Where(p => p.UserId == uid && p.CompanyId == cid).ExecuteDeleteAsync(ct);
        this.Flash($"Rights reset for {u!.FullName} — unrestricted section access again.", "success");
        return ToTab("rights");
    }

    [HttpPost("users/{uid:int}/toggle-active")]
    public async Task<IActionResult> ToggleActive(int uid, CancellationToken ct)
    {
        if (Require("rights") is { } denied) return denied;
        var (u, refusal) = await ManagedUserAsync(uid, ct);
        if (refusal is not null) return refusal;
        if (u!.Id == Me.User.Id)
        {
            this.Flash("You cannot deactivate your own account.", "error");
            return ToTab("rights");
        }
        if (await LoginChanges.RefusalAsync(db, u, Cid, Me.User.Id, ct) is { } no)
        {
            // Deactivating the login would lock them out of every company.
            this.Flash(no + " To stop them working here, remove them from this company under Members.", "error");
            return ToTab("rights");
        }
        u.IsActive = !u.Active;
        u.UpdatedAt = UtcNowMicro;
        await db.SaveChangesAsync(ct);
        this.Flash($"{u.FullName} {(u.Active ? "activated" : "deactivated")}.", "success");
        return ToTab("rights");
    }

    // ── Members & roles ──────────────────────────────────────────────────

    private async Task<object?> MembersTabAsync(CancellationToken ct)
    {
        int cid = Cid;
        var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct);
        var roles = await db.Roles.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        var members = await db.Memberships.AsNoTracking().Where(m => m.CompanyId == cid)
            .GroupJoin(db.Users.AsNoTracking(), m => m.UserId, u => u.Id, (m, us) => new { m, us })
            .SelectMany(x => x.us.DefaultIfEmpty(), (x, u) => new { x.m, u })
            .ToListAsync(ct);
        var sorted = members.OrderBy(x => x.m.Status, StringComparer.Ordinal).ThenBy(x => x.m.JoinedAt)
            .OrderBy(x => x.m.Status, StringComparer.Ordinal).ThenBy(x => x.u?.FullName ?? "", StringComparer.Ordinal)
            .Select(x => (x.m, x.u, roles.GetValueOrDefault(x.m.RoleId, ""))).ToList();
        int? adminRole = roles.FirstOrDefault(r => r.Value == Role.Admin).Key is int k && k != 0 ? k : null;
        int activeCount = members.Count(x => x.m.Status == CompanyMembership.StatusActive);
        int activeAdmins = members.Count(x => x.m.Status == CompanyMembership.StatusActive && x.m.RoleId == adminRole);
        var invitations = await db.Invitations.AsNoTracking().Where(i => i.CompanyId == cid)
            .OrderByDescending(i => i.CreatedAt).ThenBy(i => i.Id).ToListAsync(ct);
        var inviterIds = invitations.Where(i => i.InvitedBy != null).Select(i => i.InvitedBy!.Value).Distinct().ToList();
        var inviters = await db.Users.AsNoTracking().Where(u => inviterIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        int limit;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            limit = (await firstUse.GlobalLimitsAsync(ct)).MemberLimitFor(company!);
            await tx.CommitAsync(ct);
        }
        return new MembersTab(company, sorted, activeCount, limit, activeAdmins, adminRole, company?.CreatedBy,
            MemberRoles.Where(r => roles.ContainsValue(r)).OrderBy(r => roles.First(x => x.Value == r).Key).ToList(),
            invitations.Select(i => (i, roles.GetValueOrDefault(i.RoleId, ""), i.InvitedBy is int ib ? inviters.GetValueOrDefault(ib) : null)).ToList(),
            Me.User.Id);
    }

    private IActionResult? AdminOnly() => Me.IsAdmin ? null : StatusCode(403);

    [HttpGet("members/")]
    public async Task<IActionResult> MembersPage(CancellationToken ct) => AdminOnly() ?? await Index("members", ct);

    [HttpPost("members/{membershipId:int}/role")]
    public async Task<IActionResult> MemberRole(int membershipId, CancellationToken ct)
    {
        if (AdminOnly() is { } no) return no;
        var back = Redirect("/settings/members/");
        int cid = Cid;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SerializeAsync("members", cid);
        var m = await db.Memberships.FirstOrDefaultAsync(x => x.Id == membershipId, ct);
        if (m is null || m.CompanyId != cid) return NotFound();
        var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct);
        if (m.Status != CompanyMembership.StatusActive) { this.Flash("Only active members can have their role changed.", "error"); return back; }
        if (company is not null && m.UserId == company.CreatedBy) { this.Flash("The company owner's role is fixed and cannot be changed.", "error"); return back; }
        if (m.UserId == Me.User.Id) { this.Flash("You cannot change your own role.", "error"); return back; }
        string wanted = Request.Form["role"].ToString();
        var role = await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Name == wanted, ct);
        if (role is null || !MemberRoles.Contains(role.Name)) { this.Flash("Pick a valid role.", "error"); return back; }
        var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == m.UserId, ct);
        string? oldRole = await db.Roles.AsNoTracking().Where(r => r.Id == m.RoleId).Select(r => r.Name).FirstOrDefaultAsync(ct);
        m.RoleId = role.Id;
        await AccessDefaults.RoleChangedAsync(db, m, oldRole, role.Name, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"Role of {u?.FullName ?? "member"} changed to {ViewHelpers.Title(role.Name)}.", "success");
        return back;
    }

    [HttpPost("members/{membershipId:int}/remove")]
    public async Task<IActionResult> MemberRemove(int membershipId, CancellationToken ct)
    {
        if (AdminOnly() is { } no) return no;
        var back = Redirect("/settings/members/");
        int cid = Cid;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SerializeAsync("members", cid);
        var m = await db.Memberships.FirstOrDefaultAsync(x => x.Id == membershipId, ct);
        if (m is null || m.CompanyId != cid) return NotFound();
        var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct);
        if (company is not null && m.UserId == company.CreatedBy) { this.Flash("The company owner cannot be removed.", "error"); return back; }
        int? adminRole = await db.Roles.Where(r => r.Name == Role.Admin).Select(r => (int?)r.Id).FirstOrDefaultAsync(ct);
        if (m.Status == CompanyMembership.StatusActive && adminRole is int ar)
        {
            int admins = await db.Memberships.CountAsync(x => x.CompanyId == m.CompanyId && x.Status == CompanyMembership.StatusActive && x.RoleId == ar, ct);
            if (m.RoleId == ar && admins <= 1) { this.Flash("Cannot remove the last active admin of the company.", "error"); return back; }
        }
        var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == m.UserId, ct);
        m.Status = CompanyMembership.Removed;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"{u?.FullName ?? "Member"} removed from the company.", "success");
        return back;
    }

    [HttpPost("members/{membershipId:int}/restore")]
    public async Task<IActionResult> MemberRestore(int membershipId, CancellationToken ct)
    {
        if (AdminOnly() is { } no) return no;
        var back = Redirect("/settings/members/");
        int cid = Cid;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SerializeAsync("members", cid);
        var m = await db.Memberships.FirstOrDefaultAsync(x => x.Id == membershipId, ct);
        if (m is null || m.CompanyId != cid) return NotFound();
        // Only a REMOVED membership is discarded (never an active member, the owner or the last admin).
        if (m.Status != CompanyMembership.Removed)
        {
            this.Flash("Only a removed membership can be discarded. Remove the member first.", "error");
            return back;
        }
        var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == m.UserId, ct);
        db.Memberships.Remove(m);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"Removed membership of {u?.FullName ?? "member"} discarded.", "success");
        return back;
    }

    // ── Invitations ──────────────────────────────────────────────────────

    [HttpPost("invite")]
    public async Task<IActionResult> Invite(CancellationToken ct)
    {
        if (AdminOnly() is { } no) return no;
        var back = Redirect("/settings/members/");
        var company = Me.CompanyId is int cid0 ? await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid0, ct) : null;
        if (company is null) { this.Flash("No active company to invite into.", "error"); return back; }
        string email = Request.Form["email"].ToString().Trim().ToLowerInvariant();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SerializeAsync("members", company.Id);
        var user = email.Length > 0 ? await db.Users.AsNoTracking().Where(u => u.Email.ToLower() == email).OrderBy(u => u.Id).FirstOrDefaultAsync(ct) : null;
        if (user is null) { this.Flash("No registered user has that email address.", "error"); return back; }
        string wanted = Request.Form["role"].ToString();
        var role = await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Name == wanted, ct);
        if (role is null || !MemberRoles.Contains(role.Name)) { this.Flash("Pick a valid role.", "error"); return back; }
        if (await db.Memberships.AnyAsync(m => m.CompanyId == company.Id && m.UserId == user.Id && m.Status != CompanyMembership.Removed, ct))
        {
            this.Flash($"{user.FullName} is already a member of this company.", "error");
            return back;
        }
        int active = await db.Memberships.CountAsync(m => m.CompanyId == company.Id && m.Status == CompanyMembership.StatusActive, ct);
        int limit = (await firstUse.GlobalLimitsAsync(ct)).MemberLimitFor(company);
        if (active >= limit)
        {
            this.Flash($"Member limit reached ({limit} active members) — no more invitations can be sent.", "error");
            return back;
        }
        if (await db.Invitations.AnyAsync(i => i.CompanyId == company.Id && i.Email.ToLower() == email && i.Status == CompanyInvitation.Sent, ct))
        {
            this.Flash("An invitation to that email is already pending.", "error");
            return back;
        }
        db.Invitations.Add(new CompanyInvitation
        {
            CompanyId = company.Id, Email = email, RoleId = role.Id, InvitedBy = Me.User.Id, Status = CompanyInvitation.Sent,
            CreatedAt = UtcNowMicro,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"Invitation sent to {email}.", "success");
        return back;
    }

    [HttpPost("invite/{invitationId:int}/revoke")]
    public async Task<IActionResult> RevokeInvite(int invitationId, CancellationToken ct)
    {
        if (AdminOnly() is { } no) return no;
        var inv = await db.Invitations.FirstOrDefaultAsync(i => i.Id == invitationId, ct);
        if (inv is null || inv.CompanyId != Me.CompanyId) return NotFound();
        inv.Status = CompanyInvitation.Revoked;
        await db.SaveChangesAsync(ct);
        this.Flash($"Invitation to {inv.Email} revoked.", "success");
        return Redirect("/settings/members/");
    }

    private async Task<object?> InvitesTabAsync(CancellationToken ct)
    {
        string email = (Me.User.Email ?? "").ToLowerInvariant();
        var invites = await db.Invitations.AsNoTracking().Include(i => i.Company).Include(i => i.Role)
            .Where(i => i.Email.ToLower() == email && i.Status == CompanyInvitation.Sent)
            .OrderByDescending(i => i.CreatedAt).ThenBy(i => i.Id).ToListAsync(ct);
        return invites.Select(i => (i, (string?)i.Company?.Name, i.Role?.Name ?? "")).ToList()
            as IReadOnlyList<(CompanyInvitation, string?, string)>;
    }

    [HttpGet("invitations/")]
    public Task<IActionResult> InvitationsPage(CancellationToken ct) => Index("invites", ct);

    [HttpPost("invitations/{invitationId:int}/accept")]
    public async Task<IActionResult> AcceptInvitation(int invitationId, CancellationToken ct)
    {
        string? next = SafeUrl.Local(Request.Form["next"]);
        var back = Redirect(next ?? "/settings/invitations/");
        var inv = await db.Invitations.FirstOrDefaultAsync(i => i.Id == invitationId, ct);
        if (inv is null || (inv.Email ?? "").ToLowerInvariant() != (Me.User.Email ?? "").ToLowerInvariant()) return NotFound();
        if (inv.Status != CompanyInvitation.Sent) { this.Flash("That invitation is no longer valid.", "error"); return back; }
        var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == inv.CompanyId, ct);
        if (company is null || !company.Active) { this.Flash("That company is no longer accepting members.", "error"); return back; }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SerializeAsync("members", company.Id);
        await db.SerializeAsync("joins", Me.User.Id);
        await db.Entry(inv).ReloadAsync(ct);
        if (inv.Status != CompanyInvitation.Sent) { this.Flash("That invitation is no longer valid.", "error"); return back; }
        var existing = await db.Memberships.FirstOrDefaultAsync(m => m.CompanyId == company.Id && m.UserId == Me.User.Id, ct);
        if (existing is not null && existing.Status != CompanyMembership.Removed)
        {
            this.Flash("You are already a member of that company.", "error");
            return back;
        }
        var limits = await firstUse.GlobalLimitsAsync(ct);
        int active = await db.Memberships.CountAsync(m => m.CompanyId == company.Id && m.Status == CompanyMembership.StatusActive, ct);
        int limit = limits.MemberLimitFor(company);
        if (active >= limit)
        {
            this.Flash($"Cannot join {company.Name}: it has reached its member limit ({limit}).", "error");
            return back;
        }
        if (limits.JoinLimitFor(Me.User) is int cap
            && await db.Memberships.CountAsync(m => m.UserId == Me.User.Id && m.Status == CompanyMembership.StatusActive, ct) >= cap)
        {
            this.Flash($"You already belong to the maximum number of companies allowed for your account ({cap}).", "error");
            return back;
        }
        inv.Status = CompanyInvitation.Accepted;
        if (existing is not null)
        {
            // Re-hire: the REMOVED row survives (unique company + user), so it is reactivated.
            existing.Status = CompanyMembership.StatusActive;
            existing.RoleId = inv.RoleId;
            await db.SaveChangesAsync(ct);
        }
        else
        {
            await db.SaveChangesAsync(ct);
            await memberships.AddAsync(company.Id, Me.User.Id, inv.RoleId, CompanyMembership.StatusActive, ct: ct);
        }
        await tx.CommitAsync(ct);
        signIn.SetCompany(HttpContext, company.Id);
        this.Flash($"Welcome to {company.Name}.", "success");
        return Redirect(next ?? "/dashboard/");
    }

    [HttpPost("invitations/{invitationId:int}/decline")]
    public async Task<IActionResult> DeclineInvitation(int invitationId, CancellationToken ct)
    {
        var back = Redirect(SafeUrl.Local(Request.Form["next"]) ?? "/settings/invitations/");
        var inv = await db.Invitations.FirstOrDefaultAsync(i => i.Id == invitationId, ct);
        if (inv is null || (inv.Email ?? "").ToLowerInvariant() != (Me.User.Email ?? "").ToLowerInvariant()) return NotFound();
        if (inv.Status != CompanyInvitation.Sent) { this.Flash("That invitation is no longer valid.", "error"); return back; }
        inv.Status = CompanyInvitation.Revoked;
        await db.SaveChangesAsync(ct);
        this.Flash("Invitation declined.", "success");
        return back;
    }

    // ── Audit log ────────────────────────────────────────────────────────

    /// <summary>_audit_members: {user_id: name} of this company's members.</summary>
    private async Task<Dictionary<int, string>> AuditMembersAsync(CancellationToken ct)
    {
        int cid = Cid;
        return await db.Memberships.AsNoTracking().Where(m => m.CompanyId == cid)
            .Join(db.Users.AsNoTracking(), m => m.UserId, u => u.Id, (m, u) => new { u.Id, u.FullName, u.Email })
            .Distinct().ToDictionaryAsync(x => x.Id, x => string.IsNullOrEmpty(x.FullName) ? x.Email : x.FullName, ct);
    }

    private async Task<object?> AuditTabAsync(CancellationToken ct)
    {
        var members = await AuditMembersAsync(ct);
        var q = AuditSearch.Search(db, Cid, Request.Query, members.Keys);
        int page = Math.Max(int.TryParse(Request.Query["page"], out int p) ? p : 1, 1);
        const int per = 50;
        int total = await q.CountAsync(ct);
        var rows = await q.Skip((page - 1) * per).Take(per).ToListAsync(ct);
        return new AuditTab(Request.Query)
        {
            Rows = rows, Total = total, Page = page, Pages = Math.Max((total + per - 1) / per, 1), Members = members,
        };
    }

    /// <summary>The filtered audit trail as CSV (or Excel / PDF), same filters as the screen, 20,000 rows at most.</summary>
    [HttpGet("audit-log.csv")]
    public async Task<IActionResult> AuditCsv(CancellationToken ct)
    {
        if (!Me.IsAdmin) return StatusCode(403);
        var members = await AuditMembersAsync(ct);
        var rows = await AuditSearch.Search(db, Cid, Request.Query, members.Keys).Take(20000).ToListAsync(ct);
        string fmt = Request.Query["format"].FirstOrDefault() ?? "csv";
        string[] headers = ["When (UTC)", "User", "Action", "Module", "Record", "Reference", "Summary", "Changes", "IP"];
        if (fmt is "excel" or "pdf")
        {
            var export = HttpContext.RequestServices.GetRequiredService<IReportExport>();
            var file = await export.ExportTableAsync(fmt, new ReportTable
            {
                Title = "Audit Log",
                Period = $"{rows.Count} entries",
                Filters = Request.Query.Where(kv => kv.Key is not ("format" or "page") && kv.Value.ToString().Length > 0)
                    .Select(kv => ("", $"{kv.Key}: {kv.Value.FirstOrDefault()}")).ToList(),
                FilePeriod = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Columns = headers.Select(h => new ReportColumn(h)).ToList(),
                Rows = rows.Select(r => new ReportRow([r.CreatedAt, r.UserName ?? "system", r.Action, r.Module ?? "", r.EntityType ?? "",
                                                       r.Reference ?? "", r.Summary ?? "", AuditSearch.ChangesText(r), r.IpAddress ?? ""])).ToList(),
            }, ct);
            return File(file.Content, file.ContentType, file.FileName);
        }
        var sb = new StringBuilder();
        void Row(IEnumerable<string> cells) => sb.Append(string.Join(",", cells.Select(Csv))).Append("\r\n");
        Row(headers);
        foreach (var r in rows)
            Row([r.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), r.UserName ?? "system", r.Action, r.Module ?? "",
                 r.EntityType ?? "", r.Reference ?? "", r.Summary ?? "", AuditSearch.ChangesText(r), r.IpAddress ?? ""]);
        Response.Headers.ContentDisposition = "attachment; filename=audit-log.csv";
        return Content(sb.ToString(), "text/csv; charset=utf-8");
    }

    /// <summary>Python's csv.writer quoting (QUOTE_MINIMAL).</summary>
    private static string Csv(string v) =>
        v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
}
