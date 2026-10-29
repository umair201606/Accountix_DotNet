using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class TimesheetMergedReport
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int UserId { get; set; }

    public DateOnly PeriodStart { get; set; }

    public DateOnly PeriodEnd { get; set; }

    public double? TotalHours { get; set; }

    public double? OvertimeHours { get; set; }

    public string? ProjectBreakdown { get; set; }

    public DateTime? GeneratedAt { get; set; }

    public virtual User User { get; set; } = null!;
}
