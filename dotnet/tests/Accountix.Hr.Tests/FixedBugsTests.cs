using System.Net;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Hr.Tests;

/// <summary>
/// Python bugs fixed in both apps on 29 Oct (the Python side is covered by
/// test_leave_flow.py::test_17, test_attendance_flow.py::test_07 and the last
/// three tests of test_hr_authorization.py): no "None" in empty fields,
/// "approved" / "rejected" in the messages, 0 late / half days with no records.
/// </summary>
public class FixedBugsTests
{
    [ReferenceFact]
    public async Task The_user_form_never_prints_none_and_saves_empty_fields()
    {
        var w = await HrAuthorizationTests.AuthzWorldAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrModule).Assembly);
        var boss = w.Client(host, "boss");
        Assert.DoesNotContain("value=\"None\"", await boss.GetStringAsync($"/auth/users/{w["emp2"]}/edit"));
        // rows an earlier save left holding the text "None" read as empty
        await using (var db = World.Db(null))
        {
            int cid = w.Company, uid = w["emp2"];
            var m = await db.Memberships.FirstAsync(x => x.CompanyId == cid && x.UserId == uid);
            m.Designation = "None";
            m.Department = "None";
            (await db.Users.FirstAsync(u => u.Id == uid)).Phone = "None";
            await db.SaveChangesAsync();
        }
        Assert.DoesNotContain("value=\"None\"", await boss.GetStringAsync($"/auth/users/{w["emp2"]}/edit"));
        Assert.DoesNotContain(">None<", await boss.GetStringAsync("/auth/users"));
        Assert.DoesNotContain(">None<", await w.Client(host, "mgr2").GetStringAsync("/mss/team"));
        string eval = await boss.GetStringAsync($"/mss/evaluate/{w["emp2"]}");
        Assert.Contains("<td style=\"font-weight:600;\">Designation</td><td>-</td>", eval);
        Assert.Contains("<td style=\"font-weight:600;\">Department</td><td>-</td>", eval);
        // saving the form as shown stores empty fields, not the word
        var r = await HrAuthorizationTests.EditAsync(boss, w, "emp2", ("manager_id", w["mgr2"].ToString()));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var mm = await w.MembershipAsync("emp2");
        Assert.Equal(("", ""), (mm.Designation, mm.Department));
        Assert.Equal("", (await w.UserAsync("emp2")).Phone);
    }

    [ReferenceFact]
    public async Task Decisions_read_approved_and_rejected()
    {
        var w = await LeaveFlowTests.LeaveWorldAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrModule).Assembly, typeof(Accountix.Accounting.AccountingModule).Assembly);
        var mgr = w.Client(host, "mgr");
        var emp = w.Client(host, "emp");
        var day = DateOnly.FromDateTime(DateTime.Today).AddDays(35);
        day = day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
        await LeaveFlowTests.ApplyAsync(emp, w, day, day);
        var ap = await LeaveFlowTests.ApprovalAsync(w, (await LeaveFlowTests.RequestsAsync(w, "emp"))[^1].Id);
        await World.PostAsync(mgr, $"/leaves/review/{ap!.Value.Id}", "/leaves/approvals", ("action", "reject"), ("comment", "cover"));
        Assert.Contains("Leave request rejected.", await mgr.GetStringAsync("/leaves/approvals"));
        await LeaveFlowTests.ApplyAsync(emp, w, day.AddDays(1), day.AddDays(1));
        ap = await LeaveFlowTests.ApprovalAsync(w, (await LeaveFlowTests.RequestsAsync(w, "emp"))[^1].Id);
        await World.PostAsync(mgr, $"/mss/approve-leave/{ap!.Value.Id}", "/mss/approvals", ("action", "approve"));
        Assert.Contains("Leave request approved.", await mgr.GetStringAsync("/mss/approvals"));

        // a timesheet sent back
        await emp.GetAsync("/timesheets/");
        int wid;
        DateOnly start;
        await using (var db = w.Db())
        {
            int uid = w["emp"];
            var wk = await db.Set<TimesheetWeek>().AsNoTracking().FirstAsync(x => x.UserId == uid);
            (wid, start) = (wk.Id, wk.WeekStart);
        }
        await World.PostAsync(emp, "/timesheets/add-entry", "/timesheets/", ("week_id", wid.ToString()),
            ("day", start.ToString("yyyy-MM-dd")), ("task", "Audit"), ("hours", "3"));
        await World.PostAsync(emp, $"/timesheets/submit/{wid}", "/timesheets/");
        int tid;
        await using (var db = w.Db())
            tid = await db.Set<TimesheetApproval>().Where(a => a.WeekId == wid).Select(a => a.Id).FirstAsync();
        await World.PostAsync(mgr, $"/timesheets/review/{tid}", "/timesheets/approvals", ("action", "reject"), ("comment", "split by project"));
        Assert.Contains("Timesheet rejected.", await mgr.GetStringAsync("/timesheets/approvals"));

        // a loan and a change request turned down
        int lid;
        await using (var db = w.Db())
        {
            var l = new LoanAdvanceRequest { UserId = w["emp"], RequestType = "loan", Amount = 5000, Purpose = "x", InstallmentMonths = 5,
                                             MonthlyInstallment = 1000, RemainingAmount = 5000, Status = "pending", QueueForPayroll = false, CreatedAt = DateTime.UtcNow };
            db.Add(l);
            await db.SaveChangesAsync();
            lid = l.Id;
        }
        var admin = w.Client(host, "admin");
        await World.PostAsync(admin, $"/mss/approve-loan/{lid}", "/mss/approvals", ("action", "reject"));
        Assert.Contains("Loan request rejected.", await admin.GetStringAsync("/mss/approvals"));
        await World.PostAsync(emp, "/ess/update-profile", "/ess/", ("emergency_phone", "0300-0000000"));
        int crid;
        await using (var db = w.Db())
        {
            int uid = w["emp"];
            crid = await db.Set<ChangeRequest>().Where(c => c.UserId == uid && c.Status == "pending").Select(c => c.Id).FirstAsync();
        }
        await World.PostAsync(admin, $"/ess/review-change/{crid}", "/ess/change-requests", ("action", "reject"), ("notes", "no"));
        string page = await admin.GetStringAsync("/ess/change-requests");
        Assert.Contains("Change request rejected.", page);
        Assert.DoesNotContain("rejectd", page);
    }

    [ReferenceFact]
    public async Task No_records_show_zero_late_and_half_days()
    {
        var w = await World.CreateAsync("Zero Co", ("admin", "Zed Admin", Role.Admin), ("emp", "Nell New", Role.Employee));
        await using var host = await ModuleHost.StartAsync(typeof(HrModule).Assembly);
        string page = await w.Client(host, "emp").GetStringAsync("/attendance/");
        Assert.Contains("<div class=\"stat-label\">Late Days</div><div class=\"stat-value\">0</div>", page);
        Assert.Contains("<div class=\"stat-label\">Half Days</div><div class=\"stat-value\">0</div>", page);
        Assert.DoesNotContain(">None<", page);
    }

    [ReferenceFact]
    public async Task A_company_without_a_time_policy_shows_the_standard_shift()
    {
        var w = await World.CreateAsync("No Policy Co", ("admin", "Ned Admin", Role.Admin), ("emp", "Pia Plain", Role.Employee));
        await using var host = await ModuleHost.StartAsync(typeof(HrModule).Assembly);
        string page = await w.Client(host, "emp").GetStringAsync("/attendance/");
        Assert.Contains("Standard Shift: 09:00 - 18:00", page);
        Assert.DoesNotContain("None Shift", page);
    }

    [ReferenceFact]
    public async Task The_evaluation_shows_a_dash_for_a_missing_joining_date()
    {
        var w = await World.CreateAsync("Join Co", ("boss", "Jo Boss", Role.Admin), ("emp", "Jay New", Role.Employee));
        Assert.Null((await w.UserAsync("emp")).DateOfJoining);
        await using var host = await ModuleHost.StartAsync(typeof(HrModule).Assembly);
        string page = await w.Client(host, "boss").GetStringAsync($"/mss/evaluate/{w["emp"]}");
        Assert.Contains("<td style=\"font-weight:600;\">Date of Joining</td><td>-</td>", page);
        Assert.DoesNotContain(">None<", page);
    }
}
