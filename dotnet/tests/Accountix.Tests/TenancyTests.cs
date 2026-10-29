using Accountix.TestSupport;
using Accountix.Core.Data;
using Accountix.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Tests;

/// <summary>Company isolation rules, on the in-memory provider (Postgres runs in the integration tests).</summary>
public class TenancyTests
{
    private static (AccountixDbContext Db, CompanyContext Company) NewDb(string name)
    {
        var company = new CompanyContext();
        var options = new DbContextOptionsBuilder<AccountixDbContext>().UseInMemoryDatabase(name).Options;
        return (new AccountixDbContext(options, company), company);
    }

    private static void Seed(string name)
    {
        var (db, company) = NewDb(name);
        using (company.Unscoped())
        {
            db.UserPermissions.AddRange(
                new UserPermission { CompanyId = 1, UserId = 10, Resource = "journal_vouchers" },
                new UserPermission { CompanyId = 1, UserId = 10, Resource = "chart_of_accounts" },
                new UserPermission { CompanyId = 2, UserId = 10, Resource = "journal_vouchers" });
            db.SaveChanges();
        }
    }

    [Fact]
    public void Queries_see_only_the_active_company()
    {
        string name = nameof(Queries_see_only_the_active_company);
        Seed(name);
        var (db, company) = NewDb(name);
        company.Set(1);
        Assert.Equal(2, db.UserPermissions.Count());
        Assert.All(db.UserPermissions.ToList(), p => Assert.Equal(1, p.CompanyId));
        company.Set(2);
        Assert.Single(db.UserPermissions.ToList());
    }

    [Fact]
    public void Querying_with_no_company_fails_instead_of_returning_everything()
    {
        string name = nameof(Querying_with_no_company_fails_instead_of_returning_everything);
        Seed(name);
        var (db, _) = NewDb(name);
        Assert.Throws<NoActiveCompanyException>(() => db.UserPermissions.ToList());
        Assert.Throws<NoActiveCompanyException>(() => db.UserPermissions.Count());
    }

    [Fact]
    public void Unscoped_and_IgnoreQueryFilters_are_the_only_ways_to_see_every_company()
    {
        string name = nameof(Unscoped_and_IgnoreQueryFilters_are_the_only_ways_to_see_every_company);
        Seed(name);
        var (db, company) = NewDb(name);
        using (company.Unscoped())
            Assert.Equal(3, db.UserPermissions.Count());
        Assert.Equal(3, db.UserPermissions.IgnoreQueryFilters([AccountixDbContext.TenantFilter]).Count());
        Assert.Throws<NoActiveCompanyException>(() => db.UserPermissions.Count());
    }

    [Fact]
    public void New_rows_are_stamped_and_cannot_name_another_company()
    {
        string name = nameof(New_rows_are_stamped_and_cannot_name_another_company);
        var (db, company) = NewDb(name);
        company.Set(5);
        var row = new UserPermission { UserId = 1, Resource = "products" };
        db.UserPermissions.Add(row);
        db.SaveChanges();
        Assert.Equal(5, row.CompanyId);

        db.UserPermissions.Add(new UserPermission { CompanyId = 6, UserId = 1, Resource = "stock" });
        Assert.Throws<CrossCompanyWriteException>(() => db.SaveChanges());
    }

    [Fact]
    public void A_new_row_with_no_company_is_refused()
    {
        var (db, _) = NewDb(nameof(A_new_row_with_no_company_is_refused));
        db.UserPermissions.Add(new UserPermission { UserId = 1, Resource = "products" });
        Assert.Throws<NoActiveCompanyException>(() => db.SaveChanges());
    }

    [Fact]
    public void Rows_never_move_company_and_another_company_s_row_cannot_be_written()
    {
        string name = nameof(Rows_never_move_company_and_another_company_s_row_cannot_be_written);
        Seed(name);
        var (db, company) = NewDb(name);
        company.Set(1);
        var mine = db.UserPermissions.First();
        mine.CompanyId = 2;
        Assert.Throws<CrossCompanyWriteException>(() => db.SaveChanges());

        var (db2, company2) = NewDb(name);
        UserPermission theirs;
        using (company2.Unscoped())
            theirs = db2.UserPermissions.First(p => p.CompanyId == 2);
        company2.Set(1);
        theirs.CanEdit = true;
        Assert.Throws<CrossCompanyWriteException>(() => db2.SaveChanges());
        db2.Remove(theirs);
        Assert.Throws<CrossCompanyWriteException>(() => db2.SaveChanges());
    }

    [Theory]
    [InlineData("HasHrAccess", "has_hr_access")]
    [InlineData("ModFixedAssetsEnabled", "mod_fixed_assets_enabled")]
    [InlineData("IpAddress", "ip_address")]
    [InlineData("Cnic", "cnic")]
    [InlineData("Id", "id")]
    public void Column_names_are_snake_case(string property, string column)
    {
        Assert.Equal(column, AccountixDbContext.ToSnake(property));
    }
}
