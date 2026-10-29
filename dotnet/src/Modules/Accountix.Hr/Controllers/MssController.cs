using System.Text.Json;
using Accountix.Hr.Services;

namespace Accountix.Hr.Controllers;

/// <summary>
/// Manager self-service (hr_app/routes/mss.py): the team, the approvals inbox
/// (leave, timesheets, loans) and salary revisions. Loan approval here only
/// decides the request (and opens its ledger account); paying it out and
/// recovering it is Payroll's.
/// </summary>
[Area("Hr")]
[Module(Modules.Hr)]
[Route("mss")]
public sealed class MssController(AccountixDbContext db, CompanyClock clock, People people, LeaveFlow flow,
                                  Notify notify, ILedger ledger) : HrController
{
    /// <summary>The team a lead sees: an admin everyone (but themself), a manager their direct reports.</summary>
    private async Task<List<Member>> TeamAsync(CancellationToken ct) => IsAdmin
        ? await (await people.EmployeesAsync(Cid, ct)).Where(m => m.User.IsActive == true && m.User.Id != Me)
            .OrderBy(m => m.User.FullName).AsNoTracking().ToListAsync(ct)
        : await people.ReportsOf(Me, Cid).AsNoTracking().ToListAsync(ct);

    public sealed class IndexModel
    {
        public IReadOnlyList<Member> Reports { get; init; } = [];
        public int PendingLeaves { get; init; }
        public int PendingTimesheets { get; init; }
        public int PendingLoans { get; init; }
    }

    [HttpGet("")]
    [AnyMember]   // managers and admins
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        if (!IsLead) return AccessDenied();
        var reports = await TeamAsync(ct);
        var ids = reports.Select(r => r.Id).ToList();
        return View(new IndexModel
        {
            Reports = reports,
            PendingLeaves = await db.Set<LeaveRequest>().CountAsync(r => ids.Contains(r.UserId) && r.Status == "pending", ct),
            PendingTimesheets = await db.Set<TimesheetApproval>().CountAsync(a => a.ApproverId == Me && a.Status == "pending", ct),
            PendingLoans = await db.Set<LoanAdvanceRequest>().CountAsync(l => ids.Contains(l.UserId) && l.Status == "pending", ct),
        });
    }

    public sealed record LoanRow(LoanAdvanceRequest Loan, string Employee);

    public sealed class ApprovalsModel
    {
        public IReadOnlyList<LeaveController.ApprovalRow> Leaves { get; init; } = [];
        public IReadOnlyList<TimesheetController.ApprovalRow> Timesheets { get; init; } = [];
        public IReadOnlyList<LoanRow> Loans { get; init; } = [];
    }

    [HttpGet("approvals")]
    [AnyMember]   // managers and admins
    public async Task<IActionResult> Approvals(CancellationToken ct)
    {
        if (!IsLead) return AccessDenied();
        var mine = people.ReportsOf(Me, Cid).Select(m => m.User.Id);
        var loans = await (from l in db.Set<LoanAdvanceRequest>().AsNoTracking()
                           where mine.Contains(l.UserId) && l.Status == "pending"
                           join u in db.Users on l.UserId equals u.Id
                           orderby l.Id
                           select new LoanRow(l, u.FullName)).ToListAsync(ct);
        ViewData["BackUrl"] = "/mss/";
        return View(new ApprovalsModel
        {
            Leaves = await LeaveController.PendingLeaveApprovalsAsync(db, Me, ct),
            Timesheets = await TimesheetController.PendingAsync(db, Me, ct),
            Loans = loans,
        });
    }

    /// <summary>_answer: JSON for scripts, a flash and back to the inbox for the page's plain forms.</summary>
    private IActionResult Answer(bool ok, string msg, int status = 200)
    {
        if (AsksJson())
            return ok ? JsonStatus(new { success = true }, status) : JsonStatus(new { error = msg }, status);
        this.Flash(msg, ok ? "success" : "danger");
        return Redirect("/mss/approvals");
    }

    [HttpPost("approve-leave/{aid:int}")]
    [AnyMember]   // the request's own approver, checked by the decision
    public async Task<IActionResult> ApproveLeave(int aid, CancellationToken ct)
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
            return Answer(false, err, approval.ApproverId != Me ? 403 : 400);
        }
        var lr = await db.Set<LeaveRequest>().FirstAsync(r => r.Id == approval.LeaveRequestId, ct);
        string type = await db.Set<LeaveType>().Where(t => t.Id == lr.LeaveTypeId).Select(t => t.Name).FirstAsync(ct);
        if (action == "reject")
            await notify.ToAsync(lr.UserId, "Leave Rejected", $"Your {type} was rejected: {comment.Trim()}", "danger", "mss", null, Me, clock.UtcNow, ct);
        else if (lr.Status == "approved")
            await notify.ToAsync(lr.UserId, "Leave Approved", $"Your {type} has been approved.", "success", "mss", null, Me, clock.UtcNow, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Answer(true, $"Leave request {Py.Decided(action)}.");
    }

    [HttpPost("approve-timesheet/{aid:int}")]
    [AnyMember]   // the approval's own approver, checked below
    public async Task<IActionResult> ApproveTimesheet(int aid, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var approval = await db.LockAsync<TimesheetApproval>(aid, ct);
        if (approval is null) return NotFound();
        if (approval.ApproverId != Me) return Answer(false, "Not authorized", 403);
        string? action = Request.Form["action"].FirstOrDefault();
        string comment = FormStr("comment");
        if (approval.Status != "pending") return Answer(false, "This timesheet has already been decided.", 400);
        if (action != "approve" && comment.Trim().Length == 0) return Answer(false, "Reason required for rejection", 400);
        var week = await db.Set<TimesheetWeek>().FirstAsync(w => w.Id == approval.WeekId, ct);
        if (action == "approve")
            TimesheetController.ApproveWeek(approval, week, Me, clock.UtcNow);
        else
        {
            approval.Status = "rejected";
            week.Status = "unapproved";
        }
        approval.Comment = comment;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Answer(true, $"Timesheet {Py.Decided(action)}.");
    }

    [HttpPost("approve-loan/{lid:int}")]
    [AnyMember]   // an admin, or a manager here for their own report with the Loans approve right
    public async Task<IActionResult> ApproveLoan(int lid, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var loan = await db.LockAsync<LoanAdvanceRequest>(lid, ct);
        if (loan is null) return NotFound();
        if (!IsAdmin)
        {
            if (!IsManager || !await people.ReportsOf(Me, Cid).AnyAsync(m => m.User.Id == loan.UserId, ct))
                return Answer(false, "Not authorized", 403);
            if (!Access.Can("loans", Rights.Approve))
                return Answer(false, "You do not have the Loans approve right. Ask an admin in Settings.", 403);
        }
        string? action = Request.Form["action"].FirstOrDefault();
        if (action is not ("approve" or "reject")) return Answer(false, "Unknown action.", 400);
        // Re-deciding re-opened paid or rejected loans for payroll deductions.
        if (loan.Status != "pending") return Answer(false, "This loan request has already been decided.", 400);
        if (action == "approve")
        {
            loan.Status = "approved";
            loan.ApprovedBy = Me;
            loan.ApprovedAt = clock.UtcNow;
            // Loans created before ledger integration get their account on approval.
            string borrower = await db.Users.Where(u => u.Id == loan.UserId).Select(u => u.FullName).FirstAsync(ct);
            await ledger.EntityAccountAsync("loan", loan.Id, $"{Py.Title(loan.RequestType)} #{loan.Id} - {borrower}", ct);
            loan.QueueForPayroll = true;
            await notify.ToAsync(loan.UserId, "Loan Approved", $"Your loan of Rs.{Py.Comma0(loan.Amount)} has been approved.",
                                 "success", "mss", null, Me, clock.UtcNow, ct);
        }
        else
        {
            loan.Status = "rejected";
            await notify.ToAsync(loan.UserId, "Loan Rejected", "Your loan request was rejected.", "danger", "mss", null, Me, clock.UtcNow, ct);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Answer(true, $"Loan request {Py.Decided(action)}.");
    }

    [HttpPost("bulk-approve-timesheets")]
    [AnyMember]   // each approval's own approver, checked below
    public async Task<IActionResult> BulkApproveTimesheets(CancellationToken ct)
    {
        var ids = new SortedSet<int>();
        if (Request.HasJsonContentType())
        {
            try
            {
                using var doc = await JsonDocument.ParseAsync(Request.Body, cancellationToken: ct);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("ids", out var arr)
                    && arr.ValueKind == JsonValueKind.Array)
                    foreach (var x in arr.EnumerateArray())
                    {
                        // int(t) for t in ids if str(t).isdigit()
                        string s = x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ValueKind == JsonValueKind.Number ? x.GetRawText() : "";
                        if (s.Length > 0 && s.All(char.IsAsciiDigit) && int.TryParse(s, out int v)) ids.Add(v);
                    }
            }
            catch (JsonException) { }
        }
        int done = 0;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (int tid in ids)
        {
            var approval = await db.LockAsync<TimesheetApproval>(tid, ct);
            if (approval is { Status: "pending" } && approval.ApproverId == Me)
            {
                var week = await db.Set<TimesheetWeek>().FirstAsync(w => w.Id == approval.WeekId, ct);
                TimesheetController.ApproveWeek(approval, week, Me, clock.UtcNow);
                done++;
            }
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Json(new { success = true, count = done });
    }

    [HttpGet("team")]
    [AnyMember]   // managers and admins
    public async Task<IActionResult> Team(CancellationToken ct)
    {
        if (!IsLead) return AccessDenied();
        ViewData["BackUrl"] = "/mss/";
        return View(await TeamAsync(ct));
    }

    [HttpGet("team-availability")]
    [AnyMember]   // managers and admins
    public async Task<IActionResult> TeamAvailability(CancellationToken ct)
    {
        if (!IsLead)
        {
            this.Flash("Access denied.", "danger");
            return JsonStatus(new { error = "Access denied" }, 403);
        }
        var today = DateOnly.FromDateTime(DateTime.Today);    // Python: date.today()
        var reports = await people.ReportsOf(Me, Cid).AsNoTracking().ToListAsync(ct);
        var ids = reports.Select(r => r.Id).ToList();
        var atts = (await db.Set<Attendance>().AsNoTracking().Where(a => ids.Contains(a.UserId) && a.Date == today)
            .OrderBy(a => a.Id).ToListAsync(ct)).GroupBy(a => a.UserId).ToDictionary(g => g.Key, g => g.First());
        var onLeave = (await db.Set<LeaveRequest>().Where(l => ids.Contains(l.UserId) && l.Status == "approved"
                                                               && l.StartDate <= today && l.EndDate >= today)
            .Select(l => l.UserId).ToListAsync(ct)).ToHashSet();
        return Json(reports.Select(u =>
        {
            atts.TryGetValue(u.Id, out var att);
            string s = att is { ClockIn: not null, ClockOut: null } ? "on_duty"
                     : att is { ClockOut: not null } ? "completed"
                     : onLeave.Contains(u.Id) ? "on_leave" : "absent";
            return new Dictionary<string, object?> { ["id"] = u.Id, ["name"] = u.FullName, ["designation"] = u.Designation, ["status"] = s };
        }).ToList());
    }

    [HttpGet("team-calendar")]
    [AnyMember]   // managers and admins
    public async Task<IActionResult> TeamCalendar(CancellationToken ct)
    {
        if (!IsLead)
        {
            this.Flash("Access denied.", "danger");
            return JsonStatus(new { error = "Access denied" }, 403);
        }
        int month = QueryInt("month", DateTime.Now.Month), year = QueryInt("year", DateTime.Now.Year);
        var first = new DateOnly(year, month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        var reportIds = people.ReportsOf(Me, Cid).Select(m => m.User.Id);
        var leaves = await (from lr in db.Set<LeaveRequest>().AsNoTracking()
                            where reportIds.Contains(lr.UserId) && lr.Status == "approved" && lr.StartDate <= last && lr.EndDate >= first
                            join u in db.Users on lr.UserId equals u.Id
                            join t in db.Set<LeaveType>() on lr.LeaveTypeId equals t.Id
                            orderby lr.Id
                            select new { lr.Id, u.FullName, Type = t.Name, lr.StartDate, lr.EndDate }).ToListAsync(ct);
        return Json(leaves.Select(l => new Dictionary<string, object?>
        {
            ["id"] = l.Id, ["user"] = l.FullName, ["type"] = l.Type, ["start"] = Py.Date(l.StartDate), ["end"] = Py.Date(l.EndDate),
        }).ToList());
    }

    public sealed class EvaluateModel
    {
        public required User Employee { get; init; }
        public string? Designation { get; init; }
        public string? Department { get; init; }
        public PayrollProfile? Profile { get; init; }
        public DateOnly Today { get; init; }
    }

    [HttpGet("evaluate/{uid:int}")]
    [AnyMember]   // managers for their own reports, admins for anyone
    public async Task<IActionResult> Evaluate(int uid, CancellationToken ct)
    {
        if (!IsLead) return AccessDenied();
        var emp = await people.GetMemberAsync(uid, Access, ct);
        if (emp is null) return NotFound();
        var (managerId, designation, department) = await people.EmploymentInAsync(emp, Cid, ct);
        if (managerId != Me && !IsAdmin)
        {
            this.Flash("Not your direct report.", "danger");
            return Redirect("/mss/");
        }
        ViewData["BackUrl"] = "/mss/team";
        return View(new EvaluateModel
        {
            Employee = emp, Designation = designation, Department = department, Today = clock.Today,
            Profile = await db.Set<PayrollProfile>().AsNoTracking().OrderBy(p => p.Id).FirstOrDefaultAsync(p => p.UserId == emp.Id, ct),
        });
    }

    [HttpPost("evaluate/{uid:int}")]
    [AnyMember]   // managers for their own reports, admins; a revision needs the Payroll edit right
    public async Task<IActionResult> EvaluatePost(int uid, CancellationToken ct)
    {
        if (!IsLead) return AccessDenied();
        var emp = await people.GetMemberAsync(uid, Access, ct);
        if (emp is null) return NotFound();
        var (managerId, _, _) = await people.EmploymentInAsync(emp, Cid, ct);
        if (managerId != Me && !IsAdmin)
        {
            this.Flash("Not your direct report.", "danger");
            return Redirect("/mss/");
        }
        double? newBasic = FormFloat("new_basic");
        string reason = FormStr("reason").Trim();
        var effective = FormInput.OptionalDate(Request.Form, "effective_from") ?? clock.Today;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var profile = await db.Set<PayrollProfile>().OrderBy(p => p.Id).FirstOrDefaultAsync(p => p.UserId == emp.Id, ct);
        if (newBasic > 0 && !Access.Can("payroll", Rights.Edit))
        {
            // A salary revision changes what payroll pays from its date.
            this.Flash("You do not have the Payroll edit right needed to revise a salary. Ask an admin in Settings.", "danger");
            return Redirect("/mss/");
        }
        if (newBasic is double nb && nb > 0)
        {
            decimal newValue = (decimal)nb;
            db.Add(new SalaryRevision
            {
                UserId = emp.Id, PreviousBasic = profile?.BasicSalary ?? 0m, NewBasic = newValue, Reason = reason,
                ApprovedBy = Me, EffectiveFrom = effective, CreatedAt = clock.UtcNow,
            });
            // Payroll reads the dated revision; the profile shows the rate in force today.
            if (profile is not null && effective <= clock.Today)
            {
                profile.BasicSalary = newValue;
                profile.UpdatedAt = clock.UtcNow;
            }
            this.Flash($"Salary revision recorded, effective {effective.ToString("dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}.", "success");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        else
            this.Flash("Enter a new basic salary above zero.", "error");
        return Redirect("/mss/");
    }
}
