using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class BreakLog
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int AttendanceId { get; set; }

    public DateTime BreakStart { get; set; }

    public DateTime? BreakEnd { get; set; }

    public string? BreakType { get; set; }

    public int? DurationMinutes { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual Attendance Attendance { get; set; } = null!;
}
