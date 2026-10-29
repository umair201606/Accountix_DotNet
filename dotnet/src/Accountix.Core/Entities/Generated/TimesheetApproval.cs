using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class TimesheetApproval
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int WeekId { get; set; }

    public int ApproverId { get; set; }

    public string? Status { get; set; }

    public string? Comment { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual User Approver { get; set; } = null!;

    public virtual TimesheetWeek Week { get; set; } = null!;
}
