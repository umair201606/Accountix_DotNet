using System.Text.RegularExpressions;
using Accountix.Accounting.Services;
using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.Core.Security;
using Accountix.Core.Shared;
using Accountix.Finance.Services;
using Accountix.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Finance.Tests;

/// <summary>
/// The statements' data, built through .NET only in a company of its own ("golden-finstmt",
/// provisioned by Platform's CompanySetup, owner_b its admin): two years of journals posted once
/// through the Accounting module's <see cref="Ledger"/> (marker: voucher type FINSTMT) — capital,
/// labelled sales and costs, seven admin expenses (with pl_detail_rows = 4, so the P&amp;L folds
/// "Others"), a fixed asset, a loan, interest, a sales return, other income, tax, dividends, other
/// comprehensive income, a transfer out of retained earnings, a three-way receipt (direct-method
/// weights), an expense refund (a net credit on an expense) and unlabelled sales; plus a member
/// without the financial_reports view right. Nothing else posts there, so the figures are fixed,
/// and Golden/*.json holds the Python app's statements of exactly these books
/// (Golden/capture/statements.py, statement_pages.py).
/// </summary>
public static partial class StatementSeed
{
    public const string Slug = "golden-finstmt";
    public const string NoViewEmail = "fin.noview.golden@ref.test";

    /// <summary>The day "today" is for every default the statements derive (the day the goldens were captured).</summary>
    public static readonly DateOnly Today = new(2026, 10, 29);

    /// <summary>Company B: the race test posts there, never into the golden books.</summary>
    public static int CompanyB => Reference.Company("b");
    public static int OwnerB => Reference.UserId(Reference.Email("owner_b"));

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static int? _company, _noView;

    /// <summary>The golden company (after <see cref="EnsureAsync"/>).</summary>
    public static int Company => _company ?? throw new InvalidOperationException("StatementSeed.EnsureAsync first");
    public static int NoViewUser => _noView ?? throw new InvalidOperationException("StatementSeed.EnsureAsync first");

    public static AccountixDbContext Db() => World.Db(Company);

    private static readonly (string Date, string Desc, (string Code, decimal Dr, decimal Cr, string? Label)[] Lines)[] Journals =
    [
        ("2025-02-01", "Capital", [("1-01-01-01-0001", 500000, 0, null), ("3-01-01-01-0001", 0, 500000, null)]),
        ("2025-03-10", "Sale on credit", [("1-01-02-01-0001", 120000, 0, null), ("4-01-01-01-0001", 0, 70000, "A"), ("4-01-01-01-0001", 0, 50000, "B")]),
        ("2025-03-12", "Cost of sale", [("5-01-01-01-0001", 60000, 0, "A"), ("1-01-04-01-0001", 0, 60000, null)]),
        ("2025-04-05", "Receipt", [("1-01-01-01-0001", 100000, 0, null), ("1-01-02-01-0001", 0, 100000, null)]),
        ("2025-05-01", "Rent", [("5-02-02-01-0001", 15000, 0, "B"), ("1-01-01-01-0001", 0, 15000, null)]),
        ("2025-06-30", "Payroll and overheads",
        [
            ("5-02-01-01-0001", 40000, 0, "A"), ("5-02-01-01-0002", 2000, 0, "A"), ("5-02-02-01-0002", 3500.5m, 0, "B"),
            ("5-02-03-01-0001", 1200.25m, 0, null), ("5-02-05-01-0001", 800, 0, null), ("1-01-01-01-0001", 0, 47500.75m, null),
        ]),
        ("2025-06-30", "Depreciation", [("5-02-04-01-0001", 5000, 0, "A"), ("1-02-02-01-0001", 0, 5000, null)]),
        ("2025-07-01", "Fixed asset", [("1-02-01-01-0001", 80000, 0, null), ("1-01-01-01-0001", 0, 80000, null)]),
        ("2025-08-15", "Loan drawn", [("1-01-01-01-0001", 200000, 0, null), ("2-02-01-01-0001", 0, 200000, null)]),
        ("2025-09-30", "Interest", [("5-05-01-01-0002", 4500, 0, null), ("1-01-01-01-0001", 0, 4500, null)]),
        ("2025-10-10", "Sales return", [("4-02-01-01-0001", 5000, 0, "A"), ("1-01-02-01-0001", 0, 5000, null)]),
        ("2025-11-20", "Other income", [("1-01-01-01-0001", 2345.67m, 0, null), ("4-03-01-01-0001", 0, 2345.67m, "B")]),
        ("2025-12-31", "Income tax", [("5-06-01-01-0001", 9000, 0, null), ("2-01-03-02-0001", 0, 9000, null)]),
        ("2026-01-15", "Dividends", [("3-02-02-01-0001", 30000, 0, null), ("1-01-01-01-0001", 0, 30000, null)]),
        ("2026-02-10", "Revaluation", [("1-02-01-01-0001", 12000, 0, null), ("3-02-03-01-0001", 0, 12000, null)]),
        ("2026-03-03", "Purchase", [("1-01-04-01-0001", 45000, 0, null), ("2-01-01-01-0001", 0, 45000, null)]),
        ("2026-04-04", "Supplier payment", [("2-01-01-01-0001", 30000, 0, null), ("1-01-01-01-0001", 0, 30000, null)]),
        ("2026-05-05", "Cash sale",
        [
            ("1-01-01-01-0001", 33333.33m, 0, null), ("4-01-01-01-0001", 0, 25000, "A"),
            ("2-01-03-01-0001", 0, 4250, null), ("4-01-02-01-0001", 0, 4083.33m, "B"),
        ]),
        ("2026-06-06", "Supplier refund", [("1-01-01-01-0001", 700, 0, null), ("5-02-03-01-0001", 0, 700, null)]),
        ("2026-06-20", "Office", [("5-02-03-01-0001", 300, 0, null), ("5-03-01-01-0001", 1234.56m, 0, "A"), ("1-01-01-01-0001", 0, 1534.56m, null)]),
        ("2026-07-07", "Transfer to reserve", [("3-02-01-02-0001", 2500, 0, null), ("3-01-01-01-0002", 0, 2500, null)]),
        ("2026-09-09", "Unlabelled sale", [("1-01-02-01-0001", 18000, 0, null), ("4-01-01-01-0001", 0, 18000, null)]),
    ];

    /// <summary>Make the company and its books once (found again by slug and marker on later runs). Returns the company.</summary>
    public static async Task<int> EnsureAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_company is int done && _noView is not null)
                return done;
            int cid = await World.CompanyAsync(Slug, "Golden Statements");
            await using (var db = World.Db(cid))
            {
                if (!await db.Set<JournalEntry>().AnyAsync(e => e.VoucherType == "FINSTMT"))
                {
                    await using var tx = await db.Database.BeginTransactionAsync();
                    var now = DateTime.UtcNow;
                    var alpha = new ProjectLabel { Name = "Proj Alpha", IsActive = true, CreatedAt = now };
                    var beta = new ProjectLabel { Name = "Proj Beta", IsActive = false, CreatedAt = now };
                    db.AddRange(alpha, beta);
                    var settings = await db.Set<ReportSetting>().OrderBy(s => s.Id).FirstAsync();
                    settings.PlDetailRows = 4;
                    await db.SaveChangesAsync();
                    var accounts = await db.Set<ChartOfAccount>().ToDictionaryAsync(a => a.Code, a => a.Id);
                    var ledger = new Ledger(db, new Periods(db));
                    for (int i = 0; i < Journals.Length; i++)
                    {
                        var (date, desc, lines) = Journals[i];
                        var when = DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture).ToDateTime(new TimeOnly(10, 30));
                        await ledger.PostAsync("FINSTMT", i + 1, $"FS-{i + 1:000}", desc,
                            lines.Select(l => new JournalLineInput(accounts[l.Code], l.Dr, l.Cr, desc + " line",
                                l.Label switch { "A" => alpha.Id, "B" => beta.Id, _ => null })).ToList(),
                            when, OwnerB);
                    }
                    await db.SaveChangesAsync();
                    await tx.CommitAsync();
                }
            }
            _noView = await NoViewMemberAsync(cid);
            _company = cid;
            return cid;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>A member with the finance module but no financial_reports view right (an employee).</summary>
    private static async Task<int> NoViewMemberAsync(int cid)
    {
        await using var db = Reference.NewDb();
        if (await db.Users.Where(u => u.Email == NoViewEmail).Select(u => (int?)u.Id).SingleOrDefaultAsync() is int id)
            return id;
        var now = DateTime.UtcNow;
        int employee = await db.Roles.Where(r => r.Name == Role.Employee).Select(r => r.Id).FirstAsync();
        var user = new User
        {
            EmployeeCode = "FINNVG1", Email = NoViewEmail, FullName = "No Finance View", RoleId = employee, IsActive = true,
            PasswordHash = PasswordHasher.Hash("Str0ng-pass-1"), HasHrAccess = false, HasInventoryAccess = false,
            HasInvoicingAccess = false, HasFinanceAccess = false, HasAccountingAccess = false, HasFbrAccess = false,
            HasFixedAssetsAccess = false, HasExecutiveAccess = false, IsSuperAdmin = false, CreatedAt = now, UpdatedAt = now,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        db.Memberships.Add(new CompanyMembership
        {
            CompanyId = cid, UserId = user.Id, RoleId = employee, Status = CompanyMembership.StatusActive,
            ModuleFlags = "{\"finance\": true, \"hr\": true}", JoinedAt = now,
        });
        var company = new CompanyContext();
        company.Set(cid);
        await db.SaveChangesAsync();
        await using var scoped = Reference.NewDb(company);
        scoped.Add(new UserPermission
        {
            UserId = user.Id, Resource = "financial_reports", CanView = false, CanCreate = false, CanEdit = false,
            CanApprove = false, CanDelete = false,
        });
        await scoped.SaveChangesAsync();
        return user.Id;
    }

    public static async Task<int> PeriodIdAsync(string name)
    {
        await using var db = Db();
        return await db.Set<AccountingPeriod>().Where(p => p.PeriodName == name).Select(p => p.Id).SingleAsync();
    }

    public static async Task<(int Alpha, int Beta)> LabelsAsync()
    {
        await using var db = Db();
        var l = await db.Set<ProjectLabel>().ToListAsync();
        return (l.Single(x => x.Name == "Proj Alpha").Id, l.Single(x => x.Name == "Proj Beta").Id);
    }

    public static async Task<ReportPeriod> ResolveAsync(AccountixDbContext db, string query)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString("?" + query);
        return await ReportPeriod.ResolveAsync(ctx.Request, db, Today);
    }

    [GeneratedRegex(@"\{(?:(acct|head):)?([^{}]+)\}")]
    private static partial Regex Symbol();

    /// <summary>
    /// A query or URL as the goldens write it, with this copy's ids: {FY 2026} → that period's id,
    /// {Proj Alpha} → the label's, {acct:CODE} / {head:CODE} → the account's.
    /// </summary>
    public static async Task<string> RealAsync(string symbolic)
    {
        await using var db = Db();
        var periods = await db.Set<AccountingPeriod>().ToDictionaryAsync(p => p.PeriodName, p => p.Id);
        var labels = await db.Set<ProjectLabel>().ToDictionaryAsync(l => l.Name, l => l.Id);
        var accounts = await db.Set<ChartOfAccount>().ToDictionaryAsync(a => a.Code, a => a.Id);
        return Symbol().Replace(symbolic, m => (m.Groups[1].Success ? accounts[m.Groups[2].Value]
            : periods.TryGetValue(m.Groups[2].Value, out int p) ? p : labels[m.Groups[2].Value]).ToString());
    }
}
