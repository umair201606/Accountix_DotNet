using System.Net;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Platform.Tests;

/// <summary>Settings: rights, members, invitations and the audit log (test_settings_access.py, test_access_fixes.py,
/// test_login_ownership.py, test_multicompany.py, test_audit_log.py).</summary>
public class SettingsAdminTests
{
    private static async Task<string> RoleOf(int cid, int uid)
    {
        await using var db = P.Db();
        return await db.Memberships.Where(m => m.CompanyId == cid && m.UserId == uid).Select(m => m.Role.Name).SingleAsync();
    }

    private static async Task<Dictionary<string, string>> GridAsync(int cid, int uid)
    {
        await using var db = P.Db(cid);
        return await db.UserPermissions.Where(p => p.UserId == uid)
            .ToDictionaryAsync(p => p.Resource, p => $"{p.CanView}{p.CanCreate}{p.CanEdit}{p.CanApprove}{p.CanDelete}");
    }

    [ReferenceFact]
    public async Task The_admin_tabs_render_within_the_query_budget_and_employees_cannot_use_them()
    {
        await using var host = await P.HostAsync();
        var (cid, admin) = await P.FreshCompanyAsync(host);
        int emp = await P.AddMemberAsync(host, cid, Role.Employee);
        var c = host.ClientFor(admin, cid);
        foreach (string tab in new[] { "rights", "members", "audit", "invites" })
        {
            await c.GetAsync($"/settings/?tab={tab}");
            host.Queries.Reset();
            var r = await c.GetAsync($"/settings/?tab={tab}");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.True(host.Queries.Count <= 40, $"{tab}: {host.Queries.Count} queries\n{string.Join("\n", host.Queries.Repeated(5))}");
        }
        host.Queries.Reset();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/settings/users/{emp}/rights")).StatusCode);
        Assert.True(host.Queries.Count <= 40);

        var e = host.ClientFor(emp, cid);
        Assert.Equal(HttpStatusCode.Forbidden, (await e.GetAsync("/settings/audit-log.csv")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await e.GetAsync("/settings/members/")).StatusCode);
        // An employee cannot grant themselves modules, reset someone's rights or switch an admin off.
        foreach (string url in new[] { $"/settings/users/{emp}/rights", $"/settings/users/{admin}/reset-rights", $"/settings/users/{admin}/toggle-active" })
        {
            var r = await P.PostAsync(e, url, "/settings/", ("module_accounting", "on"));
            Assert.Equal("/settings/", r.Headers.Location?.OriginalString);
        }
        await using var db = P.Db();
        Assert.True((await db.Users.SingleAsync(u => u.Id == admin)).Active);
        Assert.NotEqual(true, (await db.Memberships.SingleAsync(m => m.UserId == emp && m.CompanyId == cid)).ModuleFlag("accounting"));
        // And an admin cannot switch themselves off.
        var self = await P.PostAsync(c, $"/settings/users/{admin}/toggle-active", "/settings/?tab=rights");
        Assert.Contains("You cannot deactivate your own account.", await P.FollowAsync(c, self));
    }

    private static readonly (string, string)[] RightsForm =
    [
        ("module_hr", "on"), ("module_accounting", "on"),
        ("perm_journal_vouchers_view", "on"), ("perm_journal_vouchers_create", "on"), ("perm_employees_view", "on"),
    ];

    /// <summary>The employee's rights grid, module flags and the membership's audit rows (golden: Rights).</summary>
    private static async Task<object> RightsShapeAsync(int cid, int emp, int afterAuditId)
    {
        await using var db = P.Db();
        var m = await db.Memberships.AsNoTracking().SingleAsync(x => x.CompanyId == cid && x.UserId == emp);
        var ids = new Dictionary<int, string> { [emp] = "EMP", [m.Id] = "MEMBERSHIP" };
        return new
        {
            grid = new SortedDictionary<string, string>(await GridAsync(cid, emp), StringComparer.Ordinal),
            module_flags = m.ModuleFlags,
            // A membership's audit reference is its person's id ("Membership 349 edited").
            audit = (await SettingsFinanceTests.AuditRowsAsync(cid, "Membership", afterAuditId))
                .Select(r => P.Norm(r.Replace($"|{emp}|", "|EMP|").Replace($"Membership {emp} ", "Membership EMP "), ids: ids)).ToList(),
        };
    }

    [ReferenceFact]
    public async Task Saving_rights_writes_what_python_wrote()
    {
        await using var host = await P.HostAsync();
        var (net, netAdmin) = await P.FreshCompanyAsync(host);
        int netEmp = await P.AddMemberAsync(host, net, Role.Employee);
        int before = await SettingsFinanceTests.MaxAuditIdAsync();
        var c = host.ClientFor(netAdmin, net);
        var r = await P.PostAsync(c, $"/settings/users/{netEmp}/rights", $"/settings/users/{netEmp}/rights", RightsForm);
        Assert.Contains("Access rights saved for", await P.FollowAsync(c, r));
        Assert.Equal(Accountix.Platform.Services.AccessDefaults.AllSections.Count, (await GridAsync(net, netEmp)).Count);
        Golden.Match("Rights", await RightsShapeAsync(net, netEmp, before));
    }

    [ReferenceFact]
    public async Task A_login_shared_with_another_company_cannot_be_reset_here_and_the_owner_is_protected()
    {
        await using var host = await P.HostAsync();
        var (cid, admin) = await P.FreshCompanyAsync(host);
        var (other, _) = await P.FreshCompanyAsync(host);
        int shared = await P.AddMemberAsync(host, other, Role.Employee);
        await P.AddMemberAsync(host, cid, Role.Employee, shared);
        int local = await P.AddMemberAsync(host, cid, Role.Employee);
        var c = host.ClientFor(admin, cid);
        string page = WebUtility.HtmlDecode(await (await c.GetAsync($"/settings/users/{shared}/rights")).Content.ReadAsStringAsync());
        Assert.Contains("also uses this login in another company, so only they can change it.", page);
        var r = await P.PostAsync(c, $"/settings/users/{shared}/rights", $"/settings/users/{shared}/rights",
            ("new_password", "Taken-over-123"), ("confirm_password", "Taken-over-123"));
        Assert.Contains("also uses this login in another company", await P.FollowAsync(c, r));
        var t = await P.PostAsync(c, $"/settings/users/{shared}/toggle-active", "/settings/?tab=rights");
        Assert.Contains("remove them from this company under Members", await P.FollowAsync(c, t));
        // A login used only here can still be reset.
        var ok = await P.PostAsync(c, $"/settings/users/{local}/rights", $"/settings/users/{local}/rights",
            ("new_password", "Fresh-start-123"), ("confirm_password", "Fresh-start-123"), ("module_hr", "on"));
        Assert.Contains("Access rights saved", await P.FollowAsync(c, ok));
        await using var db = P.Db();
        Assert.True(Accountix.Core.Security.PasswordHasher.Verify((await db.Users.SingleAsync(u => u.Id == local)).PasswordHash, "Fresh-start-123"));
        Assert.True(Accountix.Core.Security.PasswordHasher.Verify((await db.Users.SingleAsync(u => u.Id == shared)).PasswordHash, P.Password));
        // The super admin account cannot be reached from a tenant's settings.
        string sa = $"sa.{P.Tag()}@ref.test";
        int saId = await P.NewUserAsync(sa, superAdmin: true);
        await P.AddMemberAsync(host, cid, Role.Employee, saId);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync($"/settings/users/{saId}/rights")).StatusCode);
    }

    [ReferenceFact]
    public async Task Invite_accept_role_change_remove_and_discard_follow_the_python_rules()
    {
        await using var host = await P.HostAsync();
        var (cid, admin) = await P.FreshCompanyAsync(host);
        var (home, _) = await P.FreshCompanyAsync(host);
        string email = $"Mixed.Case.{P.Tag()}@Ref.Test";
        int guest = await P.NewUserAsync(email);
        await P.AddMemberAsync(host, home, Role.Admin, guest);           // their first company
        var a = host.ClientFor(admin, cid);
        Assert.Contains("No registered user has that email address.", await P.FollowAsync(a, await P.PostAsync(a, "/settings/invite", "/settings/?tab=members", ("email", "nobody@x.test"), ("role", "manager"))));
        Assert.Contains("Invitation sent to", await P.FollowAsync(a, await P.PostAsync(a, "/settings/invite", "/settings/?tab=members", ("email", email), ("role", "manager"))));
        Assert.Contains("already pending", await P.FollowAsync(a, await P.PostAsync(a, "/settings/invite", "/settings/?tab=members", ("email", email.ToUpperInvariant()), ("role", "manager"))));

        var g = host.ClientFor(guest, home);
        string invites = await (await g.GetAsync("/settings/invitations/")).Content.ReadAsStringAsync();
        Assert.Contains("/accept", invites);
        int invId;
        await using (var db = P.Db()) invId = await db.Invitations.Where(i => i.CompanyId == cid).Select(i => i.Id).SingleAsync();
        // An answer never redirects off-site.
        var acc = await P.PostAsync(g, $"/settings/invitations/{invId}/accept", "/settings/invitations/", ("next", "https://evil.example/"));
        Assert.Equal("/dashboard/", acc.Headers.Location?.OriginalString);
        Assert.Equal("manager", await RoleOf(cid, guest));
        // A further company starts from the role, not the account: HR only, HR rights written out.
        await using (var db = P.Db())
        {
            var m = await db.Memberships.SingleAsync(x => x.CompanyId == cid && x.UserId == guest);
            Assert.Equal("{\"accounting\": false, \"executive\": false, \"fbr\": false, \"finance\": false, \"fixed_assets\": false, \"hr\": true, \"inventory\": false, \"invoicing\": false}", m.ModuleFlags);
        }
        var grid = await GridAsync(cid, guest);
        Assert.Equal("TrueTrueTrueTrueTrue", grid["employees"]);
        Assert.Equal("FalseFalseFalseFalseFalse", grid["journal_vouchers"]);

        int mid;
        await using (var db = P.Db()) mid = await db.Memberships.Where(x => x.CompanyId == cid && x.UserId == guest).Select(x => x.Id).SingleAsync();
        // Role change moves the template rights...
        Assert.Contains("changed to Employee", await P.FollowAsync(a, await P.PostAsync(a, $"/settings/members/{mid}/role", "/settings/?tab=members", ("role", "employee"))));
        Assert.Equal("FalseFalseFalseFalseFalse", (await GridAsync(cid, guest))["employees"]);
        // ...but never hand-set ones.
        await P.PostAsync(a, $"/settings/users/{guest}/rights", $"/settings/users/{guest}/rights", ("module_hr", "on"), ("perm_leaves_view", "on"));
        await P.PostAsync(a, $"/settings/members/{mid}/role", "/settings/?tab=members", ("role", "manager"));
        Assert.Equal("TrueFalseFalseFalseFalse", (await GridAsync(cid, guest))["leaves"]);
        Assert.Equal("FalseFalseFalseFalseFalse", (await GridAsync(cid, guest))["employees"]);

        // The owner cannot be removed; a member can, then the removed row can be discarded (and only then).
        int ownerMid;
        await using (var db = P.Db()) ownerMid = await db.Memberships.Where(x => x.CompanyId == cid && x.UserId == admin).Select(x => x.Id).SingleAsync();
        Assert.Contains("The company owner cannot be removed.", await P.FollowAsync(a, await P.PostAsync(a, $"/settings/members/{ownerMid}/remove", "/settings/?tab=members")));
        Assert.Contains("Only a removed membership can be discarded", await P.FollowAsync(a, await P.PostAsync(a, $"/settings/members/{mid}/restore", "/settings/?tab=members")));
        Assert.Contains("removed from the company.", await P.FollowAsync(a, await P.PostAsync(a, $"/settings/members/{mid}/remove", "/settings/?tab=members")));
        Assert.Contains("discarded.", await P.FollowAsync(a, await P.PostAsync(a, $"/settings/members/{mid}/restore", "/settings/?tab=members")));
        await using (var db = P.Db())
            Assert.False(await db.Memberships.AnyAsync(x => x.Id == mid));
    }

    [ReferenceFact]
    public async Task Six_accepts_at_once_admit_exactly_the_member_limit()
    {
        await using var host = await P.HostAsync();
        var (cid, admin) = await P.FreshCompanyAsync(host);
        await using (var db = P.Db())
        {
            var co = await db.Companies.SingleAsync(c => c.Id == cid);
            co.MaxMembers = 3;                                  // the owner + two more
            await db.SaveChangesAsync();
        }
        var guests = new List<(int Id, int Inv)>();
        int managerRole;
        await using (var db = P.Db()) managerRole = await db.Roles.Where(r => r.Name == Role.Manager).Select(r => r.Id).FirstAsync();
        for (int i = 0; i < 6; i++)
        {
            string email = $"g{i}.{P.Tag()}@ref.test";
            int id = await P.NewUserAsync(email);
            await using var db = P.Db();
            var inv = new CompanyInvitation { CompanyId = cid, Email = email, RoleId = managerRole, InvitedBy = admin, Status = "sent", CreatedAt = DateTime.UtcNow };
            db.Invitations.Add(inv);
            await db.SaveChangesAsync();
            guests.Add((id, inv.Id));
        }
        var clients = guests.Select(gu => host.ClientFor(gu.Id)).ToArray();
        var tokens = await Task.WhenAll(clients.Select(cl => P.TokenAsync(cl, "/settings/invitations/")));
        var rs = await Race.RunAsync(6, i => clients[i].PostFormWithTokenAsync($"/settings/invitations/{guests[i].Inv}/accept", [], tokens[i]));
        Race.NoServerErrors(rs);
        await using var check = P.Db();
        Assert.Equal(3, await check.Memberships.CountAsync(m => m.CompanyId == cid && m.Status == "active"));
    }

    [ReferenceFact]
    public async Task Two_admins_removing_each_other_at_once_never_leave_the_company_without_an_admin()
    {
        await using var host = await P.HostAsync();
        var (cid, _) = await P.FreshCompanyAsync(host);
        int a1 = await P.AddMemberAsync(host, cid, Role.Admin);
        int a2 = await P.AddMemberAsync(host, cid, Role.Admin);
        await using (var db = P.Db())
        {
            // The owner leaves (removed directly), so a1 and a2 are the last two admins.
            var owner = await db.Memberships.Where(m => m.CompanyId == cid && m.UserId != a1 && m.UserId != a2).SingleAsync();
            owner.Status = "removed";
            var co = await db.Companies.SingleAsync(c => c.Id == cid);
            co.CreatedBy = null;
            await db.SaveChangesAsync();
        }
        int m1, m2;
        await using (var db = P.Db())
        {
            m1 = await db.Memberships.Where(m => m.CompanyId == cid && m.UserId == a1).Select(m => m.Id).SingleAsync();
            m2 = await db.Memberships.Where(m => m.CompanyId == cid && m.UserId == a2).Select(m => m.Id).SingleAsync();
        }
        var c1 = host.ClientsFor(a1, cid, 3);
        var c2 = host.ClientsFor(a2, cid, 3);
        var clients = c1.Concat(c2).ToArray();
        var tokens = await Task.WhenAll(clients.Select(cl => P.TokenAsync(cl, "/settings/?tab=members")));
        var rs = await Race.RunAsync(6, i => clients[i].PostFormWithTokenAsync($"/settings/members/{(i < 3 ? m2 : m1)}/remove", [], tokens[i]));
        Race.NoServerErrors(rs);
        await using var check = P.Db();
        Assert.Equal(1, await check.Memberships.CountAsync(m => m.CompanyId == cid && m.Status == "active" && m.Role.Name == Role.Admin));
    }

    [ReferenceFact]
    public async Task The_audit_log_shows_this_company_only_leaves_out_console_visits_and_exports_csv()
    {
        await using var host = await P.HostAsync();
        var (cid, admin) = await P.FreshCompanyAsync(host);
        var (other, _) = await P.FreshCompanyAsync(host);
        string tag = P.Tag();
        await using (var db = P.Db())
        {
            db.AuditLogs.AddRange(
                new AuditLog { CompanyId = cid, CreatedAt = DateTime.UtcNow, UserId = admin, UserName = "Mine", Action = "update", Module = "settings",
                               EntityType = "Company profile", Reference = "ref-" + tag, Summary = "Company profile edited (phone)",
                               Changes = "[{\"field\": \"phone\", \"old\": \"1\", \"new\": \"2\"}]", Path = "/settings/company" },
                new AuditLog { CompanyId = other, CreatedAt = DateTime.UtcNow, Action = "update", Module = "settings", EntityType = "Company profile",
                               Reference = "theirs-" + tag, Summary = "Not mine" },
                new AuditLog { CompanyId = null, CreatedAt = DateTime.UtcNow, UserId = admin, Action = "login", Summary = "console-" + tag, Path = "/superadmin/login" },
                new AuditLog { CompanyId = null, CreatedAt = DateTime.UtcNow, UserId = admin, Action = "login", Summary = "signin-" + tag, Path = "/auth/login" });
            await db.SaveChangesAsync();
        }
        var c = host.ClientFor(admin, cid);
        string page = WebUtility.HtmlDecode(await (await c.GetAsync("/settings/?tab=audit")).Content.ReadAsStringAsync());
        Assert.Contains("ref-" + tag, page);
        Assert.Contains("signin-" + tag, page);
        Assert.DoesNotContain("theirs-" + tag, page);
        Assert.DoesNotContain("console-" + tag, page);
        Assert.Contains("<span class=\"al-old\">1</span> → <span class=\"al-new\">2</span>", page);
        string filtered = WebUtility.HtmlDecode(await (await c.GetAsync($"/settings/?tab=audit&q=ref-{tag}&action=update")).Content.ReadAsStringAsync());
        Assert.Contains("1 entry", filtered);
        var csv = await c.GetAsync($"/settings/audit-log.csv?q=ref-{tag}");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        string body = await csv.Content.ReadAsStringAsync();
        Assert.StartsWith("When (UTC),User,Action,Module,Record,Reference,Summary,Changes,IP\r\n", body);
        Assert.Contains($",Mine,update,settings,Company profile,ref-{tag},Company profile edited (phone),phone: 1 -> 2,", body);
    }

    [PerfFact]
    public async Task The_audit_log_page_and_csv_are_fast_on_a_year_of_data()
    {
        await using var host = await ModuleHost.StartOnAsync(Perf.ConnectionString, typeof(PlatformModule).Assembly);
        var c = host.ClientFor(Perf.AdminUserId, Perf.CompanyId);
        (await host.MeasureAsync(c, "/settings/?tab=audit")).Within(800, 40);
        (await host.MeasureAsync(c, "/settings/?tab=audit&q=INV&action=create")).Within(800, 40);
        (await host.MeasureAsync(c, "/settings/?tab=audit&page=50")).Within(800, 40);
        (await host.MeasureAsync(c, "/settings/audit-log.csv")).Within(3000, 40);
        foreach (string url in new[] { "/portal/", "/dashboard/", "/settings/?tab=members", "/settings/?tab=rights", "/settings/?tab=labels", "/settings/?tab=invoicing" })
            (await host.MeasureAsync(c, url)).Within(800, 40);
    }
}
