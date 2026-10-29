using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.Executive.Services;
using Accountix.Platform.Provisioning;
using Accountix.Platform.Services;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Accountix.Payroll.Tests;

/// <summary>
/// A fresh company on this module's database copy, built by .NET the way the Python
/// e2e tests built theirs: an admin and employees (each an HR + Accounting login,
/// an active member with an employee code, through Platform's <see cref="Memberships"/>),
/// the company (all modules on, Python's column defaults), provisioned by Platform's
/// <see cref="CompanySetup"/> (chart, periods, numbers, settings). Nothing is shared between worlds, so tests repeat.
/// </summary>
public sealed class World
{
    public required int Company { get; init; }
    public required string CompanyName { get; init; }
    public required int Admin { get; init; }
    public required IReadOnlyList<int> Employees { get; init; }
    public required IReadOnlyList<string> Codes { get; init; }
    public required int Cash { get; init; }
    public required string Tag { get; init; }

    /// <summary>Admin first, then the employees: a person's position (0 = the admin) is how golden rows name them.</summary>
    public IReadOnlyList<int> People => [Admin, .. Employees];

    public sealed record Person(string Name, string Joined = "2000-01-01", string Role = "employee");

    /// <summary>Create a company. <paramref name="adminJoined"/>: the admin's date of joining.</summary>
    public static async Task<World> CreateAsync(string name, IReadOnlyList<Person>? employees = null, string adminJoined = "2000-01-01")
    {
        string tag = Guid.NewGuid().ToString("N")[..8];
        List<Person> people = [new("Admin " + tag, adminJoined, "admin"), .. employees ?? []];
        string hash = Reference.Json.GetProperty("hashes").GetProperty("scrypt").GetString()!;
        var context = new CompanyContext();
        await using var db = Reference.NewDb(context);
        var roles = await db.Roles.ToDictionaryAsync(r => r.Name, r => r.Id);
        var now = DateTime.UtcNow;

        // User(...) with Python's column defaults for what the old Python world left out; one at a time, so ids ascend.
        var users = new List<User>();
        foreach (var (p, i) in people.Select((p, i) => (p, i)))
        {
            var u = new User
            {
                EmployeeCode = $"T{tag}{i}".ToUpperInvariant(), Email = $"p{i}.{tag}@payroll.test", LoginId = $"p{i}.{tag}@payroll.test",
                PasswordHash = hash, FullName = p.Name, RoleId = roles[p.Role], DateOfJoining = DateOnly.Parse(p.Joined),
                IsActive = true, HasHrAccess = true, HasAccountingAccess = true,
                HasInventoryAccess = false, HasInvoicingAccess = false, HasFinanceAccess = false, HasFbrAccess = false,
                HasFixedAssetsAccess = false, HasExecutiveAccess = false, IsSuperAdmin = false, CreatedAt = now, UpdatedAt = now,
            };
            db.Users.Add(u);
            await db.SaveChangesAsync();
            users.Add(u);
        }

        // Company(name, slug, is_active, created_by) and the model's defaults.
        var company = new Company
        {
            Name = $"{name} {tag}", Slug = "pay-" + tag, IsActive = true, CreatedBy = users[0].Id, PlanName = "free",
            ModHrEnabled = true, ModInventoryEnabled = true, ModInvoicingEnabled = true, ModFinanceEnabled = true,
            ModAccountingEnabled = true, ModFbrEnabled = true, ModFixedAssetsEnabled = true, ModExecutiveEnabled = true,
            Country = "Pakistan", FiscalYearStartMonth = 1, Currency = "PKR", CurrencySymbol = "Rs.", DateFormat = "Y-m-d",
            NumberFormat = "en", Timezone = "Asia/Karachi", CreatedAt = now, UpdatedAt = now,
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        // CompanyMembership(...) through Platform's port of its insert hooks: a first company takes the account's
        // module switches and employment details.
        var memberships = new Memberships(db, TimeProvider.System);
        var codes = new List<string>();
        foreach (var (u, i) in users.Select((u, i) => (u, i)))
        {
            string code = $"{tag[..3].ToUpperInvariant()}-{i:000}";
            codes.Add(code);
            await memberships.AddAsync(company.Id, u.Id, roles[people[i].Role], CompanyMembership.StatusActive, code);
        }

        // provision_company(cid)
        await new CompanySetup(db, new FirstUse(db, TimeProvider.System), TimeProvider.System).ProvisionAsync(company.Id);
        context.Set(company.Id);
        int cash = await db.Set<ChartOfAccount>().Where(a => a.Code == "1-01-01-01-0001").Select(a => a.Id).SingleAsync();
        return new World
        {
            Company = company.Id, CompanyName = company.Name, Admin = users[0].Id, Employees = users.Skip(1).Select(u => u.Id).ToList(),
            Codes = codes, Cash = cash, Tag = tag,
        };
    }

    public AccountixDbContext Db()
    {
        var c = new CompanyContext();
        c.Set(Company);
        return Reference.NewDb(c);
    }

    /// <summary>A salary package (profile + components), as the Python tests add them.</summary>
    public async Task<int> ProfileAsync(int userId, decimal basic, string effective = "2000-01-01",
                                        params (string Name, string Type, string Method, decimal Value, bool Taxable)[] components)
    {
        await using var db = Db();
        var p = new PayrollProfile
        {
            UserId = userId, BasicSalary = basic, EffectiveFrom = DateOnly.Parse(effective), PaymentMethod = "bank_transfer",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        foreach (var c in components)
            p.PayrollComponents.Add(new PayrollComponent { Name = c.Name, Type = c.Type, CalculationMethod = c.Method, Value = c.Value, IsTaxable = c.Taxable });
        db.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    /// <summary>
    /// A leave type by code: the one the company already has (provisioning seeds CL, SL and AL since
    /// E2E finding F6; Python's provisioning seeded none), else a new one. Payroll reads only whether
    /// a type is paid and carried forward, so an existing type must agree on both.
    /// </summary>
    public async Task<int> LeaveTypeAsync(string name, string code, bool paid, bool carryForward)
    {
        await using var db = Db();
        var have = await db.Set<LeaveType>().FirstOrDefaultAsync(t => t.Code == code);
        if (have is not null)
        {
            Assert.True(have.IsPaid == paid && have.CarryForward == carryForward,
                        $"leave type {code}: paid {have.IsPaid}, carried forward {have.CarryForward}; the test needs {paid}, {carryForward}");
            return have.Id;
        }
        var t = new LeaveType { Name = name, Code = code, IsPaid = paid, CarryForward = carryForward, RequiresApproval = true };
        db.Add(t);
        await db.SaveChangesAsync();
        return t.Id;
    }

    /// <summary>Books Integrity (Executive's port of shared/integrity.py) for this company: the checks that fail.</summary>
    public async Task<List<string>> BooksFailuresAsync()
    {
        await using var db = Db();
        return (await new BooksIntegrity(db).FailuresAsync())
            .Select(b => $"{b.Label}: expected {b.Expected} got {b.Actual} ({b.Detail})").ToList();
    }

    /// <summary>After a step that posts: the general ledger and every sub-ledger still agree.</summary>
    public async Task AssertBooksTieAsync(string step)
    {
        var bad = await BooksFailuresAsync();
        Assert.True(bad.Count == 0, $"after {step}: {string.Join("; ", bad)}");
    }

    /// <summary>{account name: net debit} of a voucher's posted journals.</summary>
    public async Task<Dictionary<string, decimal>> VoucherLinesAsync(string type, int id)
    {
        await using var db = Db();
        var rows = await db.Set<JournalLine>()
            .Where(l => l.JournalEntry.VoucherType == type && l.JournalEntry.VoucherId == id && l.JournalEntry.IsPosted == true)
            .Select(l => new { l.Account.Name, Dr = l.Debit ?? 0, Cr = l.Credit ?? 0 }).ToListAsync();
        return rows.GroupBy(r => r.Name).ToDictionary(g => g.Key, g => g.Sum(r => r.Dr - r.Cr));
    }

    /// <summary>Net debit balance of an account by name (posted lines).</summary>
    public async Task<decimal> BalanceAsync(string accountName)
    {
        await using var db = Db();
        return await db.Set<JournalLine>()
            .Where(l => l.Account.Name == accountName && l.JournalEntry.IsPosted == true)
            .SumAsync(l => (l.Debit ?? 0) - (l.Credit ?? 0));
    }

    public async Task<PayrollRun?> RunAsync(int month, int year)
    {
        await using var db = Db();
        return await db.Set<PayrollRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Month == month && r.Year == year);
    }
}

/// <summary>Runs a class's tests in name order (S01_, S02_...): the e2e steps build on each other, as in Python.</summary>
public sealed class StepOrderer : ITestCaseOrderer
{
    public const string Name = "Accountix.Payroll.Tests.StepOrderer";
    public const string Assembly = "Accountix.Payroll.Tests";

    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases) where TTestCase : ITestCase =>
        testCases.OrderBy(t => t.TestMethod.Method.Name, StringComparer.Ordinal);
}
