using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class LeaveRequest
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int UserId { get; set; }

    public int LeaveTypeId { get; set; }

    public DateOnly StartDate { get; set; }

    public DateOnly EndDate { get; set; }

    public decimal TotalDays { get; set; }

    public string Reason { get; set; } = null!;

    public string? Status { get; set; }

    public bool? IsHalfDay { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public virtual ICollection<LeaveApproval> LeaveApprovals { get; set; } = new List<LeaveApproval>();

    public virtual LeaveType LeaveType { get; set; } = null!;

    public virtual User User { get; set; } = null!;
}
