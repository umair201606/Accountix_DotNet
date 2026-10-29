using Accountix.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accountix.HrRecords.Tests;

/// <summary>
/// A company of ~200 employees with a year of HR records (attendance with
/// odd seconds, approved / pending sick and annual leave in halves, overtime,
/// PF ledger and staff loans with six-decimal amounts), built once per test
/// run: the report builder's figures and the downloads are compared with
/// Python's answers on the same data (Golden/big_office.json), and its query
/// counts must not grow with the number of people. Every date is placed
/// around a fixed <see cref="Anchor"/>, so the same rows (and figures) come
/// out on any day the tests run.
/// </summary>
public static class BigOffice
{
    public const int Employees = 200;
    public static readonly string[] Departments = ["Engineering", "Finance", "Marketing", "Human Resources"];

    private static readonly Lazy<Task<World>> Built = new(BuildAsync);

    public static Task<World> GetAsync() => Built.Value;

    /// <summary>The day the data is placed around (the "today" of the capture, 15 Oct 2026; never the real today).</summary>
    public static readonly DateOnly Anchor = new(2026, 10, 15);

    /// <summary>The company's real "today" (Asia/Karachi), as the pages see it.</summary>
    public static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Asia/Karachi"));

    private static async Task<World> BuildAsync()
    {
        var people = new List<(string, string, string)>
        {
            ("admin", "Big Admin", Role.Admin), ("mgr", "Team Manager", Role.Manager), ("rmgr", "Restricted Manager", Role.Manager),
        };
        for (int i = 0; i < Employees; i++)
            people.Add(($"e{i}", $"Employee {i:000} {(i % 7 == 0 ? "Ünïcødé" : "Khan")}", Role.Employee));
        var w = await World.CreateAsync("Big Office", [.. people]);
        await w.RightsAsync("rmgr", ("pf", false), ("loans", false), ("leaves", false), ("attendance", false));

        var rng = new Random(4242);
        await using (var db = World.Db(null))
        {
            var members = await db.Memberships.Where(m => m.CompanyId == w.Company).ToListAsync();
            var byUser = members.ToDictionary(m => m.UserId);
            for (int i = 0; i < Employees; i++)
            {
                var m = byUser[w[$"e{i}"]];
                m.Department = i % 9 == 0 ? null : Departments[i % Departments.Length];
                m.ManagerId = i < 40 ? w["mgr"] : i < 60 ? w["rmgr"] : null;
                if (i % 23 == 5) m.Status = CompanyMembership.Blocked;        // out of every list
            }
            var users = await db.Users.Where(u => members.Select(m => m.UserId).Contains(u.Id)).ToListAsync();
            foreach (var u in users.Where(u => u.FullName.EndsWith("7 Khan") || u.FullName.EndsWith("13 Khan")))
                u.IsActive = false;                                            // switched-off accounts
            await db.SaveChangesAsync();
        }

        await using var cdb = w.Db();
        var al = new LeaveType { Name = "Annual", Code = "AL", DefaultQuota = 14, IsPaid = true, RequiresApproval = true, CarryForward = false, MaxCarryForward = 0 };
        var sl = new LeaveType { Name = "Sick", Code = "SL", DefaultQuota = 8, IsPaid = true, RequiresApproval = true, CarryForward = false, MaxCarryForward = 0 };
        cdb.AddRange(al, sl);
        await cdb.SaveChangesAsync();
        var today = Anchor;
        string[] statuses = ["approved", "approved", "approved", "pending", "rejected"];
        decimal[] halves = [0.5m, 1m, 1.5m, 2m, 3m, 4.5m];
        for (int i = 0; i < Employees; i++)
        {
            int uid = w[$"e{i}"];
            // Attendance: this year (two months ago up to today) and a few days last year.
            var days = new HashSet<DateOnly>();      // one attendance row per person per day (uq_attendance_company_date)
            for (int d = 0; d < 8; d++)
            {
                var date = d == 0 ? today : today.AddDays(-rng.Next(1, 70));
                if (d == 7) date = new DateOnly(today.Year - 1, 12, rng.Next(1, 28));
                if (!days.Add(date)) continue;
                var clockIn = date.ToDateTime(new TimeOnly(4, 0)).AddSeconds(rng.Next(0, 3 * 3600));     // UTC, as Python stores
                DateTime? clockOut = rng.Next(10) == 0 ? null : clockIn.AddSeconds(rng.Next(4 * 3600, 10 * 3600) + rng.NextDouble());
                bool late = rng.Next(4) == 0, half = rng.Next(9) == 0;
                cdb.Add(new Attendance
                {
                    UserId = uid, Date = date, ClockIn = rng.Next(15) == 0 ? null : clockIn, ClockOut = clockOut,
                    Status = half ? "half_day" : "present", IsLate = late, IsHalfDay = half, CreatedAt = DateTime.UtcNow,
                });
            }
            for (int k = 0; k < rng.Next(0, 4); k++)
            {
                var start = today.AddDays(-rng.Next(0, 80));
                cdb.Add(new LeaveRequest
                {
                    UserId = uid, LeaveTypeId = rng.Next(2) == 0 ? al.Id : sl.Id, StartDate = start, EndDate = start.AddDays(rng.Next(0, 4)),
                    TotalDays = halves[rng.Next(halves.Length)], Reason = $"Reason {k}, \"quoted\"", Status = statuses[rng.Next(statuses.Length)],
                    IsHalfDay = false, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                });
            }
            if (rng.Next(3) > 0)
                for (int k = 0; k < 2; k++)
                    cdb.Add(new OvertimeAccount
                    {
                        UserId = uid, Date = today.AddDays(-k - 1), RegularHours = 8, OvertimeHours = Math.Round(rng.NextDouble() * 4, rng.Next(1, 5)),
                        DoubleOvertime = 0, Approved = true, CreatedAt = DateTime.UtcNow,
                    });
            if (rng.Next(4) > 0)
                for (int k = 0; k < 3; k++)
                    cdb.Add(new PfLedger
                    {
                        UserId = uid, TransactionDate = today.AddMonths(-k), TransactionType = k == 2 ? "withdrawal" : "contribution",
                        Description = "PF", Debit = k == 2 ? Math.Round((decimal)rng.NextDouble() * 900, 6) : 0m,
                        Credit = k == 2 ? 0m : Math.Round((decimal)rng.NextDouble() * 5000, 6), Balance = 0m, CreatedAt = DateTime.UtcNow,
                    });
            if (rng.Next(5) == 0)
                cdb.Add(new LoanAdvanceRequest
                {
                    UserId = uid, RequestType = "loan", Amount = 50000m, Purpose = "Car", InstallmentMonths = 10, MonthlyInstallment = 5000m,
                    RemainingAmount = Math.Round((decimal)rng.NextDouble() * 50000, 6), Status = rng.Next(3) == 0 ? "pending" : "approved",
                    QueueForPayroll = false, CreatedAt = DateTime.UtcNow,
                });
        }
        cdb.Add(new PayrollRun { Month = today.Month, Year = today.Year, RunDate = DateTime.UtcNow, Status = "unapproved", EmployeeCount = 0 });
        await cdb.SaveChangesAsync();
        return w;
    }
}
