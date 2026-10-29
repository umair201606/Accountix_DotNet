using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Accountix.HrRecords.Services;

namespace Accountix.HrRecords.Controllers;

/// <summary>
/// hr_app/routes/reports.py (blueprint "reports"): the ad-hoc report
/// builder, the dashboard figures, the attendance chart and the attendance /
/// leave downloads. Admins and managers only (checked in the route); a
/// manager sees only their direct reports in this company, and the PF, loan
/// and leave columns need that section's view right.
/// </summary>
[Area("HrRecords")]
[Module(Modules.Hr)]
[Route("reports")]
public sealed partial class ReportsController(AccountixDbContext db, Staff staff, HrReport report, IReportExport export,
                                               CompanyClock clock) : RecordsController
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly Dictionary<string, string> ColumnTitles = new()
    {
        ["pf_balance"] = "PF Balance", ["loan_balance"] = "Loan Balance", ["sick_days"] = "Sick Days",
        ["late_days"] = "Late Days", ["present_days"] = "Present Days",
    };

    public sealed record IndexModel(IReadOnlyList<Person> Employees, IReadOnlyList<string> Departments, DateOnly Today);

    /// <summary>_visible_ids(): null for an admin (the whole company), else the person's direct reports here.</summary>
    private async Task<HashSet<int>?> VisibleIdsAsync(CancellationToken ct) =>
        IsAdmin ? null : (await staff.ReportIdsAsync(Me, Cid, ct)).ToHashSet();

    /// <summary>_get_scope_users(scope, user_id, department).</summary>
    private async Task<List<Person>> ScopeUsersAsync(string? scope, int? userId, string? department, CancellationToken ct)
    {
        List<Person> users;
        if (scope == "individual" && userId is int uid && uid != 0)
            users = await staff.MemberAsync(uid, Access, ct) is { } p ? [p] : [];
        else if (scope == "department" && !string.IsNullOrEmpty(department))
            users = await (await staff.ActiveEmployeesAsync(Cid, department: department, ct: ct)).ToListAsync(ct);
        else
            users = await (await staff.ActiveEmployeesAsync(Cid, ct: ct)).ToListAsync(ct);
        var visible = await VisibleIdsAsync(ct);
        return visible is null ? users : users.Where(u => visible.Contains(u.Id)).ToList();
    }

    [HttpGet("")]
    [AnyMember]   // admins and managers get the builder; anyone else the dashboard page
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        if (!IsLead)
            return View("EmployeeDashboard");
        var employees = await ScopeUsersAsync("company", null, null, ct);
        // The departments of the people this person reports on, as kept on this company's memberships
        // (what the department report filters on); they were four made-up names.
        var departments = employees.Select(e => e.Department).Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d!).Distinct().Order(StringComparer.Ordinal).ToList();
        return View(new IndexModel(employees, departments, clock.Today));
    }

    [HttpGet("dashboard")]
    [AnyMember]   // the figures depend on the role, as in Python
    public async Task<IActionResult> Dashboard(CancellationToken ct)
    {
        var today = clock.Today;
        if (IsLead)
        {
            int total = await (await staff.ActiveEmployeesAsync(Cid, ct: ct)).CountAsync(ct);
            int present = await db.Set<Attendance>().CountAsync(a => a.Date == today && a.ClockIn != null, ct);
            int pending = await db.Set<LeaveRequest>().CountAsync(l => l.Status == "pending", ct);
            // An unapproved run dated in this month of this year (the same month of any year used to count).
            bool payroll = await db.Database.SqlQuery<int>($"""
                SELECT CAST(count(*) AS integer) AS "Value" FROM payroll_runs r
                WHERE r.company_id = {Cid} AND r.status = 'unapproved'
                  AND extract(year FROM r.run_date) = {today.Year} AND extract(month FROM r.run_date) = {today.Month}
                """).SingleAsync(ct) > 0;
            return PyJson(new Dictionary<string, object?>
            {
                ["total_employees"] = total, ["today_present"] = present, ["today_absent"] = total - present,
                ["pending_leaves"] = pending, ["active_payroll"] = payroll,
            });
        }
        var mine = await db.Set<Attendance>().AsNoTracking().Where(a => a.UserId == Me && a.Date == today)
            .OrderBy(a => a.Id).Select(a => new { a.ClockIn, a.ClockOut }).FirstOrDefaultAsync(ct);
        int myPending = await db.Set<LeaveRequest>().CountAsync(l => l.UserId == Me && l.Status == "pending", ct);
        string status = mine is not null && mine.ClockIn is not null && mine.ClockOut is null ? "Clocked In"
            : mine is not null && mine.ClockOut is not null ? "Done" : "Not Clocked";
        return PyJson(new Dictionary<string, object?>
        {
            ["total_employees"] = "-", ["today_present"] = status, ["today_absent"] = "-",
            ["pending_leaves"] = myPending, ["active_payroll"] = false,
        });
    }

    private sealed record MonthRow(int Month, int Total, int Late, int Half);

    [HttpGet("attendance-chart")]
    [AnyMember]   // admins and managers, checked in the route
    public async Task<IActionResult> AttendanceChart(CancellationToken ct)
    {
        if (!IsLead)
            return PyError("Access denied", 403);
        int year = QueryInt("year", clock.Now.Year);
        var rows = await db.Database.SqlQuery<MonthRow>($"""
            SELECT CAST(extract(month FROM a.date) AS integer) AS "Month",
                   CAST(count(a.id) AS integer) AS "Total",
                   CAST(sum(CASE WHEN (a.is_late = true) THEN 1 ELSE 0 END) AS integer) AS "Late",
                   CAST(sum(CASE WHEN (a.is_half_day = true) THEN 1 ELSE 0 END) AS integer) AS "Half"
            FROM attendance a
            WHERE a.company_id = {Cid} AND extract(year FROM a.date) = {year}
            GROUP BY 1 ORDER BY 1
            """).ToListAsync(ct);
        return PyJson(rows.Select(r => new Dictionary<string, object?>
        {
            ["month"] = r.Month, ["total"] = r.Total, ["late"] = r.Late, ["half"] = r.Half,
        }).ToList());
    }

    [HttpPost("query")]
    [AnyMember]   // admins and managers, checked in the route; column rights checked below
    public async Task<IActionResult> Query(CancellationToken ct)
    {
        if (!IsLead)
            return PyError("Access denied", 403);
        if (!Request.HasJsonContentType())
            return new ContentResult
            {
                StatusCode = 415, ContentType = "text/html; charset=utf-8",
                Content = ErrorAnswersMiddleware.ErrorPage(415, "Unsupported Media Type",
                    "Did not attempt to load JSON data because the request Content-Type was not 'application/json'."),
            };
        JsonElement data;
        try
        {
            using var doc = await JsonDocument.ParseAsync(Request.Body, cancellationToken: ct);
            data = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return BadRequestPage();
        }
        if (data.ValueKind != JsonValueKind.Object)
            return BadRequestPage();

        string? scope = data.TryGetProperty("scope", out var s) ? Text(s) : "company";
        int? userId = null;
        if (data.TryGetProperty("user_id", out var u) && u.ValueKind != JsonValueKind.Null)
        {
            userId = JsonInt(u);
            if (userId is null) return BadRequestPage();
        }
        string? department = data.TryGetProperty("department", out var d) ? Text(d) : null;
        DateOnly? from = null, to = null;
        if (data.TryGetProperty("date_from", out var f) && Truthy(f))
        {
            if (!Py.TryParseDate(Text(f), out var v)) return BadRequestPage();
            from = v;
        }
        if (data.TryGetProperty("date_to", out var t) && Truthy(t))
        {
            if (!Py.TryParseDate(Text(t), out var v)) return BadRequestPage();
            to = v;
        }
        List<string> columns = data.TryGetProperty("columns", out var cols) && cols.ValueKind == JsonValueKind.Array
            ? cols.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!).ToList()
            : [.. HrReport.DefaultColumns];
        foreach (string c in columns)
        {
            if (HrReport.ColumnRights.TryGetValue(c, out string? section) && !Access.Can(section, "view"))
            {
                string label = section == "pf" ? "PF" : Py.Title(section);
                return PyError($"You do not have the {label} view right needed for that column.", 403);
            }
        }
        if (scope == "individual" && userId is int one && one != 0)
        {
            if (await staff.MemberAsync(one, Access, ct) is null)
                return PyError("That person is not a member of this company.", 404);
            var visible = await VisibleIdsAsync(ct);
            if (visible is not null && !visible.Contains(one))
                return PyError("Not authorized for this employee", 403);
        }
        var users = await ScopeUsersAsync(scope, userId, department, ct);
        var rows = await report.RowsAsync(users, columns, from, to, Cid, ct);
        return PyJson(new Dictionary<string, object?> { ["results"] = rows });
    }

    [HttpPost("export-excel")]
    [AnyMember]   // admins and managers, checked in the route
    public async Task<IActionResult> ExportExcel(CancellationToken ct)
    {
        if (!IsLead)
            return PyError("Access denied", 403);
        JsonElement data = default;
        bool ok = false;
        if (Request.HasJsonContentType())
        {
            try
            {
                using var doc = await JsonDocument.ParseAsync(Request.Body, cancellationToken: ct);
                data = doc.RootElement.Clone();
                ok = data.ValueKind == JsonValueKind.Object && data.EnumerateObject().Any();
            }
            catch (JsonException) { }
        }
        if (!ok)
            return PyError("No data supplied for export.", 400);
        var columns = data.TryGetProperty("columns", out var cols) && cols.ValueKind == JsonValueKind.Array
            ? cols.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.String ? c.GetString()! : c.GetRawText()).ToList()
            : [];
        if (columns.Count == 0)
            return PyError("No columns to export.", 400);
        string fmt = (data.TryGetProperty("format", out var fv) && Truthy(fv) ? Text(fv) ?? "excel" : "excel").ToLowerInvariant();
        var body = new List<ReportRow>();
        if (data.TryGetProperty("rows", out var rowsEl) && rowsEl.ValueKind == JsonValueKind.Array)
            foreach (var r in rowsEl.EnumerateArray())
                body.Add(new ReportRow(columns.Select(c =>
                    r.ValueKind == JsonValueKind.Object && r.TryGetProperty(c, out var cell) ? CellValue(cell) : "").ToList()));
        string period = "";
        bool hasFrom = data.TryGetProperty("date_from", out var df) && Truthy(df);
        bool hasTo = data.TryGetProperty("date_to", out var dt) && Truthy(dt);
        if (hasFrom || hasTo)
            period = $"{(hasFrom ? PyStr(df) : "start")} to {(hasTo ? PyStr(dt) : "today")}";
        var file = await export.ExportTableAsync(fmt is "excel" or "pdf" or "csv" ? fmt : "excel", new ReportTable
        {
            Title = "HR Report",
            Period = period,
            Filters = [("", $"{body.Count} employees")],
            Columns = columns.Select(c => new ReportColumn(
                ColumnTitles.TryGetValue(c, out string? title) ? title : Py.Title(c.Replace("_", " ")),
                c.EndsWith("_days", StringComparison.Ordinal) || c == "leaves" ? ReportColumnKind.Integer : ReportColumnKind.Text)).ToList(),
            Rows = body,
            FilePeriod = clock.Today.ToString("yyyy-MM-dd", Inv),
        }, ct);
        return File(file.Content, file.ContentType, file.FileName);
    }

    private sealed record AttendanceExportRow(DateOnly Date, int Id, string FullName, bool HasMembership, string? MemberDept, string? HomeDept,
                                              DateTime? ClockIn, DateTime? ClockOut, string? Status, bool? IsLate, bool? IsHalfDay);

    [HttpGet("export-attendance")]
    [AnyMember]   // admins and managers with the Attendance view right, checked in the route
    public async Task<IActionResult> ExportAttendance(CancellationToken ct)
    {
        if (!IsLead)
            return PyError("Access denied", 403);
        if (!Access.Can("attendance", "view"))
            return PyError("You do not have the Attendance view right.", 403);
        var now = clock.Now;
        int month = QueryInt("month", now.Month), year = QueryInt("year", now.Year);
        if (month is < 1 or > 12 || year is < 1 or > 9999)
            return BadRequestPage();
        var visible = await VisibleIdsAsync(ct);
        int cid = Cid;
        var q = from a in db.Set<Attendance>().AsNoTracking()
                join usr in db.Users on a.UserId equals usr.Id
                where a.Date.Month == month && a.Date.Year == year
                select new { a, usr };
        if (visible is not null)
        {
            var ids = visible.Count > 0 ? visible.ToList() : [0];
            q = q.Where(x => ids.Contains(x.a.UserId));
        }
        var records = await q.OrderBy(x => x.a.Date).ThenBy(x => x.a.Id)
            .Select(x => new AttendanceExportRow(x.a.Date, x.a.Id, x.usr.FullName,
                db.Memberships.Any(m => m.CompanyId == cid && m.UserId == x.usr.Id),
                db.Memberships.Where(m => m.CompanyId == cid && m.UserId == x.usr.Id).OrderBy(m => m.Id).Select(m => m.Department).FirstOrDefault(),
                x.usr.Department, x.a.ClockIn, x.a.ClockOut, x.a.Status, x.a.IsLate, x.a.IsHalfDay))
            .ToListAsync(ct);
        var rows = records.Select(r => new ReportRow(
        [
            r.Date, r.FullName, (r.HasMembership ? r.MemberDept : r.HomeDept) is { Length: > 0 } dep ? dep : "",
            r.ClockIn is DateTime ci ? Py.Hm(ci) : "", r.ClockOut is DateTime co ? Py.Hm(co) : "",
            Py.Title((r.Status ?? "").Replace("_", " ")),
            r.IsLate == true ? "Yes" : "No", r.IsHalfDay == true ? "Yes" : "No",
        ])).ToList();
        int late = rows.Count(r => (string)r.Cells[6]! == "Yes");
        var file = await export.ExportTableAsync(Request.Query["format"].FirstOrDefault() ?? "csv", new ReportTable
        {
            Title = "Attendance Report",
            Period = new DateTime(year, month, 1).ToString("MMMM yyyy", Inv),
            Filters = [("", $"{rows.Count} records"), ("", $"{late} late")],
            Columns =
            [
                new("Date", ReportColumnKind.Date), new("Employee"), new("Department"), new("Clock In"), new("Clock Out"),
                new("Status"), new("Late"), new("Half Day"),
            ],
            Rows = rows,
            FilePeriod = $"{year}-{month:00}",
        }, ct);
        return File(file.Content, file.ContentType, file.FileName);
    }

    [HttpGet("export-leaves")]
    [AnyMember]   // admins and managers with the Leaves view right, checked in the route
    public async Task<IActionResult> ExportLeaves(CancellationToken ct)
    {
        if (!IsLead)
            return PyError("Access denied", 403);
        if (!Access.Can("leaves", "view"))
            return PyError("You do not have the Leaves view right.", 403);
        int year = QueryInt("year", clock.Now.Year);
        var visible = await VisibleIdsAsync(ct);
        var q = from l in db.Set<LeaveRequest>().AsNoTracking()
                join usr in db.Users on l.UserId equals usr.Id
                join lt in db.Set<LeaveType>() on l.LeaveTypeId equals lt.Id
                where l.StartDate.Year == year
                select new { l, usr.FullName, TypeName = lt.Name };
        if (visible is not null)
        {
            var ids = visible.Count > 0 ? visible.ToList() : [0];
            q = q.Where(x => ids.Contains(x.l.UserId));
        }
        var records = await q.OrderBy(x => x.l.StartDate).ThenBy(x => x.l.Id)
            .Select(x => new { x.FullName, x.TypeName, x.l.StartDate, x.l.EndDate, x.l.TotalDays, x.l.Status, x.l.Reason })
            .ToListAsync(ct);
        double approved = 0;
        bool anyApproved = false;
        var rows = new List<ReportRow>();
        foreach (var r in records)
        {
            double days = (double)r.TotalDays;           // float(lr.total_days or 0)
            string status = Py.Title(r.Status ?? "");
            if (status == "Approved") { approved += days; anyApproved = true; }
            // A decimal carrying the float's repr, so the CSV reads "1.0" as Python's does.
            decimal cell = decimal.Parse(Py.Float(days), NumberStyles.Float, Inv);
            rows.Add(new ReportRow([r.FullName, r.TypeName, r.StartDate, r.EndDate, cell, status, r.Reason ?? ""]));
        }
        var file = await export.ExportTableAsync(Request.Query["format"].FirstOrDefault() ?? "csv", new ReportTable
        {
            Title = "Leave Report",
            Period = $"Calendar year {year}",
            Filters = [("", $"{rows.Count} requests"), ("", $"{(anyApproved ? Py.G(approved) : "0")} approved days")],
            Columns =
            [
                new("Employee"), new("Type"), new("Start", ReportColumnKind.Date), new("End", ReportColumnKind.Date),
                new("Days", ReportColumnKind.Quantity), new("Status"), new("Reason"),
            ],
            Rows = rows,
            FilePeriod = year.ToString(Inv),
        }, ct);
        return File(file.Content, file.ContentType, file.FileName);
    }

    // ── JSON helpers (Python's view of a JSON value) ────────────────────

    private IActionResult BadRequestPage() => new ContentResult
    {
        StatusCode = 400, ContentType = "text/html; charset=utf-8",
        Content = ErrorAnswersMiddleware.ErrorPage(400, "Bad Request", "The browser (or proxy) sent a request that this server could not understand."),
    };

    /// <summary>bool(value) in Python.</summary>
    private static bool Truthy(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => false,
        JsonValueKind.String => v.GetString()!.Length > 0,
        JsonValueKind.Number => v.GetDouble() != 0,
        JsonValueKind.Array => v.GetArrayLength() > 0,
        JsonValueKind.Object => v.EnumerateObject().Any(),
        _ => true,
    };

    private static string? Text(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Null => null,
        _ => v.GetRawText(),
    };

    /// <summary>str(value) of a JSON value as Python prints it (for the period line).</summary>
    private static string PyStr(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString()!,
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        JsonValueKind.Null => "None",
        JsonValueKind.Number => NumberValue(v) is double d ? Py.Float(d) : v.GetRawText(),
        _ => v.GetRawText(),
    };

    /// <summary>int(value): a whole number, a float truncated, a numeric string; null otherwise.</summary>
    private static int? JsonInt(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.TryGetInt32(out int i) ? i
            : v.TryGetDouble(out double d) && Math.Abs(d) < int.MaxValue ? (int)Math.Truncate(d) : null,
        JsonValueKind.String => Py.Int(v.GetString()),
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        _ => null,
    };

    /// <summary>json.loads' number: an int when written without a fraction or exponent, else a float.</summary>
    private static object NumberValue(JsonElement v)
    {
        string raw = v.GetRawText();
        if (raw.IndexOfAny(['.', 'e', 'E']) < 0 && long.TryParse(raw, NumberStyles.AllowLeadingSign, Inv, out long l))
            return l is >= int.MinValue and <= int.MaxValue ? (int)l : l;
        return v.GetDouble();
    }

    /// <summary>
    /// _val(v): a string that reads as a number once trimmed and stripped of
    /// commas becomes a float (an empty one stays as sent); other values as they came.
    /// </summary>
    private static object? CellValue(JsonElement v)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.String:
            {
                string raw = v.GetString()!;
                string t = raw.Trim().Replace(",", "");
                if (t.Length == 0) return raw;
                return PyFloat(t) is double d ? FloatCell(d) : raw;
            }
            case JsonValueKind.Number:
                return NumberValue(v) is double f ? FloatCell(f) : NumberValue(v);
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
                return null;
            default:
                return v.GetRawText();
        }
    }

    /// <summary>A float cell as a decimal carrying its repr, so the CSV reads "0.0" / "12.5" as Python's does (a number still).</summary>
    private static object FloatCell(double d) =>
        double.IsFinite(d) && decimal.TryParse(Py.Float(d), NumberStyles.Float, Inv, out decimal m) ? m : d;

    [GeneratedRegex(@"\A[+-]?(?:(?:\d(?:_?\d)*)?\.\d(?:_?\d)*|\d(?:_?\d)*\.?)(?:[eE][+-]?\d(?:_?\d)*)?\z")]
    private static partial Regex FloatLiteral();

    /// <summary>float(text) for a stripped string: decimal literals (underscores between digits), inf / infinity / nan.</summary>
    private static double? PyFloat(string t)
    {
        string lower = t.ToLowerInvariant().TrimStart('+');
        bool neg = lower.StartsWith('-');
        string word = neg ? lower[1..] : lower;
        if (word is "inf" or "infinity") return neg ? double.NegativeInfinity : double.PositiveInfinity;
        if (word == "nan") return double.NaN;
        if (!FloatLiteral().IsMatch(t)) return null;
        return double.Parse(t.Replace("_", ""), NumberStyles.Float, Inv);
    }
}
