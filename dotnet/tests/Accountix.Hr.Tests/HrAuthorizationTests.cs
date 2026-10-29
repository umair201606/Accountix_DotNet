using System.Net;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Hr.Tests;

/// <summary>
/// tests/e2e/test_hr_authorization.py on .NET (the parts this module serves):
/// roles held in THIS company, the HR section rights, a manager's own team.
/// Global roles deliberately disagree with the company roles.
/// </summary>
public class HrAuthorizationTests
{
    internal static async Task<World> AuthzWorldAsync()
    {
        var w = await World.CreateAsync("Authz Co",
            ("boss", "Bea Boss", Role.Admin), ("mgr", "Max Manager", Role.Manager), ("mgr2", "Nia Narrowed", Role.Manager),
            ("emp", "Eli Employee", Role.Employee), ("emp2", "Ema Employee", Role.Employee),
            ("peer", "Pat Peer", Role.Employee), ("borrower", "Bo Borrower", Role.Employee));
        var other = await World.CreateAsync("Authz Other", ("outsider", "Oz Outsider", Role.Manager));
        w.Ids["outsider"] = other["outsider"];
        await w.SetManagerAsync("emp", "mgr");
        await w.SetManagerAsync("emp2", "mgr2");
        await w.SetManagerAsync("borrower", "peer");   // an employee here
        w.Ids["lt"] = await w.LeaveTypeAsync("Authz Annual", "AZA", 10);
        // mgr2's rights narrowed by an admin: a row per section governs.
        await w.RightsAsync("mgr2",
            ("employees", true, false, false, false, false), ("attendance", true, false, false, false, false),
            ("leaves", true, false, false, false, false), ("payroll", true, false, false, false, false),
            ("loans", true, false, false, false, false), ("pf", false, false, false, false, false));
        return w;
    }

    private static async Task<int> LoanAsync(World w, string who)
    {
        await using var db = w.Db();
        var l = new LoanAdvanceRequest
        {
            UserId = w[who], RequestType = "loan", Amount = 20000, Purpose = "test", InstallmentMonths = 4,
            MonthlyInstallment = 5000, RemainingAmount = 20000, Status = "pending", QueueForPayroll = false, CreatedAt = DateTime.UtcNow,
        };
        db.Add(l);
        await db.SaveChangesAsync();
        return l.Id;
    }

    private static async Task<string> LoanStatusAsync(World w, int lid)
    {
        await using var db = w.Db();
        return (await db.Set<LoanAdvanceRequest>().AsNoTracking().FirstAsync(l => l.Id == lid)).Status!;
    }

    private static Task<HttpResponseMessage> DecideLoan(PageClient c, int lid, string action) =>
        World.PostJsonAcceptAsync(c, $"/mss/approve-loan/{lid}", "/mss/approvals", ("action", action));

    [ReferenceFact]
    public async Task Roles_rights_and_teams_of_this_company_govern_hr()
    {
        var w = await AuthzWorldAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrModule).Assembly, typeof(Accountix.Accounting.AccountingModule).Assembly);
        PageClient C(string who) => w.Client(host, who);

        // ── a loan is decided by a manager here, for their report, with the Loans right ──
        int lid = await LoanAsync(w, "borrower");
        var r = await World.PostJsonAcceptAsync(C("peer"), $"/mss/approve-loan/{lid}", "/ess/", ("action", "approve"));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DecideLoan(C("mgr"), lid, "approve")).StatusCode);   // not their report
        Assert.Equal("pending", await LoanStatusAsync(w, lid));
        int lid2 = await LoanAsync(w, "emp2");
        r = await DecideLoan(C("mgr2"), lid2, "approve");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Contains("Loans approve", await r.Content.ReadAsStringAsync());
        Assert.Equal("pending", await LoanStatusAsync(w, lid2));
        int lid3 = await LoanAsync(w, "emp");
        Assert.Equal(HttpStatusCode.OK, (await DecideLoan(C("mgr"), lid3, "approve")).StatusCode);
        Assert.Equal("approved", await LoanStatusAsync(w, lid3));
        Assert.Equal(HttpStatusCode.BadRequest, (await DecideLoan(C("mgr"), lid3, "reject")).StatusCode);   // decided once
        Assert.Equal(HttpStatusCode.OK, (await DecideLoan(C("boss"), lid, "reject")).StatusCode);
        Assert.Equal("rejected", await LoanStatusAsync(w, lid));
        await using (var db = w.Db())
        {
            var l3 = await db.Set<LoanAdvanceRequest>().AsNoTracking().FirstAsync(l => l.Id == lid3);
            Assert.True(l3.QueueForPayroll);
            Assert.Equal(w["mgr"], l3.ApprovedBy);
            // the loan's own ledger account (create_entity_account("loan", id, ...))
            Assert.True(await db.Set<ChartOfAccount>().AnyAsync(a => a.Name == $"Loan #{lid3} - Eli Employee"));
        }

        // ── a salary revision needs the Payroll edit right ──
        await World.PostAsync(C("mgr2"), $"/mss/evaluate/{w["emp2"]}", $"/mss/evaluate/{w["emp2"]}", ("new_basic", "90000"), ("reason", "x"));
        await World.PostAsync(C("mgr"), $"/mss/evaluate/{w["emp"]}", $"/mss/evaluate/{w["emp"]}", ("new_basic", "80000"), ("reason", "x"));
        await using (var db = w.Db())
        {
            var got = await db.Set<SalaryRevision>().Select(s => s.UserId).ToListAsync();
            Assert.Contains(w["emp"], got);
            Assert.DoesNotContain(w["emp2"], got);
        }
        // not their direct report: refused
        r = await C("mgr").GetAsync($"/mss/evaluate/{w["emp2"]}");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);

        // ── the live board and corrections follow the team and the rights ──
        var ids = new Dictionary<string, int>();
        await using (var db = w.Db())
        {
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi")));
            foreach (string who in new[] { "emp", "emp2" })
            {
                var a = new Attendance { UserId = w[who], Date = today, ClockIn = today.ToDateTime(new TimeOnly(9, 0)), CreatedAt = DateTime.UtcNow };
                db.Add(a);
                await db.SaveChangesAsync();
                ids[who] = a.Id;
            }
        }
        string live = await C("mgr").GetStringAsync("/attendance/live");
        Assert.Contains("Eli Employee", live);
        Assert.DoesNotContain("Ema Employee", live);
        r = await World.PostJsonAcceptAsync(C("mgr2"), "/attendance/correct", "/mss/", ("attendance_id", ids["emp2"].ToString()),
            ("field", "status"), ("new_value", "absent"), ("reason", "test"));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        r = await World.PostJsonAcceptAsync(C("mgr"), "/attendance/correct", "/mss/", ("attendance_id", ids["emp"].ToString()),
            ("field", "status"), ("new_value", "late"), ("reason", "test"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        // an employee is no lead: refused
        Assert.Equal(HttpStatusCode.Forbidden, (await C("emp").GetAsync("/attendance/live")).StatusCode);

        // ── HR rights cannot be given to yourself ──
        var all = new[] { "employees", "attendance", "leaves", "payroll", "loans", "pf" }
            .SelectMany(s => Rights.Actions.Select(a => ($"perm_{s}_{a}", "on"))).ToArray();
        var before = await PermAsync(w, "mgr2", "payroll");
        await World.PostAsync(C("mgr2"), $"/auth/users/{w["mgr2"]}/hr-rights", "/auth/users", all);
        Assert.Equal(before, await PermAsync(w, "mgr2", "payroll"));
        await World.PostAsync(C("mgr"), $"/auth/users/{w["mgr"]}/hr-rights", "/auth/users", all);
        Assert.Null(await PermAsync(w, "mgr", "payroll"));
        // ...but an HR manager sets them for someone else
        await World.PostAsync(C("mgr"), $"/auth/users/{w["peer"]}/hr-rights", "/auth/users", ("perm_leaves_view", "on"));
        Assert.Equal((true, false), await PermAsync(w, "peer", "leaves"));
        Assert.Equal((false, false), await PermAsync(w, "peer", "payroll"));

        // ── the Employees rights govern HR staff ──
        Assert.Equal(HttpStatusCode.OK, (await C("mgr2").GetAsync("/auth/users")).StatusCode);              // view kept
        Assert.Equal(HttpStatusCode.Redirect, (await C("mgr2").GetAsync($"/auth/users/{w["emp2"]}/edit")).StatusCode);
        await World.PostAsync(C("mgr2"), $"/auth/users/{w["emp2"]}/delete", "/auth/users");
        Assert.True((await w.UserAsync("emp2")).IsActive);
        Assert.Equal(HttpStatusCode.OK, (await C("mgr").GetAsync($"/auth/users/{w["emp"]}/edit")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await C("emp").GetAsync("/auth/users")).StatusCode);         // employees: refused

        // ── the manager must be one of this company's managers ──
        await EditAsync(C("mgr"), w, "emp", ("manager_id", w["outsider"].ToString()));
        await EditAsync(C("mgr"), w, "emp", ("manager_id", w["emp"].ToString()));
        Assert.Equal(w["mgr"], (await w.MembershipAsync("emp")).ManagerId);
        await EditAsync(C("mgr"), w, "emp", ("manager_id", w["mgr2"].ToString()));
        Assert.Equal(w["mgr2"], (await w.MembershipAsync("emp")).ManagerId);
        await w.SetManagerAsync("emp", "mgr");
        // HR can never make an admin
        int adminRole = await RoleIdAsync(Role.Admin);
        await EditAsync(C("mgr"), w, "emp", ("role_id", adminRole.ToString()));
        Assert.NotEqual(adminRole, (await w.MembershipAsync("emp")).RoleId);

        // ── leave goes to someone who can decide it ──
        var mon = DateOnly.FromDateTime(DateTime.Today).AddDays(28);
        mon = mon.AddDays(-(((int)mon.DayOfWeek + 6) % 7));
        await ApplyOneDay(C("borrower"), w, mon);
        Assert.Equal(w["boss"], await ApproverAsync(w, "borrower"));      // the "manager" is an employee here
        await ApplyOneDay(C("emp2"), w, mon.AddDays(1));
        Assert.Equal(w["boss"], await ApproverAsync(w, "emp2"));          // mgr2 lacks Leaves approve
        await ApplyOneDay(C("emp"), w, mon.AddDays(2));
        Assert.Equal(w["mgr"], await ApproverAsync(w, "emp"));
    }

    internal static async Task<int> RoleIdAsync(string name)
    {
        await using var db = World.Db(null);
        return await db.Roles.Where(r => r.Name == name).Select(r => r.Id).FirstAsync();
    }

    private static async Task<(bool View, bool Edit)?> PermAsync(World w, string who, string resource)
    {
        await using var db = w.Db();
        int uid = w[who];
        var p = await db.UserPermissions.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == uid && x.Resource == resource);
        return p is null ? null : (p.CanView == true, p.CanEdit == true);
    }

    /// <summary>_edit_form: the user form as the page would post it, with overrides.</summary>
    internal static async Task<HttpResponseMessage> EditAsync(PageClient c, World w, string who, params (string Key, string Value)[] over)
    {
        var u = await w.UserAsync(who);
        var m = await w.MembershipAsync(who);
        var data = new Dictionary<string, string>
        {
            ["email"] = u.Email, ["full_name"] = u.FullName, ["role_id"] = m.RoleId.ToString(),
            ["manager_id"] = m.ManagerId?.ToString() ?? "", ["designation"] = "", ["department"] = "", ["phone"] = "", ["is_active"] = "1",
        };
        foreach (var (k, v) in over) data[k] = v;
        return await World.PostAsync(c, $"/auth/users/{w[who]}/edit", "/auth/users", data.Select(kv => (kv.Key, kv.Value)).ToArray());
    }

    private static Task<HttpResponseMessage> ApplyOneDay(PageClient c, World w, DateOnly day) =>
        World.PostAsync(c, "/leaves/apply", "/leaves/apply", ("leave_type", w["lt"].ToString()),
            ("start_date", day.ToString("yyyy-MM-dd")), ("end_date", day.ToString("yyyy-MM-dd")), ("reason", "family"));

    private static async Task<int?> ApproverAsync(World w, string who)
    {
        await using var db = w.Db();
        int uid = w[who];
        var lr = await db.Set<LeaveRequest>().AsNoTracking().Where(x => x.UserId == uid).OrderByDescending(x => x.Id).FirstAsync();
        return await db.Set<LeaveApproval>().Where(a => a.LeaveRequestId == lr.Id).Select(a => (int?)a.ApproverId).FirstOrDefaultAsync();
    }
}
