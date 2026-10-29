using System.Globalization;
using System.Net;
using Accountix.Core.Entities;
using Accountix.Core.Shared;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Hr.Tests;

/// <summary>
/// tests/e2e/test_attendance_flow.py on .NET: the company's clock,
/// corrections that parse and re-derive late / half day / overtime, the time
/// policy's hours; and every rule step for step against Python's answer
/// (Golden/attendance_rules.json, captured from the Python app on 29 Oct 2026).
/// </summary>
public class AttendanceFlowTests
{
    private static readonly DateOnly Day = DateOnly.FromDateTime(DateTime.Today).AddDays(-3);

    private static DateTime LocalNow(string zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(zone));

    internal static async Task<(World W, int Att)> ClockWorldAsync()
    {
        var w = await World.CreateAsync("Clock Co", ("admin", "Cleo Clock", Role.Admin), ("emp", "Eli Early", Role.Employee));
        await using var db = w.Db();
        var att = new Attendance
        {
            UserId = w["admin"], Date = Day, Status = "present", ClockIn = Day.ToDateTime(new TimeOnly(9, 0)),
            ClockOut = Day.ToDateTime(new TimeOnly(17, 0)), IsLate = false, IsHalfDay = false, CreatedAt = DateTime.UtcNow,
        };
        db.Add(att);
        await db.SaveChangesAsync();
        return (w, att.Id);
    }

    private sealed record Rec(DateTime? In, DateTime? Out, bool? Late, bool? Half, string? Status, double? Reg, double? Ot);

    private static async Task<Rec> RecordAsync(World w, int attId)
    {
        await using var db = w.Db();
        var a = await db.Set<Attendance>().AsNoTracking().FirstAsync(x => x.Id == attId);
        var oa = await db.Set<OvertimeAccount>().AsNoTracking().FirstOrDefaultAsync(o => o.UserId == a.UserId && o.Date == a.Date);
        return new Rec(a.ClockIn, a.ClockOut, a.IsLate, a.IsHalfDay, a.Status, oa?.RegularHours, oa?.OvertimeHours);
    }

    private static Task<HttpResponseMessage> CorrectAsync(PageClient c, int attId, string field, string value, bool json = false)
    {
        (string, string)[] f = [("attendance_id", attId.ToString()), ("field", field), ("new_value", value), ("reason", "badge reader down")];
        return json ? World.PostJsonAcceptAsync(c, "/attendance/correct", "/attendance/admin", f)
                    : World.PostAsync(c, "/attendance/correct", "/attendance/admin", f);
    }

    [ReferenceFact]
    public async Task Clock_in_corrections_and_policy_hours_follow_python()
    {
        var (w, att) = await ClockWorldAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrModule).Assembly);
        var c = w.Client(host, "admin");

        // 01 clock-in uses the company clock (Asia/Karachi), filed under the company's date
        var r = await World.PostAsync(c, "/attendance/clock-in", "/attendance/");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("\"success\":true", await r.Content.ReadAsStringAsync());
        var local = LocalNow("Asia/Karachi");
        await using (var db = w.Db())
        {
            var today = DateOnly.FromDateTime(local);
            int uid = w["admin"];
            var a = await db.Set<Attendance>().AsNoTracking().FirstOrDefaultAsync(x => x.UserId == uid && x.Date == today);
            Assert.NotNull(a);
            Assert.True(Math.Abs((a!.ClockIn!.Value - local).TotalSeconds) < 120, $"{a.ClockIn} vs {local}");
            Assert.Single(await db.Set<AttendanceLog>().Where(l => l.AttendanceId == a.Id && l.Event == "clock_in").ToListAsync());
        }
        // a second clock-in the same day is refused
        r = await World.PostAsync(c, "/attendance/clock-in", "/attendance/");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("Already clocked in today", await r.Content.ReadAsStringAsync());

        // 02 correcting clock-in parses and re-derives
        r = await CorrectAsync(c, att, "clock_in", "10:00");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);   // plain form: redirect, not JSON
        var rec = await RecordAsync(w, att);
        Assert.Equal(Day.ToDateTime(new TimeOnly(10, 0)), DateTime.SpecifyKind(rec.In!.Value, DateTimeKind.Unspecified));
        Assert.True(rec.Late);
        Assert.False(rec.Half);
        Assert.Equal((7.0, 0.0), (rec.Reg, rec.Ot));

        // 03 clock-out books overtime by policy; then a longer policy moves the split
        await CorrectAsync(c, att, "clock_out", "20:00");
        rec = await RecordAsync(w, att);
        Assert.Equal((8.0, 2.0), (rec.Reg, rec.Ot));
        await using (var db = w.Db())
        {
            db.Add(new TimePolicy
            {
                Name = "Long days", Department = "", IsActive = true, ShiftStart = new TimeOnly(9, 0), ShiftEnd = new TimeOnly(19, 0),
                GracePeriodMinutes = 15, MaxRegularHours = 9, MaxOvertimeHours = 4, MaxConsecutiveDays = 6, CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        await CorrectAsync(c, att, "clock_out", "20:00");
        rec = await RecordAsync(w, att);
        Assert.Equal((9.0, 1.0), (rec.Reg, rec.Ot));

        // 04 bad values refused, not a 500
        r = await CorrectAsync(c, att, "clock_in", "half past nine", json: true);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        r = await CorrectAsync(c, att, "clock_out", "08:00", json: true);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("after", await r.Content.ReadAsStringAsync());
        r = await CorrectAsync(c, att, "status", "on the moon", json: true);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(Day.ToDateTime(new TimeOnly(10, 0)), DateTime.SpecifyKind((await RecordAsync(w, att)).In!.Value, DateTimeKind.Unspecified));

        // 05 a late-afternoon clock-in is a half day
        await CorrectAsync(c, att, "clock_in", "14:00");
        rec = await RecordAsync(w, att);
        Assert.True(rec.Half);
        Assert.Equal("half-day", rec.Status);

        // the audit trail of corrections
        await using (var db = w.Db())
            Assert.Equal(4, await db.Set<AttendanceCorrection>().CountAsync(x => x.AttendanceId == att));

        // 06 the clock follows the company's zone, not the server's
        foreach (string zone in new[] { "UTC", "America/New_York", "Asia/Karachi" })
        {
            await using (var db = World.Db(null))
            {
                var co = await db.Companies.FirstAsync(x => x.Id == w.Company);
                co.Timezone = zone;
                await db.SaveChangesAsync();
            }
            var cc = new Accountix.Core.Data.CompanyContext();
            cc.Set(w.Company);
            await using var db2 = Reference.NewDb(cc);
            var clock = new CompanyClock(db2, cc, TimeProvider.System);
            Assert.True(Math.Abs((clock.Now - LocalNow(zone)).TotalSeconds) < 60, zone);
        }
    }

    // Golden/capture/attendance_rules.py runs the same SQL text on Python's record.
    private const string StateSql = """SELECT concat_ws(' | ', coalesce(to_char(a.clock_in, 'YYYY-MM-DD HH24:MI:SS'), '-'), coalesce(to_char(a.clock_out, 'YYYY-MM-DD HH24:MI:SS'), '-'), coalesce(a.is_late::text, '-'), coalesce(a.is_half_day::text, '-'), coalesce(a.status, '-'), coalesce(o.regular_hours::text, '-'), coalesce(o.overtime_hours::text, '-'), coalesce(o.double_overtime::text, '-'), coalesce(o.approved::text, '-'), (SELECT count(*) FROM overtime_accounts x WHERE x.user_id = a.user_id AND x.date = a.date)::text, (SELECT count(*) FROM attendance_corrections c WHERE c.attendance_id = a.id)::text) FROM attendance a LEFT JOIN overtime_accounts o ON o.user_id = a.user_id AND o.date = a.date WHERE a.id = @id""";

    private const string CorrectionsSql = """SELECT concat_ws(' | ', c.field, coalesce(c.old_value, '-'), c.new_value, c.reason, (c.corrected_by = a.user_id)::text) FROM attendance_corrections c JOIN attendance a ON a.id = c.attendance_id WHERE c.attendance_id = @id ORDER BY c.id""";

    /// <summary>The rows of <paramref name="sql"/> for this record, its date printed as DAY.</summary>
    private static async Task<List<string>> RowsAsync(World w, string sql, int attId)
    {
        await using var db = w.Db();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var p = cmd.CreateParameter();
        p.ParameterName = "id";
        p.Value = attId;
        cmd.Parameters.Add(p);
        var rows = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            rows.Add(r.GetString(0).Replace(Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "DAY"));
        return rows;
    }

    /// <summary>
    /// Golden/attendance_rules.json: Python's /attendance/correct on the same record
    /// and the same steps (the grace minute, one o'clock sharp, the overtime cap and
    /// double overtime, a status in any case, refusals in Python's words, a policy
    /// made through /attendance/policies, a logged break, date+seconds and "T"
    /// forms). Each step's answer and the record's state are compared.
    /// </summary>
    [ReferenceFact]
    public async Task Corrections_re_derive_late_half_day_and_hours_as_python_does()
    {
        var golden = Golden.Load("attendance_rules");
        var (w, att) = await ClockWorldAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrModule).Assembly);
        var c = w.Client(host, "admin");
        string day = Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var expected = new List<string> { "before: " + golden.GetProperty("before").GetString() };
        var actual = new List<string> { "before: " + (await RowsAsync(w, StateSql, att)).Single() };
        foreach (var step in golden.GetProperty("steps").EnumerateArray())
        {
            string what = step.GetProperty("what").GetString()!;
            if (what.StartsWith("policy", StringComparison.Ordinal))
            {
                var made = await World.PostAsync(c, "/attendance/policies", "/attendance/policies",
                    ("name", "Early shift"), ("department", ""), ("shift_start", "08:30"), ("shift_end", "17:30"), ("grace", "0"),
                    ("max_reg", "9"), ("max_ot", "1"), ("max_days", "6"), ("min_break", "30"));
                Assert.Equal(HttpStatusCode.Redirect, made.StatusCode);
            }
            if (what.StartsWith("break", StringComparison.Ordinal))
                await using (var db = w.Db())
                {
                    db.Add(new BreakLog
                    {
                        AttendanceId = att, BreakStart = Day.ToDateTime(new TimeOnly(12, 0)), BreakEnd = Day.ToDateTime(new TimeOnly(12, 37)),
                        BreakType = "lunch", DurationMinutes = 37, CreatedAt = DateTime.UtcNow,
                    });
                    await db.SaveChangesAsync();
                }
            var r = await World.PostJsonAcceptAsync(c, "/attendance/correct", "/attendance/admin",
                ("attendance_id", att.ToString()), ("field", step.GetProperty("field").GetString()!),
                ("new_value", step.GetProperty("value").GetString()!.Replace("{DAY}", day)), ("reason", step.GetProperty("reason").GetString()!));
            expected.Add($"{what}: {step.GetProperty("status").GetInt32()} {step.GetProperty("answer").GetString()} || {step.GetProperty("state").GetString()}");
            actual.Add($"{what}: {(int)r.StatusCode} {Golden.SortedJson(await r.Content.ReadAsStringAsync())} || {(await RowsAsync(w, StateSql, att)).Single()}");
        }
        Golden.SameRows(expected, actual, "attendance corrections, step by step");
        Golden.SameRows(golden.GetProperty("corrections").EnumerateArray().Select(e => e.GetString()!).ToList(),
                        await RowsAsync(w, CorrectionsSql, att), "attendance_corrections rows");
    }
}
