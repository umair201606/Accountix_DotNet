using System.Net;
using Accountix.Core.Entities;
using Accountix.Hr.Services;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Hr.Tests;

/// <summary>
/// tests/e2e/test_tenancy_hr_guards.py and test_membership_employment.py on
/// .NET, plus the 29 Oct access fixes in the HR user editor: employment
/// details per company, roles on the membership, template rights that move
/// with a role, logins this company may not change.
/// </summary>
public class MembershipTests
{
    private static async Task<(World A, World B)> TwoCompaniesAsync()
    {
        var a = await World.CreateAsync("Emp A", ("boss_a", "Ann Boss", Role.Admin), ("mgr_a", "Moe A", Role.Manager),
                                         ("person", "Pat Person", Role.Employee));
        var b = await World.CreateAsync("Emp B", ("boss_b", "Ben Boss", Role.Admin), ("mgr_b", "Mia B", Role.Manager));
        await b.JoinAsync("person", a["person"], Role.Employee);
        return (a, b);
    }

    [ReferenceFact]
    public async Task Each_company_keeps_its_own_manager_title_and_team()
    {
        var (a, b) = await TwoCompaniesAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrModule).Assembly);
        await HrAuthorizationTests.EditAsync(a.Client(host, "boss_a"), a, "person",
            ("manager_id", a["mgr_a"].ToString()), ("designation", "Analyst"), ("department", "Finance"));
        await HrAuthorizationTests.EditAsync(b.Client(host, "boss_b"), b, "person",
            ("manager_id", b["mgr_b"].ToString()), ("designation", "Consultant"), ("department", "Projects"));
        var ma = await a.MembershipAsync("person");
        var mb = await b.MembershipAsync("person");
        Assert.Equal((a["mgr_a"], "Analyst", "Finance"), (ma.ManagerId!.Value, ma.Designation, ma.Department));
        Assert.Equal((b["mgr_b"], "Consultant", "Projects"), (mb.ManagerId!.Value, mb.Designation, mb.Department));
        // changing A again leaves B as B set it
        await HrAuthorizationTests.EditAsync(a.Client(host, "boss_a"), a, "person", ("manager_id", ""), ("designation", "Senior Analyst"));
        mb = await b.MembershipAsync("person");
        Assert.Equal((b["mgr_b"], "Consultant"), (mb.ManagerId!.Value, mb.Designation));
        Assert.Null((await a.MembershipAsync("person")).ManagerId);
        // the account row is not rewritten for someone in two companies
        var u = await a.UserAsync("person");
        Assert.Null(u.Designation);
        Assert.Null(u.ManagerId);

        // leave goes to the manager of that company; my team follows the company
        await HrAuthorizationTests.EditAsync(a.Client(host, "boss_a"), a, "person", ("manager_id", a["mgr_a"].ToString()),
            ("designation", "Analyst"));
        await using (var db = a.Db())
            Assert.Equal(a["mgr_a"], await new People(db).ApproverForAsync(u, a.Company));
        await using (var db = b.Db())
            Assert.Equal(b["mgr_b"], await new People(db).ApproverForAsync(u, b.Company));
        await b.JoinAsync("mgr_a", a["mgr_a"], Role.Manager);   // mgr_a is a manager in B too, but not Pat's there
        string inB = await b.Client(host, "mgr_a").GetStringAsync("/mss/team");
        Assert.DoesNotContain("Pat Person", inB);
        string inA = await a.Client(host, "mgr_a").GetStringAsync("/mss/team");
        Assert.Contains("Pat Person", inA);
        Assert.Contains("Analyst", inA);
        Assert.DoesNotContain("Pat Person", await b.Client(host, "mgr_a").GetStringAsync("/mss/"));
    }

    [ReferenceFact]
    public async Task A_company_only_login_keeps_the_account_copy_in_step()
    {
        var w = await World.CreateAsync("Only Co", ("boss", "Oz Boss", Role.Admin), ("only", "Olu Only", Role.Employee));
        await using var host = await ModuleHost.StartAsync(typeof(HrModule).Assembly);
        await HrAuthorizationTests.EditAsync(w.Client(host, "boss"), w, "only", ("designation", "Storekeeper"));
        Assert.Equal("Storekeeper", (await w.MembershipAsync("only")).Designation);
        Assert.Equal("Storekeeper", (await w.UserAsync("only")).Designation);
    }

    [ReferenceFact]
    public async Task User_edit_writes_the_membership_role_and_guards_the_login()
    {
        var w = await World.CreateAsync("Guard Co", ("boss", "Gus Boss", Role.Admin), ("emp", "Gil Employee", Role.Employee),
                                         ("john", "John Other", Role.Employee), ("promoted", "Promoted Member", Role.Manager));
        await w.OutsiderAsync("foreign", "Foreign Global Manager", Role.Manager);
        await using var host = await ModuleHost.StartAsync(typeof(HrModule).Assembly);
        var boss = w.Client(host, "boss");
        int mgrRole = await HrAuthorizationTests.RoleIdAsync(Role.Manager);
        int empRole = await HrAuthorizationTests.RoleIdAsync(Role.Employee);
        int globalRole = (await w.UserAsync("emp")).RoleId;

        // the manager dropdown is membership-based
        string page = await boss.GetStringAsync($"/auth/users/{w["emp"]}/edit");
        Assert.Contains("Promoted Member", page);
        Assert.DoesNotContain("Foreign Global Manager", page);

        // promote: the membership role changes, the global column stays; template rights move with it
        await using (var db = w.Db())
        {
            foreach (var (s, acts) in AccessDefaults.RoleRights(Role.Employee)!)
                db.UserPermissions.Add(new UserPermission { UserId = w["emp"], Resource = s, CanView = acts[0], CanCreate = acts[1],
                                                            CanEdit = acts[2], CanApprove = acts[3], CanDelete = acts[4] });
            await db.SaveChangesAsync();
        }
        var r = await HrAuthorizationTests.EditAsync(boss, w, "emp", ("role_id", mgrRole.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(mgrRole, (await w.MembershipAsync("emp")).RoleId);
        Assert.Equal(globalRole, (await w.UserAsync("emp")).RoleId);
        await using (var db = w.Db())
        {
            int uid = w["emp"];
            var rows = await db.UserPermissions.AsNoTracking().Where(p => p.UserId == uid).ToListAsync();
            Assert.Equal(AccessDefaults.AllSections.Count, rows.Count);
            Assert.True(rows.Single(p => p.Resource == "leaves").CanApprove);         // the manager template
            Assert.False(rows.Single(p => p.Resource == "journal_vouchers").CanView);
        }
        // hand-set rights stay when the role changes back
        await using (var db = w.Db())
        {
            int uid = w["emp"];
            var p = await db.UserPermissions.FirstAsync(x => x.UserId == uid && x.Resource == "payroll");
            p.CanDelete = false;
            await db.SaveChangesAsync();
        }
        await HrAuthorizationTests.EditAsync(boss, w, "emp", ("role_id", empRole.ToString()));
        Assert.Equal(empRole, (await w.MembershipAsync("emp")).RoleId);
        await using (var db = w.Db())
        {
            int uid = w["emp"];
            Assert.True((await db.UserPermissions.AsNoTracking().FirstAsync(x => x.UserId == uid && x.Resource == "leaves")).CanApprove);
        }

        // an admin cannot demote themself
        r = await HrAuthorizationTests.EditAsync(boss, w, "boss", ("role_id", empRole.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(await HrAuthorizationTests.RoleIdAsync(Role.Admin), (await w.MembershipAsync("boss")).RoleId);

        // a duplicate email is refused in words (200), as is another account's User ID used as an email
        var john = await w.UserAsync("john");
        r = await HrAuthorizationTests.EditAsync(boss, w, "emp", ("email", john.Email));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("already in use", await r.Content.ReadAsStringAsync());
        await using (var db = World.Db(null))
        {
            var jj = await db.Users.FirstAsync(x => x.Id == john.Id);
            jj.LoginId = "john-login-" + w.Tag;
            await db.SaveChangesAsync();
        }
        r = await HrAuthorizationTests.EditAsync(boss, w, "emp", ("email", "JOHN-LOGIN-" + w.Tag.ToUpperInvariant()));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("already in use", await r.Content.ReadAsStringAsync());
        Assert.NotEqual(john.Email, (await w.UserAsync("emp")).Email);

        // a login that also reaches another company: email, password and the switch stay
        var other = await World.CreateAsync("Guard Other", ("x", "Xi Boss", Role.Admin));
        await other.JoinAsync("emp", w["emp"], Role.Employee);
        string hash = (await w.UserAsync("emp")).PasswordHash;
        r = await HrAuthorizationTests.EditAsync(boss, w, "emp", ("password", "Brand-new-pass-1"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(hash, (await w.UserAsync("emp")).PasswordHash);
        await HrAuthorizationTests.EditAsync(boss, w, "emp", ("is_active", ""));
        Assert.True((await w.UserAsync("emp")).IsActive);
        await World.PostAsync(boss, $"/auth/users/{w["emp"]}/reset-password", "/auth/users", ("new_password", "Brand-new-pass-1"));
        Assert.Equal(hash, (await w.UserAsync("emp")).PasswordHash);
        Assert.Contains("Own login", await boss.GetStringAsync("/auth/users"));
        // a login only here: the reset works, and that person's other sessions end
        var sessionCookie = TestSignIn.CookieFor(w["john"], w.Company);
        await World.PostAsync(boss, $"/auth/users/{w["john"]}/reset-password", "/auth/users", ("new_password", "Brand-new-pass-1"));
        Assert.True(Accountix.Core.Security.PasswordHasher.Verify((await w.UserAsync("john")).PasswordHash, "Brand-new-pass-1"));
        await using (var db = World.Db(null))
        {
            int jid = w["john"];
            Assert.False(await db.UserSessions.AnyAsync(s => s.UserId == jid && s.EndedAt == null));
        }
        // the owner's login is the owner's
        await using (var db = World.Db(null))
        {
            var co = await db.Companies.FirstAsync(c => c.Id == w.Company);
            co.CreatedBy = w["promoted"];
            await db.SaveChangesAsync();
        }
        r = await HrAuthorizationTests.EditAsync(boss, w, "promoted", ("password", "Brand-new-pass-2"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.False(Accountix.Core.Security.PasswordHasher.Verify((await w.UserAsync("promoted")).PasswordHash, "Brand-new-pass-2"));
        r = await HrAuthorizationTests.EditAsync(boss, w, "promoted", ("role_id", empRole.ToString()));
        Assert.Equal(mgrRole, (await w.MembershipAsync("promoted")).RoleId);   // the owner's role is fixed
    }

    [ReferenceFact]
    public async Task Changing_an_email_in_hr_moves_waiting_invitations()
    {
        var a = await World.CreateAsync("Mover A", ("boss_a", "Ada Boss", Role.Admin), ("mover", "Max Mover", Role.Employee));
        var b = await World.CreateAsync("Mover B", ("boss_b", "Bob Boss", Role.Admin));
        var old = (await a.UserAsync("mover")).Email;
        await using (var db = World.Db(null))
        {
            db.Invitations.Add(new CompanyInvitation
            {
                CompanyId = b.Company, Email = old, RoleId = await HrAuthorizationTests.RoleIdAsync(Role.Employee),
                InvitedBy = b["boss_b"], Status = CompanyInvitation.Sent, CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        await using var host = await ModuleHost.StartAsync(typeof(HrModule).Assembly);
        string neu = $"moved-{a.Tag}@example.com";
        var r = await HrAuthorizationTests.EditAsync(a.Client(host, "boss_a"), a, "mover", ("email", neu));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(neu, (await a.UserAsync("mover")).Email);
        await using (var db = World.Db(null))
        {
            Assert.Equal(0, await db.Invitations.CountAsync(i => i.Email == old));
            Assert.Equal(1, await db.Invitations.CountAsync(i => i.Email == neu && i.Status == "sent"));
        }
    }

    [ReferenceFact]
    public async Task Member_assign_gives_a_code_per_company_and_an_employee_account()
    {
        var x = await World.CreateAsync("Code X", ("bx", "Bx Boss", Role.Admin), ("holder", "Hal Holder", Role.Employee));
        var y = await World.CreateAsync("Code Y", ("by", "By Boss", Role.Admin), ("new", "Yan New", Role.Employee),
                                         ("mgr", "Yasmin Mgr", Role.Manager));
        string code = "EMP" + x.Tag[..5].ToUpperInvariant();
        await using (var db = World.Db(null))
        {
            int hid = x["holder"], nid = y["new"];
            (await db.Users.FirstAsync(u => u.Id == hid)).EmployeeCode = code;   // held GLOBALLY by someone else
            (await db.Memberships.FirstAsync(m => m.CompanyId == x.Company && m.UserId == hid)).EmployeeCode = code;
            // users.employee_code is unique: an earlier run's person may hold "" (the empty code).
            foreach (var prev in await db.Users.Where(u => u.EmployeeCode == "").ToListAsync())
                prev.EmployeeCode = "OLD" + prev.Id;
            await db.SaveChangesAsync();
            (await db.Users.FirstAsync(u => u.Id == nid)).EmployeeCode = "";
            await db.SaveChangesAsync();
        }
        await using var host = await ModuleHost.StartAsync(typeof(HrModule).Assembly, typeof(Accountix.Accounting.AccountingModule).Assembly);
        var c = y.Client(host, "by");
        Assert.Contains("Yan New", await c.GetStringAsync("/auth/members/assign"));
        var r = await World.PostAsync(c, "/auth/members/assign", "/auth/members/assign", ("user_id", y["new"].ToString()),
            ("employee_code", code), ("designation", "Clerk"), ("department", "Stores"), ("manager_id", y["mgr"].ToString()));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var m = await y.MembershipAsync("new");
        Assert.Equal((code, "Clerk", "Stores", (int?)y["mgr"]), (m.EmployeeCode, m.Designation, m.Department, m.ManagerId));
        Assert.Equal("", (await y.UserAsync("new")).EmployeeCode);    // not another company's global code
        await using (var db = y.Db())
            Assert.True(await db.Set<ChartOfAccount>().AnyAsync(a => a.Name == $"Yan New ({code})"));
        // the same code twice in one company is refused; a non-member is refused
        r = await World.PostAsync(c, "/auth/members/assign", "/auth/members/assign", ("user_id", y["mgr"].ToString()), ("employee_code", code));
        Assert.Contains($"Employee code {code} is already used in this company.", await r.Content.ReadAsStringAsync());
        r = await World.PostAsync(c, "/auth/members/assign", "/auth/members/assign", ("user_id", x["holder"].ToString()), ("employee_code", "Z1"));
        Assert.Contains("That person is not a member of this company.", await r.Content.ReadAsStringAsync());
    }
}
