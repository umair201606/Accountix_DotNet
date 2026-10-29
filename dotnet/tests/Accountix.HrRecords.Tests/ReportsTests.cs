using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Accountix.Core.Entities;
using Accountix.HrRecords.Services;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.HrRecords.Tests;

/// <summary>
/// The HR report builder and its JSON (/reports): figures equal Python's
/// answers on ~200 employees (Golden/big_office.json, captured on the same
/// BigOffice data), the HR dashboard's figures (Golden/dashboard.json), scope
/// by role, column rights, and a fixed query count.
/// </summary>
public class ReportsTests
{
    private static readonly string[] AllColumns =
        ["employee", "total_hours", "overtime", "leaves", "sick_days", "late_days", "present_days", "pf_balance", "loan_balance"];

    private static async Task<(int Status, string Body)> NetQuery(ModuleHost host, World w, string who, object payload)
    {
        var c = w.Client(host, who);
        var r = await c.PostJsonWithTokenAsync("/reports/query", payload, await c.TokenFromAsync("/reports/"));
        return ((int)r.StatusCode, await r.Content.ReadAsStringAsync());
    }

    /// <summary>The captured scenarios: (index into Golden/big_office.json "queries", who asks).</summary>
    public static TheoryData<int, string> Scenarios()
    {
        var data = new TheoryData<int, string>();
        int i = 0;
        foreach (var q in Golden.Load("big_office").GetProperty("queries").EnumerateArray())
            data.Add(i++, q.GetProperty("who").GetString()!);
        return data;
    }

    /// <summary>The scenario's payload for this world: dates around the data's anchor, people by key.</summary>
    private static object Payload(World w, string template)
    {
        var anchor = BigOffice.Anchor;
        string text = template.Replace("DFROM", anchor.AddDays(-30).ToString("yyyy-MM-dd")).Replace("DTO", anchor.AddDays(-2).ToString("yyyy-MM-dd"))
            .Replace("\"E3\"", w["e3"].ToString()).Replace("\"E100\"", w["e100"].ToString())
            .Replace("\"OUTSIDER\"", Reference.UserId(Reference.Email("owner_b")).ToString());
        return World.Json(text);
    }

    /// <summary>
    /// The 15 scenarios (admin: company, default columns, a date range, a department, no department, one person,
    /// an outsider; manager: the team, one report, someone else's; restricted manager: column rights; employee)
    /// answer Python's status and JSON byte for byte, ids as the world's keys.
    /// </summary>
    [ReferenceTheory]
    [MemberData(nameof(Scenarios))]
    public async Task The_report_builder_answers_what_python_answers(int scenario, string who)
    {
        var g = Golden.Load("big_office").GetProperty("queries")[scenario];
        var w = await BigOffice.GetAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrRecordsModule).Assembly);
        var (status, body) = await NetQuery(host, w, who, Payload(w, g.GetProperty("payload").GetString()!));
        Assert.Equal(g.GetProperty("status").GetInt32(), status);
        Golden.SameText(g.GetProperty("body").GetString()!, Golden.IdsToKeys(body, Golden.Keys(w)), $"{who} {g.GetProperty("payload").GetString()}");
    }

    [ReferenceFact]
    public async Task Scope_follows_the_role_held_in_this_company()
    {
        var w = await BigOffice.GetAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrRecordsModule).Assembly);
        var (_, admin) = await NetQuery(host, w, "admin", World.Json("""{"scope":"company","columns":["employee"]}"""));
        var (_, mgr) = await NetQuery(host, w, "mgr", World.Json("""{"scope":"company","columns":["employee"]}"""));
        var all = World.Json(admin).GetProperty("results");
        var team = World.Json(mgr).GetProperty("results");
        // Blocked memberships and switched-off accounts are out; admins are not staff; a manager sees their reports only.
        Assert.True(all.GetArrayLength() is > 170 and < BigOffice.Employees + 2, $"{all.GetArrayLength()} people");
        Assert.DoesNotContain(all.EnumerateArray(), r => r.GetProperty("id").GetInt32() == w["admin"]);
        Assert.InRange(team.GetArrayLength(), 30, 40);
        var reports = Enumerable.Range(0, 40).Select(i => w[$"e{i}"]).ToHashSet();
        Assert.All(team.EnumerateArray(), r => Assert.Contains(r.GetProperty("id").GetInt32(), reports));

        var (status, body) = await NetQuery(host, w, "rmgr", World.Json("""{"columns":["pf_balance"]}"""));
        Assert.Equal((403, "{\"error\":\"You do not have the PF view right needed for that column.\"}\n"), (status, body));
        (status, body) = await NetQuery(host, w, "e1", World.Json("{}"));
        Assert.Equal((403, "{\"error\":\"Access denied\"}\n"), (status, body));
    }

    [ReferenceFact]
    public async Task The_report_on_200_people_runs_a_fixed_number_of_queries()
    {
        var w = await BigOffice.GetAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrRecordsModule).Assembly);
        var admin = w.Client(host, "admin");
        string token = await admin.TokenFromAsync("/reports/");
        var payload = new { scope = "company", columns = AllColumns };
        await admin.PostJsonWithTokenAsync("/reports/query", payload, token);      // warm up
        var best = TimeSpan.MaxValue;
        int queries = 0;
        for (int i = 0; i < 3; i++)
        {
            host.Queries.Reset();
            var sw = Stopwatch.StartNew();
            var r = await admin.PostJsonWithTokenAsync("/reports/query", payload, token);
            await r.Content.ReadAsStringAsync();
            sw.Stop();
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            if (sw.Elapsed < best) { best = sw.Elapsed; queries = host.Queries.Count; }
        }
        Assert.True(queries <= 20, $"{queries} queries for 200 people:\n  {string.Join("\n  ", host.Queries.Repeated(5))}");
        Assert.True(best.TotalMilliseconds <= 800, $"{best.TotalMilliseconds:F0} ms");
        Timings.Record(best, queries, "POST /reports/query (200 people, all columns)");

        foreach (var (url, who) in new[]
                 {
                     ("/reports/", "admin"), ("/reports/", "mgr"), ("/reports/dashboard", "admin"), ("/reports/dashboard", "e1"),
                     ($"/reports/attendance-chart?year={BigOffice.Today.Year}", "admin"),
                     ("/digital-files/admin", "admin"), ("/communications/", "admin"), ("/workplace/", "e1"),
                 })
        {
            (await host.MeasureAsync(w.Client(host, who), url)).Within(800, 15);     // MeasureAsync logs to ACCOUNTIX_TIMINGS
        }
    }

    [PerfFact]
    public async Task Pages_are_quick_on_the_full_year_of_samba()
    {
        // Read only: GETs, and the report builder (a POST that writes nothing).
        await using var host = await ModuleHost.StartOnAsync(Perf.ConnectionString, typeof(HrRecordsModule).Assembly);
        var c = host.ClientFor(Perf.AdminUserId, Perf.CompanyId);
        foreach (string url in new[]
                 {
                     "/reports/", "/reports/dashboard", $"/reports/attendance-chart?year={Perf.YearEnd.Year}", "/communications/",
                     "/digital-files/", "/digital-files/admin", "/digital-files/check-expiry", "/workplace/", "/workplace/announcements",
                     "/workplace/events", "/workplace/kanban", "/auth/api/notifications",
                 })
            (await host.MeasureAsync(c, url)).Within(800, 20);
        string token = await c.TokenFromAsync("/reports/");
        var payload = new { scope = "company", columns = AllColumns, date_from = Perf.YearStart.ToString("yyyy-MM-dd"), date_to = Perf.YearEnd.ToString("yyyy-MM-dd") };
        await c.PostJsonWithTokenAsync("/reports/query", payload, token);
        host.Queries.Reset();
        var sw = Stopwatch.StartNew();
        var r = await c.PostJsonWithTokenAsync("/reports/query", payload, token);
        await r.Content.ReadAsStringAsync();
        sw.Stop();
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True(sw.ElapsedMilliseconds <= 1500 && host.Queries.Count <= 20, $"{sw.ElapsedMilliseconds} ms, {host.Queries.Count} queries");
        Timings.Record(sw.Elapsed, host.Queries.Count, "PERF POST /reports/query (Samba, the year, all columns)");
    }

    /// <summary>The attendance chart for four roles and two years answers Python's bytes; with no year, this year's.</summary>
    [ReferenceFact]
    public async Task The_attendance_chart_answers_what_python_answers()
    {
        var w = await BigOffice.GetAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrRecordsModule).Assembly);
        foreach (var g in Golden.Load("big_office").GetProperty("charts").EnumerateArray())
        {
            string who = g.GetProperty("who").GetString()!, url = g.GetProperty("url").GetString()!;
            var r = await w.Client(host, who).GetAsync(url);
            Assert.Equal(g.GetProperty("status").GetInt32(), (int)r.StatusCode);
            Golden.SameText(g.GetProperty("body").GetString()!, await r.Content.ReadAsStringAsync(), $"{who} {url}");
        }
        var admin = w.Client(host, "admin");
        Assert.Equal(await admin.GetStringAsync($"/reports/attendance-chart?year={BigOffice.Today.Year}"),
                     await admin.GetStringAsync("/reports/attendance-chart"));
        Assert.StartsWith("[{\"half\":", await admin.GetStringAsync($"/reports/attendance-chart?year={BigOffice.Anchor.Year}"));
    }

    /// <summary>
    /// A small office with today's attendance (clocked in, done, absent, a blocked member's), leave in each state and
    /// an unapproved run of this month: the HR dashboard's figures for admin, manager and three employees are
    /// Python's (Golden/dashboard.json, capture/dashboard.py; dates are the company's today in both).
    /// </summary>
    [ReferenceFact]
    public async Task Dashboard_figures_answer_what_python_answers()
    {
        var w = await DashboardOfficeAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrRecordsModule).Assembly);
        foreach (var g in Golden.Load("dashboard").GetProperty("answers").EnumerateArray())
        {
            string who = g.GetProperty("who").GetString()!;
            var r = await w.Client(host, who).GetAsync("/reports/dashboard");
            Assert.Equal(g.GetProperty("status").GetInt32(), (int)r.StatusCode);
            Golden.SameText(g.GetProperty("body").GetString()!, await r.Content.ReadAsStringAsync(), $"dashboard as {who}");
        }
    }

    /// <summary>The same office as capture/dashboard.py builds through Python.</summary>
    internal static async Task<World> DashboardOfficeAsync()
    {
        var w = await World.CreateAsync("Dash",
            ("admin", "Dina Admin", Role.Admin), ("mgr", "Mo Manager", Role.Manager), ("e1", "Eli In", Role.Employee),
            ("e2", "Esa Done", Role.Employee), ("e3", "Ena Absent", Role.Employee), ("gone", "Gul Blocked", Role.Employee),
            ("off", "Oz Off", Role.Employee));
        await w.EmploymentAsync("e1", manager: "mgr");
        await w.EmploymentAsync("e2", manager: "mgr");
        await w.SetStatusAsync("gone", CompanyMembership.Blocked);
        await using (var db = World.Db(null))
        {
            int off = w["off"];
            (await db.Users.FirstAsync(u => u.Id == off)).IsActive = false;
            await db.SaveChangesAsync();
        }
        var today = BigOffice.Today;
        var at = today.ToDateTime(new TimeOnly(4, 0));     // 09:00 in Karachi, stored as UTC
        await using var cdb = w.Db();
        var lt = new LeaveType { Name = "Annual", Code = "AL", DefaultQuota = 14, IsPaid = true, RequiresApproval = true, CarryForward = false, MaxCarryForward = 0 };
        cdb.Add(lt);
        void Att(string who, DateOnly day, DateTime? clockIn, DateTime? clockOut) => cdb.Add(new Attendance
        {
            UserId = w[who], Date = day, ClockIn = clockIn, ClockOut = clockOut, Status = clockIn is null ? "absent" : "present",
            IsLate = false, IsHalfDay = false, CreatedAt = DateTime.UtcNow,
        });
        Att("e1", today, at, null);
        Att("e2", today, at, at.AddHours(8));
        Att("gone", today, at, null);
        Att("admin", today, null, null);
        Att("e3", today.AddDays(-1), at.AddDays(-1), at.AddDays(-1).AddHours(8));
        await cdb.SaveChangesAsync();
        void Leave(string who, string status, int days) => cdb.Add(new LeaveRequest
        {
            UserId = w[who], LeaveTypeId = lt.Id, StartDate = today.AddDays(days), EndDate = today.AddDays(days), TotalDays = 1,
            Reason = "x", Status = status, IsHalfDay = false, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        Leave("e1", "pending", 10);
        Leave("e1", "pending", 12);
        Leave("e2", "approved", 14);
        Leave("e3", "rejected", 16);
        Leave("mgr", "pending", 18);
        cdb.Add(new PayrollRun { Month = today.Month, Year = today.Year, RunDate = today.ToDateTime(new TimeOnly(12, 0)), Status = "unapproved", EmployeeCount = 0 });
        await cdb.SaveChangesAsync();
        return w;
    }

    private static List<string> YearOptions(string page) =>
        Regex.Matches(page, "<select name=\"year\"[^>]*>(.*?)</select>", RegexOptions.Singleline)
            .SelectMany(m => Regex.Matches(m.Groups[1].Value, "<option value=\"(\\d+)\"").Select(o => o.Groups[1].Value)).Distinct().ToList();

    [ReferenceFact]
    public async Task Pages_render_for_each_role()
    {
        var w = await BigOffice.GetAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrRecordsModule).Assembly);
        string page = await w.Client(host, "admin").GetStringAsync("/reports/");
        Assert.Contains("Ad-Hoc Report Builder", page);
        Assert.Contains($"<option value=\"{w["e3"]}\">", page);
        int year = BigOffice.Today.Year;
        Assert.Contains($"<option value=\"{year}\" selected=\"selected\">{year}</option>", page);
        Assert.Contains($"<option value=\"{year - 4}\">{year - 4}</option>", page);
        // The year pickers (the employee picker's values are user ids, which may read like a year).
        Assert.Equal(Enumerable.Range(year - 4, 6).Select(y => y.ToString()).ToList(), YearOptions(page));
        Assert.Contains($"/reports/attendance-chart?year={year}", page);
        Assert.Contains("<a href=\"/reports/\" class=\"nav-item active\">", page);

        string team = await w.Client(host, "mgr").GetStringAsync("/reports/");
        Assert.Contains($"<option value=\"{w["e3"]}\">", team);
        Assert.DoesNotContain($"<option value=\"{w["e100"]}\">", team);

        string emp = await w.Client(host, "e1").GetStringAsync("/reports/");
        Assert.Contains("My Status", emp);
        Assert.DoesNotContain("Ad-Hoc Report Builder", emp);
        Assert.DoesNotContain("/reports/", emp.Split("<nav class=\"sidebar-nav\">")[1].Split("</nav>")[0]);   // Reports is for leads
    }
}

/// <summary>A theory needing the reference database.</summary>
public sealed class ReferenceTheoryAttribute : TheoryAttribute
{
    public ReferenceTheoryAttribute()
    {
        if (!Reference.Available)
            Skip = "Reference database not available";
    }
}

/// <summary>A POST's timing in $ACCOUNTIX_TIMINGS, in MeasureAsync's line format (the Speed table in docs/modules/hr_records.md).</summary>
public static class Timings
{
    public static void Record(TimeSpan elapsed, int queries, string what)
    {
        if (Environment.GetEnvironmentVariable("ACCOUNTIX_TIMINGS") is not { Length: > 0 } file) return;
        lock (typeof(Timings))
            File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss}\t{elapsed.TotalMilliseconds:F0} ms\t{queries} q\t200\t{what}\n");
    }
}
