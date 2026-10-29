using Accountix.Accounting.Services;
using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.Core.Security;
using Accountix.Core.Shared;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Finance.Tests;

/// <summary>
/// The 13-week cash flow's data, built through .NET only in its own company ("golden-twcf",
/// <see cref="World.CompanyAsync"/>): 15 journals posted by Accounting's <see cref="Ledger"/>
/// (cash, three customers, two suppliers), an approved payroll run with its PRL journal, six
/// forecast lines of every frequency, and a view-only member. The same rows the Python seed
/// wrote in company 1 before; Golden/capture/twcf_*.py recorded the Python app's answers on
/// exactly this company. Made once, found again by the TWCFTEST journals.
/// </summary>
internal static class TwcfWorld
{
    public const string Slug = "golden-twcf";
    public const string ViewerEmail = "twcf.viewer@ref.test";
    public static int Owner => World.OwnerB;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static (int Company, int Viewer)? _built;

    public static int Company => EnsureAsync().GetAwaiter().GetResult().Company;
    public static int Viewer => EnsureAsync().GetAwaiter().GetResult().Viewer;

    public static AccountixDbContext Db() => World.Db(Company);

    private static readonly (string Day, string Dr, string Cr, decimal Amount)[] Journals =
    [
        ("2026-04-01", "cash", "cap", 10000m), ("2026-05-10", "c1", "sales", 1000m), ("2026-07-20", "c1", "sales", 400.25m),
        ("2026-08-25", "c2", "sales", 2345.67m), ("2026-08-30", "cash", "c1", 300m), ("2026-06-15", "exp", "s1", 1500m),
        ("2026-08-01", "exp", "s2", 800.10m), ("2026-08-20", "s1", "cash", 500m), ("2026-08-02", "cash", "c3", 250m),
        ("2026-08-28", "c3", "sales", 1000m), ("2026-09-10", "cash", "c2", 1200m), ("2026-09-22", "s2", "cash", 300m),
        ("2026-10-05", "cash", "sales", 5000m), ("2026-10-20", "exp", "cash", 123.45m), ("2026-11-15", "c2", "sales", 777.77m),
    ];

    private static readonly (string Direction, string Category, string Description, decimal Amount, string Start, string Frequency, int Dow, int Dom, int Month)[] Lines =
    [
        ("out", "rent", "TWCFSEED rent", 750m, "2026-09-15", "oneoff", 0, 1, 1),
        ("out", "other_out", "TWCFSEED weekly", 100.125m, "2026-08-01", "weekly", 4, 1, 1),
        ("out", "payroll", "TWCFSEED bonus", 2500m, "2026-01-01", "monthly", 0, 31, 1),
        ("in", "other_in", "TWCFSEED rebate", 999.99m, "2026-03-15", "quarterly", 0, 15, 1),
        ("out", "taxes", "TWCFSEED tax", 1234.5678m, "2025-10-30", "yearly", 0, 30, 10),
        ("in", "other_in", "TWCFSEED rent in", 333.3333m, "2026-09-01", "monthly", 0, 30, 1),
    ];

    public static async Task<(int Company, int Viewer)> EnsureAsync()
    {
        if (_built is { } done) return done;
        await Gate.WaitAsync();
        try
        {
            if (_built is { } again) return again;
            int cid = await World.CompanyAsync(Slug, "Golden TWCF");
            await SeedAsync(cid);
            int viewer = await ViewerAsync(cid);
            _built = (cid, viewer);
            return _built.Value;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// The company with only its seeded forecast lines: lines a line, race or query test left behind
    /// (a failed run stops before its own clean-up) would move the figures the goldens compare.
    /// </summary>
    public static async Task<(int Company, int Viewer)> ReadyAsync()
    {
        var built = await EnsureAsync();
        await using var db = World.Db(built.Company);
        var seeded = Lines.Select(l => l.Description).ToList();
        await db.Set<TwcfLine>().Where(l => !seeded.Contains(l.Description)).ExecuteDeleteAsync();
        return built;
    }

    private static async Task SeedAsync(int cid)
    {
        await using var db = World.Db(cid);
        if (await db.Set<JournalEntry>().AnyAsync(e => e.VoucherType == "TWCFTEST"))
            return;
        await using var tx = await db.Database.BeginTransactionAsync();
        var ledger = new Ledger(db, new Periods(db));
        async Task<int> Code(string code) => await db.Set<ChartOfAccount>().Where(a => a.Code == code).Select(a => a.Id).SingleAsync();
        var acct = new Dictionary<string, int>
        {
            ["cash"] = await Code("1-01-01-01-0001"), ["sales"] = await Code("4-01-01-01-0001"),
            ["exp"] = await Code("5-04-01-01-0001"), ["cap"] = await Code("3-01-01-01-0001"),
            ["c1"] = (await ledger.EntityAccountAsync("customer", 1, "SolarTech Solutions")).Id,
            ["c2"] = (await ledger.EntityAccountAsync("customer", 2, "Green Energy Pakistan")).Id,
            ["c3"] = (await ledger.EntityAccountAsync("customer", 3, "BuildRight Construction")).Id,
            ["s1"] = (await ledger.EntityAccountAsync("supplier", 1, "Longi Solar Pakistan")).Id,
            ["s2"] = (await ledger.EntityAccountAsync("supplier", 2, "JA Solar Technologies")).Id,
        };
        for (int i = 0; i < Journals.Length; i++)
        {
            var (day, dr, cr, amount) = Journals[i];
            await ledger.PostAsync("TWCFTEST", i + 1, $"TWCFTEST-{i + 1:000}", "TWCF seed",
                [new(acct[dr], amount, 0), new(acct[cr], 0, amount)], DateTime.Parse(day + "T00:00:00", System.Globalization.CultureInfo.InvariantCulture), Owner);
        }
        var run = new PayrollRun
        {
            Month = 9, Year = 2026, RunDate = new DateTime(2026, 9, 25, 10, 0, 0), Status = "approved",
            TotalGross = 45678.9m, TotalDeductions = 0, TotalNet = 45678.9m, EmployeeCount = 1, ProcessedBy = Owner,
        };
        db.Add(run);
        await db.SaveChangesAsync();
        await ledger.PostAsync("PRL", run.Id, $"PRL-TWCF-{run.Id}", "TWCF seed payroll",
            [new(await Code("5-02-01-01-0001"), 45678.9m, 0), new(await Code("2-01-02-02-0001"), 0, 45678.9m)],
            new DateTime(2026, 9, 30), Owner);
        foreach (var (direction, category, description, amount, start, frequency, dow, dom, month) in Lines)
            db.Add(new TwcfLine
            {
                Direction = direction, Category = category, Description = description, Amount = amount,
                StartDate = DateOnly.Parse(start, System.Globalization.CultureInfo.InvariantCulture), Frequency = frequency,
                DayOfWeek = dow, DayOfMonth = dom, Month = month, CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    /// <summary>An employee of the company who may only view the financial reports (finance + HR modules).</summary>
    private static async Task<int> ViewerAsync(int cid)
    {
        await using var db = World.Db(cid);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == ViewerEmail);
        int employee = await db.Roles.Where(r => r.Name == Role.Employee).Select(r => r.Id).FirstAsync();
        if (user is null)
        {
            var now = DateTime.UtcNow;
            user = new User
            {
                EmployeeCode = "TWCFV01", Email = ViewerEmail, FullName = "TWCF Viewer", RoleId = employee, IsActive = true,
                PasswordHash = PasswordHasher.Hash("Str0ng-pass-1"), DateOfJoining = DateOnly.FromDateTime(now),
                HasHrAccess = false, HasInventoryAccess = false, HasInvoicingAccess = false, HasFinanceAccess = false,
                HasAccountingAccess = false, HasFbrAccess = false, HasFixedAssetsAccess = false, HasExecutiveAccess = false,
                IsSuperAdmin = false, CreatedAt = now, UpdatedAt = now,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }
        if (!await db.Memberships.AnyAsync(m => m.CompanyId == cid && m.UserId == user.Id))
        {
            db.Memberships.Add(new CompanyMembership
            {
                CompanyId = cid, UserId = user.Id, RoleId = employee, Status = CompanyMembership.StatusActive,
                JoinedAt = DateTime.UtcNow, ModuleFlags = "{\"finance\": true, \"hr\": true}",
            });
            await db.SaveChangesAsync();
        }
        if (!await db.Set<UserPermission>().AnyAsync(p => p.UserId == user.Id && p.Resource == "financial_reports"))
        {
            db.Add(new UserPermission
            {
                UserId = user.Id, Resource = "financial_reports", CanView = true, CanCreate = false, CanEdit = false,
                CanApprove = false, CanDelete = false,
            });
            await db.SaveChangesAsync();
        }
        return user.Id;
    }
}
