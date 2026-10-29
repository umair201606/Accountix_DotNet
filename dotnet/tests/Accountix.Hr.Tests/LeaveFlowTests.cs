using System.Net;
using System.Text.Json;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Hr.Tests;

/// <summary>
/// tests/e2e/test_leave_flow.py on .NET: apply, route to an approver, decide,
/// and the balance stays right; timesheets with the same routing.
/// </summary>
public class LeaveFlowTests
{
    private static DateOnly Monday(int weeksAhead)
    {
        var d = DateOnly.FromDateTime(DateTime.Today).AddDays(7 * weeksAhead);
        return d.AddDays(-(((int)d.DayOfWeek + 6) % 7));
    }

    private static readonly DateOnly Mon = Monday(3);
    private static readonly DateOnly Tue = Mon.AddDays(1);
    private static readonly DateOnly Fri = Mon.AddDays(4);

    internal static async Task<World> LeaveWorldAsync()
    {
        var w = await World.CreateAsync("Leave Co",
            ("admin", "Ada Admin", Role.Admin), ("mgr", "Mona Manager", Role.Manager),
            ("emp", "Ed Employee", Role.Employee), ("solo", "Fay NoManager", Role.Employee));
        await w.OutsiderAsync("outsider", "Oscar Outsider");
        await w.SetManagerAsync("emp", "mgr");
        await w.SetManagerAsync("solo", "outsider");
        w.Ids["lt"] = await w.LeaveTypeAsync("Annual Test", "ATL", 5);
        return w;
    }

    internal static async Task<(double Total, double Used, double Pending, double Remaining)?> QuotaAsync(World w, string who, int? year = null)
    {
        await using var db = w.Db();
        int uid = w[who], lt = w["lt"], y = year ?? Mon.Year;
        var q = await db.Set<LeaveQuota>().AsNoTracking().FirstOrDefaultAsync(x => x.UserId == uid && x.LeaveTypeId == lt && x.Year == y);
        return q is null ? null : ((double)q.Total!, (double)q.Used!, (double)q.Pending!, (double)q.Remaining!);
    }

    internal static async Task<List<(int Id, string Status, double Days)>> RequestsAsync(World w, string who)
    {
        await using var db = w.Db();
        int uid = w[who];
        return (await db.Set<LeaveRequest>().AsNoTracking().Where(r => r.UserId == uid).OrderBy(r => r.Id).ToListAsync())
            .Select(r => (r.Id, r.Status!, (double)r.TotalDays)).ToList();
    }

    internal static async Task<(int Id, int Approver, string Status)?> ApprovalAsync(World w, int requestId)
    {
        await using var db = w.Db();
        var a = await db.Set<LeaveApproval>().AsNoTracking().FirstOrDefaultAsync(x => x.LeaveRequestId == requestId);
        return a is null ? null : (a.Id, a.ApproverId, a.Status!);
    }

    internal static Task<HttpResponseMessage> ApplyAsync(PageClient c, World w, DateOnly start, DateOnly end, bool half = false)
    {
        var f = new List<KeyValuePair<string, string>>
        {
            new("leave_type", w["lt"].ToString()), new("start_date", start.ToString("yyyy-MM-dd")),
            new("end_date", end.ToString("yyyy-MM-dd")), new("reason", "family"),
        };
        if (half) f.Add(new("is_half_day", "on"));
        return c.PostFormAsync("/leaves/apply", f, "/leaves/apply");
    }

    private static KeyValuePair<string, string> F(string k, string v) => new(k, v);

    [ReferenceFact]
    public async Task Apply_routes_decides_and_keeps_the_balance()
    {
        var w = await LeaveWorldAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrModule).Assembly);
        var emp = w.Client(host, "emp");
        var mgr = w.Client(host, "mgr");
        var admin = w.Client(host, "admin");
        var solo = w.Client(host, "solo");

        // 01 apply routes to the manager and holds the days; a quota is made from the type's default
        var r = await ApplyAsync(emp, w, Mon, Tue);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var reqs = await RequestsAsync(w, "emp");
        Assert.Single(reqs);
        Assert.Equal(("pending", 2.0), (reqs[0].Status, reqs[0].Days));
        int r1 = reqs[0].Id;
        var ap = await ApprovalAsync(w, r1);
        Assert.Equal((w["mgr"], "pending"), (ap!.Value.Approver, ap.Value.Status));
        Assert.Equal((5.0, 0.0, 2.0, 3.0), await QuotaAsync(w, "emp"));

        // 02 MSS approve redirects (plain form) and moves the days to used
        r = await mgr.PostFormAsync($"/mss/approve-leave/{ap.Value.Id}", [F("action", "approve")], "/mss/approvals");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("approved", (await RequestsAsync(w, "emp"))[0].Status);
        Assert.Equal((5.0, 2.0, 0.0, 3.0), await QuotaAsync(w, "emp"));

        // 03 a second decision is refused
        await mgr.PostFormAsync($"/mss/approve-leave/{ap.Value.Id}", [F("action", "approve")], "/mss/approvals");
        Assert.Equal((5.0, 2.0, 0.0, 3.0), await QuotaAsync(w, "emp"));
        await mgr.PostFormAsync($"/mss/approve-leave/{ap.Value.Id}", [F("action", "reject"), F("comment", "oops")], "/mss/approvals");
        Assert.Equal("approved", (await RequestsAsync(w, "emp"))[0].Status);
        var tok = await mgr.TokenFromAsync("/mss/approvals");
        var jr = new HttpRequestMessage(HttpMethod.Post, $"/mss/approve-leave/{ap.Value.Id}")
        {
            Content = new FormUrlEncodedContent([F("action", "approve"), F("__RequestVerificationToken", tok)]),
        };
        jr.Headers.Accept.ParseAdd("application/json");
        r = await mgr.SendAsync(jr);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("already", await r.Content.ReadAsStringAsync());

        // 04 overlapping dates refused; 05 balance enforced
        await ApplyAsync(emp, w, Tue, Fri);
        Assert.Single(await RequestsAsync(w, "emp"));
        await ApplyAsync(emp, w, Fri.AddDays(3), Fri.AddDays(7));
        Assert.Single(await RequestsAsync(w, "emp"));

        // 06 a half day keeps its half, and the leave page shows the balance
        await ApplyAsync(emp, w, Fri, Fri, half: true);
        var last = (await RequestsAsync(w, "emp"))[^1];
        Assert.Equal(("pending", 0.5), (last.Status, last.Days));
        Assert.Equal((5.0, 2.0, 0.5, 2.5), await QuotaAsync(w, "emp"));
        Assert.Contains("2.5", await emp.GetStringAsync("/leaves/"));

        // 07 a half day must be one date
        int before = (await RequestsAsync(w, "emp")).Count;
        await ApplyAsync(emp, w, Mon.AddDays(14), Tue.AddDays(14), half: true);
        Assert.Equal(before, (await RequestsAsync(w, "emp")).Count);

        // 08 no manager in this company: an admin decides
        await ApplyAsync(solo, w, Mon, Tue);
        var soloReqs = await RequestsAsync(w, "solo");
        Assert.Equal("pending", soloReqs.Single().Status);
        var soloAp = await ApprovalAsync(w, soloReqs[0].Id);
        Assert.Equal(w["admin"], soloAp!.Value.Approver);

        // 09 a rejection needs a reason, then frees the days
        await admin.PostFormAsync($"/leaves/review/{soloAp.Value.Id}", [F("action", "reject"), F("comment", "")], "/leaves/approvals");
        Assert.Equal("pending", (await RequestsAsync(w, "solo"))[0].Status);
        Assert.Equal(2.0, (await QuotaAsync(w, "solo"))!.Value.Pending);
        r = await admin.PostFormAsync($"/leaves/review/{soloAp.Value.Id}", [F("action", "reject"), F("comment", "busy week")], "/leaves/approvals");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("rejected", (await RequestsAsync(w, "solo"))[0].Status);
        Assert.Equal((5.0, 0.0, 0.0, 5.0), await QuotaAsync(w, "solo"));

        // 10 only the approver decides
        await ApplyAsync(solo, w, Mon.AddDays(7), Mon.AddDays(7));
        var r10 = (await RequestsAsync(w, "solo"))[^1];
        var a10 = await ApprovalAsync(w, r10.Id);
        await mgr.PostFormAsync($"/mss/approve-leave/{a10!.Value.Id}", [F("action", "approve")], "/mss/approvals");
        Assert.Equal("pending", (await RequestsAsync(w, "solo"))[^1].Status);

        // 11 missing dates do not 500: the form comes back
        r = await emp.PostFormAsync("/leaves/apply", [F("leave_type", w["lt"].ToString()), F("reason", "x")], "/leaves/apply");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Pick a start and an end date.", await r.Content.ReadAsStringAsync());

        // 12 ESS shows live balances
        string ess = await emp.GetStringAsync("/ess/");
        Assert.Contains("Annual Test", ess);
        Assert.Contains("2.5", ess);

        // notifications: the approver was told, the employee heard the decision
        await using var db = w.Db();
        int mgrId = w["mgr"], empId = w["emp"];
        Assert.True(await db.Set<NotificationRecipient>().AnyAsync(n => n.UserId == mgrId));
        Assert.True(await db.Set<NotificationRecipient>().AnyAsync(n => n.UserId == empId));
    }

    private static async Task<(int Id, DateOnly Start, string Status)> WeekAsync(World w, PageClient c, string who)
    {
        await c.GetAsync("/timesheets/");
        await using var db = w.Db();
        int uid = w[who];
        var wk = await db.Set<TimesheetWeek>().AsNoTracking().Where(x => x.UserId == uid).OrderByDescending(x => x.Id).FirstAsync();
        return (wk.Id, wk.WeekStart, wk.Status!);
    }

    [ReferenceFact]
    public async Task Timesheets_route_to_the_manager_once_and_freeze()
    {
        var w = await LeaveWorldAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrModule).Assembly);
        var emp = w.Client(host, "emp");
        var mgr = w.Client(host, "mgr");
        var admin = w.Client(host, "admin");

        // 13 entries are validated
        var (wid, start, _) = await WeekAsync(w, emp, "emp");
        string d0 = start.ToString("yyyy-MM-dd");
        Assert.Equal(HttpStatusCode.BadRequest, (await World.PostAsync(emp, "/timesheets/add-entry", "/timesheets/",
            ("week_id", wid.ToString()), ("day", ""), ("task", "x"), ("hours", "2"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await World.PostAsync(emp, "/timesheets/add-entry", "/timesheets/",
            ("week_id", wid.ToString()), ("day", d0), ("task", "x"), ("hours", "30"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await World.PostAsync(emp, "/timesheets/add-entry", "/timesheets/",
            ("week_id", wid.ToString()), ("day", start.AddDays(-1).ToString("yyyy-MM-dd")), ("task", "x"), ("hours", "2"))).StatusCode);
        var r = await World.PostAsync(emp, "/timesheets/add-entry", "/timesheets/",
            ("week_id", wid.ToString()), ("day", d0), ("task", "Site survey"), ("hours", "6"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("\"total_hours\":6", await r.Content.ReadAsStringAsync());
        // someone else's week is not theirs to fill
        Assert.Equal(HttpStatusCode.Forbidden, (await World.PostAsync(mgr, "/timesheets/add-entry", "/timesheets/",
            ("week_id", wid.ToString()), ("day", d0), ("task", "x"), ("hours", "1"))).StatusCode);

        // 14 submit routes to the manager once, then the week is frozen
        await World.PostAsync(emp, $"/timesheets/submit/{wid}", "/timesheets/");
        await World.PostAsync(emp, $"/timesheets/submit/{wid}", "/timesheets/");
        int tsAid;
        await using (var db = w.Db())
        {
            var apps = await db.Set<TimesheetApproval>().AsNoTracking().Where(a => a.WeekId == wid).ToListAsync();
            Assert.Equal([w["mgr"]], apps.Select(a => a.ApproverId).ToArray());
            tsAid = apps[0].Id;
        }
        r = await World.PostAsync(emp, "/timesheets/add-entry", "/timesheets/",
            ("week_id", wid.ToString()), ("day", d0), ("task", "late add"), ("hours", "1"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);

        // 15 MSS approve redirects and is final
        r = await World.PostAsync(mgr, $"/mss/approve-timesheet/{tsAid}", "/mss/approvals", ("action", "approve"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        r = await World.PostJsonAcceptAsync(mgr, $"/mss/approve-timesheet/{tsAid}", "/mss/approvals", ("action", "reject"), ("comment", "no"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("approved", (await WeekAsync(w, emp, "emp")).Status);

        // 16 the merge report adds up approved hours
        r = await World.PostAsync(admin, "/timesheets/merge-report", "/timesheets/projects",
            ("period_start", start.AddDays(-7).ToString("yyyy-MM-dd")), ("period_end", start.AddDays(7).ToString("yyyy-MM-dd")));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        string body = await r.Content.ReadAsStringAsync();
        Assert.Contains("\"user\":\"Ed Employee\"", body);
        Assert.Contains("\"total_hours\":6", body);
    }
}
