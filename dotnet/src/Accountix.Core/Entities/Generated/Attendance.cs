using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class Attendance
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int UserId { get; set; }

    public DateOnly Date { get; set; }

    public DateTime? ClockIn { get; set; }

    public DateTime? ClockOut { get; set; }

    public string? IpAddress { get; set; }

    public double? GeoLat { get; set; }

    public double? GeoLng { get; set; }

    public string? Status { get; set; }

    public bool? IsLate { get; set; }

    public bool? IsHalfDay { get; set; }

    public string? Notes { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual ICollection<AttendanceCorrection> AttendanceCorrections { get; set; } = new List<AttendanceCorrection>();

    public virtual ICollection<AttendanceLog> AttendanceLogs { get; set; } = new List<AttendanceLog>();

    public virtual ICollection<BreakLog> BreakLogs { get; set; } = new List<BreakLog>();

    public virtual User User { get; set; } = null!;
}
