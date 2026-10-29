using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class TimePolicy
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string Name { get; set; } = null!;

    public string? Department { get; set; }

    public TimeOnly ShiftStart { get; set; }

    public TimeOnly ShiftEnd { get; set; }

    public int? GracePeriodMinutes { get; set; }

    public double? MaxRegularHours { get; set; }

    public double? MaxOvertimeHours { get; set; }

    public int? MaxConsecutiveDays { get; set; }

    public bool? RequireBreak { get; set; }

    public int? MinBreakMinutes { get; set; }

    public bool? IsActive { get; set; }

    public DateTime? CreatedAt { get; set; }
}
