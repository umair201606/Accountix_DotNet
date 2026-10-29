using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class LeaveQuota
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int UserId { get; set; }

    public int LeaveTypeId { get; set; }

    public int Year { get; set; }

    public decimal? Total { get; set; }

    public decimal? Used { get; set; }

    public decimal? Pending { get; set; }

    public decimal? Remaining { get; set; }

    public virtual LeaveType LeaveType { get; set; } = null!;

    public virtual User User { get; set; } = null!;
}
