namespace Accountix.Platform.Services;

/// <summary>
/// shared/access_defaults.py: what a person may open when they join a
/// company, by the role they join as; and the membership insert hooks of
/// shared/models/company.py.
/// </summary>
public static class AccessDefaults
{
    public static readonly IReadOnlyList<string> ModuleKeys = Modules.All.Select(m => m.Key).ToList();
    public static readonly IReadOnlyList<string> HrSections = Modules.All.Single(m => m.Key == Modules.Hr).Sections.Select(s => s.Key).ToList();
    public static readonly IReadOnlyList<string> AllSections = Modules.All.SelectMany(m => m.Sections).Select(s => s.Key).ToList();

    /// <summary>ROLE_MODULES: modules a role opens in a company that never saw the person before.</summary>
    public static IReadOnlyDictionary<string, bool> RoleModules(string? role) => role switch
    {
        Role.Admin => ModuleKeys.ToDictionary(k => k, _ => true),
        _ => ModuleKeys.ToDictionary(k => k, k => k == Modules.Hr),
    };

    public static bool KnownRole(string? role) => role is Role.Admin or Role.Manager or Role.Employee;

    public sealed record Grant(bool View, bool Create, bool Edit, bool Approve, bool Delete);

    /// <summary>ROLE_RIGHTS: null for admins (they pass every check), HR in full for managers, nothing for employees.</summary>
    public static IReadOnlyDictionary<string, Grant>? RoleRights(string? role) => role switch
    {
        Role.Admin => null,
        Role.Manager => Grid(HrSections.ToHashSet()),
        _ => Grid([]),
    };

    private static Dictionary<string, Grant> Grid(HashSet<string> granted) =>
        AllSections.ToDictionary(s => s, s => { bool g = granted.Contains(s); return new Grant(g, g, g, g, g); });

    /// <summary>modules_json: json.dumps({k: bool(v)}, sort_keys=True).</summary>
    public static string ModulesJson(IReadOnlyDictionary<string, bool> flags)
    {
        var m = new CompanyMembership();
        m.SetModuleFlags(flags);
        return m.ModuleFlags!;
    }

    public static IReadOnlyDictionary<string, bool> AccountModules(User u) =>
        ModuleKeys.ToDictionary(k => k, k => u.AccountModuleFlag(k));

    /// <summary>write_rights: replace this person's rows in this company (null removes them all). Plain SQL, the caller's transaction.</summary>
    public static async Task WriteRightsAsync(AccountixDbContext db, int companyId, int userId,
                                              IReadOnlyDictionary<string, Grant>? grid, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM user_permissions WHERE company_id = {companyId} AND user_id = {userId}", ct);
        if (grid is null || grid.Count == 0) return;
        var sb = new System.Text.StringBuilder(
            "INSERT INTO user_permissions (company_id, user_id, resource, can_view, can_create, can_edit, can_approve, can_delete) VALUES ");
        var args = new List<object>();
        int i = 0;
        foreach (var (section, g) in grid)
        {
            if (i > 0) sb.Append(", ");
            int b = args.Count;
            sb.Append($"({{{b}}}, {{{b + 1}}}, {{{b + 2}}}, {{{b + 3}}}, {{{b + 4}}}, {{{b + 5}}}, {{{b + 6}}}, {{{b + 7}}})");
            args.AddRange([companyId, userId, section, g.View, g.Create, g.Edit, g.Approve, g.Delete]);
            i++;
        }
        await db.Database.ExecuteSqlRawAsync(sb.ToString(), args, ct);
    }

    /// <summary>current_grid: this person's rights rows in this company ({} when none).</summary>
    public static async Task<Dictionary<string, Grant>> CurrentGridAsync(AccountixDbContext db, int companyId, int userId, CancellationToken ct = default)
    {
        var rows = await db.UserPermissions.IgnoreQueryFilters([AccountixDbContext.TenantFilter]).AsNoTracking()
            .Where(p => p.CompanyId == companyId && p.UserId == userId)
            .Select(p => new { p.Resource, p.CanView, p.CanCreate, p.CanEdit, p.CanApprove, p.CanDelete })
            .ToListAsync(ct);
        var grid = new Dictionary<string, Grant>();
        foreach (var r in rows)
            grid[r.Resource] = new Grant(r.CanView == true, r.CanCreate == true, r.CanEdit == true, r.CanApprove == true, r.CanDelete == true);
        return grid;
    }

    internal static bool SameGrid(IReadOnlyDictionary<string, Grant> a, IReadOnlyDictionary<string, Grant>? b) =>
        b is not null && a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var g) && g == kv.Value);

    /// <summary>
    /// role_changed: rights still exactly the old role's template move to the
    /// new role's; anything set by hand (or no rows: a first company) stays.
    /// Module switches follow while still the old role's.
    /// </summary>
    public static async Task RoleChangedAsync(AccountixDbContext db, CompanyMembership m, string? oldRole, string? newRole, CancellationToken ct = default)
    {
        if (oldRole == newRole) return;
        // ROLE_RIGHTS.get(role): None for admin and for an unknown role.
        var oldGrid = KnownRole(oldRole) ? RoleRights(oldRole) : null;
        var have = await CurrentGridAsync(db, m.CompanyId, m.UserId, ct);
        if (oldGrid is null)
        {
            if (have.Count > 0) return;
        }
        else if (!SameGrid(have, oldGrid))
            return;
        await WriteRightsAsync(db, m.CompanyId, m.UserId, KnownRole(newRole) ? RoleRights(newRole) : null, ct);
        string oldJson = ModulesJson(KnownRole(oldRole) ? RoleModules(oldRole) : new Dictionary<string, bool>());
        if ((m.ModuleFlags is null || m.ModuleFlags == oldJson) && KnownRole(newRole))
            m.ModuleFlags = ModulesJson(RoleModules(newRole));
    }
}

/// <summary>Making memberships, with the insert hooks of shared/models/company.py.</summary>
public sealed class Memberships(AccountixDbContext db, TimeProvider time)
{
    /// <summary>
    /// A new membership. A person's FIRST company starts from their account's
    /// module switches and employment details; a FURTHER one from the role
    /// they join as, with that role's rights written out (unless rights rows
    /// already exist there). Saved at once (it needs its id).
    /// </summary>
    public async Task<CompanyMembership> AddAsync(int companyId, int userId, int roleId, string status = CompanyMembership.StatusActive,
                                                  string? employeeCode = null, CancellationToken ct = default)
    {
        string? role = await db.Roles.AsNoTracking().Where(r => r.Id == roleId).Select(r => r.Name).FirstOrDefaultAsync(ct);
        int others = await db.Memberships.CountAsync(m => m.UserId == userId && m.Status != CompanyMembership.Removed, ct);
        var m = new CompanyMembership
        {
            CompanyId = companyId, UserId = userId, RoleId = roleId, Status = status, EmployeeCode = employeeCode,
            JoinedAt = FirstUse.Micro(time.GetUtcNow().UtcDateTime),
        };
        if (others > 0)
            m.ModuleFlags = AccessDefaults.ModulesJson(AccessDefaults.RoleModules(AccessDefaults.KnownRole(role) ? role : Role.Employee));
        else
        {
            var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId, ct);
            if (u is not null)
            {
                m.ModuleFlags = AccessDefaults.ModulesJson(AccessDefaults.AccountModules(u));
                m.ManagerId = u.ManagerId;
                m.Designation = u.Designation;
                m.Department = u.Department;
            }
        }
        db.Memberships.Add(m);
        await db.SaveChangesAsync(ct);
        if (others > 0 && (await AccessDefaults.CurrentGridAsync(db, companyId, userId, ct)).Count == 0)
        {
            var grid = AccessDefaults.RoleRights(AccessDefaults.KnownRole(role) ? role : Role.Employee);
            if (grid is not null)
                await AccessDefaults.WriteRightsAsync(db, companyId, userId, grid, ct);
        }
        return m;
    }
}
