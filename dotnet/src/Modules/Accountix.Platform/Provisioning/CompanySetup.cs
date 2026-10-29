using Accountix.Platform.Services;

namespace Accountix.Platform.Provisioning;

/// <summary>
/// shared/company_setup.py provision_company: the chart, numbering, asset
/// categories, settings, periods and invoice templates of a new company, in
/// Python's order, idempotent. Runs in the company (the previous active
/// company is restored afterwards) and commits, as Python does.
/// </summary>
public sealed class CompanySetup(AccountixDbContext db, FirstUse firstUse, TimeProvider time)
{
    /// <summary>VOUCHER_PREFIXES.</summary>
    public static readonly IReadOnlyList<string> VoucherPrefixes =
        ["PI", "PR", "CONS", "SCRAP", "ADJ", "ST", "CPV", "CRV", "BPV", "BRV", "JV", "PRL", "FA-TRF", "INV-FA"];

    /// <summary>AssetCategory.seed defaults: (name, description, life, method, salvage %).</summary>
    public static readonly IReadOnlyList<(string Name, string Description, int Life, string Method, decimal SalvagePct)> AssetCategories =
    [
        ("Computers & IT Equipment", "Desktop computers, laptops, servers, printers", 5, "straight_line", 0),
        ("Office Furniture", "Desks, chairs, cabinets, shelving", 10, "straight_line", 5),
        ("Vehicles", "Cars, trucks, forklifts", 8, "declining_balance", 10),
        ("Machinery & Equipment", "Production machines, tools, heavy equipment", 15, "straight_line", 5),
        ("Buildings & Leasehold Improvements", "Office buildings, warehouse, renovations", 30, "straight_line", 10),
        ("Software & Licenses", "ERP licenses, development tools", 3, "straight_line", 0),
    ];

    /// <summary>
    /// hr_app/seed.py's leave types: (name, code, days a year, carry forward, most days carried);
    /// all paid and approved by a manager (the model's defaults). E2E finding F6: Python wrote them only
    /// in that stand-alone script, so a company made through the portal or the console had none and
    /// Leave → Apply offered nothing.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, string Code, int Quota, bool CarryForward, int MaxCarryForward)> LeaveTypes =
    [
        ("Casual Leave", "CL", 12, false, 0),
        ("Sick Leave", "SL", 10, true, 5),
        ("Annual Leave", "AL", 20, true, 10),
    ];

    private DateTime Now => FirstUse.Micro(time.GetUtcNow().UtcDateTime);

    public async Task ProvisionAsync(int companyId, CancellationToken ct = default)
    {
        int? previous = db.Company.CompanyId;
        db.Company.Set(companyId);
        try
        {
            bool own = db.Database.CurrentTransaction is null;
            await using var tx = own ? await db.Database.BeginTransactionAsync(ct) : null;

            await EnsureFixedCoaAsync(ct);

            var have = await db.Set<VoucherNumber>().Select(v => v.Prefix).ToListAsync(ct);
            var pending = new List<object>();
            foreach (string prefix in VoucherPrefixes)
                if (!have.Contains(prefix))
                    pending.Add(new VoucherNumber { Prefix = prefix, NextNumber = 1 });

            var cats = await db.Set<FaCategory>().Select(c => c.Name).ToListAsync(ct);
            foreach (var (name, desc, life, method, sv) in AssetCategories)
                if (!cats.Contains(name))
                    pending.Add(new FaCategory
                    {
                        Name = name, Description = desc, DefaultUsefulLife = life, DefaultDepreciationMethod = method,
                        DefaultSalvageValuePct = sv, IsActive = true, CreatedAt = Now,
                    });

            await firstUse.CompanyInfoAsync(ct);
            var rule = await firstUse.FiscalYearRuleAsync(ct);
            await AddInOrderAsync(pending, ct);     // autoflush before the period query
            if (!await db.Set<AccountingPeriod>().AnyAsync(ct))
                await GeneratePeriodsAsync(rule, null, null, ct);
            await firstUse.ReportSettingsAsync(ct);
            await firstUse.InventorySettingsAsync(ct);
            await InvoiceTemplateSeed.SeedDefaultsAsync(db, time, ct);
            await EnsureLeaveTypesAsync(ct);

            await db.SaveChangesAsync(ct);
            if (tx is not null)
                await tx.CommitAsync(ct);
        }
        finally
        {
            db.Company.Set(previous);
        }
    }

    /// <summary>
    /// The default leave types (<see cref="LeaveTypes"/>) for the active company when it has none.
    /// A company with leave types of its own keeps them as they are. Returns how many were added
    /// (saved, in this order, so they get ids in Python's order).
    /// </summary>
    public async Task<int> EnsureLeaveTypesAsync(CancellationToken ct = default)
    {
        if (await db.Set<LeaveType>().AnyAsync(ct))
            return 0;
        await AddInOrderAsync(LeaveTypes.Select(t => (object)new LeaveType
        {
            Name = t.Name, Code = t.Code, DefaultQuota = t.Quota, IsPaid = true, RequiresApproval = true,
            CarryForward = t.CarryForward, MaxCarryForward = t.MaxCarryForward,
        }), ct);
        return LeaveTypes.Count;
    }

    /// <summary>
    /// coa.ensure_fixed_coa for a company: the fixed five-level tree, levels
    /// 1–4 first, then the level-5 defaults, each account saved as it is made
    /// (its children need its id). A chart with legacy (non-segmented) codes
    /// is the Python app's to migrate; it is left as it is.
    /// </summary>
    public async Task EnsureFixedCoaAsync(CancellationToken ct = default)
    {
        await SeedFixedTreeAsync([1, 2, 3, 4], ct);
        await SeedFixedTreeAsync([5], ct);
        await db.SaveChangesAsync(ct);
    }

    private static int CodeLevel(string code) => code.Split('-').Length;
    private static string? ParentCodeOf(string code) => code.Contains('-') ? code[..code.LastIndexOf('-')] : null;

    private async Task SeedFixedTreeAsync(int[] levels, CancellationToken ct)
    {
        var existing = await db.Set<ChartOfAccount>().ToDictionaryAsync(a => a.Code, ct);
        foreach (var (code, name, type, cf, pl) in FixedChart.Accounts)
        {
            int level = CodeLevel(code);
            if (!levels.Contains(level)) continue;
            if (!existing.TryGetValue(code, out var acct))
            {
                string? parentCode = ParentCodeOf(code);
                ChartOfAccount? parent = parentCode is not null ? existing.GetValueOrDefault(parentCode) : null;
                acct = new ChartOfAccount
                {
                    Code = code, Name = name, Type = type, ParentId = parent?.Id, Level = level, IsFixed = level <= 4,
                    IsActive = true, CashFlowActivity = cf, PlSection = pl, CreatedAt = Now,
                };
                db.Add(acct);
                await db.SaveChangesAsync(ct);
                existing[code] = acct;
            }
            else
            {
                acct.Level = level;
                if (level <= 4) acct.IsFixed = true;
                if (cf is not null && string.IsNullOrEmpty(acct.CashFlowActivity)) acct.CashFlowActivity = cf;
                if (pl is not null && string.IsNullOrEmpty(acct.PlSection)) acct.PlSection = pl;
            }
        }
    }

    /// <summary>
    /// FiscalYearRule.generate_periods: delete the company's periods and make
    /// yearly ones from the rule — from_year..to_year, or (earliest journal
    /// year | today − 5) .. today + 2 — then commit. "Today" is the server's
    /// date, as Python's date.today().
    /// </summary>
    public async Task GeneratePeriodsAsync(FiscalYearRule rule, int? fromYear, int? toYear, CancellationToken ct = default)
    {
        int cid = rule.CompanyId ?? db.Company.CompanyId ?? throw new NoActiveCompanyException("No company for the periods.");
        await db.Set<AccountingPeriod>().Where(p => p.CompanyId == cid).ExecuteDeleteAsync(ct);
        int todayYear = time.GetLocalNow().Year;
        int min, max;
        if (fromYear is int f && toYear is int t)
            (min, max) = (f, t);
        else
        {
            DateTime? earliest = await db.Set<JournalEntry>().MinAsync(j => (DateTime?)j.EntryDate, ct);
            min = earliest?.Year ?? todayYear - 5;
            max = todayYear + 2;
        }
        int sm = rule.StartMonth, sd = rule.StartDay;
        var periods = new List<object>();
        for (int y = min; y <= max; y++)
        {
            var start = new DateOnly(y, sm, sd);
            var end = new DateOnly(y + 1, sm, sd).AddDays(-1);
            bool calendar = sm == 1 && sd == 1;
            periods.Add(new AccountingPeriod
            {
                FiscalYear = calendar ? y.ToString() : $"{y}-{y + 1}",
                PeriodName = calendar ? $"FY {y}" : $"FY {y}-{y + 1}",
                StartDate = start, EndDate = end, IsOpen = true, IsClosed = false, IsActive = true, CreatedAt = Now,
            });
        }
        await AddInOrderAsync(periods, ct);
    }

    /// <summary>
    /// New rows inserted in the order they were made, as SQLAlchemy flushes
    /// them (one SaveChanges per row: EF orders a batch of inserts by its
    /// temporary keys, which reversed them).
    /// </summary>
    internal async Task AddInOrderAsync(IEnumerable<object> rows, CancellationToken ct)
    {
        foreach (var row in rows)
        {
            db.Add(row);
            await db.SaveChangesAsync(ct);
        }
    }
}
