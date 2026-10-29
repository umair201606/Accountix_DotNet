using System.Globalization;
using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.Core.Security;
using Accountix.Platform.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Accountix.Load;

/// <summary>
/// The load test's people, added to one company of a WRITABLE copy of the speed-test database
/// (never accountix_ref_perf itself): accounts load01@load.test … as members, the first
/// <c>--admins</c> as admins and the rest as managers. Each is a first-company member, so the
/// module switches come from the account (all on) and section rights are unrestricted, as for
/// any first-company member. Re-running reuses the accounts. Writes what seed_load_users.py
/// (the Python version this replaces) wrote — the membership through Platform's
/// <see cref="Memberships"/>, which copies the account's module switches onto a first membership
/// as Python's before-insert hook did; prints the user ids as JSON.
/// </summary>
public static class LoadUsers
{
    public const string Password = "Load-pass-123!";

    public static async Task<List<int>> SeedAsync(string connectionString, int companyId, int count, int admins)
    {
        string database = new NpgsqlConnectionStringBuilder(connectionString).Database ?? "";
        if (database.Contains("accountix_ref_perf", StringComparison.Ordinal) && !database.EndsWith("_load", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing: point --db at a writable copy, not the speed-test database.");

        await using var db = new AccountixDbContext(
            new DbContextOptionsBuilder<AccountixDbContext>().UseNpgsql(connectionString).Options, new CompanyContext());
        if (!await db.Companies.AnyAsync(c => c.Id == companyId))
            throw new InvalidOperationException($"No company {companyId} in {database}.");
        var roles = await db.Roles.AsNoTracking().ToDictionaryAsync(r => r.Name, r => r.Id);
        var now = DateTime.UtcNow;
        var ids = new List<int>();
        await using var tx = await db.Database.BeginTransactionAsync();
        for (int i = 1; i <= count; i++)
        {
            string email = string.Create(CultureInfo.InvariantCulture, $"load{i:00}@load.test");
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
            if (user is null)
            {
                user = new User
                {
                    EmployeeCode = string.Create(CultureInfo.InvariantCulture, $"LD{i:0000}"), Email = email, LoginId = email,
                    FullName = string.Create(CultureInfo.InvariantCulture, $"Load User {i:00}"), RoleId = roles[Role.Employee],
                    PasswordHash = PasswordHasher.Hash(Password), IsActive = true, IsSuperAdmin = false,
                    HasHrAccess = true, HasInventoryAccess = true, HasInvoicingAccess = true, HasFinanceAccess = true,
                    HasAccountingAccess = true, HasFbrAccess = true, HasFixedAssetsAccess = true, HasExecutiveAccess = true,
                    DateOfJoining = DateOnly.FromDateTime(DateTime.Now), CreatedAt = now, UpdatedAt = now,
                };
                db.Users.Add(user);
                await db.SaveChangesAsync();
            }
            if (!await db.Memberships.AnyAsync(m => m.CompanyId == companyId && m.UserId == user.Id))
                await new Memberships(db, TimeProvider.System).AddAsync(companyId, user.Id, roles[i <= admins ? Role.Admin : Role.Manager]);
            ids.Add(user.Id);
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return ids;
    }
}
