namespace Accountix.SuperAdmin.Services;

/// <summary>
/// The console's reads. Every table here is a platform table (companies,
/// memberships, users, roles, global limits, requests, audit), so no company
/// filter applies; each list is a fixed number of queries whatever its size
/// (Python counted members once per row).
/// </summary>
public sealed class ConsoleQueries(AccountixDbContext db)
{
    /// <summary>RegistrationRequest.pending_count: pending or seen.</summary>
    public Task<int> PendingRequestsAsync(CancellationToken ct = default) =>
        db.Set<RegistrationRequest>().CountAsync(r => r.Status == "pending" || r.Status == "seen", ct);

    /// <summary>_active_member_count for several companies in one query.</summary>
    public async Task<Dictionary<int, int>> ActiveMemberCountsAsync(IEnumerable<int> companyIds, CancellationToken ct = default)
    {
        var ids = companyIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        return await db.Memberships.AsNoTracking()
            .Where(m => ids.Contains(m.CompanyId) && m.Status == CompanyMembership.StatusActive)
            .GroupBy(m => m.CompanyId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
    }

    public Task<int> ActiveMemberCountAsync(int companyId, CancellationToken ct = default) =>
        db.Memberships.CountAsync(m => m.CompanyId == companyId && m.Status == CompanyMembership.StatusActive, ct);

    public sealed record CompanyRow(Company Company, int ActiveMembers, bool Mine);

    /// <summary>companies(): every company by name, its active members, whether this super admin made it.</summary>
    public async Task<List<CompanyRow>> CompaniesAsync(int me, CancellationToken ct = default)
    {
        var companies = await db.Companies.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
        var counts = await ActiveMemberCountsAsync(companies.Select(c => c.Id), ct);
        return companies.Select(c => new CompanyRow(c, counts.GetValueOrDefault(c.Id), c.CreatedBy == me)).ToList();
    }

    public sealed record UserRow(User User, int ActiveCompanies);

    /// <summary>users(): every account by email, with the companies it is an active member of.</summary>
    public async Task<List<UserRow>> UsersAsync(CancellationToken ct = default)
    {
        var users = await db.Users.AsNoTracking().OrderBy(u => u.Email).ToListAsync(ct);
        var counts = await db.Memberships.AsNoTracking()
            .Where(m => m.Status == CompanyMembership.StatusActive)
            .GroupBy(m => m.UserId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return users.Select(u => new UserRow(u, counts.GetValueOrDefault(u.Id))).ToList();
    }

    public sealed record MemberRow(CompanyMembership Membership, User? User, string RoleName);

    /// <summary>company_edit(): the company's memberships by status, then joining date, with person and role.</summary>
    public async Task<List<MemberRow>> MembersAsync(int companyId, CancellationToken ct = default)
    {
        var rows = await (from m in db.Memberships.AsNoTracking()
                          where m.CompanyId == companyId
                          join u in db.Users.AsNoTracking() on m.UserId equals u.Id into us
                          from u in us.DefaultIfEmpty()
                          join r in db.Roles.AsNoTracking() on m.RoleId equals r.Id into rs
                          from r in rs.DefaultIfEmpty()
                          orderby m.Status, m.JoinedAt, m.Id
                          select new { m, u, Role = r == null ? null : r.Name })
            .ToListAsync(ct);
        return rows.Select(x => new MemberRow(x.m, x.u, x.Role ?? "")).ToList();
    }

    public sealed record UserMembershipRow(CompanyMembership Membership, Company? Company, string RoleName);

    /// <summary>user_detail(): this person's memberships by status, with company and role.</summary>
    public async Task<List<UserMembershipRow>> UserMembershipsAsync(int userId, CancellationToken ct = default)
    {
        var rows = await (from m in db.Memberships.AsNoTracking()
                          where m.UserId == userId
                          join c in db.Companies.AsNoTracking() on m.CompanyId equals c.Id into cs
                          from c in cs.DefaultIfEmpty()
                          join r in db.Roles.AsNoTracking() on m.RoleId equals r.Id into rs
                          from r in rs.DefaultIfEmpty()
                          orderby m.Status, m.Id
                          select new { m, c, Role = r == null ? null : r.Name })
            .ToListAsync(ct);
        return rows.Select(x => new UserMembershipRow(x.m, x.c, x.Role ?? "")).ToList();
    }

    public sealed record InviteRow(CompanyInvitation Invitation, Company? Company, string RoleName);

    public sealed record MyCompaniesPage(
        IReadOnlyList<Company> Owned, IReadOnlyList<UserMembershipRow> Memberships, IReadOnlyList<InviteRow> Invites,
        IReadOnlyDictionary<int, int> ActiveMembers, int QuotaTotal, int QuotaUsed);

    /// <summary>my_companies() GET.</summary>
    public async Task<MyCompaniesPage> MyCompaniesAsync(User me, GlobalLimit limits, CancellationToken ct = default)
    {
        var owned = await db.Companies.AsNoTracking().Where(c => c.CreatedBy == me.Id).OrderBy(c => c.Name).ToListAsync(ct);
        var mine = await (from m in db.Memberships.AsNoTracking()
                          where m.UserId == me.Id && m.Status == CompanyMembership.StatusActive
                          join c in db.Companies.AsNoTracking() on m.CompanyId equals c.Id into cs
                          from c in cs.DefaultIfEmpty()
                          join r in db.Roles.AsNoTracking() on m.RoleId equals r.Id into rs
                          from r in rs.DefaultIfEmpty()
                          orderby m.Id
                          select new { m, c, Role = r == null ? null : r.Name })
            .ToListAsync(ct);
        string email = me.Email;
        var invites = await (from i in db.Invitations.AsNoTracking()
                             where i.Email == email && i.Status == CompanyInvitation.Sent
                             join c in db.Companies.AsNoTracking() on i.CompanyId equals c.Id into cs
                             from c in cs.DefaultIfEmpty()
                             join r in db.Roles.AsNoTracking() on i.RoleId equals r.Id into rs
                             from r in rs.DefaultIfEmpty()
                             orderby i.Id
                             select new { i, c, Role = r == null ? null : r.Name })
            .ToListAsync(ct);
        var counts = await ActiveMemberCountsAsync(owned.Select(c => c.Id).Concat(mine.Where(x => x.c != null).Select(x => x.c!.Id)), ct);
        return new MyCompaniesPage(
            owned,
            mine.Select(x => new UserMembershipRow(x.m, x.c, x.Role ?? "")).ToList(),
            invites.Select(x => new InviteRow(x.i, x.c, x.Role ?? "")).ToList(),
            counts,
            limits.CompanyLimitFor(me),
            owned.Count);
    }
}
