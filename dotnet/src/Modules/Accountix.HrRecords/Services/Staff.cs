namespace Accountix.HrRecords.Services;

/// <summary>A person as the HR records pages list them: id, name, this company's department.</summary>
public sealed record Person(int Id, string FullName, string? Department);

/// <summary>
/// The people helpers these routes lean on (shared/models/base.py User,
/// shared/tenancy.py), each one query: User.employees(), reports_of(),
/// get_member(), the department kept per company on the membership.
/// One instance per request.
/// </summary>
public sealed class Staff(AccountixDbContext db)
{
    private Dictionary<string, int>? _roles;

    public async Task<Dictionary<string, int>> RoleIdsAsync(CancellationToken ct = default) =>
        _roles ??= await db.Roles.AsNoTracking().ToDictionaryAsync(r => r.Name, r => r.Id, ct);

    /// <summary>
    /// User.employees().filter(User.is_active.is_(True)): active members of the
    /// company, admins left out, accounts switched on. Optionally only those
    /// holding <paramref name="roleId"/> here, or in <paramref name="department"/> here.
    /// </summary>
    public async Task<IQueryable<Person>> ActiveEmployeesAsync(int companyId, int? roleId = null, string? department = null,
                                                               CancellationToken ct = default)
    {
        int? admin = (await RoleIdsAsync(ct)).TryGetValue(Role.Admin, out int a) ? a : null;
        var q = from m in db.Memberships
                where m.CompanyId == companyId && m.Status == CompanyMembership.StatusActive
                      && (admin == null || m.RoleId != admin)
                join u in db.Users on m.UserId equals u.Id
                where u.IsActive == true
                select new { u, m };
        if (roleId is int r) q = q.Where(x => x.m.RoleId == r);
        if (department is not null) q = q.Where(x => x.m.Department == department);
        return q.OrderBy(x => x.u.Id).Select(x => new Person(x.u.Id, x.u.FullName, x.m.Department));
    }

    /// <summary>reports_of(): ids of active members here (accounts switched on) whose manager in this company is <paramref name="managerId"/>.</summary>
    public Task<List<int>> ReportIdsAsync(int managerId, int companyId, CancellationToken ct = default) =>
        (from m in db.Memberships
         where m.CompanyId == companyId && m.Status == CompanyMembership.StatusActive && m.ManagerId == managerId
         join u in db.Users on m.UserId equals u.Id
         where u.IsActive == true
         select u.Id).Distinct().ToListAsync(ct);

    /// <summary>
    /// shared.tenancy.get_member: the user behind an id from a request, only
    /// when an active member of the active company (a super admin resolves only
    /// to themself). With this company's department (membership, else the account's).
    /// </summary>
    public async Task<Person?> MemberAsync(int uid, EffectiveAccess access, CancellationToken ct = default)
    {
        int cid = access.CompanyId ?? -1;
        var row = await (from u in db.Users
                         where u.Id == uid
                         select new
                         {
                             u.Id, u.FullName, u.IsSuperAdmin, Home = u.Department,
                             Active = db.Memberships.Any(m => m.CompanyId == cid && m.UserId == uid && m.Status == CompanyMembership.StatusActive),
                             Dept = db.Memberships.Where(m => m.CompanyId == cid && m.UserId == uid).OrderBy(m => m.Id)
                                 .Select(m => new { m.Department }).FirstOrDefault(),
                         }).AsNoTracking().FirstOrDefaultAsync(ct);
        if (row is null) return null;
        bool self = row.IsSuperAdmin == true && access.User.Id == row.Id;
        if (!self && (access.CompanyId is null || !row.Active)) return null;
        return new Person(row.Id, row.FullName, row.Dept is null ? row.Home : row.Dept.Department);
    }
}
