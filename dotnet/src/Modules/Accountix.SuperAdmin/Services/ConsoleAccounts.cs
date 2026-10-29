using System.Security.Cryptography;
using Accountix.Core.Security;
using Accountix.Platform.Provisioning;
using Accountix.Platform.Services;

namespace Accountix.SuperAdmin.Services;

/// <summary>
/// The console's writes that make accounts and companies
/// (superadmin_app/routes.py users / companies / my_companies), with every
/// column the Python models default client-side set here, as Python sets it.
/// </summary>
public sealed class ConsoleAccounts(AccountixDbContext db, Memberships memberships, CompanySetup setup, TimeProvider time)
{
    /// <summary>datetime.utcnow() as Postgres keeps it (microseconds).</summary>
    public DateTime UtcNow => Micro(time.GetUtcNow().UtcDateTime);

    internal static DateTime Micro(DateTime t) => new(t.Ticks - t.Ticks % 10, t.Kind);

    /// <summary>date.today(): the server's own date (the User.date_of_joining default).</summary>
    private DateOnly ServerToday => DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    /// <summary>_random_password: letters and digits from the system's secure generator.</summary>
    public static string RandomPassword(int length = 10)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return new string(Enumerable.Range(0, length).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());
    }

    /// <summary>
    /// _next_employee_code: SA + (number of accounts + 1), the first one free.
    /// users.employee_code is globally unique and NOT NULL, so an account
    /// needs one before it belongs anywhere; the real per-company code comes
    /// from HR's Assign Member.
    /// </summary>
    public async Task<string> NextEmployeeCodeAsync(CancellationToken ct = default)
    {
        int n = await db.Users.CountAsync(ct) + 1;
        var taken = (await db.Users.AsNoTracking()
                .Where(u => u.EmployeeCode.StartsWith("SA"))
                .Select(u => u.EmployeeCode).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        while (taken.Contains($"SA{n:D4}"))
            n++;
        return $"SA{n:D4}";
    }

    /// <summary>A new account (User(...) with the model's defaults), added to the context, not saved.</summary>
    public async Task<User> NewUserAsync(string email, string fullName, int roleId, string passwordHash, bool allModules,
                                         string? phone = null, CancellationToken ct = default)
    {
        var now = UtcNow;
        var u = new User
        {
            EmployeeCode = await NextEmployeeCodeAsync(ct),
            Email = email,
            LoginId = email,
            FullName = fullName,
            RoleId = roleId,
            IsActive = true,
            HasHrAccess = true,
            HasInventoryAccess = allModules,
            HasInvoicingAccess = allModules,
            HasFinanceAccess = allModules,
            HasAccountingAccess = allModules,
            HasFbrAccess = allModules,
            HasFixedAssetsAccess = allModules,
            HasExecutiveAccess = allModules,
            IsSuperAdmin = false,
            DateOfJoining = ServerToday,
            CreatedAt = now,
            UpdatedAt = now,
            Phone = phone,
            PasswordHash = passwordHash,
        };
        db.Users.Add(u);
        return u;
    }

    /// <summary>A new company (Company(...) with the model's defaults), added to the context, not saved.</summary>
    public Company NewCompany(string name, string slug, string plan, int? createdBy)
    {
        var now = UtcNow;
        var c = new Company
        {
            Name = name, Slug = slug, PlanName = plan, IsActive = true, CreatedBy = createdBy,
            ModHrEnabled = true, ModInventoryEnabled = true, ModInvoicingEnabled = true, ModFinanceEnabled = true,
            ModAccountingEnabled = true, ModFbrEnabled = true, ModFixedAssetsEnabled = true, ModExecutiveEnabled = true,
            Country = "Pakistan", FiscalYearStartMonth = 1, Currency = "PKR", CurrencySymbol = "Rs.",
            DateFormat = "Y-m-d", NumberFormat = "en", Timezone = "Asia/Karachi",
            CreatedAt = now, UpdatedAt = now,
        };
        db.Companies.Add(c);
        return c;
    }

    public Task<int?> RoleIdAsync(string name, CancellationToken ct = default) =>
        db.Roles.AsNoTracking().Where(r => r.Name == name).OrderBy(r => r.Id).Select(r => (int?)r.Id).FirstOrDefaultAsync(ct);

    /// <summary>The result of the console's Create Company form.</summary>
    public sealed record CreatedCompany(Company Company, string AdminEmail, bool CreatedUser, string? Password);

    /// <summary>
    /// companies() POST after its checks: the admin (found by email, or made
    /// with every module switched on), the company owned by that admin, the
    /// admin's membership; committed together, then provisioned (chart,
    /// numbering, periods, settings) in its own commit, as Python does.
    /// </summary>
    public async Task<CreatedCompany> CreateConsoleCompanyAsync(string name, string slug, string plan, User? admin,
        string email, string adminName, string? typedPassword, int adminRoleId, CancellationToken ct = default)
    {
        bool createdUser = false;
        string? password = null;
        Company company;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            if (admin is null)
            {
                password = string.IsNullOrEmpty(typedPassword) ? RandomPassword(12) : typedPassword;
                admin = await NewUserAsync(email, adminName, adminRoleId, PasswordHasher.Hash(password), allModules: true, ct: ct);
                await db.SaveChangesAsync(ct);
                createdUser = true;
            }
            // The admin it names owns it (ACCESS_REVIEW #3).
            company = NewCompany(name, slug, plan, admin.Id);
            await db.SaveChangesAsync(ct);
            var existing = await db.Memberships.FirstOrDefaultAsync(m => m.CompanyId == company.Id && m.UserId == admin.Id, ct);
            if (existing is null)
                await memberships.AddAsync(company.Id, admin.Id, adminRoleId, CompanyMembership.StatusActive, admin.EmployeeCode, ct);
            else
            {
                existing.Status = CompanyMembership.StatusActive;
                existing.RoleId = adminRoleId;
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        await setup.ProvisionAsync(company.Id, ct);
        return new CreatedCompany(company, email, createdUser, password);
    }

    /// <summary>my_companies() POST after its checks: the company, owned by the super admin, who is its admin.</summary>
    public async Task<Company> CreateOwnCompanyAsync(string name, string slug, int userId, CancellationToken ct = default)
    {
        Company company;
        await using (var tx = await db.Database.CurrentTransactionOrBeginAsync(ct))
        {
            company = NewCompany(name, slug, "free", userId);
            await db.SaveChangesAsync(ct);
            if (await RoleIdAsync(Role.Admin, ct) is int adminRole)
                await memberships.AddAsync(company.Id, userId, adminRole, CompanyMembership.StatusActive, null, ct);
            await tx.CommitAsync(ct);
        }
        await setup.ProvisionAsync(company.Id, ct);
        return company;
    }
}

internal static class TransactionExtensions
{
    /// <summary>The caller's open transaction (a queue lock may already hold it), else a new one.</summary>
    public static async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> CurrentTransactionOrBeginAsync(
        this Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database, CancellationToken ct) =>
        database.CurrentTransaction ?? await database.BeginTransactionAsync(ct);
}
