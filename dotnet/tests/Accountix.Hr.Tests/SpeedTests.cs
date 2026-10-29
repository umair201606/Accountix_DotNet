using System.Diagnostics;
using Accountix.Core.Entities;
using Accountix.Core.Security;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Accountix.Hr.Tests;

/// <summary>
/// Speed (docs/SPEED_AND_CONCURRENCY.md): list pages cost a fixed number of
/// queries however many people they show. A company of 200 employees with a
/// month of attendance, a pending leave, timesheet and loan each, built once
/// in the HR test database and reused; then the perf database's Samba.
/// </summary>
public class SpeedTests(ITestOutputHelper output)
{
    private const string Slug = "hr-speed-world-v1";
    private const int People = 200;

    private sealed record Big(int Company, int Admin, int Manager, int Year, int Month);

    private static async Task<Big> BigCompanyAsync()
    {
        await using var db = World.Db(null);
        var roles = await db.Roles.ToDictionaryAsync(r => r.Name, r => r.Id);
        var existing = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Slug == Slug);
        var month = DateOnly.FromDateTime(DateTime.Today).AddMonths(-1);
        var first = new DateOnly(month.Year, month.Month, 1);
        if (existing is not null)
        {
            var ids = await db.Memberships.Where(m => m.CompanyId == existing.Id).OrderBy(m => m.Id).Select(m => m.UserId).Take(2).ToListAsync();
            return new Big(existing.Id, ids[0], ids[1], existing.CreatedAt!.Value.AddMonths(-1).Year, existing.CreatedAt!.Value.AddMonths(-1).Month);
        }
        var now = DateTime.UtcNow;
        var co = new Company { Name = "HR Speed Co", Slug = Slug, IsActive = true, PlanName = "free", Timezone = "Asia/Karachi", CreatedAt = now, UpdatedAt = now,
                               ModHrEnabled = true, ModAccountingEnabled = true, ModFinanceEnabled = true, ModInventoryEnabled = true,
                               ModInvoicingEnabled = true, ModFbrEnabled = true, ModFixedAssetsEnabled = true, ModExecutiveEnabled = true };
        db.Companies.Add(co);
        await db.SaveChangesAsync();
        string hash = PasswordHasher.Hash("pw12345678");
        User U(string name) => new()
        {
            EmployeeCode = "SP" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(), Email = Guid.NewGuid().ToString("N")[..12] + "@speed.test",
            PasswordHash = hash, FullName = name, RoleId = roles[Role.Employee], IsActive = true, HasHrAccess = true, IsSuperAdmin = false,
            CreatedAt = now, UpdatedAt = now,
        };
        var admin = U("Sid Admin");
        var mgr = U("Sam Manager");
        var staff = Enumerable.Range(1, People).Select(i => U($"Staff {i:D3}")).ToList();
        db.Users.AddRange([admin, mgr, .. staff]);
        await db.SaveChangesAsync();
        db.Memberships.Add(World.NewMembership(co.Id, admin.Id, roles[Role.Admin]));
        db.Memberships.Add(World.NewMembership(co.Id, mgr.Id, roles[Role.Manager]));
        foreach (var (u, i) in staff.Select((u, i) => (u, i)))
        {
            var m = World.NewMembership(co.Id, u.Id, roles[Role.Employee]);
            m.ManagerId = mgr.Id;
            m.Department = i % 2 == 0 ? "Sales" : "Stores";
            m.Designation = "Associate";
            if (i % 4 != 0) m.EmployeeCode = $"E{i:D4}";
            db.Memberships.Add(m);
        }
        await db.SaveChangesAsync();

        var cc = new Accountix.Core.Data.CompanyContext();
        cc.Set(co.Id);
        await using var tdb = Reference.NewDb(cc);
        var lt = new LeaveType { Name = "Annual", Code = "AL", DefaultQuota = 20, IsPaid = true, RequiresApproval = true, CarryForward = false, MaxCarryForward = 0 };
        tdb.Add(lt);
        await tdb.SaveChangesAsync();
        var last = first.AddMonths(1).AddDays(-1);
        foreach (var u in staff)
        {
            for (var d = first; d <= last; d = d.AddDays(1))
                if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                    tdb.Add(new Attendance { UserId = u.Id, Date = d, ClockIn = d.ToDateTime(new TimeOnly(9, u.Id % 30)), ClockOut = d.ToDateTime(new TimeOnly(18, 0)),
                                             Status = "present", IsLate = u.Id % 30 > 15, IsHalfDay = false, CreatedAt = now });
            var lr = new LeaveRequest { UserId = u.Id, LeaveTypeId = lt.Id, StartDate = last.AddDays(40), EndDate = last.AddDays(41), TotalDays = 2,
                                        Reason = "family", Status = "pending", IsHalfDay = false, CreatedAt = now, UpdatedAt = now };
            var wk = new TimesheetWeek { UserId = u.Id, WeekStart = first, WeekEnd = first.AddDays(6), Status = "submitted", TotalHours = 40, CreatedAt = now, SubmittedAt = now };
            tdb.AddRange(lr, wk);
            tdb.Add(new LoanAdvanceRequest { UserId = u.Id, RequestType = "loan", Amount = 10000, Purpose = "speed", InstallmentMonths = 10, MonthlyInstallment = 1000,
                                             RemainingAmount = 10000, Status = "pending", QueueForPayroll = false, CreatedAt = now });
            await tdb.SaveChangesAsync();
            tdb.Add(new LeaveApproval { LeaveRequestId = lr.Id, ApproverId = mgr.Id, Level = 1, Status = "pending", CreatedAt = now });
            tdb.Add(new TimesheetApproval { WeekId = wk.Id, ApproverId = mgr.Id, Status = "pending", CreatedAt = now });
            tdb.Add(new TimesheetEntry { WeekId = wk.Id, Day = first, Project = "x", Task = "x", Hours = 8, CreatedAt = now });
        }
        await tdb.SaveChangesAsync();
        return new Big(co.Id, admin.Id, mgr.Id, first.Year, first.Month);
    }

    [ReferenceFact]
    public async Task List_pages_cost_a_fixed_number_of_queries_for_200_people()
    {
        var big = await BigCompanyAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrModule).Assembly);
        var admin = host.ClientFor(big.Admin, big.Company);
        var mgr = host.ClientFor(big.Manager, big.Company);
        (PageClient C, string Url)[] pages =
        [
            (admin, "/auth/users"),
            (admin, $"/attendance/admin?month={big.Month}&year={big.Year}"),
            (admin, "/attendance/live"),
            (admin, "/dashboard"),
            (admin, "/auth/members/assign"),
            (admin, "/mss/"),
            (admin, "/mss/team"),
            (mgr, "/mss/"),
            (mgr, "/mss/team"),
            (mgr, "/mss/approvals"),
            (mgr, "/leaves/approvals"),
            (mgr, "/timesheets/approvals"),
            (mgr, "/mss/team-availability"),
            (mgr, $"/mss/team-calendar?month={big.Month}&year={big.Year}"),
            (mgr, "/attendance/live"),
            (admin, $"/leaves/calendar?month={big.Month}&year={big.Year}"),
        ];
        foreach (var (c, url) in pages)
        {
            var t = await host.MeasureAsync(c, url);
            output.WriteLine(t.ToString());
            t.Within(800, 25);
        }
        // the merge report (a POST over every employee's approved weeks)
        string token = await admin.TokenFromAsync("/timesheets/projects");
        host.Queries.Reset();
        var sw = Stopwatch.StartNew();
        var r = await admin.PostFormWithTokenAsync("/timesheets/merge-report",
            [new("period_start", $"{big.Year}-{big.Month:D2}-01"), new("period_end", $"{big.Year}-{big.Month:D2}-28")], token);
        sw.Stop();
        output.WriteLine($"POST /timesheets/merge-report: {(int)r.StatusCode} in {sw.ElapsedMilliseconds} ms, {host.Queries.Count} queries");
        Assert.True(host.Queries.Count <= 25, string.Join("\n", host.Queries.Repeated()));
    }

    [PerfFact]
    public async Task Hr_pages_are_quick_on_the_samba_year()
    {
        await using var host = await ModuleHost.StartOnAsync(Perf.ConnectionString, typeof(HrModule).Assembly);
        var c = host.ClientFor(Perf.AdminUserId, Perf.CompanyId);
        // read-only pages only: /leaves/, /ess/ and /timesheets/ write a quota or a week on first view
        foreach (string url in new[] { "/dashboard", $"/attendance/admin?month={Perf.BusiestMonthStart.Month}&year={Perf.BusiestMonthStart.Year}",
                                       "/attendance/overview?period=yearly", "/auth/users", "/auth/members/assign", "/mss/", "/mss/team",
                                       "/mss/approvals", "/leaves/approvals", "/leaves/holidays", "/timesheets/approvals", "/attendance/live",
                                       "/auth/profile" })
        {
            var t = await host.MeasureAsync(c, url);
            output.WriteLine(t.ToString());
            t.Within(800, 25);
        }
    }
}
