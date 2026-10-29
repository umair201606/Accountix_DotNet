using System.Net;
using Accountix.Core.Entities;
using Accountix.Core.Security;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.SuperAdmin.Tests;

/// <summary>My Books: the super admin's own companies, the same quota, slug rule and provisioning as the portal.</summary>
public class MyCompaniesTests
{
    /// <summary>A fresh super admin (so quota counts start from zero).</summary>
    private static async Task<int> NewSuperAsync() => await S.NewUserAsync(superAdmin: true);

    [ReferenceFact]
    public async Task The_super_admin_creates_their_own_provisioned_company_and_enters_it()
    {
        await using var host = await S.HostAsync();
        int me = await NewSuperAsync();
        var c = host.ClientFor(me);
        string slug = $"sacon-{S.Tag()}";
        var r = await S.PostAsync(c, "/superadmin/my-companies/", "/superadmin/my-companies/", ("name", " SA Console Books "), ("slug", slug));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/dashboard/", S.Location(r));

        await using var db = S.Db();
        var co = await db.Companies.AsNoTracking().SingleAsync(x => x.Slug == slug);
        Assert.Equal("SA Console Books", co.Name);
        Assert.Equal(me, co.CreatedBy);
        Assert.Equal("free", co.PlanName);
        var m = await db.Memberships.AsNoTracking().SingleAsync(x => x.CompanyId == co.Id);
        Assert.Equal((me, CompanyMembership.StatusActive, await S.RoleIdAsync(Role.Admin)), (m.UserId, m.Status, m.RoleId));
        Assert.Null(m.EmployeeCode);
        Assert.True(await db.Set<ChartOfAccount>().IgnoreQueryFilters().CountAsync(a => a.CompanyId == co.Id) > 50);
        Assert.True(await db.Set<VoucherNumber>().IgnoreQueryFilters().CountAsync(v => v.CompanyId == co.Id) >= 14);

        // session["company_id"]: the session now opens that company.
        var cookie = c.Cookies.GetCookies(new Uri("http://localhost/"))[SessionCookie.CookieName]!.Value;
        var session = SessionCookie.Read(cookie, Reference.SecretKey, DateTimeOffset.UtcNow)!;
        Assert.Equal(co.Id, SessionCookie.CompanyOf(session));

        string page = S.Text(await c.GetStringAsync("/superadmin/my-companies/"));
        Assert.Contains("Company 'SA Console Books' created — its books are ready.", page);
        Assert.Contains("SA Console Books", page);
        Assert.Contains($"href=\"/company/switch/{co.Id}\"", page);
        Assert.Contains("Companies I Created", page);
        Assert.Contains("Companies I Belong To", page);
    }

    [ReferenceFact]
    public async Task Quota_name_and_slug_are_refused_in_pythons_words()
    {
        await using var host = await S.HostAsync();
        int me = await NewSuperAsync();
        await using (var db = S.Db())
            await db.Users.Where(u => u.Id == me).ExecuteUpdateAsync(s => s.SetProperty(u => u.MaxCompaniesOwned, 1));
        var c = host.ClientFor(me);
        async Task<string> Try(string name, string slug)
        {
            var r = await S.PostAsync(c, "/superadmin/my-companies/", "/superadmin/my-companies/", ("name", name), ("slug", slug));
            return S.Location(r) == "/superadmin/my-companies/" ? await S.FollowAsync(c, r) : S.Location(r);
        }
        Assert.Contains("Company name is required.", await Try(" ", "abc"));
        Assert.Contains("Company address must be 2-62 characters: lowercase letters, digits and hyphens only.", await Try("X", "Bad slug"));
        string taken = $"taken-{S.Tag()}";
        await S.NewCompanyAsync("Taken", taken);
        Assert.Contains("That company address is already taken.", await Try("X", taken));
        Assert.Equal("/dashboard/", await Try("First", $"first-{S.Tag()}"));
        Assert.Contains("You have reached your company creation limit (1).", await Try("Second", $"second-{S.Tag()}"));
        string page = S.Text(await c.GetStringAsync("/superadmin/my-companies/"));
        Assert.Contains("Your creation limit is 1 — you've used 1.", page);
        Assert.Contains("You've reached your creation limit.", page);
        Assert.Contains("disabled=\"disabled\"", page);
    }

    [ReferenceFact]
    public async Task Six_creations_at_once_never_pass_the_quota_twice()
    {
        await using var host = await S.HostAsync();
        int me = await NewSuperAsync();
        await using (var db = S.Db())
            await db.Users.Where(u => u.Id == me).ExecuteUpdateAsync(s => s.SetProperty(u => u.MaxCompaniesOwned, 2));
        var clients = host.ClientsFor(me, null, 6);
        var tokens = await Task.WhenAll(clients.Select(x => x.TokenFromAsync("/superadmin/my-companies/")));
        string tag = S.Tag();
        var rs = await Race.RunAsync(6, i => clients[i].PostFormWithTokenAsync("/superadmin/my-companies/",
            [new("name", $"Mine {i}"), new("slug", $"mine-{tag}-{i}")], tokens[i]));
        Race.NoServerErrors(rs);
        await using var db2 = S.Db();
        Assert.Equal(2, await db2.Companies.CountAsync(x => x.CreatedBy == me));
        Assert.Equal(2, rs.Count(r => S.Location(r) == "/dashboard/"));
    }

    [ReferenceFact]
    public async Task Pending_invitations_and_memberships_elsewhere_are_listed()
    {
        await using var host = await S.HostAsync();
        string email = $"sa.{S.Tag()}@ref.test";
        int me = await S.NewUserAsync(email, superAdmin: true);
        int other = await S.NewCompanyAsync($"Inviting Co {S.Tag()}");
        int belongs = await S.NewCompanyAsync($"Member Co {S.Tag()}");
        await S.AddMembershipAsync(belongs, me, Role.Manager);
        await using (var db = S.Db())
        {
            db.Invitations.Add(new CompanyInvitation
            {
                CompanyId = other, Email = email, RoleId = await S.RoleIdAsync(Role.Manager), Status = CompanyInvitation.Sent,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        string page = await host.ClientFor(me).GetStringAsync("/superadmin/my-companies/");
        Assert.Contains("Pending Invitations", page);
        Assert.Contains("Inviting Co", page);
        Assert.Contains("<input type=\"hidden\" name=\"next\" value=\"/superadmin/my-companies/\">", page);
        Assert.Contains("Member Co", page);
        Assert.Contains("<td>manager</td>", page);
        Assert.DoesNotContain("No companies yet", page);
        // Someone with nothing sees the empty state.
        string empty = await host.ClientFor(await NewSuperAsync()).GetStringAsync("/superadmin/my-companies/");
        Assert.Contains("No companies yet", empty);
    }
}
