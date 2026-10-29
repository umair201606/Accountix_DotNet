using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class PayrollAuditLog
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int? PayrollRunId { get; set; }

    public string Action { get; set; } = null!;

    public int PerformedBy { get; set; }

    public string? Details { get; set; }

    public string? IpAddress { get; set; }

    public DateTime? PerformedAt { get; set; }

    public virtual PayrollRun? PayrollRun { get; set; }

    public virtual User PerformedByNavigation { get; set; } = null!;
}
