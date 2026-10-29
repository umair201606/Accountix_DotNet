using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class TimesheetWeek
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int UserId { get; set; }

    public DateOnly WeekStart { get; set; }

    public DateOnly WeekEnd { get; set; }

    public string? Status { get; set; }

    public double? TotalHours { get; set; }

    public DateTime? SubmittedAt { get; set; }

    public DateTime? ApprovedAt { get; set; }

    public int? ApprovedBy { get; set; }

    public string? Notes { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual User? ApprovedByNavigation { get; set; }

    public virtual ICollection<TimesheetApproval> TimesheetApprovals { get; set; } = new List<TimesheetApproval>();

    public virtual ICollection<TimesheetEntry> TimesheetEntries { get; set; } = new List<TimesheetEntry>();

    public virtual User User { get; set; } = null!;
}
