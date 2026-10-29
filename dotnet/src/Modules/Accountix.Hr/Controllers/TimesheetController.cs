using System.Globalization;
using Accountix.Hr.Services;

namespace Accountix.Hr.Controllers;

/// <summary>Timesheets (hr_app/routes/timesheet.py): one week per person, frozen once submitted.</summary>
[Area("Hr")]
[Module(Modules.Hr)]
[Route("timesheets")]
public sealed class TimesheetController(AccountixDbContext db, CompanyClock clock, People people, Notify notify) : HrController
{
    public sealed record Day(DateOnly Date, IReadOnlyList<TimesheetEntry> Entries, double? Total);

    public sealed class IndexModel
    {
        public required TimesheetWeek Week { get; init; }
        public IReadOnlyList<Day> Days { get; init; } = [];
        public IReadOnlyList<TimesheetWeek> Recent { get; init; } = [];
    }

    private static (DateOnly Start, DateOnly End) WeekRange(DateOnly d)
    {
        var start = d.AddDays(-(((int)d.DayOfWeek + 6) % 7));
        return (start, start.AddDays(6));
    }

    [HttpGet("")]
    [AnyMember]   // self service
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var (start, end) = WeekRange(clock.Today);
        var week = await db.Set<TimesheetWeek>().AsNoTracking().OrderBy(w => w.Id)
            .FirstOrDefaultAsync(w => w.UserId == Me && w.WeekStart == start, ct);
        if (week is null)
        {
            week = new TimesheetWeek
            {
                UserId = Me, WeekStart = start, WeekEnd = end, Status = "unapproved", TotalHours = 0.0, CreatedAt = clock.UtcNow,
            };
            db.Add(week);
            await db.SaveChangesAsync(ct);
        }
        var entries = await db.Set<TimesheetEntry>().AsNoTracking().Where(e => e.WeekId == week.Id)
            .OrderBy(e => e.Day).ThenBy(e => e.Id).ToListAsync(ct);
        var days = Enumerable.Range(0, 7).Select(i =>
        {
            var d = start.AddDays(i);
            var de = entries.Where(e => e.Day == d).ToList();
            return new Day(d, de, de.Count == 0 ? null : de.Sum(e => e.Hours));
        }).ToList();
        var recent = await db.Set<TimesheetWeek>().AsNoTracking().Where(w => w.UserId == Me)
            .OrderByDescending(w => w.WeekStart).Take(12).ToListAsync(ct);
        return View(new IndexModel { Week = week, Days = days, Recent = recent });
    }

    private IActionResult Err(string msg, int status = 400) => JsonStatus(new { error = msg }, status);

    [HttpPost("add-entry")]
    [AnyMember]   // the week's owner, checked below
    public async Task<IActionResult> AddEntry(CancellationToken ct)
    {
        int? weekId = FormInt("week_id");
        if (!DateOnly.TryParseExact(FormStr("day"), ["yyyy-MM-dd", "yyyy-M-d"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return Err("Pick a day");
        string task = FormStr("task").Trim();
        double? hours = FormFloat("hours");
        string description = FormStr("description").Trim();
        if (task.Length == 0 || hours is null or 0) return Err("Task and hours required");
        if (!(hours > 0 && hours <= 24)) return Err("Hours must be between 0 and 24");
        var week = weekId is int wid ? await db.Set<TimesheetWeek>().FirstOrDefaultAsync(w => w.Id == wid, ct) : null;
        if (week is null) return NotFound();
        if (week.UserId != Me) return Err("Not authorized", 403);
        // A submitted or approved week is frozen.
        if (week.Status != "unapproved") return Err($"This week is {week.Status}; it can no longer be changed");
        if (!(week.WeekStart <= day && day <= week.WeekEnd)) return Err("That day is not in this week");
        var entry = new TimesheetEntry
        {
            WeekId = week.Id, Day = day, Project = task, Task = task, Hours = hours.Value, Description = description,
            CreatedAt = clock.UtcNow,
        };
        db.Add(entry);
        week.TotalHours = (week.TotalHours ?? 0) + hours.Value;
        await db.SaveChangesAsync(ct);
        return Json(new Dictionary<string, object?> { ["success"] = true, ["id"] = entry.Id, ["total_hours"] = week.TotalHours });
    }

    [HttpPost("delete-entry/{eid:int}")]
    [AnyMember]   // the week's owner, checked below
    public async Task<IActionResult> DeleteEntry(int eid, CancellationToken ct)
    {
        var entry = await db.Set<TimesheetEntry>().FirstOrDefaultAsync(e => e.Id == eid, ct);
        if (entry is null) return NotFound();
        var week = await db.Set<TimesheetWeek>().FirstAsync(w => w.Id == entry.WeekId, ct);
        if (week.UserId != Me) return Err("Not authorized", 403);
        if (week.Status != "unapproved") return Err($"This week is {week.Status}; it can no longer be changed");
        week.TotalHours = Math.Max(0, (week.TotalHours ?? 0) - entry.Hours);
        db.Remove(entry);
        await db.SaveChangesAsync(ct);
        return Json(new { success = true });
    }

    [HttpPost("submit/{wid:int}")]
    [AnyMember]   // the week's owner, checked below
    public async Task<IActionResult> Submit(int wid, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Locked: a double-clicked Submit queues one approval.
        var week = await db.LockAsync<TimesheetWeek>(wid, ct);
        if (week is null) return NotFound();
        if (week.UserId != Me) return Err("Not authorized", 403);
        if (week.Status != "unapproved")
        {
            this.Flash($"This week is already {week.Status}.", "danger");
            return Redirect("/timesheets/");
        }
        if (!await db.Set<TimesheetEntry>().AnyAsync(e => e.WeekId == wid, ct))
        {
            this.Flash("Add at least one entry before submitting.", "danger");
            return Redirect("/timesheets/");
        }
        var me = await db.Users.AsNoTracking().FirstAsync(u => u.Id == Me, ct);
        int? approver = await people.ApproverForAsync(me, Cid, null, ct);
        var now = clock.UtcNow;
        week.SubmittedAt = now;
        if (approver is null)
        {
            // Nobody else to sign it off (the company's only admin).
            week.Status = "approved";
            week.ApprovedAt = now;
            week.ApprovedBy = Me;
        }
        else
        {
            week.Status = "submitted";
            db.Add(new TimesheetApproval { WeekId = wid, ApproverId = approver.Value, Status = "pending", CreatedAt = now });
            await notify.ToAsync(approver.Value, "Timesheet Submitted", $"{me.FullName} submitted timesheet", "info", "timesheets", wid, Me, now, ct);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash("Timesheet submitted for approval.", "success");
        return Redirect("/timesheets/");
    }

    public sealed record ApprovalRow(TimesheetApproval Approval, TimesheetWeek Week, string Employee);

    internal static Task<List<ApprovalRow>> PendingAsync(AccountixDbContext db, int approverId, CancellationToken ct) =>
        (from a in db.Set<TimesheetApproval>().AsNoTracking()
         where a.ApproverId == approverId && a.Status == "pending"
         join w in db.Set<TimesheetWeek>() on a.WeekId equals w.Id
         join u in db.Users on w.UserId equals u.Id
         orderby a.Id
         select new ApprovalRow(a, w, u.FullName)).ToListAsync(ct);

    [HttpGet("approvals")]
    [AnyMember]   // admins and managers
    public async Task<IActionResult> Approvals(CancellationToken ct)
    {
        if (!IsLead) return AccessDenied();
        return View(await PendingAsync(db, Me, ct));
    }

    /// <summary>Approve one timesheet approval (locked, re-checked): the week is approved too.</summary>
    internal static void ApproveWeek(TimesheetApproval approval, TimesheetWeek week, int by, DateTime utcNow)
    {
        approval.Status = "approved";
        week.Status = "approved";
        week.ApprovedAt = utcNow;
        week.ApprovedBy = by;
    }

    [HttpPost("review/{aid:int}")]
    [AnyMember]   // the approval's own approver, checked below
    public async Task<IActionResult> Review(int aid, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var approval = await db.LockAsync<TimesheetApproval>(aid, ct);
        if (approval is null) return NotFound();
        if (approval.ApproverId != Me) return Err("Not authorized", 403);
        string? action = Request.Form["action"].FirstOrDefault();
        string comment = FormStr("comment");
        if (approval.Status != "pending")
        {
            this.Flash("This timesheet has already been decided.", "danger");
            return Redirect("/timesheets/approvals");
        }
        var week = await db.Set<TimesheetWeek>().FirstAsync(w => w.Id == approval.WeekId, ct);
        if (action == "approve")
            ApproveWeek(approval, week, Me, clock.UtcNow);
        else
        {
            if (comment.Length == 0)
            {
                this.Flash("Please provide a reason for rejection.", "danger");
                return Redirect("/timesheets/approvals");
            }
            approval.Status = "rejected";
            week.Status = "unapproved";
        }
        approval.Comment = comment;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"Timesheet {Py.Decided(action)}.", "success");
        return Redirect("/timesheets/approvals");
    }

    public sealed class ProjectsModel
    {
        public IReadOnlyList<Project> Projects { get; init; } = [];
        public IReadOnlyDictionary<int, string> Managers { get; init; } = new Dictionary<int, string>();
        public IReadOnlyDictionary<int, int> Packages { get; init; } = new Dictionary<int, int>();
    }

    [HttpGet("projects")]
    [AnyMember]   // admins only, checked below
    public async Task<IActionResult> Projects(CancellationToken ct)
    {
        if (!IsAdmin) return AccessDenied();
        var projects = await db.Set<Project>().AsNoTracking().OrderByDescending(p => p.CreatedAt).ToListAsync(ct);
        var pmIds = projects.Where(p => p.ProjectManagerId != null).Select(p => p.ProjectManagerId!.Value).Distinct().ToList();
        var managers = await db.Users.AsNoTracking().Where(u => pmIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        var packages = await db.Set<WorkPackage>().GroupBy(w => w.ProjectId).Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N, ct);
        ViewData["BackUrl"] = "/timesheets/";
        return View(new ProjectsModel { Projects = projects, Managers = managers, Packages = packages });
    }

    [HttpPost("projects/create")]
    [AnyMember]   // admins only, checked below
    public async Task<IActionResult> CreateProject(CancellationToken ct)
    {
        if (!IsAdmin) return JsonStatus(new { error = "Access denied" }, 403);
        var f = Request.Form;
        if (!f.ContainsKey("name")) return BadRequest();
        DateOnly? start = null;
        if (!string.IsNullOrEmpty(f["start_date"].FirstOrDefault()))
            start = FormInput.Date(f, "start_date", label: "Start date");
        db.Add(new Project
        {
            Name = f["name"].FirstOrDefault() ?? "", Code = f["code"].FirstOrDefault() ?? "",
            Description = f["description"].FirstOrDefault() ?? "", Department = f["department"].FirstOrDefault() ?? "",
            ProjectManagerId = FormInt("pm_id"), StartDate = start, Status = f["status"].FirstOrDefault() ?? "active",
            IsBillable = true, CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        this.Flash("Project created.", "success");
        return Redirect("/timesheets/projects");
    }

    [HttpPost("merge-report")]
    [AnyMember]   // admins only, checked below
    public async Task<IActionResult> MergeReport(CancellationToken ct)
    {
        if (!IsAdmin) return JsonStatus(new { error = "Access denied" }, 403);
        var inv = CultureInfo.InvariantCulture;
        if (!DateOnly.TryParseExact(FormStr("period_start"), ["yyyy-MM-dd", "yyyy-M-d"], inv, DateTimeStyles.None, out var pFrom)
            || !DateOnly.TryParseExact(FormStr("period_end"), ["yyyy-MM-dd", "yyyy-M-d"], inv, DateTimeStyles.None, out var pTo))
            return Err("Pick a period");
        var users = await (await people.EmployeesAsync(Cid, ct)).Where(m => m.User.IsActive == true)
            .OrderBy(m => m.User.Id).Select(m => new { m.User.Id, m.User.FullName }).ToListAsync(ct);
        var ids = users.Select(u => u.Id).ToList();
        // Approved hours per person in the period: one grouped query.
        var totals = await (from e in db.Set<TimesheetEntry>()
                            join w in db.Set<TimesheetWeek>() on e.WeekId equals w.Id
                            where ids.Contains(w.UserId) && w.Status == "approved" && e.Day >= pFrom && e.Day <= pTo
                            group e by w.UserId into g
                            select new { UserId = g.Key, Hours = g.Sum(x => x.Hours) }).ToDictionaryAsync(x => x.UserId, x => x.Hours, ct);
        var results = new List<Dictionary<string, object>>();
        foreach (var u in users)
        {
            double total = totals.GetValueOrDefault(u.Id);
            if (total > 0)
            {
                double rounded = Py.R(total, 2);
                results.Add(new Dictionary<string, object> { ["user"] = u.FullName, ["total_hours"] = rounded });
                db.Add(new TimesheetMergedReport
                {
                    UserId = u.Id, PeriodStart = pFrom, PeriodEnd = pTo, TotalHours = rounded, OvertimeHours = 0.0, GeneratedAt = clock.UtcNow,
                });
            }
        }
        await db.SaveChangesAsync(ct);
        return Json(new { results });
    }
}
