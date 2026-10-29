using Accountix.Hr.Services;

namespace Accountix.Hr.Controllers;

/// <summary>Time &amp; attendance (hr_app/routes/attendance.py). Times are the company's wall clock.</summary>
[Area("Hr")]
[Module(Modules.Hr)]
[Route("attendance")]
public sealed class AttendanceController(AccountixDbContext db, CompanyClock clock, AttendanceRules rules, People people)
    : HrController
{
    public sealed class IndexModel
    {
        public required TimePolicy Policy { get; init; }
        public IReadOnlyList<string> Violations { get; init; } = [];
        public int Total { get; init; }
        public int? LateDays { get; init; }
        public int? HalfDays { get; init; }
        public double? Overtime { get; init; }
        public Attendance? Today { get; init; }
        public bool OnBreak { get; init; }
        public IReadOnlyList<Attendance> Records { get; init; } = [];
    }

    [HttpGet("")]
    [AnyMember]   // self service
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var today = clock.Today;
        var mine = db.Set<Attendance>().AsNoTracking().Where(a => a.UserId == Me);
        var records = await mine.OrderByDescending(a => a.Date).Take(30).ToListAsync(ct);
        var todayAtt = await mine.FirstOrDefaultAsync(a => a.Date == today, ct);
        bool onBreak = false;
        if (todayAtt is { ClockIn: not null, ClockOut: null })
            onBreak = await db.Set<BreakLog>().AnyAsync(b => b.AttendanceId == todayAtt.Id && b.BreakEnd == null, ct);
        var stats = await mine.GroupBy(_ => 1).Select(g => new
        {
            Total = g.Count(),
            Late = g.Sum(a => a.IsLate == true ? 1 : 0),
            Half = g.Sum(a => a.IsHalfDay == true ? 1 : 0),
        }).FirstOrDefaultAsync(ct);
        double? overtime = await db.Set<OvertimeAccount>().Where(o => o.UserId == Me).SumAsync(o => o.OvertimeHours, ct);
        var policy = await rules.PolicyAsync(MyDepartment, ct);
        return View(new IndexModel
        {
            Policy = policy,
            Violations = await rules.ViolationsAsync(Me, policy, ct),
            Total = stats?.Total ?? 0,
            // SUM over no rows is NULL: the page shows 0 then.
            LateDays = stats?.Late, HalfDays = stats?.Half,
            Overtime = overtime, Today = todayAtt, OnBreak = onBreak, Records = records,
        });
    }

    private IActionResult Err(string message) => JsonStatus(new { error = message }, 400);

    [HttpPost("clock-in")]
    [AnyMember]
    public async Task<IActionResult> ClockIn(CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // One attendance action at a time per person: a double tap logs once.
        await db.SerializeAsync("attendance", Me);
        var today = clock.Today;
        var existing = await db.Set<Attendance>().FirstOrDefaultAsync(a => a.UserId == Me && a.Date == today, ct);
        if (existing is { ClockIn: not null })
            return Err("Already clocked in today");
        var now = clock.Now;
        var policy = await rules.PolicyAsync(MyDepartment, ct);
        var (late, half) = AttendanceRules.LateAndHalf(today, now, policy);
        string? ip = RemoteAddr();
        var att = new Attendance
        {
            UserId = Me, Date = today, ClockIn = now, IpAddress = ip,
            Status = half ? "half-day" : "present", IsLate = late, IsHalfDay = half, CreatedAt = clock.UtcNow,
        };
        db.Add(att);
        await db.SaveChangesAsync(ct);
        db.Add(new AttendanceLog { AttendanceId = att.Id, Event = "clock_in", Timestamp = now, IpAddress = ip });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        string msg = $"{(late ? "Late - " : "")}{(half ? "Half Day - " : "")}Clocked in at {Py.Hm(now)}";
        return Json(new { success = true, message = msg, time = Py.Hms(now) });
    }

    [HttpPost("clock-out")]
    [AnyMember]
    public async Task<IActionResult> ClockOut(CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SerializeAsync("attendance", Me);
        var today = clock.Today;
        var att = await db.Set<Attendance>().FirstOrDefaultAsync(a => a.UserId == Me && a.Date == today, ct);
        if (att is null || att.ClockIn is null) return Err("Not clocked in today");
        if (att.ClockOut is not null) return Err("Already clocked out today");
        var active = await db.Set<BreakLog>().OrderBy(b => b.Id).FirstOrDefaultAsync(b => b.AttendanceId == att.Id && b.BreakEnd == null, ct);
        if (active is not null)
        {
            active.BreakEnd = clock.Now;
            active.DurationMinutes = (int)((active.BreakEnd.Value - active.BreakStart).TotalSeconds / 60);
        }
        var now = clock.Now;
        att.ClockOut = now;
        db.Add(new AttendanceLog { AttendanceId = att.Id, Event = "clock_out", Timestamp = now, IpAddress = RemoteAddr() });
        var (hours, regular, overtime) = await rules.BookHoursAsync(att, await rules.PolicyAsync(MyDepartment, ct), clock.UtcNow, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Json(new
        {
            success = true,
            message = $"Clocked out. Net: {Py.Float(hours)}h (Regular: {Py.Float(regular)}h, OT: {Py.Float(overtime)}h)",
            time = Py.Hms(now),
        });
    }

    [HttpPost("break-start")]
    [AnyMember]
    public async Task<IActionResult> BreakStart(CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SerializeAsync("attendance", Me);
        var today = clock.Today;
        var att = await db.Set<Attendance>().FirstOrDefaultAsync(a => a.UserId == Me && a.Date == today, ct);
        if (att is null || att.ClockIn is null) return Err("Not clocked in");
        if (att.ClockOut is not null) return Err("Already clocked out");
        if (await db.Set<BreakLog>().AnyAsync(b => b.AttendanceId == att.Id && b.BreakEnd == null, ct))
            return Err("Already on break");
        db.Add(new BreakLog
        {
            AttendanceId = att.Id, BreakStart = clock.Now, BreakType = FormStr("type", "lunch"), CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Json(new { success = true, message = "Break started" });
    }

    [HttpPost("break-end")]
    [AnyMember]
    public async Task<IActionResult> BreakEnd(CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SerializeAsync("attendance", Me);
        var today = clock.Today;
        var att = await db.Set<Attendance>().FirstOrDefaultAsync(a => a.UserId == Me && a.Date == today, ct);
        if (att is null) return Err("Not clocked in");
        var bl = await db.Set<BreakLog>().OrderBy(b => b.Id).FirstOrDefaultAsync(b => b.AttendanceId == att.Id && b.BreakEnd == null, ct);
        if (bl is null) return Err("No active break");
        bl.BreakEnd = clock.Now;
        bl.DurationMinutes = (int)((bl.BreakEnd.Value - bl.BreakStart).TotalSeconds / 60);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Json(new { success = true, message = $"Break ended ({bl.DurationMinutes} min)" });
    }

    [HttpGet("live")]
    [AnyMember]   // admins and managers, with the Attendance view right
    public async Task<IActionResult> Live(CancellationToken ct)
    {
        if (!IsLead) return JsonStatus(new { error = "Access denied" }, 403);
        if (!Access.Can("attendance", Rights.View)) return JsonStatus(new { error = "You do not have the Attendance view right." }, 403);
        var today = clock.Today;
        List<Member> users = IsAdmin
            ? await (await people.EmployeesAsync(Cid, ct)).Where(x => x.User.IsActive == true).AsNoTracking().ToListAsync(ct)
            : await people.ReportsOf(Me, Cid).AsNoTracking().ToListAsync(ct);   // a manager sees their own team
        var ids = users.Select(u => u.Id).ToList();
        // Today's records and open breaks in two queries, not two per person.
        var atts = (await db.Set<Attendance>().AsNoTracking().Where(a => ids.Contains(a.UserId) && a.Date == today)
            .OrderBy(a => a.Id).ToListAsync(ct)).GroupBy(a => a.UserId).ToDictionary(g => g.Key, g => g.First());
        var attIds = atts.Values.Select(a => a.Id).ToList();
        var onBreak = (await db.Set<BreakLog>().Where(b => attIds.Contains(b.AttendanceId) && b.BreakEnd == null)
            .Select(b => b.AttendanceId).ToListAsync(ct)).ToHashSet();
        var statuses = users.Select(u =>
        {
            atts.TryGetValue(u.Id, out var att);
            string s = att is { ClockIn: not null, ClockOut: null } ? (onBreak.Contains(att.Id) ? "on_break" : "online")
                     : att is { ClockOut: not null } ? "completed" : "absent";
            return new Dictionary<string, object?>
            {
                ["id"] = u.Id, ["name"] = u.FullName, ["department"] = u.Department, ["status"] = s,
                ["clock_in"] = att?.ClockIn is { } ci ? Py.Hm(ci) : null,
            };
        }).ToList();
        return Json(statuses);
    }

    public sealed class AdminModel
    {
        public int Month { get; init; }
        public int Year { get; init; }
        public IReadOnlyList<Attendance> Records { get; init; } = [];
        public IReadOnlyDictionary<int, string> Names { get; init; } = new Dictionary<int, string>();
        public IReadOnlyList<AttendanceCorrection> Corrections { get; init; } = [];
    }

    [HttpGet("admin")]
    [AnyMember]   // admins only, checked below
    public async Task<IActionResult> AdminView(CancellationToken ct)
    {
        if (!IsAdmin) return AccessDenied();
        int month = QueryInt("month", clock.Today.Month);
        int year = QueryInt("year", clock.Today.Year);
        // extract(month) = m AND extract(year) = y, as a date range an index on (company_id, date) can serve.
        List<Attendance> records = [];
        if (month is >= 1 and <= 12 && year is >= 1 and <= 9998)
        {
            var first = new DateOnly(year, month, 1);
            var next = first.AddMonths(1);
            records = await db.Set<Attendance>().AsNoTracking().Where(a => a.Date >= first && a.Date < next)
                .OrderByDescending(a => a.Date).ToListAsync(ct);
        }
        var corrections = await db.Set<AttendanceCorrection>().AsNoTracking()
            .OrderByDescending(c => c.CorrectedAt).Take(20).ToListAsync(ct);
        var ids = records.Select(r => r.UserId).Concat(corrections.Select(c => c.CorrectedBy)).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        ViewData["BackUrl"] = "/attendance/";
        return View("Admin", new AdminModel { Month = month, Year = year, Records = records, Names = names, Corrections = corrections });
    }

    /// <summary>_reply: JSON for scripts, a flash and back to the register for the plain form.</summary>
    private IActionResult Reply(bool ok, string msg, int status = 200)
    {
        if (AsksJson())
            return ok ? JsonStatus(new { success = true, message = msg }, status) : JsonStatus(new { error = msg }, status);
        this.Flash(msg, ok ? "success" : "danger");
        return Redirect(SafeUrl.Back(Request, "/attendance/admin"));
    }

    private static readonly HashSet<string> Correctable = ["clock_in", "clock_out", "status"];
    private static readonly SortedSet<string> Statuses = new(StringComparer.Ordinal) { "present", "absent", "half-day", "leave", "late", "holiday" };

    [HttpPost("correct")]
    [AnyMember]   // admins and managers with the Attendance edit right; managers for their own team only
    public async Task<IActionResult> Correct(CancellationToken ct)
    {
        if (!IsLead) return Reply(false, "Access denied", 403);
        if (!Access.Can("attendance", Rights.Edit))
            return Reply(false, "You do not have the Attendance edit right. Ask an admin in Settings.", 403);
        int? attId = FormInt("attendance_id");
        string? field = Request.Form["field"].FirstOrDefault();
        string? newValue = Request.Form["new_value"].FirstOrDefault();
        string reason = FormStr("reason").Trim();
        if (attId is null || attId == 0 || string.IsNullOrEmpty(field) || string.IsNullOrEmpty(newValue) || reason.Length == 0)
            return Reply(false, "All fields required", 400);
        if (!Correctable.Contains(field))
            return Reply(false, "That field cannot be corrected here", 400);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var att = await db.Set<Attendance>().FirstOrDefaultAsync(a => a.Id == attId, ct);
        if (att is null) return NotFound();
        if (!IsAdmin && !await people.ReportsOf(Me, Cid).AnyAsync(m => m.User.Id == att.UserId, ct))
            return Reply(false, "Access denied", 403);
        string oldValue = field switch
        {
            "clock_in" => att.ClockIn is { } a ? Py.DateTime(a) : "",
            "clock_out" => att.ClockOut is { } b ? Py.DateTime(b) : "",
            _ => att.Status ?? "",
        };
        if (field is "clock_in" or "clock_out")
        {
            var parsed = AttendanceRules.ParseCorrection(newValue);
            if (parsed is null)
                return Reply(false, "Enter a time as HH:MM (or YYYY-MM-DD HH:MM)", 400);
            var value = parsed.Value;
            if (value.Year == 1900)                    // time only: on the record's day
                value = att.Date.ToDateTime(TimeOnly.FromDateTime(value));
            var cin = field == "clock_in" ? value : att.ClockIn;
            var cout = field == "clock_out" ? value : att.ClockOut;
            if (cin is not null && cout is not null && cout <= cin)
                return Reply(false, "Clock-out must be after clock-in", 400);
            if (field == "clock_in") att.ClockIn = value; else att.ClockOut = value;
            var owner = await db.Users.AsNoTracking().FirstAsync(u => u.Id == att.UserId, ct);
            var (_, _, dept) = await people.EmploymentInAsync(owner, Cid, ct);
            var policy = await rules.PolicyAsync(dept, ct);
            if (att.ClockIn is { } ci)
            {
                var (late, half) = AttendanceRules.LateAndHalf(att.Date, ci, policy);
                att.IsLate = late;
                att.IsHalfDay = half;
                if (att.Status is "present" or "half-day")
                    att.Status = half ? "half-day" : "present";
            }
            if (att.ClockIn is not null && att.ClockOut is not null)
                await rules.BookHoursAsync(att, policy, clock.UtcNow, ct);
            newValue = value.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            string s = newValue.Trim().ToLowerInvariant();
            if (!Statuses.Contains(s))
                return Reply(false, "Status must be one of: " + string.Join(", ", Statuses), 400);
            att.Status = newValue = s;
        }
        db.Add(new AttendanceCorrection
        {
            AttendanceId = att.Id, CorrectedBy = Me, Field = field, OldValue = oldValue, NewValue = newValue,
            Reason = reason, CorrectedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Reply(true, "Attendance corrected");
    }

    [HttpGet("policies")]
    [AnyMember]   // admins only, checked below
    public async Task<IActionResult> Policies(CancellationToken ct)
    {
        if (!IsAdmin) return AccessDenied();
        ViewData["BackUrl"] = "/attendance/";
        return View(await db.Set<TimePolicy>().AsNoTracking().OrderBy(p => p.Id).ToListAsync(ct));
    }

    [HttpPost("policies")]
    [AnyMember]   // admins only, checked below
    public async Task<IActionResult> CreatePolicy(CancellationToken ct)
    {
        if (!IsAdmin) return AccessDenied();
        var f = Request.Form;
        if (!f.ContainsKey("name") || !f.ContainsKey("shift_start") || !f.ContainsKey("shift_end")) return BadRequest();   // request.form[...]
        static TimeOnly Hm(string? s) => TimeOnly.ParseExact(s ?? "", ["HH:mm", "H:mm", "H:m"], System.Globalization.CultureInfo.InvariantCulture);
        TimeOnly start, end;
        try { start = Hm(f["shift_start"].FirstOrDefault()); end = Hm(f["shift_end"].FirstOrDefault()); }
        catch (FormatException) { throw new FormInputException("Shift times must be HH:MM."); }
        db.Add(new TimePolicy
        {
            Name = f["name"].FirstOrDefault() ?? "",
            Department = f["department"].FirstOrDefault() ?? "",
            ShiftStart = start, ShiftEnd = end,
            GracePeriodMinutes = FormInput.Int(f, "grace", 15),
            MaxRegularHours = FormInput.Float(f, "max_reg", 8),
            MaxOvertimeHours = FormInput.Float(f, "max_ot", 4),
            MaxConsecutiveDays = FormInput.Int(f, "max_days", 6),
            RequireBreak = f["require_break"].FirstOrDefault() == "on",
            MinBreakMinutes = FormInput.Int(f, "min_break", 30),
            IsActive = true, CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        this.Flash("Time policy created.", "success");
        return Redirect("/attendance/policies");
    }

    [HttpGet("api/logs")]
    [AnyMember]   // own records
    public async Task<IActionResult> ApiLogs(CancellationToken ct)
    {
        var records = await db.Set<Attendance>().AsNoTracking().Where(a => a.UserId == Me)
            .OrderByDescending(a => a.Date).Take(60).ToListAsync(ct);
        return Json(records.Select(r => new Dictionary<string, object?>
        {
            ["id"] = r.Id, ["date"] = Py.Date(r.Date),
            ["clock_in"] = r.ClockIn is { } ci ? Py.Iso(ci) : null,
            ["clock_out"] = r.ClockOut is { } co ? Py.Iso(co) : null,
            ["status"] = r.Status, ["is_late"] = r.IsLate, ["is_half_day"] = r.IsHalfDay,
        }));
    }

    public sealed class OverviewModel
    {
        public string Period { get; init; } = "monthly";
        public IReadOnlyList<Attendance> Records { get; init; } = [];
        public IReadOnlyList<OvertimeAccount> Overtime { get; init; } = [];
    }

    [HttpGet("overview")]
    [AnyMember]   // own records
    public async Task<IActionResult> Overview(CancellationToken ct)
    {
        string period = Request.Query["period"].FirstOrDefault() ?? "monthly";
        var today = clock.Today;
        var mine = db.Set<Attendance>().AsNoTracking().Where(a => a.UserId == Me);
        List<Attendance> records;
        if (period == "daily")
            records = await mine.Where(a => a.Date == today).OrderBy(a => a.Id).ToListAsync(ct);
        else if (period == "weekly")
        {
            var start = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            var stop = start.AddDays(7);
            records = await mine.Where(a => a.Date >= start && a.Date < stop).OrderBy(a => a.Id).ToListAsync(ct);
        }
        else if (period == "yearly")
            records = await mine.Where(a => a.Date.Year == today.Year).OrderByDescending(a => a.Date).ToListAsync(ct);
        else
            records = await mine.Where(a => a.Date.Month == today.Month && a.Date.Year == today.Year)
                .OrderByDescending(a => a.Date).ToListAsync(ct);
        var ot = await db.Set<OvertimeAccount>().AsNoTracking().Where(o => o.UserId == Me)
            .OrderByDescending(o => o.Date).Take(30).ToListAsync(ct);
        ViewData["BackUrl"] = "/attendance/";
        return View(new OverviewModel { Period = period, Records = records, Overtime = ot });
    }
}
