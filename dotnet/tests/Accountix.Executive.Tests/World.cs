using Accountix.Accounting.Services;
using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.Platform.Provisioning;
using Accountix.Platform.Services;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Executive.Tests;

/// <summary>
/// The data the tests build for themselves, through .NET only. A golden test that needs books
/// nobody else touches gets its own company: made as the portal makes one and provisioned by
/// Platform's <see cref="CompanySetup"/> (chart, numbering, periods, settings), owner_b its admin.
/// It is found again by its slug on later runs, so ids stay put until the copy is rebuilt.
/// </summary>
internal static class World
{
    public static int OwnerB => Reference.UserId(Reference.Email("owner_b"));
    public static int CompanyB => Reference.Company("b");

    /// <summary>A context in one company, with the reporting-table interceptor the app registers.</summary>
    public static AccountixDbContext Db(int companyId)
    {
        var company = new CompanyContext();
        company.Set(companyId);
        return new AccountixDbContext(new DbContextOptionsBuilder<AccountixDbContext>()
            .UseNpgsql(Reference.ConnectionString).AddInterceptors(new GlSummaryInterceptor()).Options, company);
    }

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<int> CompanyAsync(string slug, string name)
    {
        await Gate.WaitAsync();
        try
        {
            await using var db = Reference.NewDb();
            if (await db.Companies.Where(c => c.Slug == slug).Select(c => (int?)c.Id).SingleOrDefaultAsync() is int id)
                return id;
            var now = DateTime.UtcNow;
            var company = new Company
            {
                Name = name, Slug = slug, IsActive = true, PlanName = "free", CreatedBy = OwnerB,
                ModHrEnabled = true, ModInventoryEnabled = true, ModInvoicingEnabled = true, ModFinanceEnabled = true,
                ModAccountingEnabled = true, ModFbrEnabled = true, ModFixedAssetsEnabled = true, ModExecutiveEnabled = true,
                Country = "Pakistan", FiscalYearStartMonth = 1, Currency = "PKR", CurrencySymbol = "Rs.",
                DateFormat = "Y-m-d", NumberFormat = "en", Timezone = "Asia/Karachi", CreatedAt = now, UpdatedAt = now,
            };
            db.Companies.Add(company);
            await db.SaveChangesAsync();
            int admin = await db.Roles.Where(r => r.Name == Role.Admin).Select(r => r.Id).FirstAsync();
            await new Memberships(db, TimeProvider.System).AddAsync(company.Id, OwnerB, admin);
            await new CompanySetup(db, new FirstUse(db, TimeProvider.System), TimeProvider.System).ProvisionAsync(company.Id);
            return company.Id;
        }
        finally
        {
            Gate.Release();
        }
    }
}
