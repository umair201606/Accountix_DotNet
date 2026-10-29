using System.Net;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.SuperAdmin.Tests;

/// <summary>Block / unblock one person in one company: kept membership, guards, the per-company queue.</summary>
public class MemberBlockTests
{
    private static string Url(int cid, int mid) => $"/superadmin/companies/{cid}/members/{mid}/block";

    private static async Task<CompanyMembership> MembershipAsync(int id)
    {
        await using var db = S.Db();
        return await db.Memberships.AsNoTracking().FirstAsync(m => m.Id == id);
    }

    /// <summary>An owner-admin and one more member in a fresh company.</summary>
    private static async Task<(int Company, int Owner, int Member, int MemberUser)> CompanyAsync(string role = Role.Manager, string code = "EMP042")
    {
        int owner = await S.NewUserAsync();
        int cid = await S.NewCompanyAsync($"Block Co {S.Tag()}", createdBy: owner);
        int ownerM = await S.AddMembershipAsync(cid, owner, Role.Admin);
        int victim = await S.NewUserAsync();
        int mid = await S.AddMembershipAsync(cid, victim, role, employeeCode: code);
        _ = ownerM;
        return (cid, owner, mid, victim);
    }

    [ReferenceFact]
    public async Task Blocking_keeps_the_membership_and_only_stops_the_person_entering()
    {
        await using var host = await S.HostAsync();
        var c = S.Super(host);
        var (cid, _, mid, victim) = await CompanyAsync();
        string page = $"/superadmin/companies/{cid}/";

        var r = await S.PostAsync(c, Url(cid, mid), page);
        Assert.Equal(page, S.Location(r));
        Assert.Contains(" blocked.", await S.FollowAsync(c, r));
        var m = await MembershipAsync(mid);
        Assert.Equal(CompanyMembership.Blocked, m.Status);
        Assert.Equal("EMP042", m.EmployeeCode);
        Assert.Equal(await S.RoleIdAsync(Role.Manager), m.RoleId);
        Assert.Contains("<span class=\"pill pill-danger\">blocked</span>", await c.GetStringAsync(page));

        // A blocked member cannot open the company (the .NET access check admits ACTIVE only).
        var who = host.ClientFor(victim, cid);
        Assert.Equal(HttpStatusCode.Forbidden, (await who.GetAsync("/superadmin/")).StatusCode);   // signed in, but no company
        await using (var db = S.Db())
        {
            var access = await new Accountix.Core.Access.AccessService(db).ResolveAsync(victim, cid);
            Assert.NotEqual(cid, access?.CompanyId);
        }

        r = await S.PostAsync(c, Url(cid, mid), page);
        Assert.Contains(" unblocked.", await S.FollowAsync(c, r));
        Assert.Equal(CompanyMembership.StatusActive, (await MembershipAsync(mid)).Status);
        await using var db2 = S.Db();
        var back = await new Accountix.Core.Access.AccessService(db2).ResolveAsync(victim, cid);
        Assert.Equal(cid, back?.CompanyId);
    }

    [ReferenceFact]
    public async Task The_owner_the_last_admin_and_a_pending_membership_are_refused()
    {
        await using var host = await S.HostAsync();
        var c = S.Super(host);
        var (cid, owner, _, _) = await CompanyAsync();
        string page = $"/superadmin/companies/{cid}/";
        await using var db = S.Db();
        int ownerM = await db.Memberships.Where(m => m.CompanyId == cid && m.UserId == owner).Select(m => m.Id).FirstAsync();
        Assert.Contains("The company owner cannot be blocked.", await S.FollowAsync(c, await S.PostAsync(c, Url(cid, ownerM), page)));

        // A second admin who is the last active one (the owner made a plain member elsewhere).
        int lone = await S.NewCompanyAsync($"Lone {S.Tag()}");
        int adminUser = await S.NewUserAsync();
        int adminM = await S.AddMembershipAsync(lone, adminUser, Role.Admin);
        Assert.Contains("Cannot block the last active admin of the company.",
                        await S.FollowAsync(c, await S.PostAsync(c, Url(lone, adminM), $"/superadmin/companies/{lone}/")));
        Assert.Equal(CompanyMembership.StatusActive, (await MembershipAsync(adminM)).Status);

        int pendingM = await S.AddMembershipAsync(lone, await S.NewUserAsync(), status: CompanyMembership.Pending);
        Assert.Contains("Only an active or blocked membership can be toggled.",
                        await S.FollowAsync(c, await S.PostAsync(c, Url(lone, pendingM), $"/superadmin/companies/{lone}/")));
        Assert.Equal(CompanyMembership.Pending, (await MembershipAsync(pendingM)).Status);
    }

    [ReferenceFact]
    public async Task A_membership_of_another_company_is_404()
    {
        await using var host = await S.HostAsync();
        var c = S.Super(host);
        int a = await S.NewCompanyAsync("Cross A");
        int b = await S.NewCompanyAsync("Cross B");
        int mid = await S.AddMembershipAsync(a, await S.NewUserAsync());
        var r = await S.PostAsync(c, Url(b, mid), $"/superadmin/companies/{b}/");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal(CompanyMembership.StatusActive, (await MembershipAsync(mid)).Status);
    }

    [ReferenceFact]
    public async Task Six_toggles_at_once_are_six_flips_in_turn()
    {
        await using var host = await S.HostAsync();
        var (cid, _, mid, _) = await CompanyAsync();
        var clients = host.ClientsFor(S.SuperAdmin, null, 6);
        var tokens = await Task.WhenAll(clients.Select(x => x.TokenFromAsync($"/superadmin/companies/{cid}/")));
        var started = DateTime.UtcNow.AddSeconds(-2);
        var rs = await Race.RunAsync(6, i => clients[i].PostFormWithTokenAsync(Url(cid, mid), [], tokens[i]));
        Race.NoServerErrors(rs);
        Assert.All(rs, r => Assert.Equal(HttpStatusCode.Redirect, r.StatusCode));
        // Queued, so none was lost: an even number of flips leaves it active, and the
        // audit trail shows six status changes, each from the other value.
        Assert.Equal(CompanyMembership.StatusActive, (await MembershipAsync(mid)).Status);
        await using var db = S.Db();
        var changes = await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == "Membership" && a.EntityId == mid && a.Action == "update" && a.CreatedAt >= started)
            .OrderBy(a => a.Id).Select(a => a.Changes).ToListAsync();
        Assert.Equal(6, changes.Count);
        for (int i = 0; i < 6; i++)
            Assert.Contains(i % 2 == 0 ? "\"old\": \"active\", \"new\": \"blocked\"" : "\"old\": \"blocked\", \"new\": \"active\"", changes[i]);
    }

    [ReferenceFact]
    public async Task Blocking_two_admins_at_once_never_leaves_the_company_without_one()
    {
        await using var host = await S.HostAsync();
        int cid = await S.NewCompanyAsync($"Two Admins {S.Tag()}");
        int a = await S.AddMembershipAsync(cid, await S.NewUserAsync(), Role.Admin);
        int b = await S.AddMembershipAsync(cid, await S.NewUserAsync(), Role.Admin);
        var clients = host.ClientsFor(S.SuperAdmin, null, 6);
        var tokens = await Task.WhenAll(clients.Select(x => x.TokenFromAsync($"/superadmin/companies/{cid}/")));
        var rs = await Race.RunAsync(6, i => clients[i].PostFormWithTokenAsync(Url(cid, i % 2 == 0 ? a : b), [], tokens[i]));
        Race.NoServerErrors(rs);
        var statuses = new[] { (await MembershipAsync(a)).Status, (await MembershipAsync(b)).Status };
        Assert.Contains(CompanyMembership.StatusActive, statuses);
    }
}
