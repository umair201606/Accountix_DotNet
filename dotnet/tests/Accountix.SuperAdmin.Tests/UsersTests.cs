using System.Net;
using Accountix.Core.Entities;
using Accountix.Core.Security;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.SuperAdmin.Tests;

/// <summary>Manage Users (tests/e2e/test_superadmin_console.py: Manage Users, quotas, password, deactivation).</summary>
public class UsersTests
{
    private static async Task<User> UserAsync(int id)
    {
        await using var db = S.Db();
        return await db.Users.AsNoTracking().FirstAsync(u => u.Id == id);
    }

    [ReferenceFact]
    public async Task The_console_creates_an_account_that_belongs_to_no_company()
    {
        await using var host = await S.HostAsync();
        var c = S.Super(host);
        string email = $"made.{S.Tag()}@example.com";
        var r = await S.PostAsync(c, "/superadmin/users/", "/superadmin/users/",
            ("email", "  " + email.ToUpperInvariant() + " "), ("full_name", " Made Person "), ("password", " made1234x "));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);

        await using var db = S.Db();
        var u = await db.Users.AsNoTracking().SingleAsync(x => x.Email == email);
        Assert.Equal($"/superadmin/users/{u.Id}/", S.Location(r));
        Assert.Equal("Made Person", u.FullName);
        Assert.Equal(email, u.LoginId);
        Assert.True(PasswordHasher.Verify(u.PasswordHash, "made1234x"));
        Assert.StartsWith("scrypt:", u.PasswordHash);
        Assert.Matches("^SA\\d{4,}$", u.EmployeeCode);          // placeholder (NOT NULL, unique)
        Assert.Equal(await db.Roles.Where(x => x.Name == Role.Employee).Select(x => x.Id).FirstAsync(), u.RoleId);
        Assert.True(u.IsActive == true && u.HasHrAccess == true && u.IsSuperAdmin == false);
        Assert.True(u.HasAccountingAccess == false && u.HasInventoryAccess == false && u.HasExecutiveAccess == false);
        Assert.Null(u.MaxCompaniesOwned);
        Assert.NotNull(u.DateOfJoining);
        Assert.False(await db.Memberships.AnyAsync(m => m.UserId == u.Id), "creating an account must not put anyone in a company");
        // Python's audit hook logs the new account: so does the .NET app.
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "create" && a.EntityType == "User" && a.EntityId == u.Id));

        string page = await S.FollowAsync(c, r);
        Assert.Contains($"User Made Person created. Invite them into a company from that company's Settings.", page);
        Assert.Contains(u.EmployeeCode, page);
    }

    /// <summary>The made account signs in at the customer door and lands on the portal (it belongs to no company yet).</summary>
    [ReferenceFact]
    public async Task The_new_account_signs_in_at_the_customer_door_and_lands_on_the_portal()
    {
        await using var host = await S.HostWithPlatformAsync();
        var c = S.Super(host);
        string email = $"made.{S.Tag()}@example.com";
        await S.PostAsync(c, "/superadmin/users/", "/superadmin/users/", ("email", email), ("full_name", "Signs In"), ("password", "made1234x"));
        var anon = host.AnonymousClient();
        anon.DefaultRequestHeaders.Add("X-Forwarded-For", $"10.9.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}");
        var r = await anon.PostFormWithTokenAsync("/auth/login", [new("login", email), new("password", "made1234x")],
                                                  await S.TokenAsync(anon, "/auth/login"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/portal/", S.Location(r));
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/portal/")).StatusCode);
    }

    [ReferenceFact]
    public async Task Refusals_in_pythons_words_and_order()
    {
        await using var host = await S.HostAsync();
        var c = S.Super(host);
        string taken = $"dupe.{S.Tag()}@example.com";
        int existing = await S.NewUserAsync(taken);
        string loginTaken = $"lid.{S.Tag()}@example.com";
        int other = await S.NewUserAsync();
        await using (var db = S.Db())
            await db.Users.Where(u => u.Id == other).ExecuteUpdateAsync(s => s.SetProperty(u => u.LoginId, loginTaken.ToUpperInvariant()));

        async Task<string> Try(string email, string pw)
        {
            var r = await S.PostAsync(c, "/superadmin/users/", "/superadmin/users/", ("email", email), ("full_name", "X"), ("password", pw));
            Assert.Equal("/superadmin/users/", S.Location(r));
            return await S.FollowAsync(c, r);
        }
        Assert.Contains("Email and password are required.", await Try("", S.Pw));
        Assert.Contains("Password must be at least 8 characters.", await Try("a@b.c", "ab"));
        Assert.Contains("That password is too common — choose another.", await Try("a@b.c", "password123"));
        Assert.Contains("Enter a valid email address.", await Try("no-at-sign", S.Pw));
        Assert.Contains("Enter a valid email address.", await Try("a b@c.d", S.Pw));
        Assert.Contains($"A user with {taken} already exists.", await Try(taken, S.Pw));
        Assert.Contains($"The login id {loginTaken} is already taken by another account.", await Try(loginTaken, S.Pw));
        await using var db2 = S.Db();
        Assert.Equal(1, await db2.Users.CountAsync(u => u.Email == taken));
        Assert.False(await db2.Users.AnyAsync(u => u.Email == loginTaken));
        _ = existing;
    }

    [ReferenceFact]
    public async Task User_detail_shows_memberships_with_codes_and_ownership()
    {
        await using var host = await S.HostAsync();
        int uid = await S.NewUserAsync();
        int cid = await S.NewCompanyAsync($"Detail Co {S.Tag()}", createdBy: uid);
        await S.AddMembershipAsync(cid, uid, "admin", employeeCode: "EMP007");
        string page = await S.Super(host).GetStringAsync($"/superadmin/users/{uid}/");
        Assert.Contains("Detail Co", page);
        Assert.Contains("<code>EMP007</code>", page);
        Assert.Contains("pill pill-mine\" style=\"margin-left:6px;\">owner", page);
        Assert.Contains("Owns 1 of 3.", page);
        Assert.Contains("No limit on how many companies this user may belong to.", page);
    }

    [ReferenceFact]
    public async Task Quota_overrides_are_set_and_blank_falls_back_to_the_default()
    {
        await using var host = await S.HostAsync();
        var c = S.Super(host);
        int uid = await S.NewUserAsync(fullName: "Before");
        string page = $"/superadmin/users/{uid}/";
        await S.PostAsync(c, page, page, ("action", "profile"), ("full_name", " Quota User "),
                          ("max_companies_owned", " 1 "), ("max_companies_joined", ""));
        var u = await UserAsync(uid);
        Assert.Equal(1, u.MaxCompaniesOwned);
        Assert.Null(u.MaxCompaniesJoined);                       // blank is NULL, not zero
        Assert.Equal("Quota User", u.FullName);

        // Negative becomes 0; a blank name keeps the name.
        await S.PostAsync(c, page, page, ("action", "profile"), ("full_name", "  "), ("max_companies_owned", ""), ("max_companies_joined", "-4"));
        u = await UserAsync(uid);
        Assert.Null(u.MaxCompaniesOwned);
        Assert.Equal(0, u.MaxCompaniesJoined);
        Assert.Equal("Quota User", u.FullName);
        string shown = S.Text(await c.GetStringAsync(page));
        Assert.Contains("May belong to at most 0 companies.", shown);

        // Not a number: nothing saved.
        var r = await S.PostAsync(c, page, page, ("action", "profile"), ("full_name", "Changed"), ("max_companies_owned", "2"), ("max_companies_joined", "lots"));
        Assert.Contains("Limits must be whole numbers.", await S.FollowAsync(c, r));
        u = await UserAsync(uid);
        Assert.Null(u.MaxCompaniesOwned);
        Assert.Equal("Quota User", u.FullName);
    }

    [ReferenceFact]
    public async Task Password_reset_follows_the_policy_and_ends_the_persons_other_sessions()
    {
        await using var host = await S.HostAsync();
        var c = S.Super(host);
        int uid = await S.NewUserAsync();
        var theirs = host.ClientFor(uid);                    // the person, signed in elsewhere
        Assert.Equal(HttpStatusCode.Forbidden, (await theirs.GetAsync("/superadmin/")).StatusCode);   // signed in (403, not a redirect)
        string page = $"/superadmin/users/{uid}/";

        var bad = await S.PostAsync(c, page, page, ("action", "password"), ("new_password", "ab"));
        Assert.Contains("Password must be at least 8 characters.", await S.FollowAsync(c, bad));
        Assert.True(PasswordHasher.Verify((await UserAsync(uid)).PasswordHash, S.Pw), "a refused reset leaves the old password");

        var ok = await S.PostAsync(c, page, page, ("action", "password"), ("new_password", " brandnew1 "));
        string shown = await S.FollowAsync(c, ok);
        Assert.Contains("Password reset for ", shown);
        Assert.True(PasswordHasher.Verify((await UserAsync(uid)).PasswordHash, "brandnew1"));
        await using var db = S.Db();
        Assert.False(await db.UserSessions.AnyAsync(s => s.UserId == uid && s.EndedAt == null), "their sessions end");
        // Their old cookie no longer signs them in (fingerprint and server row both gone).
        Assert.Equal(HttpStatusCode.Redirect, (await theirs.GetAsync("/superadmin/")).StatusCode);
        // The super admin's own session carries on.
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/superadmin/")).StatusCode);
    }

    [ReferenceFact]
    public async Task Deactivation_toggles_and_the_super_admin_cannot_deactivate_themselves()
    {
        await using var host = await S.HostAsync();
        var c = S.Super(host);
        int uid = await S.NewUserAsync();
        string page = $"/superadmin/users/{uid}/";
        var r = await S.PostAsync(c, page, page, ("action", "active"));
        Assert.False((await UserAsync(uid)).IsActive);
        Assert.Contains(" is now deactivated.", await S.FollowAsync(c, r));
        await S.PostAsync(c, page, page, ("action", "active"));
        Assert.True((await UserAsync(uid)).IsActive);

        string mine = $"/superadmin/users/{S.SuperAdmin}/";
        var self = await S.PostAsync(c, mine, mine, ("action", "active"));
        Assert.Contains("You cannot deactivate your own account.", await S.FollowAsync(c, self));
        Assert.True((await UserAsync(S.SuperAdmin)).IsActive);
        Assert.Contains("This is your own account — deactivation and super-admin changes are disabled.", S.Text(await c.GetStringAsync(mine)));
    }

    [ReferenceFact]
    public async Task Super_admin_rights_are_granted_and_revoked_but_never_by_yourself()
    {
        await using var host = await S.HostAsync();
        var c = S.Super(host);
        int uid = await S.NewUserAsync(fullName: "O'Brien <Ann>");
        var r = await S.PostAsync(c, $"/superadmin/users/{uid}/toggle-super", "/superadmin/users/");
        Assert.Equal("/superadmin/users/", S.Location(r));
        Assert.Contains("O'Brien <Ann> is now a super admin.", await S.FollowAsync(c, r));
        Assert.True((await UserAsync(uid)).IsSuperAdmin);
        // The revoke confirm carries the name the way Jinja's |tojson|forceescape writes it.
        string list = await c.GetStringAsync("/superadmin/users/");
        Assert.Contains("confirm('Revoke super admin access for ' + &#34;O\\u0027Brien \\u003cAnn\\u003e&#34; + '?');", list);

        r = await S.PostAsync(c, $"/superadmin/users/{uid}/toggle-super", "/superadmin/users/");
        Assert.Contains("is no longer a super admin.", await S.FollowAsync(c, r));
        Assert.False((await UserAsync(uid)).IsSuperAdmin);

        r = await S.PostAsync(c, $"/superadmin/users/{S.SuperAdmin}/toggle-super", "/superadmin/users/");
        Assert.Contains("You cannot remove your own super admin rights.", await S.FollowAsync(c, r));
        Assert.True((await UserAsync(S.SuperAdmin)).IsSuperAdmin);
    }

    /// <summary>
    /// The users table renders as Python's rendered the same five accounts on 29 Oct 2026 (Golden/UsersList.json):
    /// the signed-in super admin ("(you)", no grant/revoke), a plain account, another super admin whose name needs
    /// escaping (the revoke confirm), a deactivated account, and one in two companies (+ one blocked membership).
    /// </summary>
    [ReferenceFact]
    public async Task The_users_table_renders_as_python_rendered_it()
    {
        await using var host = await S.HostAsync();
        string tag = S.Tag();
        string Email(int n) => $"gl-{tag}-{n}@ref.test";
        int self = await S.NewUserAsync(Email(0), "Golden Self", superAdmin: true);
        int plain = await S.NewUserAsync(Email(1), "Golden Plain");
        int sa = await S.NewUserAsync(Email(2), "O'Brien <Ann>", superAdmin: true);
        int off = await S.NewUserAsync(Email(3), "Golden Off");
        int member = await S.NewUserAsync(Email(4), "Golden Member");
        await using (var db = S.Db())
            await db.Users.Where(u => u.Id == off).ExecuteUpdateAsync(x => x.SetProperty(u => u.IsActive, false));
        foreach (string status in new[] { CompanyMembership.StatusActive, CompanyMembership.StatusActive, CompanyMembership.Blocked })
            await S.AddMembershipAsync(await S.NewCompanyAsync($"Golden Co {S.Tag()}"), member, status: status);
        int[] ids = [self, plain, sa, off, member];

        string net = await host.ClientFor(self).GetStringAsync("/superadmin/users/");
        Golden.Match("UsersList", UserRows(net, tag, ids));
    }

    /// <summary>The table rows of these accounts (in their order), normalised markup; the tag as TAG, ids as U0…U4.</summary>
    private static List<string> UserRows(string html, string tag, int[] ids)
    {
        var rows = System.Text.RegularExpressions.Regex.Matches(html, "<tr>.*?</tr>", System.Text.RegularExpressions.RegexOptions.Singleline)
            .Select(m => m.Value).ToList();
        return ids.Select((id, n) =>
        {
            string row = Assert.Single(rows, r => r.Contains($"gl-{tag}-{n}@ref.test"));
            return Markup.Normalize(row).Replace(tag, "TAG").Replace($"/superadmin/users/{id}/", $"/superadmin/users/U{n}/");
        }).ToList();
    }
}
