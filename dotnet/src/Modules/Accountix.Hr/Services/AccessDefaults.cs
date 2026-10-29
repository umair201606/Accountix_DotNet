namespace Accountix.Hr.Services;

/// <summary>
/// What a role starts with in a company (shared/access_defaults.py). Used by
/// the HR user editor: a role change moves the person's rights to the new
/// role's template only while they still are exactly the old role's — an
/// admin's hand-set rights are never overwritten — and the module switches
/// follow while they still are the old role's.
/// </summary>
public sealed class AccessDefaults(AccountixDbContext db)
{
    public static readonly IReadOnlyList<string> ModuleKeys = Modules.All.Select(m => m.Key).ToList();
    public static readonly IReadOnlyList<string> HrSections = Modules.All.First(m => m.Key == Modules.Hr).Sections.Select(s => s.Key).ToList();
    public static readonly IReadOnlyList<string> AllSections = Modules.All.SelectMany(m => m.Sections).Select(s => s.Key).ToList();

    /// <summary>ROLE_MODULES: modules a role opens in a company that never saw the person before.</summary>
    public static IReadOnlyDictionary<string, bool>? RoleModules(string? role) => role switch
    {
        Role.Admin => ModuleKeys.ToDictionary(k => k, _ => true),
        Role.Manager or Role.Employee => ModuleKeys.ToDictionary(k => k, k => k == Modules.Hr),
        _ => null,
    };

    /// <summary>ROLE_RIGHTS: admin none (passes every check); manager every HR section; employee nothing.</summary>
    public static Dictionary<string, bool[]>? RoleRights(string? role) => role switch
    {
        Role.Manager => Grid(HrSections.ToHashSet()),
        Role.Employee => Grid([]),
        _ => null,
    };

    private static Dictionary<string, bool[]> Grid(HashSet<string> granted) =>
        AllSections.ToDictionary(s => s, s => Rights.Actions.Select(_ => granted.Contains(s)).ToArray());

    /// <summary>modules_json: the text stored in company_memberships.module_flags.</summary>
    public static string ModulesJson(IReadOnlyDictionary<string, bool> flags)
    {
        var tmp = new CompanyMembership();
        tmp.SetModuleFlags(flags);
        return tmp.ModuleFlags!;
    }

    private async Task<Dictionary<string, bool[]>> CurrentGridAsync(int userId, CancellationToken ct)
    {
        var rows = await db.UserPermissions.AsNoTracking().Where(p => p.UserId == userId).ToListAsync(ct);
        return rows.GroupBy(r => r.Resource).ToDictionary(g => g.Key, g =>
        {
            var r = g.Last();
            return new[] { r.CanView == true, r.CanCreate == true, r.CanEdit == true, r.CanApprove == true, r.CanDelete == true };
        });
    }

    private static bool Same(Dictionary<string, bool[]> a, Dictionary<string, bool[]> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v.SequenceEqual(kv.Value));

    private async Task WriteRightsAsync(int userId, Dictionary<string, bool[]>? grid, CancellationToken ct)
    {
        await db.UserPermissions.Where(p => p.UserId == userId).ExecuteDeleteAsync(ct);
        foreach (var e in db.ChangeTracker.Entries<UserPermission>().Where(e => e.Entity.UserId == userId).ToList())
            e.State = EntityState.Detached;
        if (grid is null || grid.Count == 0) return;
        foreach (var (section, acts) in grid)
            db.Add(new UserPermission
            {
                UserId = userId, Resource = section,
                CanView = acts[0], CanCreate = acts[1], CanEdit = acts[2], CanApprove = acts[3], CanDelete = acts[4],
            });
    }

    /// <summary>role_changed: call after changing a membership's role (the active company's).</summary>
    public async Task RoleChangedAsync(CompanyMembership membership, string? oldRole, string? newRole, CancellationToken ct = default)
    {
        if (oldRole == newRole) return;
        var oldGrid = RoleRights(oldRole);
        var have = await CurrentGridAsync(membership.UserId, ct);
        if (oldGrid is null)
        {
            if (have.Count > 0) return;      // from admin: only while there are still no rows
        }
        else if (!Same(have, oldGrid))
            return;
        await WriteRightsAsync(membership.UserId, RoleRights(newRole), ct);
        string? oldModules = RoleModules(oldRole) is { } om ? ModulesJson(om) : ModulesJson(new Dictionary<string, bool>());
        if ((membership.ModuleFlags is null || membership.ModuleFlags == oldModules) && RoleModules(newRole) is { } nm)
            membership.ModuleFlags = ModulesJson(nm);
    }
}
