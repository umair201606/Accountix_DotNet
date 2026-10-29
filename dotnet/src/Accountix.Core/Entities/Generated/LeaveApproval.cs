using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class LeaveApproval
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int LeaveRequestId { get; set; }

    public int ApproverId { get; set; }

    public int? Level { get; set; }

    public string? Status { get; set; }

    public string? Comment { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual User Approver { get; set; } = null!;

    public virtual LeaveRequest LeaveRequest { get; set; } = null!;
}
