using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class OvertimeAccount
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int UserId { get; set; }

    public DateOnly Date { get; set; }

    public double? RegularHours { get; set; }

    public double? OvertimeHours { get; set; }

    public double? DoubleOvertime { get; set; }

    public bool? Approved { get; set; }

    public string? Notes { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual User User { get; set; } = null!;
}
