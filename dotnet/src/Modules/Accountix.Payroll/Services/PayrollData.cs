namespace Accountix.Payroll.Services;

/// <summary>
/// Everything one payroll month needs, for every profile of the company, read
/// with one query per table (profiles, components, revisions, memberships,
/// unpaid leave, overtime, loans, PF loans, year-to-date slips, leave quotas,
/// settings, slabs, PF config). Python asked per employee; the figures are the
/// same, the query count no longer grows with the staff.
/// </summary>
public sealed class PayrollData
{
    public required int Month { get; init; }
    public required int Year { get; init; }
    public required IReadOnlyList<PayrollProfile> Profiles { get; init; }
    public required ILookup<int, PayrollComponent> Components { get; init; }
    public required ILookup<int, RevisionRow> Revisions { get; init; }
    public required IReadOnlyDictionary<int, CompanyMembership> Memberships { get; init; }
    public required ILookup<int, LeaveRow> UnpaidLeave { get; init; }
    public required IReadOnlyDictionary<int, double> OvertimeHours { get; init; }
    /// <summary>Approved staff loans with something left, by user, in id order (paid out or not).</summary>
    public required ILookup<int, LoanAdvanceRequest> Loans { get; init; }
    public required ILookup<int, PfLoanRequest> PfLoans { get; init; }
    public required ILookup<int, YtdSlip> YtdSlips { get; init; }
    public required ILookup<int, double> CarryForwardRemaining { get; init; }
    public required PayrollPolicy Policy { get; init; }
    public required IReadOnlyList<TaxSlab> Slabs { get; init; }
    public required PfConfig? PfConfig { get; init; }

    public DateOnly? LeavingDate(int userId) => Memberships.TryGetValue(userId, out var m) ? m.DateOfLeaving : null;

    /// <summary>Who the person reports to here (the membership's manager, else the account's).</summary>
    public int? ManagerOf(User u) => Memberships.TryGetValue(u.Id, out var m) ? m.ManagerId : u.ManagerId;

    public string? DesignationOf(User u) => Memberships.TryGetValue(u.Id, out var m) ? m.Designation : u.Designation;

    /// <summary>
    /// Load the month. <paramref name="tracked"/>: loans and PF loans come back
    /// tracked, for the run to draw them down.
    /// </summary>
    public static async Task<PayrollData> LoadAsync(AccountixDbContext db, int month, int year, bool tracked = false,
                                                    CancellationToken ct = default)
    {
        int cid = db.Company.CompanyId ?? throw new NoActiveCompanyException("payroll with no active company");
        var (first, last) = PayrollMath.MonthBounds(month, year);

        var profiles = await db.Set<PayrollProfile>().AsNoTracking().Include(p => p.User)
            .OrderBy(p => p.Id).ToListAsync(ct);
        var users = profiles.Select(p => p.UserId).Distinct().ToList();

        var profileIds = profiles.Select(p => p.Id).ToList();
        var components = (await db.Set<PayrollComponent>().AsNoTracking()
            .Where(c => profileIds.Contains(c.ProfileId))
            .OrderBy(c => c.Id).ToListAsync(ct)).ToLookup(c => c.ProfileId);

        var revisions = (await db.Set<SalaryRevision>().AsNoTracking()
                .Where(r => users.Contains(r.UserId))
                .OrderBy(r => r.EffectiveFrom).ThenBy(r => r.Id)
                .Select(r => new { r.UserId, Row = new RevisionRow(r.EffectiveFrom, r.PreviousBasic, r.NewBasic) })
                .ToListAsync(ct))
            .ToLookup(r => r.UserId, r => r.Row);

        var memberships = (await db.Memberships.AsNoTracking()
                .Where(m => m.CompanyId == cid && users.Contains(m.UserId))
                .OrderBy(m => m.Id).ToListAsync(ct))
            .GroupBy(m => m.UserId).ToDictionary(g => g.Key, g => g.First());

        var leave = (await db.Set<LeaveRequest>().AsNoTracking()
                .Where(l => users.Contains(l.UserId) && l.Status == "approved" && l.LeaveType.IsPaid == false
                            && l.StartDate <= last && l.EndDate >= first)
                .OrderBy(l => l.Id)
                .Select(l => new { l.UserId, Row = new LeaveRow(l.StartDate, l.EndDate, l.TotalDays) })
                .ToListAsync(ct))
            .ToLookup(l => l.UserId, l => l.Row);

        var overtime = await db.Set<OvertimeAccount>().AsNoTracking()
            .Where(o => users.Contains(o.UserId) && o.Date >= first && o.Date <= last)
            .GroupBy(o => o.UserId)
            .Select(g => new { g.Key, Hours = g.Sum(o => o.OvertimeHours) })
            .ToDictionaryAsync(x => x.Key, x => x.Hours ?? 0.0, ct);

        IQueryable<LoanAdvanceRequest> loanQ = db.Set<LoanAdvanceRequest>();
        IQueryable<PfLoanRequest> pfLoanQ = db.Set<PfLoanRequest>();
        if (!tracked)
        {
            loanQ = loanQ.AsNoTracking();
            pfLoanQ = pfLoanQ.AsNoTracking();
        }
        var loans = (await loanQ.Include(l => l.User)
                .Where(l => users.Contains(l.UserId) && l.Status == "approved" && l.RemainingAmount > 0)
                .OrderBy(l => l.Id).ToListAsync(ct))
            .ToLookup(l => l.UserId);
        var pfLoans = (await pfLoanQ
                .Where(l => users.Contains(l.UserId) && l.Status == "approved" && l.RemainingAmount > 0)
                .OrderBy(l => l.Id).ToListAsync(ct))
            .ToLookup(l => l.UserId);

        var ((sm, sy), _) = PayrollMath.TaxYearMonths(month, year);
        int startKey = sy * 12 + sm, key = year * 12 + month;
        var ytd = (await db.Set<PayrollSlip>().AsNoTracking()
                .Where(s => users.Contains(s.UserId)
                            && s.PayrollRun.Year * 12 + s.PayrollRun.Month >= startKey
                            && s.PayrollRun.Year * 12 + s.PayrollRun.Month < key)
                .OrderBy(s => s.Id)
                .Select(s => new { s.UserId, Row = new YtdSlip(s.PayrollRun.Month, s.PayrollRun.Year, s.TaxablePay, s.GrossPay, s.ComponentsJson) })
                .ToListAsync(ct))
            .ToLookup(s => s.UserId, s => s.Row);

        var leavers = memberships.Values
            .Where(m => m.DateOfLeaving is DateOnly d && d >= first && d <= last).Select(m => m.UserId).ToList();
        var quotas = leavers.Count == 0
            ? Enumerable.Empty<(int, double)>().ToLookup(x => x.Item1, x => x.Item2)
            : (await db.Set<LeaveQuota>().AsNoTracking()
                    .Where(q => leavers.Contains(q.UserId) && q.Year == year
                                && q.LeaveType.IsPaid == true && q.LeaveType.CarryForward == true)
                    .OrderBy(q => q.Id)
                    .Select(q => new { q.UserId, q.Remaining })
                    .ToListAsync(ct))
                .ToLookup(q => q.UserId, q => Py.F(q.Remaining));

        var settings = await db.Set<PayrollSetting>().AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        var slabs = await SlabsAsync(db, ct);
        var pf = await db.Set<PfConfig>().AsNoTracking().OrderBy(c => c.Id).FirstOrDefaultAsync(ct);

        return new PayrollData
        {
            Month = month, Year = year, Profiles = profiles, Components = components, Revisions = revisions,
            Memberships = memberships, UnpaidLeave = leave, OvertimeHours = overtime, Loans = loans, PfLoans = pfLoans,
            YtdSlips = ytd, CarryForwardRemaining = quotas,
            Policy = settings is null ? new PayrollPolicy() : PayrollPolicy.From(settings),
            Slabs = slabs, PfConfig = pf,
        };
    }

    public static async Task<IReadOnlyList<TaxSlab>> SlabsAsync(AccountixDbContext db, CancellationToken ct = default) =>
        (await db.Set<IncomeTaxSlab>().AsNoTracking().Where(s => s.IsActive == true)
            .OrderBy(s => s.MinIncome).ThenBy(s => s.Id).ToListAsync(ct))
        .Select(TaxSlab.From).ToList();
}
