using System.Globalization;
using Accountix.Hr.Services;

namespace Accountix.Hr.Controllers;

/// <summary>Leave (hr_app/routes/leave.py + leave_flow.py).</summary>
[Area("Hr")]
[Module(Modules.Hr)]
[Route("leaves")]
public sealed class LeaveController(AccountixDbContext db, CompanyClock clock, LeaveFlow flow, People people, Notify notify)
    : HrController
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public sealed class IndexModel
    {
        public IReadOnlyList<(LeaveQuota Quota, LeaveType Type)> Quotas { get; init; } = [];
        public IReadOnlyList<(LeaveRequest Request, string TypeName)> Requests { get; init; } = [];
    }

    [HttpGet("")]
    [AnyMember]   // self service
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        int year = DateTime.Today.Year;       // Python: date.today()
        var types = await db.Set<LeaveType>().OrderBy(t => t.Id).ToListAsync(ct);
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            foreach (var lt in types)
                await flow.RefreshAsync(await flow.QuotaForAsync(Me, lt, year, ct), ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        var byId = types.ToDictionary(t => t.Id);
        var quotas = await db.Set<LeaveQuota>().AsNoTracking().Where(q => q.UserId == Me && q.Year == year)
            .OrderBy(q => q.Id).ToListAsync(ct);
        var requests = await db.Set<LeaveRequest>().AsNoTracking().Where(r => r.UserId == Me)
            .OrderByDescending(r => r.CreatedAt).ToListAsync(ct);
        return View(new IndexModel
        {
            Quotas = quotas.Where(q => byId.ContainsKey(q.LeaveTypeId)).Select(q => (q, byId[q.LeaveTypeId])).ToList(),
            Requests = requests.Select(r => (r, byId.TryGetValue(r.LeaveTypeId, out var t) ? t.Name : "")).ToList(),
        });
    }

    private static (DateOnly First, DateOnly Last) MonthBounds(int year, int month)
    {
        var first = new DateOnly(year, month, 1);
        return (first, first.AddMonths(1).AddDays(-1));
    }

    /// <summary>Flask's JSON for a date object: an HTTP date ("Mon, 17 Nov 2026 00:00:00 GMT").</summary>
    private static string HttpDate(DateOnly d) => d.ToDateTime(TimeOnly.MinValue).ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", Inv);

    [HttpGet("calendar")]
    [AnyMember]
    public async Task<IActionResult> Calendar(CancellationToken ct)
    {
        int month = QueryInt("month", DateTime.Now.Month), year = QueryInt("year", DateTime.Now.Year);
        var (first, last) = MonthBounds(year, month);
        var leaves = await (from lr in db.Set<LeaveRequest>().AsNoTracking()
                            where lr.Status == "approved" && lr.StartDate <= last && lr.EndDate >= first
                            join u in db.Users on lr.UserId equals u.Id
                            join t in db.Set<LeaveType>() on lr.LeaveTypeId equals t.Id
                            orderby lr.Id
                            select new { lr.UserId, lr.StartDate, u.FullName, Type = t.Name }).ToListAsync(ct);
        var holidays = await db.Set<CompanyHoliday>().AsNoTracking()
            .Where(h => h.HolidayDate >= first && h.HolidayDate <= last && h.IsActive == true).OrderBy(h => h.Id).ToListAsync(ct);
        var entries = new List<Dictionary<string, object?>>();
        if (IsLead)
            entries.AddRange(leaves.Select(l => new Dictionary<string, object?>
            {
                ["date"] = HttpDate(l.StartDate), ["title"] = $"{l.FullName} - {l.Type}", ["type"] = "leave",
            }));
        else
        {
            var team = new HashSet<int> { Me };
            if (MyManagerId is int mgr)
                team.UnionWith(await db.Memberships.Where(m => m.CompanyId == Cid && m.Status == CompanyMembership.StatusActive
                                                              && m.ManagerId == mgr).Select(m => m.UserId).ToListAsync(ct));
            entries.AddRange(leaves.Where(l => team.Contains(l.UserId)).Select(l => new Dictionary<string, object?>
            {
                ["date"] = HttpDate(l.StartDate), ["title"] = $"{l.FullName} - Out of Office", ["type"] = "leave_anon",
            }));
        }
        entries.AddRange(holidays.Select(h => new Dictionary<string, object?>
        {
            ["date"] = HttpDate(h.HolidayDate), ["title"] = $"&#127775; {h.Name}", ["type"] = "holiday",
        }));
        return Json(new Dictionary<string, object?> { ["entries"] = entries, ["month"] = month, ["year"] = year });
    }

    [HttpGet("team-calendar")]
    [AnyMember]
    public async Task<IActionResult> TeamCalendar(CancellationToken ct)
    {
        int month = QueryInt("month", DateTime.Now.Month), year = QueryInt("year", DateTime.Now.Year);
        var (first, last) = MonthBounds(year, month);
        var approved = await (from lr in db.Set<LeaveRequest>().AsNoTracking()
                              where lr.Status == "approved" && lr.StartDate <= last && lr.EndDate >= first
                              join u in db.Users on lr.UserId equals u.Id
                              join t in db.Set<LeaveType>() on lr.LeaveTypeId equals t.Id
                              orderby lr.Id
                              select new { lr.Id, lr.UserId, lr.StartDate, lr.EndDate, u.FullName, Type = t.Name }).ToListAsync(ct);
        var holidays = await db.Set<CompanyHoliday>().AsNoTracking()
            .Where(h => h.HolidayDate >= first && h.HolidayDate <= last && h.IsActive == true).OrderBy(h => h.Id).ToListAsync(ct);
        var entries = approved.Select(l => new Dictionary<string, object?>
        {
            ["id"] = l.Id, ["user"] = l.FullName, ["user_id"] = l.UserId,
            ["date"] = Py.Date(l.StartDate), ["end_date"] = Py.Date(l.EndDate), ["type"] = l.Type,
            ["label"] = IsLead ? $"{l.FullName} - {l.Type}" : $"{l.FullName} - Out of Office",
        }).ToList();
        entries.AddRange(holidays.Select(h => new Dictionary<string, object?>
        {
            ["id"] = -h.Id, ["title"] = h.Name, ["date"] = Py.Date(h.HolidayDate), ["type"] = "holiday", ["label"] = h.Name,
        }));
        return Json(entries);
    }

    [HttpGet("apply")]
    [AnyMember]
    public async Task<IActionResult> Apply(CancellationToken ct) => await ApplyPage(ct);

    private async Task<IActionResult> ApplyPage(CancellationToken ct)
    {
        ViewData["BackUrl"] = "/leaves/";
        return View("Apply", await db.Set<LeaveType>().AsNoTracking().OrderBy(t => t.Id).ToListAsync(ct));
    }

    private static DateOnly? StrictDate(string? s) =>
        DateOnly.TryParseExact(s ?? "", "yyyy-MM-dd", Inv, DateTimeStyles.None, out var d) ? d
        : DateOnly.TryParseExact(s ?? "", "yyyy-M-d", Inv, DateTimeStyles.None, out d) ? d : null;

    [HttpPost("apply")]
    [AnyMember]
    public async Task<IActionResult> ApplyPost(CancellationToken ct)
    {
        async Task<IActionResult> Again(string msg)
        {
            this.Flash(msg, "danger");
            return await ApplyPage(ct);
        }
        int? typeId = FormInt("leave_type");
        var start = StrictDate(Request.Form["start_date"].FirstOrDefault());
        var end = StrictDate(Request.Form["end_date"].FirstOrDefault());
        if (start is null || end is null) return await Again("Pick a start and an end date.");
        string reason = FormStr("reason").Trim();
        bool half = Request.Form["is_half_day"].FirstOrDefault() == "on";
        if (start > end) return await Again("Start date must be before end date.");
        if (half && start != end) return await Again("A half day starts and ends on the same date.");
        var lt = typeId is int tid ? await db.Set<LeaveType>().FirstOrDefaultAsync(t => t.Id == tid, ct) : null;
        if (lt is null) return await Again("Invalid leave type.");
        int working = await flow.WorkingDaysAsync(start.Value, end.Value, ct);
        decimal totalDays = half ? (working > 0 ? 0.5m : 0m) : working;
        if (totalDays < 0.5m) return await Again("No working days in selected range (weekends/holidays excluded).");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // A double-clicked Apply files one request: the second waits here, then finds the first.
        await db.SerializeAsync("leave", Me);
        var clash = await flow.OverlappingAsync(Me, start.Value, end.Value, ct);
        if (clash is not null)
            return await Again($"You already have a {clash.Status} request for " +
                               $"{clash.StartDate.ToString("dd MMM", Inv)} – {clash.EndDate.ToString("dd MMM yyyy", Inv)}.");
        var quota = await flow.QuotaForAsync(Me, lt, start.Value.Year, ct);
        await flow.RefreshAsync(quota, ct);
        if ((quota.Used ?? 0) + (quota.Pending ?? 0) + totalDays > (quota.Total ?? 0))
            return await Again($"Insufficient leave balance: {Py.G(quota.Remaining)} day(s) of {lt.Name} left for {start.Value.Year}.");
        var now = clock.UtcNow;
        var lr = new LeaveRequest
        {
            UserId = Me, LeaveTypeId = lt.Id, StartDate = start.Value, EndDate = end.Value, TotalDays = totalDays,
            Reason = reason, IsHalfDay = half, Status = "pending", CreatedAt = now, UpdatedAt = now,
        };
        db.Add(lr);
        await db.SaveChangesAsync(ct);
        var workflow = await db.Set<ApprovalWorkflow>().OrderBy(w => w.Id)
            .FirstOrDefaultAsync(w => w.LeaveTypeId == lt.Id && w.IsActive == true, ct);
        var me = await db.Users.AsNoTracking().FirstAsync(u => u.Id == Me, ct);
        int? approver = await people.ApproverForAsync(me, Cid, ("leaves", Rights.Approve), ct);
        if (workflow?.AutoApprove == true || approver is null)
            lr.Status = "approved";       // auto-approved type, or nobody else to decide (the only admin)
        else
        {
            db.Add(new LeaveApproval { LeaveRequestId = lr.Id, ApproverId = approver.Value, Level = 1, Status = "pending", CreatedAt = now });
            await notify.ToAsync(approver.Value, "Leave Request", $"{me.FullName} requests {Py.G(totalDays)} day(s) of {lt.Name}",
                                 "info", "leaves", null, Me, now, ct);
        }
        await flow.RefreshAsync(quota, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash(lr.Status == "approved" ? "Leave approved." : "Leave request submitted.", "success");
        return Redirect("/leaves/");
    }

    public sealed record ApprovalRow(LeaveApproval Approval, LeaveRequest Request, string Employee, string Type);

    /// <summary>Pending approvals of one approver with their requests, people and types, in one query.</summary>
    internal static Task<List<ApprovalRow>> PendingLeaveApprovalsAsync(AccountixDbContext db, int approverId, CancellationToken ct) =>
        (from a in db.Set<LeaveApproval>().AsNoTracking()
         where a.ApproverId == approverId && a.Status == "pending"
         join lr in db.Set<LeaveRequest>() on a.LeaveRequestId equals lr.Id
         join u in db.Users on lr.UserId equals u.Id
         join t in db.Set<LeaveType>() on lr.LeaveTypeId equals t.Id
         orderby a.Id
         select new ApprovalRow(a, lr, u.FullName, t.Name)).ToListAsync(ct);

    [HttpGet("approvals")]
    [AnyMember]   // admins and managers
    public async Task<IActionResult> Approvals(CancellationToken ct)
    {
        if (!IsLead) return AccessDenied();
        return View(await PendingLeaveApprovalsAsync(db, Me, ct));
    }

    [HttpPost("review/{aid:int}")]
    [AnyMember]   // the request's own approver, checked by the decision
    public async Task<IActionResult> Review(int aid, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var approval = await db.Set<LeaveApproval>().FirstOrDefaultAsync(a => a.Id == aid, ct);
        if (approval is null) return NotFound();
        string? action = Request.Form["action"].FirstOrDefault();
        string comment = FormStr("comment");
        string? err = await flow.DecideAsync(approval, action, comment, Me, ct);
        if (err is not null)
        {
            await tx.RollbackAsync(ct);
            this.Flash(err, "danger");
            return Redirect("/leaves/approvals");
        }
        await NotifyDecisionAsync(approval, action!, comment, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"Leave request {Py.Decided(action)}.", "success");
        return Redirect("/leaves/approvals");
    }

    private async Task NotifyDecisionAsync(LeaveApproval approval, string action, string comment, CancellationToken ct)
    {
        var lr = await db.Set<LeaveRequest>().FirstAsync(r => r.Id == approval.LeaveRequestId, ct);
        string type = await db.Set<LeaveType>().Where(t => t.Id == lr.LeaveTypeId).Select(t => t.Name).FirstAsync(ct);
        if (action == "reject")
            await notify.ToAsync(lr.UserId, "Leave Rejected",
                $"Your {type} request has been rejected. Reason: {comment.Trim()}", "danger", "leaves", lr.Id, Me, clock.UtcNow, ct);
        else if (lr.Status == "approved")
            await notify.ToAsync(lr.UserId, "Leave Approved",
                $"Your {type} request ({Py.G(lr.TotalDays)} day(s)) has been approved.", "success", "leaves", lr.Id, Me, clock.UtcNow, ct);
    }

    [HttpGet("holidays")]
    [AnyMember]   // admins only, checked below
    public async Task<IActionResult> Holidays(CancellationToken ct)
    {
        if (!IsAdmin) return AccessDenied();
        ViewData["BackUrl"] = "/leaves/";
        return View(await db.Set<CompanyHoliday>().AsNoTracking().OrderByDescending(h => h.HolidayDate).ToListAsync(ct));
    }

    [HttpPost("holidays")]
    [AnyMember]   // admins only, checked below
    public async Task<IActionResult> AddHoliday(CancellationToken ct)
    {
        if (!IsAdmin) return AccessDenied();
        var f = Request.Form;
        if (!f.ContainsKey("name") || !f.ContainsKey("holiday_date")) return BadRequest();
        var day = StrictDate(f["holiday_date"].FirstOrDefault()) ?? throw new FormInputException("Date is not a valid date.");
        db.Add(new CompanyHoliday
        {
            Name = f["name"].FirstOrDefault() ?? "", HolidayDate = day,
            IsRecurring = f["is_recurring"].FirstOrDefault() == "on", Department = f["department"].FirstOrDefault() ?? "",
            IsActive = true, CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        this.Flash("Holiday added.", "success");
        return Redirect("/leaves/holidays");
    }

    public sealed class WorkflowsModel
    {
        public IReadOnlyList<ApprovalWorkflow> Workflows { get; init; } = [];
        public IReadOnlyList<LeaveType> Types { get; init; } = [];
    }

    [HttpGet("workflows")]
    [AnyMember]   // admins only, checked below
    public async Task<IActionResult> Workflows(CancellationToken ct)
    {
        if (!IsAdmin) return AccessDenied();
        ViewData["BackUrl"] = "/leaves/";
        return View(new WorkflowsModel
        {
            Workflows = await db.Set<ApprovalWorkflow>().AsNoTracking().OrderBy(w => w.Id).ToListAsync(ct),
            Types = await db.Set<LeaveType>().AsNoTracking().OrderBy(t => t.Id).ToListAsync(ct),
        });
    }

    [HttpPost("workflows")]
    [AnyMember]   // admins only, checked below
    public async Task<IActionResult> AddWorkflow(CancellationToken ct)
    {
        if (!IsAdmin) return AccessDenied();
        var f = Request.Form;
        if (!f.ContainsKey("name")) return BadRequest();
        db.Add(new ApprovalWorkflow
        {
            Name = f["name"].FirstOrDefault() ?? "", Module = "leaves", LeaveTypeId = FormInt("leave_type_id"),
            ApprovalLevels = FormInput.Int(f, "approval_levels", 1),
            AutoApprove = f["auto_approve"].FirstOrDefault() == "on",
            NotifyAdmins = f["notify_admins"].FirstOrDefault() == "on",
            RequiresApproval = true, IsActive = true, CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        this.Flash("Approval workflow created.", "success");
        return Redirect("/leaves/workflows");
    }
}
