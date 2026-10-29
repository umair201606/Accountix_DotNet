namespace Accountix.Hr.Services;

/// <summary>
/// Time-policy rules of hr_app/routes/attendance.py: which policy applies,
/// late / half day from the clock-in, and how a closed day's hours book into
/// the overtime account (regular up to the policy's maximum, then overtime,
/// then double overtime). Times are the company's local wall clock, naive,
/// as Python stores them.
/// </summary>
public sealed class AttendanceRules(AccountixDbContext db)
{
    /// <summary>_get_policy: the department's active policy, else any active one, else the built-in 09:00–18:00.</summary>
    public async Task<TimePolicy> PolicyAsync(string? department, CancellationToken ct = default)
    {
        var policy = await db.Set<TimePolicy>().AsNoTracking().OrderBy(p => p.Id)
                         .FirstOrDefaultAsync(p => p.Department == department && p.IsActive == true, ct)
                     ?? await db.Set<TimePolicy>().AsNoTracking().OrderBy(p => p.Id)
                         .FirstOrDefaultAsync(p => p.IsActive == true, ct);
        return policy ?? Default();
    }

    /// <summary>The unsaved TimePolicy(...) Python falls back to (no name: the page calls it "Standard").</summary>
    public static TimePolicy Default() => new()
    {
        Name = null!, ShiftStart = new TimeOnly(9, 0), ShiftEnd = new TimeOnly(18, 0),
        GracePeriodMinutes = 15, MaxRegularHours = 8, MaxOvertimeHours = 4, MaxConsecutiveDays = 6,
    };

    /// <summary>_late_and_half: late after shift start + grace; half day after 13:00.</summary>
    public static (bool Late, bool Half) LateAndHalf(DateOnly day, DateTime clockIn, TimePolicy policy)
    {
        var graceEnd = day.ToDateTime(policy.ShiftStart).AddMinutes(policy.GracePeriodMinutes ?? 0);
        var t = DateTime.SpecifyKind(clockIn, DateTimeKind.Unspecified);
        return (t > graceEnd, t > day.ToDateTime(new TimeOnly(13, 0)));
    }

    /// <summary>
    /// _book_hours: net worked hours (breaks taken off) into the day's
    /// OvertimeAccount, split by the policy. Returns (hours, regular, overtime).
    /// </summary>
    public async Task<(double Hours, double Regular, double Overtime)> BookHoursAsync(
        Attendance att, TimePolicy policy, DateTime utcNow, CancellationToken ct = default)
    {
        double totalSeconds = (att.ClockOut!.Value - att.ClockIn!.Value).TotalSeconds;
        await db.SaveChangesAsync(ct);
        int totalBreaks = await db.Set<BreakLog>()
            .Where(b => b.AttendanceId == att.Id && b.BreakEnd != null)
            .SumAsync(b => (int?)b.DurationMinutes, ct) ?? 0;
        double hours = Math.Max(0.0, Py.R((totalSeconds - totalBreaks * 60) / 3600, 2));
        double maxReg = policy.MaxRegularHours is double r && r != 0 ? r : 8;
        double maxOt = policy.MaxOvertimeHours is double o && o != 0 ? o : 4;
        double regular = Math.Min(hours, maxReg);
        double overtime = Py.R(Math.Max(0.0, hours - maxReg), 2);
        var oa = await db.Set<OvertimeAccount>().OrderBy(x => x.Id)
            .FirstOrDefaultAsync(x => x.UserId == att.UserId && x.Date == att.Date, ct);
        if (oa is null)
        {
            oa = new OvertimeAccount
            {
                UserId = att.UserId, Date = att.Date, RegularHours = 0.0, OvertimeHours = 0.0,
                DoubleOvertime = 0.0, Approved = false, CreatedAt = utcNow,
            };
            db.Add(oa);
        }
        oa.RegularHours = regular;
        oa.OvertimeHours = Math.Min(overtime, maxOt);
        oa.DoubleOvertime = Py.R(Math.Max(0.0, overtime - maxOt), 2);
        return (hours, regular, overtime);
    }

    /// <summary>_check_consecutive_days: the last max+1 records cover max consecutive days ending at the latest.</summary>
    public async Task<bool> ConsecutiveAsync(int userId, int maxDays, CancellationToken ct = default)
    {
        var recent = await db.Set<Attendance>().AsNoTracking().Where(a => a.UserId == userId)
            .OrderByDescending(a => a.Date).Take(maxDays + 1).Select(a => a.Date).ToListAsync(ct);
        if (recent.Count < maxDays + 1) return false;
        for (int i = 0; i < maxDays; i++)
        {
            var expected = recent[0].AddDays(-i);
            if (!recent.Contains(expected)) return false;
        }
        return true;
    }

    public async Task<List<string>> ViolationsAsync(int userId, TimePolicy policy, CancellationToken ct = default)
    {
        var violations = new List<string>();
        int max = policy.MaxConsecutiveDays ?? 6;
        if (await ConsecutiveAsync(userId, max, ct))
            violations.Add($"Working {max}+ consecutive days");
        return violations;
    }

    /// <summary>Python's "%Y-%m-%d %H:%M:%S" etc. parse of a corrected time; year 1900 marks a time-only value.</summary>
    public static DateTime? ParseCorrection(string raw)
    {
        string s = raw.Trim().Replace("T", " ");
        foreach (string fmt in new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "HH:mm:ss", "HH:mm" })
        {
            if (TryStrptime(s, fmt, out var v))
                return v;
        }
        return null;
    }

    /// <summary>strptime accepts one-digit fields ("9:05"); .NET exact formats need both shapes.</summary>
    private static bool TryStrptime(string s, string fmt, out DateTime value)
    {
        var styles = System.Globalization.DateTimeStyles.None;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string[] shapes = fmt switch
        {
            "yyyy-MM-dd HH:mm:ss" => ["yyyy-MM-dd HH:mm:ss", "yyyy-M-d H:m:s", "yyyy-MM-dd H:m:s"],
            "yyyy-MM-dd HH:mm" => ["yyyy-MM-dd HH:mm", "yyyy-M-d H:m", "yyyy-MM-dd H:m"],
            "HH:mm:ss" => ["HH:mm:ss", "H:m:s"],
            _ => ["HH:mm", "H:m"],
        };
        if (DateTime.TryParseExact(s, shapes, inv, styles, out value))
        {
            if (!fmt.StartsWith("yyyy"))
                value = new DateTime(1900, 1, 1, value.Hour, value.Minute, value.Second);
            return true;
        }
        return false;
    }
}
