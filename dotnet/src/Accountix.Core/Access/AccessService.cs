using Accountix.Core.Data;
using Accountix.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Core.Access;

/// <summary>
/// Everything that decides what one person may do in one company, loaded once
/// per request (the Python app re-queries the membership, role and company on
/// every check — dozens of queries on a busy page).
///
/// Rules (same answers as the Python app, so both apps agree while they share
/// the database):
/// <list type="number">
/// <item>Company: only an ACTIVE membership of an ACTIVE company opens it.</item>
/// <item>Role: the role on that membership. No membership, no role — there is
/// no fallback to the account-wide legacy role.</item>
/// <item>Module: the company must be entitled to it (super admin sets that),
/// AND the person must be admin or have the module switched on.</item>
/// <item>Section rights: admin passes; otherwise the person's row for that
/// section decides; no row means unrestricted.</item>
/// </list>
/// </summary>
public sealed class EffectiveAccess
{
    private readonly IReadOnlyDictionary<string, UserPermission> _rights;

    internal EffectiveAccess(User user, Company? company, CompanyMembership? membership,
                             string? roleName, IReadOnlyDictionary<string, UserPermission> rights,
                             IReadOnlyList<(int Id, string Name)> openCompanies)
    {
        OpenCompanies = openCompanies;
        User = user;
        Company = company;
        Membership = membership;
        RoleName = roleName;
        _rights = rights;
    }

    public User User { get; }
    public Company? Company { get; }
    public CompanyMembership? Membership { get; }
    public string? RoleName { get; }

    /// <summary>Companies this person may open (active membership, active company), by name: the company switcher.</summary>
    public IReadOnlyList<(int Id, string Name)> OpenCompanies { get; }

    /// <summary>
    /// The company's profile row (company_info: CompanyInfo.get() in Python):
    /// its number format formats every amount, its name heads every export.
    /// Null until the company first saves one (Python then uses the defaults).
    /// </summary>
    public CompanyInfo? Profile { get; internal set; }

    /// <summary>The number format amounts print in ("en" western, "hi" Indian grouping).</summary>
    public string NumberFormat => Profile?.NumberFormat is { Length: > 0 } f ? f : "en";

    public int? CompanyId => Company?.Id;
    public bool IsSuperAdmin => User.SuperAdmin;
    public bool IsAdmin => IsSuperAdmin || RoleName == Role.Admin;
    public bool IsManager => IsSuperAdmin || RoleName == Role.Manager;
    public bool IsOwner => Company?.CreatedBy is int owner && owner == User.Id;

    /// <summary>True once an admin has saved this person's section rights in this company.</summary>
    public bool RightsConfigured => _rights.Count > 0;

    public bool ModuleEntitled(string module) => Company is null || Company.ModuleEnabled(module);

    /// <summary>
    /// The person's own switch for a module. Inside a company only what that
    /// company holds on the membership counts (no fallback to the account,
    /// which every company shares); outside a company, the account's switch.
    /// </summary>
    public bool ModuleFlag(string module) =>
        Membership is null ? User.AccountModuleFlag(module) : Membership.ModuleFlag(module) ?? false;

    public bool CanOpenModule(string module) => ModuleEntitled(module) && (IsAdmin || ModuleFlag(module));

    public bool Can(string section, string action)
    {
        if (IsAdmin)
            return true;
        return !_rights.TryGetValue(section, out var row) || row.Allows(action);
    }

    public IEnumerable<Modules.Module> OpenModules() => Modules.All.Where(m => CanOpenModule(m.Key));
}

public sealed class AccessService(AccountixDbContext db)
{
    /// <summary>
    /// The person's access for this request. <paramref name="requestedCompanyId"/>
    /// is the company their session chose; when it is not (or no longer) an
    /// active membership of an active company, the oldest such membership is
    /// used instead, as the Python app does. Sets the request's company.
    /// Returns null when the account is missing or switched off.
    /// </summary>
    public async Task<EffectiveAccess?> ResolveAsync(int userId, int? requestedCompanyId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.Active)
            return null;

        var open = db.Memberships.AsNoTracking()
            .Include(m => m.Company)
            .Include(m => m.Role)
            .Where(m => m.UserId == userId
                        && m.Status == CompanyMembership.StatusActive
                        && m.Company.IsActive != false);

        var all = await open.OrderBy(m => m.JoinedAt).ThenBy(m => m.Id).ToListAsync(ct);
        CompanyMembership? membership = requestedCompanyId is int wanted
            ? all.FirstOrDefault(m => m.CompanyId == wanted)
            : null;
        membership ??= all.FirstOrDefault();
        var companies = all.Select(m => (m.CompanyId, m.Company.Name)).OrderBy(c => c.Name).ToList();

        db.Company.Set(membership?.CompanyId);

        var rights = membership is null
            ? new Dictionary<string, UserPermission>()
            : await db.UserPermissions.AsNoTracking()
                .Where(p => p.UserId == userId)          // company filter added by the model
                .ToDictionaryAsync(p => p.Resource, ct);

        var access = new EffectiveAccess(user, membership?.Company, membership, membership?.Role?.Name, rights, companies);
        if (membership is not null)
            access.Profile = await db.Set<CompanyInfo>().AsNoTracking().OrderBy(c => c.Id).FirstOrDefaultAsync(ct);
        return access;
    }
}
