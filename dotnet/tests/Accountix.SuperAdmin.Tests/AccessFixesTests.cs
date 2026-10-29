using System.Text.Json;
using Accountix.Core.Access;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.SuperAdmin.Tests;

/// <summary>
/// The console's part of tests/e2e/test_access_fixes.py (dotnet/docs/ACCESS_REVIEW.md):
/// console-made companies are owned by their named admin; a person the
/// console made starts their FIRST company from the account, any FURTHER one
/// from the role they join as. The invitation itself is Settings' route, so these
/// tests boot Platform's controllers next to the console and drive its invite and
/// accept forms; what the person may then do is read with the .NET access check.
/// The expectations are the answers Python's routes gave (role, modules, rights).
/// </summary>
public class AccessFixesTests
{
    private static async Task<int> ConsoleCompanyAsync(PageClient sa, string adminEmail)
    {
        string slug = $"co-{S.Tag()}";
        var r = await S.PostAsync(sa, "/superadmin/companies/", "/superadmin/companies/",
            ("name", $"Company {slug}"), ("slug", slug), ("admin_email", adminEmail), ("admin_name", adminEmail.Split('@')[0]),
            ("password", S.Pw));
        Assert.Equal("/superadmin/companies/", S.Location(r));
        await using var db = S.Db();
        return await db.Companies.Where(c => c.Slug == slug).Select(c => c.Id).SingleAsync();
    }

    private static async Task<int> UserIdAsync(string email)
    {
        await using var db = S.Db();
        return await db.Users.Where(u => u.Email.ToLower() == email.ToLower()).Select(u => u.Id).SingleAsync();
    }

    /// <summary>
    /// The company's admin invites <paramref name="email"/> as <paramref name="role"/> and they accept, through
    /// Settings' routes (as Python's test did through its own); then what the person may do in that company.
    /// </summary>
    private static async Task<EffectiveAccess> InviteAndAcceptAsync(ModuleHost host, int adminId, int companyId, string email, string role, int whoId)
    {
        var admin = host.ClientFor(adminId, companyId);
        var r = await admin.PostFormWithTokenAsync("/settings/invite", [new("email", email), new("role", role)],
                                                   await S.TokenAsync(admin, "/settings/members/"));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, r.StatusCode);
        int invId;
        await using (var db = S.Db())
            invId = await db.Invitations.Where(i => i.Email == email.ToLowerInvariant() && i.Status == CompanyInvitation.Sent && i.CompanyId == companyId)
                .Select(i => i.Id).SingleAsync();
        var who = host.ClientFor(whoId);
        r = await who.PostFormWithTokenAsync($"/settings/invitations/{invId}/accept", [], await S.TokenAsync(who, "/portal/"));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, r.StatusCode);
        await using var check = S.Db();
        Assert.Equal(CompanyInvitation.Accepted, await check.Invitations.Where(i => i.Id == invId).Select(i => i.Status).SingleAsync());
        var access = await new AccessService(check).ResolveAsync(whoId, companyId);
        Assert.NotNull(access);
        Assert.Equal(companyId, access!.CompanyId);
        return access;
    }

    [ReferenceFact]
    public async Task The_console_names_the_owner()
    {
        await using var host = await S.HostAsync();
        string owner = $"owner_{S.Tag()}@x.com";
        int cid = await ConsoleCompanyAsync(S.Super(host), owner);
        await using var db = S.Db();
        Assert.Equal(await UserIdAsync(owner), await db.Companies.Where(c => c.Id == cid).Select(c => c.CreatedBy).SingleAsync());
        // Their membership is the company's admin, and the .NET access check says they own it.
        var access = await new AccessService(db).ResolveAsync(await UserIdAsync(owner), cid);
        Assert.True(access is { IsOwner: true, IsAdmin: true });
        Assert.Equal(cid, access.CompanyId);
    }

    [ReferenceFact]
    public async Task A_further_company_starts_from_the_role_not_the_account()
    {
        await using var host = await S.HostWithPlatformAsync();
        var sa = S.Super(host);
        string ali = $"ali_{S.Tag()}@x.com", bina = $"bina_{S.Tag()}@x.com";
        int companyA = await ConsoleCompanyAsync(sa, ali);               // Ali: admin, every switch on
        int companyB = await ConsoleCompanyAsync(sa, bina);
        int aliId = await UserIdAsync(ali), binaId = await UserIdAsync(bina);
        var inB = await InviteAndAcceptAsync(host, binaId, companyB, ali, "employee", aliId);

        // Python's answers for this flow: an employee, HR only, no rights beyond.
        Assert.Equal(Role.Employee, inB.RoleName);
        Assert.True(inB.CanOpenModule(Modules.Hr));
        foreach (string m in new[] { "accounting", "inventory", "invoicing", "finance", "fbr", "fixed_assets", "executive" })
            Assert.False(inB.CanOpenModule(m), m);
        Assert.False(inB.Can("journal_vouchers", Rights.Approve));
        Assert.False(inB.Can("employees", Rights.View));

        // Ali's own company is untouched.
        await using var db2 = S.Db();
        var inA = (await new AccessService(db2).ResolveAsync(aliId, companyA))!;
        Assert.True(inA.IsAdmin && inA.CanOpenModule(Modules.Accounting));
    }

    [ReferenceFact]
    public async Task A_manager_joining_a_further_company_gets_the_hr_rights_to_run_a_team()
    {
        await using var host = await S.HostWithPlatformAsync();
        var sa = S.Super(host);
        string boss = $"boss_{S.Tag()}@x.com", mgr = $"mgr_{S.Tag()}@x.com";
        int cid = await ConsoleCompanyAsync(sa, boss);
        await ConsoleCompanyAsync(sa, mgr);                               // mgr's first company elsewhere
        var access = await InviteAndAcceptAsync(host, await UserIdAsync(boss), cid, mgr, "manager", await UserIdAsync(mgr));
        Assert.Equal(Role.Manager, access.RoleName);
        Assert.True(access.CanOpenModule(Modules.Hr));
        Assert.False(access.CanOpenModule(Modules.Accounting));
        Assert.True(access.Can("leaves", Rights.Approve) && access.Can("employees", Rights.Edit));
        Assert.False(access.Can("chart_of_accounts", Rights.View));
    }

    [ReferenceFact]
    public async Task A_first_company_still_starts_from_the_account_the_console_made()
    {
        await using var host = await S.HostWithPlatformAsync();
        var sa = S.Super(host);
        string owner = $"own_{S.Tag()}@x.com", newcomer = $"New_{S.Tag()}@X.com";   // mixed case: stored lower
        int cid = await ConsoleCompanyAsync(sa, owner);
        var r = await S.PostAsync(sa, "/superadmin/users/", "/superadmin/users/", ("email", newcomer), ("full_name", "New Comer"), ("password", S.Pw));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, r.StatusCode);
        int newId = await UserIdAsync(newcomer);
        await using (var db0 = S.Db())
            Assert.Equal(newcomer.ToLowerInvariant(), await db0.Users.Where(u => u.Id == newId).Select(u => u.Email).SingleAsync());

        await InviteAndAcceptAsync(host, await UserIdAsync(owner), cid, newcomer, "employee", newId);
        await using var db = S.Db();
        var m = await db.Memberships.AsNoTracking().SingleAsync(x => x.CompanyId == cid && x.UserId == newId);
        var flags = JsonDocument.Parse(m.ModuleFlags!).RootElement;
        Assert.True(flags.GetProperty("hr").GetBoolean());
        Assert.False(flags.GetProperty("accounting").GetBoolean());
        // First company: rights stay unrestricted until an admin sets them, exactly as before.
        Assert.False(await db.UserPermissions.IgnoreQueryFilters().AnyAsync(p => p.CompanyId == cid && p.UserId == newId));
    }
}
