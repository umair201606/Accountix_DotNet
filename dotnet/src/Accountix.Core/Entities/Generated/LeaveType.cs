using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class LeaveType
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string Name { get; set; } = null!;

    public string Code { get; set; } = null!;

    public string? Description { get; set; }

    public int? DefaultQuota { get; set; }

    public bool? IsPaid { get; set; }

    public bool? RequiresApproval { get; set; }

    public bool? CarryForward { get; set; }

    public int? MaxCarryForward { get; set; }

    public virtual ICollection<ApprovalWorkflow> ApprovalWorkflows { get; set; } = new List<ApprovalWorkflow>();

    public virtual ICollection<LeaveQuota> LeaveQuota { get; set; } = new List<LeaveQuota>();

    public virtual ICollection<LeaveRequest> LeaveRequests { get; set; } = new List<LeaveRequest>();
}
