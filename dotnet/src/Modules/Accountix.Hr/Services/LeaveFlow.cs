namespace Accountix.Hr.Services;

/// <summary>
/// Leave balances and decisions (hr_app/leave_flow.py): one path for the
/// leave page and MSS. Balances are derived from the requests — used =
/// approved days, pending = days awaiting a decision, remaining = total less
/// both — so a repeated decision can never count twice. A request belongs to
/// the quota of its start date's year.
/// </summary>
public sealed class LeaveFlow(AccountixDbContext db)
{
    public static readonly string[] Open = ["pending", "approved"];

    /// <summary>quota_for: the quota for this type and year, made from the type's default when missing.</summary>
    public async Task<LeaveQuota> QuotaForAsync(int userId, LeaveType type, int year, CancellationToken ct = default)
    {
        var q = await db.Set<LeaveQuota>().OrderBy(x => x.Id)
            .FirstOrDefaultAsync(x => x.UserId == userId && x.LeaveTypeId == type.Id && x.Year == year, ct);
        if (q is null)
        {
            q = new LeaveQuota
            {
                UserId = userId, LeaveTypeId = type.Id, Year = year,
                Total = type.DefaultQuota ?? 0, Used = 0, Pending = 0, Remaining = type.DefaultQuota ?? 0,
            };
            db.Add(q);
            await db.SaveChangesAsync(ct);
        }
        return q;
    }

    /// <summary>refresh: recompute used / pending / remaining from the quota's requests.</summary>
    public async Task<LeaveQuota> RefreshAsync(LeaveQuota quota, CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct);   // the session's flush: requests just changed count
        var sums = await db.Set<LeaveRequest>()
            .Where(r => r.UserId == quota.UserId && r.LeaveTypeId == quota.LeaveTypeId && r.StartDate.Year == quota.Year
                        && (r.Status == "approved" || r.Status == "pending"))
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Days = g.Sum(r => r.TotalDays) })
            .ToListAsync(ct);
        quota.Used = sums.FirstOrDefault(s => s.Status == "approved")?.Days ?? 0m;
        quota.Pending = sums.FirstOrDefault(s => s.Status == "pending")?.Days ?? 0m;
        quota.Remaining = (quota.Total ?? 0m) - quota.Used - quota.Pending;
        return quota;
    }

    public async Task<LeaveQuota?> RefreshForAsync(LeaveRequest lr, CancellationToken ct = default)
    {
        int year = lr.StartDate.Year;
        var q = await db.Set<LeaveQuota>().OrderBy(x => x.Id)
            .FirstOrDefaultAsync(x => x.UserId == lr.UserId && x.LeaveTypeId == lr.LeaveTypeId && x.Year == year, ct);
        return q is null ? null : await RefreshAsync(q, ct);
    }

    /// <summary>overlapping: an open request of this person covering any of these dates.</summary>
    public Task<LeaveRequest?> OverlappingAsync(int userId, DateOnly start, DateOnly end, CancellationToken ct = default) =>
        db.Set<LeaveRequest>().OrderBy(r => r.Id)
            .FirstOrDefaultAsync(r => r.UserId == userId && (r.Status == "pending" || r.Status == "approved")
                                      && r.StartDate <= end && r.EndDate >= start, ct);

    /// <summary>_get_working_days: Monday–Friday that are not active company holidays.</summary>
    public async Task<int> WorkingDaysAsync(DateOnly start, DateOnly end, CancellationToken ct = default)
    {
        var holidays = (await db.Set<CompanyHoliday>().AsNoTracking()
                .Where(h => h.HolidayDate >= start && h.HolidayDate <= end && h.IsActive == true)
                .Select(h => h.HolidayDate).ToListAsync(ct)).ToHashSet();
        int days = 0;
        for (var d = start; d <= end; d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !holidays.Contains(d))
                days++;
        return days;
    }

    /// <summary>
    /// decide: one approver's decision; returns the refusal, or null. Only a
    /// pending approval on a pending request, by its own approver; a rejection
    /// needs a reason. Queued per employee (serialize("leave", user)) and
    /// re-read after the wait, so two deciders never both see it pending.
    /// Call inside a transaction.
    /// </summary>
    public async Task<string?> DecideAsync(LeaveApproval approval, string? action, string? comment, int userId,
                                           CancellationToken ct = default)
    {
        if (approval.ApproverId != userId) return "You are not the approver of this request.";
        if (action is not ("approve" or "reject")) return "Unknown action.";
        var lr = await db.Set<LeaveRequest>().FirstAsync(r => r.Id == approval.LeaveRequestId, ct);
        await db.SerializeAsync("leave", lr.UserId);
        await db.Entry(approval).ReloadAsync(ct);
        await db.Entry(lr).ReloadAsync(ct);
        if (approval.Status != "pending" || lr.Status != "pending")
            return "This request has already been decided.";
        comment = (comment ?? "").Trim();
        if (action == "reject" && comment.Length == 0)
            return "Please give a reason for the rejection.";
        approval.Status = action == "approve" ? "approved" : "rejected";
        approval.Comment = comment;
        await db.SaveChangesAsync(ct);
        string? before = lr.Status;
        if (action == "reject")
            lr.Status = "rejected";
        else if (await db.Set<LeaveApproval>().Where(a => a.LeaveRequestId == lr.Id).AllAsync(a => a.Status == "approved", ct))
            lr.Status = "approved";
        if (lr.Status != before)
            lr.UpdatedAt = DateTime.UtcNow;   // onupdate=datetime.utcnow
        await RefreshForAsync(lr, ct);
        return null;
    }
}
