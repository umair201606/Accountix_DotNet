using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.E2E.Harness;

/// <summary>Reads the e2e copy as one company (assertions and data set-up look-ups only).</summary>
public static class Db
{
    public static AccountixDbContext For(int companyId, string? connectionString = null)
    {
        var company = new CompanyContext();
        company.Set(companyId);
        return connectionString is null
            ? Reference.NewDb(company)
            : new AccountixDbContext(new DbContextOptionsBuilder<AccountixDbContext>().UseNpgsql(connectionString).Options, company);
    }

    public static int AccountId(int companyId, string code)
    {
        using var db = For(companyId);
        return db.Set<ChartOfAccount>().Where(a => a.Code == code).Select(a => a.Id).Single();
    }
}
