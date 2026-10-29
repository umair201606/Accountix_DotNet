using System.Text.RegularExpressions;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.HrRecords.Tests;

/// <summary>
/// Three report bugs fixed in both apps (29 Oct): total hours were 24 times too
/// many on Postgres, the department picker offered made-up names, and an
/// unapproved payroll of this month in any year counted as active. Each is
/// checked here with the answers the fixed Python app gave on the same data
/// (Golden/report_fixes.json for the hours; the picker and the payroll flag
/// by their concrete values, which Python matched on 29 Oct).
/// </summary>
public class ReportFixesTests
{
    private static async Task<World> OfficeAsync()
    {
        var w = await World.CreateAsync("Fixes",
            ("boss", "Bea Boss", Role.Admin), ("mgr", "Max Manager", Role.Manager), ("emp", "Eli Employee", Role.Employee),
            ("emp2", "Ema Employee", Role.Employee), ("emp3", "Ned Nodept", Role.Employee), ("gone", "Bo Blocked", Role.Employee));
        await w.EmploymentAsync("boss", department: "Board");
        await w.EmploymentAsync("mgr", department: "Operations");
        await w.EmploymentAsync("emp", manager: "mgr", department: "Operations");
        await w.EmploymentAsync("emp2", department: "Sales & Marketing");
        await w.EmploymentAsync("emp3", department: "  ");
        await w.EmploymentAsync("gone", department: "Legal");
        await w.SetStatusAsync("gone", CompanyMembership.Blocked);
        return w;
    }

    private static async Task<string> QueryAsync(ModuleHost host, World w, string who, object payload)
    {
        var c = w.Client(host, who);
        var r = await c.PostJsonWithTokenAsync("/reports/query", payload, await c.TokenFromAsync("/reports/"));
        return await r.Content.ReadAsStringAsync();
    }

    [ReferenceFact]
    public async Task Total_hours_are_the_hours_worked()
    {
        var w = await OfficeAsync();
        var today = BigOffice.Today;
        await using (var db = w.Db())
        {
            foreach (var (day, start, end) in new[] { (3, new TimeOnly(9, 0), (TimeOnly?)new TimeOnly(17, 30)), (2, new TimeOnly(8, 45), new TimeOnly(13, 0)), (1, new TimeOnly(9, 0), null) })
            {
                var d = today.AddDays(-day);
                db.Add(new Attendance
                {
                    UserId = w["emp"], Date = d, ClockIn = d.ToDateTime(start), ClockOut = end is { } e ? d.ToDateTime(e) : null,
                    Status = "present", IsLate = false, IsHalfDay = false, CreatedAt = DateTime.UtcNow,
                });
            }
            db.Add(new Attendance { UserId = w["emp2"], Date = today.AddDays(-3), ClockIn = today.AddDays(-3).ToDateTime(new TimeOnly(9, 0)), ClockOut = today.AddDays(-3).ToDateTime(new TimeOnly(9, 20)), Status = "present", IsLate = false, IsHalfDay = false, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        await using var host = await ModuleHost.StartAsync(typeof(HrRecordsModule).Assembly);
        string net = await QueryAsync(host, w, "boss", new { scope = "company", columns = new[] { "employee", "total_hours", "present_days" } });
        var rows = World.Json(net).GetProperty("results").EnumerateArray().ToDictionary(r => r.GetProperty("employee").GetString()!);
        Assert.Equal(12.75, rows["Eli Employee"].GetProperty("total_hours").GetDouble());     // 8.5 h + 4.25 h; was 306.0
        Assert.Equal(0.33, rows["Ema Employee"].GetProperty("total_hours").GetDouble());
        Golden.SameText(Golden.Load("report_fixes").GetProperty("body").GetString()!, Golden.IdsToKeys(net, Golden.Keys(w)), "the hours report");
    }

    private static List<string> Departments(string html) =>
        Regex.Matches(Regex.Match(html, "<select id=\"report-dept\"[^>]*>(.*?)</select>", RegexOptions.Singleline).Groups[1].Value,
                      "<option value=\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToList();

    [ReferenceFact]
    public async Task The_department_picker_lists_the_companys_departments()
    {
        var w = await OfficeAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrRecordsModule).Assembly);
        string admin = await w.Client(host, "boss").GetStringAsync("/reports/");
        Assert.Equal(["Operations", "Sales &amp; Marketing"], Departments(admin));
        Assert.DoesNotContain("Engineering", admin);
        Assert.Equal(["Operations"], Departments(await w.Client(host, "mgr").GetStringAsync("/reports/")));
        // What the picker offers, the department report finds.
        string net = await QueryAsync(host, w, "boss", new { scope = "department", department = "Sales & Marketing", columns = new[] { "employee" } });
        Assert.Equal(["Ema Employee"], World.Json(net).GetProperty("results").EnumerateArray().Select(r => r.GetProperty("employee").GetString()).ToList());

        // No departments at all: one disabled line, in both apps.
        await using (var db = World.Db(null))
            await db.Memberships.Where(m => m.CompanyId == w.Company).ExecuteUpdateAsync(s => s.SetProperty(m => m.Department, (string?)null));
        string empty = await w.Client(host, "boss").GetStringAsync("/reports/");
        Assert.Contains("<option value=\"\" disabled>No departments recorded</option>", empty);
    }

    [ReferenceFact]
    public async Task Active_payroll_is_a_run_of_this_month_of_this_year()
    {
        var w = await OfficeAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrRecordsModule).Assembly);
        var today = BigOffice.Today;
        var lastYear = new DateOnly(today.Year - 1, today.Month, Math.Min(today.Day, 28));
        async Task<bool> ActiveAsync() =>
            World.Json(await w.Client(host, "boss").GetStringAsync("/reports/dashboard")).GetProperty("active_payroll").GetBoolean();
        int runId;
        await using (var db = w.Db())
        {
            db.Add(new PayrollRun { Month = lastYear.Month, Year = lastYear.Year, RunDate = lastYear.ToDateTime(new TimeOnly(12, 0)), Status = "unapproved", EmployeeCount = 0 });
            await db.SaveChangesAsync();
        }
        Assert.False(await ActiveAsync());       // last year's run of this month used to count
        await using (var db = w.Db())
        {
            var run = new PayrollRun { Month = today.Month, Year = today.Year, RunDate = today.ToDateTime(new TimeOnly(12, 0)), Status = "unapproved", EmployeeCount = 0 };
            db.Add(run);
            await db.SaveChangesAsync();
            runId = run.Id;
        }
        Assert.True(await ActiveAsync());
        await using (var db = w.Db())
            await db.Set<PayrollRun>().Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "approved"));
        Assert.False(await ActiveAsync());
    }
}
