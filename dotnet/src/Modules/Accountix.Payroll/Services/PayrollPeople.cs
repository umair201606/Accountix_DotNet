namespace Accountix.Payroll.Services;

/// <summary>People and settings lookups the payroll pages share (shared/tenancy.get_member, _emp_code, User.employees, PayrollSettings.get).</summary>
public sealed class PayrollPeople(AccountixDbContext db, ILedger ledger)
{
    private int Cid => db.Company.CompanyId ?? throw new NoActiveCompanyException("payroll with no active company");

    /// <summary>get_member: an active member of this company (a super admin only as themselves).</summary>
    public async Task<User?> MemberAsync(int userId, int currentUserId, CancellationToken ct = default)
    {
        var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId, ct);
        if (u is null) return null;
        if (u.IsSuperAdmin == true && currentUserId == u.Id) return u;
        int cid = Cid;
        bool member = await db.Memberships.AnyAsync(m => m.CompanyId == cid && m.UserId == userId && m.Status == CompanyMembership.StatusActive, ct);
        return member ? u : null;
    }

    /// <summary>Active members of this company, keyed by user id (one query for a list).</summary>
    public async Task<Dictionary<int, User>> MembersAsync(IEnumerable<int> userIds, CancellationToken ct = default)
    {
        int cid = Cid;
        var ids = userIds.Distinct().ToList();
        var active = await db.Memberships.AsNoTracking()
            .Where(m => m.CompanyId == cid && ids.Contains(m.UserId) && m.Status == CompanyMembership.StatusActive)
            .Select(m => m.UserId).ToListAsync(ct);
        return await db.Users.AsNoTracking().Where(u => active.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
    }

    /// <summary>_emp_code for many people: this company's code on the membership, else the account's.</summary>
    public async Task<Dictionary<int, string>> CodesAsync(IEnumerable<int> userIds, CancellationToken ct = default)
    {
        int cid = Cid;
        var ids = userIds.Distinct().ToList();
        var mine = (await db.Memberships.AsNoTracking().Where(m => m.CompanyId == cid && ids.Contains(m.UserId))
                .OrderBy(m => m.Id).Select(m => new { m.UserId, m.EmployeeCode }).ToListAsync(ct))
            .GroupBy(m => m.UserId).ToDictionary(g => g.Key, g => g.First().EmployeeCode);
        var account = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.EmployeeCode }).ToDictionaryAsync(u => u.Id, u => u.EmployeeCode, ct);
        return ids.ToDictionary(id => id, id =>
            mine.TryGetValue(id, out var c) && !string.IsNullOrEmpty(c) ? c : account.GetValueOrDefault(id) ?? "");
    }

    /// <summary>The membership rows of these people here (any status): designation, department, manager.</summary>
    public async Task<Dictionary<int, CompanyMembership>> EmploymentAsync(IEnumerable<int> userIds, CancellationToken ct = default)
    {
        int cid = Cid;
        var ids = userIds.Distinct().ToList();
        return (await db.Memberships.AsNoTracking().Where(m => m.CompanyId == cid && ids.Contains(m.UserId))
                .OrderBy(m => m.Id).ToListAsync(ct))
            .GroupBy(m => m.UserId).ToDictionary(g => g.Key, g => g.First());
    }

    /// <summary>User.employees() and active: active members here who are not admins.</summary>
    public async Task<List<User>> EmployeesAsync(CancellationToken ct = default)
    {
        int cid = Cid;
        int? adminRole = await db.Roles.Where(r => r.Name == Role.Admin).OrderBy(r => r.Id).Select(r => (int?)r.Id).FirstOrDefaultAsync(ct);
        var q = db.Memberships.AsNoTracking().Where(m => m.CompanyId == cid && m.Status == CompanyMembership.StatusActive);
        if (adminRole is int ar)
            q = q.Where(m => m.RoleId != ar);
        var ids = q.Select(m => m.UserId);
        return await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id) && u.IsActive == true).OrderBy(u => u.Id).ToListAsync(ct);
    }

    /// <summary>
    /// PayrollSettings.get(): the company's row, made on first use (queued per
    /// company, so two first requests make one). Call inside a transaction.
    /// </summary>
    public async Task<PayrollSetting> SettingsAsync(CancellationToken ct = default)
    {
        var s = await db.Set<PayrollSetting>().OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (s is not null) return s;
        await db.SerializeAsync("first-use", "payroll_settings", Cid);
        s = await db.Set<PayrollSetting>().OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (s is not null) return s;
        s = new PayrollSetting
        {
            OvertimeMultiplier = 1.5m, OvertimeHoursBasis = 160, EobiEnabled = false, EobiWageBase = 37000,
            EobiEmployeePct = 1, EobiEmployerPct = 5, SurchargeThreshold = 10_000_000, SurchargePct = 9,
            GratuityEnabled = false, GratuityBasis = "basic", GratuityDaysPerYear = 30, LeaveEncashmentEnabled = false,
        };
        db.Add(s);
        await db.SaveChangesAsync(ct);
        return s;
    }

    /// <summary>cash_bank_account: the chosen pay-from account, only if it is one of this company's cash/bank accounts.</summary>
    public async Task<ChartOfAccount?> CashBankAccountAsync(string? raw, CancellationToken ct = default)
    {
        if (Py.ParseInt(raw) is not int id || id == 0) return null;
        return (await ledger.CashBankAccountsAsync(ct)).FirstOrDefault(a => a.Id == id);
    }

    public static string RemoteIp(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
}
