namespace Accountix.Hr.Services;

/// <summary>A person seen inside the active company: the account row and this company's membership.</summary>
public sealed class Member
{
    public User User { get; init; } = null!;
    public CompanyMembership Membership { get; init; } = null!;
    public int Id => User.Id;
    public string FullName => User.FullName;
    public string? Department => Membership.Department;
    public string? Designation => Membership.Designation;
    public int? ManagerId => Membership.ManagerId;
}

/// <summary>
/// The people helpers the HR routes lean on (shared/models/base.py User,
/// shared/tenancy.py): employees of the active company, a manager's team,
/// employment details kept per company on the membership, who decides a
/// request, whose login this company may change. One instance per request.
/// </summary>
public sealed class People(AccountixDbContext db)
{
    private Dictionary<string, int>? _roles;

    public async Task<Dictionary<string, int>> RoleIdsAsync(CancellationToken ct = default) =>
        _roles ??= await db.Roles.AsNoTracking().ToDictionaryAsync(r => r.Name, r => r.Id, ct);

    public async Task<int?> RoleIdAsync(string name, CancellationToken ct = default) =>
        (await RoleIdsAsync(ct)).TryGetValue(name, out int id) ? id : null;

    public async Task<string?> RoleNameAsync(int roleId, CancellationToken ct = default) =>
        (await RoleIdsAsync(ct)).FirstOrDefault(kv => kv.Value == roleId).Key;

    /// <summary>
    /// User.employees(): active members of the company, admins left out (they
    /// regulate, they are not staff). Joined to the membership, so filters on
    /// department or role read this company's values.
    /// </summary>
    public async Task<IQueryable<Member>> EmployeesAsync(int companyId, CancellationToken ct = default)
    {
        int? admin = await RoleIdAsync(Role.Admin, ct);
        return from m in db.Memberships
               where m.CompanyId == companyId && m.Status == CompanyMembership.StatusActive
                     && (admin == null || m.RoleId != admin)
               join u in db.Users on m.UserId equals u.Id
               select new Member { User = u, Membership = m };
    }

    /// <summary>User.reports_of(): active members here whose manager (this company's) is this person.</summary>
    public IQueryable<Member> ReportsOf(int managerId, int companyId) =>
        from m in db.Memberships
        where m.CompanyId == companyId && m.Status == CompanyMembership.StatusActive && m.ManagerId == managerId
        join u in db.Users on m.UserId equals u.Id
        where u.IsActive == true
        select new Member { User = u, Membership = m };

    /// <summary>_company_managers(): active members whose role HERE is manager, by name.</summary>
    public async Task<List<User>> CompanyManagersAsync(int companyId, CancellationToken ct = default)
    {
        int? mgr = await RoleIdAsync(Role.Manager, ct);
        if (mgr is null) return [];
        return await (from m in db.Memberships
                      where m.CompanyId == companyId && m.Status == CompanyMembership.StatusActive && m.RoleId == mgr
                      join u in db.Users on m.UserId equals u.Id
                      orderby u.FullName
                      select u).AsNoTracking().ToListAsync(ct);
    }

    /// <summary>
    /// shared.tenancy.get_member: a user id from the URL resolves only to an
    /// active member of the active company (a super admin only to themself).
    /// Tracked, so callers may change it.
    /// </summary>
    public async Task<User?> GetMemberAsync(int uid, EffectiveAccess access, CancellationToken ct = default)
    {
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == uid, ct);
        if (u is null) return null;
        if (u.IsSuperAdmin == true && access.User.Id == u.Id) return u;
        if (access.CompanyId is not int cid) return null;
        bool member = await db.Memberships.AnyAsync(m => m.CompanyId == cid && m.UserId == uid
                                                         && m.Status == CompanyMembership.StatusActive, ct);
        return member ? u : null;
    }

    /// <summary>User._employment(): this person's membership row in the company, any status (null: none).</summary>
    public Task<CompanyMembership?> EmploymentAsync(int userId, int companyId, CancellationToken ct = default) =>
        db.Memberships.Where(m => m.CompanyId == companyId && m.UserId == userId).OrderBy(m => m.Id).FirstOrDefaultAsync(ct);

    /// <summary>membership_in(cid): the ACTIVE membership.</summary>
    public Task<CompanyMembership?> ActiveMembershipAsync(int userId, int companyId, CancellationToken ct = default) =>
        db.Memberships.Where(m => m.CompanyId == companyId && m.UserId == userId && m.Status == CompanyMembership.StatusActive)
            .OrderBy(m => m.Id).FirstOrDefaultAsync(ct);

    /// <summary>(manager_id, designation, department) in the company: the membership's, else the account copy.</summary>
    public async Task<(int? ManagerId, string? Designation, string? Department)> EmploymentInAsync(
        User user, int? companyId, CancellationToken ct = default)
    {
        if (companyId is int cid && await EmploymentAsync(user.Id, cid, ct) is { } m)
            return (m.ManagerId, m.Designation, m.Department);
        return (user.ManagerId, user.Designation, user.Department);
    }

    /// <summary>
    /// Employment setters (User.manager_id / designation / department): write
    /// this company's membership; the account copy follows only for someone
    /// who belongs to no other company (removed memberships aside).
    /// </summary>
    public async Task SetEmploymentAsync(User user, int? companyId, Action<CompanyMembership>? onMembership,
                                         Action<User> onAccount, CancellationToken ct = default)
    {
        if (companyId is int cid && await EmploymentAsync(user.Id, cid, ct) is { } m)
        {
            onMembership?.Invoke(m);
            bool elsewhere = await db.Memberships.AnyAsync(x => x.UserId == user.Id && x.CompanyId != cid
                                                                && x.Status != CompanyMembership.Removed, ct);
            if (elsewhere) return;
        }
        onAccount(user);
    }

    /// <summary>role_in(cid): the role on the active membership, else the account's legacy role.</summary>
    public async Task<string> RoleInAsync(User user, int? companyId, CancellationToken ct = default)
    {
        if (companyId is int cid && await ActiveMembershipAsync(user.Id, cid, ct) is { } m)
            return await RoleNameAsync(m.RoleId, ct) ?? "";
        return await RoleNameAsync(user.RoleId, ct) ?? "";
    }

    /// <summary>u.is_admin() for someone other than the signed-in person, in the active company.</summary>
    public async Task<bool> IsAdminAsync(User user, int? companyId, CancellationToken ct = default) =>
        user.IsSuperAdmin == true || await RoleInAsync(user, companyId, ct) == Role.Admin;

    /// <summary>u.can(section, action) for someone else, in the active company (no row: unrestricted).</summary>
    public async Task<bool> CanAsync(User user, int companyId, string section, string action, CancellationToken ct = default)
    {
        if (await IsAdminAsync(user, companyId, ct)) return true;
        var row = await db.UserPermissions.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == user.Id && p.Resource == section, ct);
        return row is null || row.Allows(action);
    }

    /// <summary>
    /// leave_flow.approver_for: who decides this person's leave (or timesheet)
    /// here. Their manager when that manager is an active admin or manager of
    /// this company (and holds <paramref name="right"/>), else the company's
    /// first admin other than them; null when nobody else can decide.
    /// </summary>
    public async Task<int?> ApproverForAsync(User user, int companyId, (string Section, string Action)? right = null,
                                             CancellationToken ct = default)
    {
        var roles = await RoleIdsAsync(ct);
        var lead = new List<int>();
        if (roles.TryGetValue(Role.Admin, out int a)) lead.Add(a);
        if (roles.TryGetValue(Role.Manager, out int mg)) lead.Add(mg);
        var (managerId, _, _) = await EmploymentInAsync(user, companyId, ct);
        if (managerId is int mid && mid != user.Id
            && await db.Memberships.AnyAsync(m => m.CompanyId == companyId && m.Status == CompanyMembership.StatusActive
                                                  && m.UserId == mid && lead.Contains(m.RoleId), ct))
        {
            var manager = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == mid, ct);
            if (right is null || (manager is not null && await CanAsync(manager, companyId, right.Value.Section, right.Value.Action, ct)))
                return mid;
        }
        if (!roles.TryGetValue(Role.Admin, out int adminRole)) return null;
        return await db.Memberships
            .Where(m => m.CompanyId == companyId && m.Status == CompanyMembership.StatusActive
                        && m.RoleId == adminRole && m.UserId != user.Id)
            .OrderBy(m => m.Id).Select(m => (int?)m.UserId).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// shared.tenancy.login_change_refusal: why this company may not change
    /// the person's login (email, password, sign-in), or null when it may.
    /// </summary>
    public async Task<string?> LoginChangeRefusalAsync(User? user, EffectiveAccess access, CancellationToken ct = default)
    {
        if (user is null) return "That person is not a member of this company.";
        string name = string.IsNullOrEmpty(user.FullName) ? user.Email : user.FullName;
        if (user.IsSuperAdmin == true)
            return $"{name} is a platform administrator; that login is managed from the console.";
        int? cid = access.CompanyId;
        if (user.Id == access.User.Id) return null;
        if (cid is not null && access.Company?.CreatedBy == user.Id)
            return $"{name} owns this company; only they can change their own login.";
        bool elsewhere = await db.Memberships.AnyAsync(m => m.UserId == user.Id && m.CompanyId != cid
                                                            && m.Status != CompanyMembership.Removed, ct);
        if (elsewhere)
            return $"{name} also uses this login in another company, so only they can change it.";
        return null;
    }

    /// <summary>logins_managed_elsewhere: ids of these people whose login this company may not change (one query).</summary>
    public async Task<HashSet<int>> LoginsManagedElsewhereAsync(IReadOnlyCollection<User> users, EffectiveAccess access,
                                                                CancellationToken ct = default)
    {
        var ids = users.Select(u => u.Id).ToList();
        if (ids.Count == 0) return [];
        int? cid = access.CompanyId;
        var output = users.Where(u => u.IsSuperAdmin == true).Select(u => u.Id).ToHashSet();
        if (access.Company?.CreatedBy is int owner && owner != 0) output.Add(owner);
        var others = await db.Memberships
            .Where(m => ids.Contains(m.UserId) && m.CompanyId != cid && m.Status != CompanyMembership.Removed)
            .Select(m => m.UserId).Distinct().ToListAsync(ct);
        output.UnionWith(others);
        output.Remove(access.User.Id);
        return output;
    }
}
