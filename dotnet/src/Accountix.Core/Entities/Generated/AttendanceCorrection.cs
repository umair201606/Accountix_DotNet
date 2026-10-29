using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class AttendanceCorrection
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int AttendanceId { get; set; }

    public int CorrectedBy { get; set; }

    public string Field { get; set; } = null!;

    public string? OldValue { get; set; }

    public string NewValue { get; set; } = null!;

    public string Reason { get; set; } = null!;

    public DateTime? CorrectedAt { get; set; }

    public virtual Attendance Attendance { get; set; } = null!;

    public virtual User CorrectedByNavigation { get; set; } = null!;
}
