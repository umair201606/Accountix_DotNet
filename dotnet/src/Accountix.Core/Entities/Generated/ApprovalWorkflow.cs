using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class ApprovalWorkflow
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string Name { get; set; } = null!;

    public string Module { get; set; } = null!;

    public int? LeaveTypeId { get; set; }

    public bool? RequiresApproval { get; set; }

    public int? ApprovalLevels { get; set; }

    public bool? AutoApprove { get; set; }

    public string? AutoApproveRole { get; set; }

    public bool? NotifyAdmins { get; set; }

    public bool? IsActive { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual LeaveType? LeaveType { get; set; }
}
