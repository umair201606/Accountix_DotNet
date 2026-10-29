using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class AttendanceLog
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int AttendanceId { get; set; }

    public string Event { get; set; } = null!;

    public DateTime? Timestamp { get; set; }

    public string? IpAddress { get; set; }

    public double? GeoLat { get; set; }

    public double? GeoLng { get; set; }

    public virtual Attendance Attendance { get; set; } = null!;
}
