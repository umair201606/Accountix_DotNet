using System.Net;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Hr.Tests;

/// <summary>
/// Several people (or several taps) at the same instant, as
/// tests/concurrency/test_concurrent_users.py and Python's serialize scopes:
/// "leave" per employee, "attendance" per person, row locks on timesheet
/// approvals, "members" per company.
/// </summary>
public class ManyUsersTests
{
    private static List<KeyValuePair<string, string>> Apply(World w, DateOnly start, DateOnly end) =>
    [
        new("leave_type", w["lt"].ToString()), new("start_date", start.ToString("yyyy-MM-dd")),
        new("end_date", end.ToString("yyyy-MM-dd")), new("reason", "race"),
    ];

    private static DateOnly Monday(int weeksAhead)
    {
        var d = DateOnly.FromDateTime(DateTime.Today).AddDays(7 * weeksAhead);
        return d.AddDays(-(((int)d.DayOfWeek + 6) % 7));
    }

    [ReferenceFact]
    public async Task A_double_apply_files_one_request_and_counts_its_days_once()
    {
        var w = await LeaveFlowTests.LeaveWorldAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrModule).Assembly);
        var clients = host.ClientsFor(w["emp"], w.Company, 6);
        var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync("/leaves/apply")));
        var mon = Monday(5);
        var rs = await Race.RunAsync(6, i => clients[i].PostFormWithTokenAsync("/leaves/apply", Apply(w, mon, mon.AddDays(1)), tokens[i]));
        Race.NoServerErrors(rs);
        Assert.Single(await LeaveFlowTests.RequestsAsync(w, "emp"));
        Assert.Equal((5.0, 0.0, 2.0, 3.0), await LeaveFlowTests.QuotaAsync(w, "emp", mon.Year));
        await using var db = w.Db();
        int mgr = w["mgr"];
        Assert.Equal(1, await db.Set<NotificationRecipient>().CountAsync(n => n.UserId == mgr));
    }

    [ReferenceFact]
    public async Task Six_decisions_on_one_leave_give_one_and_the_balance_counts_once()
    {
        var w = await LeaveFlowTests.LeaveWorldAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrModule).Assembly);
        var mon = Monday(6);
        await LeaveFlowTests.ApplyAsync(w.Client(host, "emp"), w, mon, mon.AddDays(1));
        var ap = await LeaveFlowTests.ApprovalAsync(w, (await LeaveFlowTests.RequestsAsync(w, "emp"))[0].Id);
        var clients = host.ClientsFor(w["mgr"], w.Company, 6);
        var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync("/mss/approvals")));
        // half approve through MSS, half reject through the leave page
        var rs = await Race.RunAsync(6, i => i % 2 == 0
            ? clients[i].PostFormWithTokenAsync($"/mss/approve-leave/{ap!.Value.Id}", [new("action", "approve")], tokens[i])
            : clients[i].PostFormWithTokenAsync($"/leaves/review/{ap!.Value.Id}", [new("action", "reject"), new("comment", "busy")], tokens[i]));
        Race.NoServerErrors(rs);
        var status = (await LeaveFlowTests.RequestsAsync(w, "emp"))[0].Status;
        var q = await LeaveFlowTests.QuotaAsync(w, "emp", mon.Year);
        Assert.Equal(status == "approved" ? (5.0, 2.0, 0.0, 3.0) : (5.0, 0.0, 0.0, 5.0), q);
        await using var db = w.Db();
        int emp = w["emp"];
        Assert.Equal(1, await db.Set<NotificationRecipient>().CountAsync(n => n.UserId == emp));   // one decision told
    }

    [ReferenceFact]
    public async Task Clocking_in_six_times_at_once_makes_one_attendance_row()
    {
        var w = await World.CreateAsync("Race Clock", ("admin", "Rae Admin", Role.Admin), ("emp", "Rob Racer", Role.Employee));
        await using var host = await ModuleHost.StartAsync(typeof(HrModule).Assembly);
        var clients = host.ClientsFor(w["emp"], w.Company, 6);
        var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync("/attendance/")));
        var rs = await Race.RunAsync(6, i => clients[i].PostFormWithTokenAsync("/attendance/clock-in", [], tokens[i]));
        Race.NoServerErrors(rs);
        Assert.Single(rs, r => r.StatusCode == HttpStatusCode.OK);
        await using var db = w.Db();
        int uid = w["emp"];
        Assert.Equal(1, await db.Set<Attendance>().CountAsync(a => a.UserId == uid));
        Assert.Equal(1, await db.Set<AttendanceLog>().CountAsync(l => l.Attendance.UserId == uid && l.Event == "clock_in"));
        // and six clock-outs book the day once
        tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync("/attendance/")));
        rs = await Race.RunAsync(6, i => clients[i].PostFormWithTokenAsync("/attendance/clock-out", [], tokens[i]));
        Race.NoServerErrors(rs);
        Assert.Single(rs, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(1, await db.Set<OvertimeAccount>().CountAsync(o => o.UserId == uid));
    }

    [ReferenceFact]
    public async Task Six_approvals_of_one_timesheet_approve_it_once()
    {
        var w = await LeaveFlowTests.LeaveWorldAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrModule).Assembly);
        var emp = w.Client(host, "emp");
        await emp.GetAsync("/timesheets/");
        int wid, aid;
        await using (var db = w.Db())
        {
            int uid = w["emp"];
            var wk = await db.Set<TimesheetWeek>().AsNoTracking().FirstAsync(x => x.UserId == uid);
            wid = wk.Id;
            await World.PostAsync(emp, "/timesheets/add-entry", "/timesheets/", ("week_id", wid.ToString()),
                ("day", wk.WeekStart.ToString("yyyy-MM-dd")), ("task", "Survey"), ("hours", "5"));
        }
        // six submits queue one approval
        var subs = host.ClientsFor(w["emp"], w.Company, 6);
        var stoks = await Task.WhenAll(subs.Select(c => c.TokenFromAsync("/timesheets/")));
        Race.NoServerErrors(await Race.RunAsync(6, i => subs[i].PostFormWithTokenAsync($"/timesheets/submit/{wid}", [], stoks[i])));
        await using (var db = w.Db())
        {
            var apps = await db.Set<TimesheetApproval>().Where(a => a.WeekId == wid).ToListAsync();
            Assert.Single(apps);
            aid = apps[0].Id;
        }
        var clients = host.ClientsFor(w["mgr"], w.Company, 6);
        var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync("/mss/approvals")));
        var rs = await Race.RunAsync(6, i => (i % 3) switch
        {
            0 => clients[i].PostFormWithTokenAsync($"/mss/approve-timesheet/{aid}", [new("action", "approve")], tokens[i]),
            1 => clients[i].PostFormWithTokenAsync($"/timesheets/review/{aid}", [new("action", "approve")], tokens[i]),
            _ => clients[i].PostJsonWithTokenAsync("/mss/bulk-approve-timesheets", new { ids = new[] { aid } }, tokens[i]),
        });
        Race.NoServerErrors(rs);
        await using (var db = w.Db())
        {
            var a = await db.Set<TimesheetApproval>().AsNoTracking().FirstAsync(x => x.Id == aid);
            var wk = await db.Set<TimesheetWeek>().AsNoTracking().FirstAsync(x => x.Id == wid);
            Assert.Equal(("approved", "approved"), (a.Status, wk.Status));
        }
        // the bulk answers count what each one approved: one in all, at most
        int bulkApproved = 0;
        foreach (var r in rs.Where((_, i) => i % 3 == 2))
            if ((await r.Content.ReadAsStringAsync()).Contains("\"count\":1")) bulkApproved++;
        Assert.True(bulkApproved <= 1);
    }

    [ReferenceFact]
    public async Task Member_edits_queue_per_company()
    {
        var w = await HrAuthorizationTests.AuthzWorldAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrModule).Assembly);
        // six HR-rights saves for one person at once: delete-then-insert must not clash on the unique key
        var clients = host.ClientsFor(w["boss"], w.Company, 6);
        var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync("/auth/users")));
        var rs = await Race.RunAsync(6, i => clients[i].PostFormWithTokenAsync($"/auth/users/{w["peer"]}/hr-rights",
            [new("perm_leaves_view", "on"), new($"perm_attendance_{(i % 2 == 0 ? "view" : "edit")}", "on")], tokens[i]));
        Race.NoServerErrors(rs);
        await using var db = w.Db();
        int uid = w["peer"];
        Assert.Equal(6, await db.UserPermissions.CountAsync(p => p.UserId == uid));
        // six edits of one person's employment at once: each saves whole, no 500
        var u = await w.UserAsync("emp");
        int role = (await w.MembershipAsync("emp")).RoleId;
        tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync("/auth/users")));
        rs = await Race.RunAsync(6, i => clients[i].PostFormWithTokenAsync($"/auth/users/{w["emp"]}/edit",
        [
            new("email", u.Email), new("full_name", u.FullName), new("role_id", role.ToString()), new("manager_id", w["mgr"].ToString()),
            new("designation", $"Title {i}"), new("department", $"Dept {i}"), new("phone", ""), new("is_active", "1"),
        ], tokens[i]));
        Race.NoServerErrors(rs);
        var m = await w.MembershipAsync("emp");
        Assert.Equal(m.Designation![^1], m.Department![^1]);       // one edit's values, whole
    }
}
