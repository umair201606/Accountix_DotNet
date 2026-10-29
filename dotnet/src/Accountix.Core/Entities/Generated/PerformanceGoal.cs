using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class PerformanceGoal
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int UserId { get; set; }

    public string Title { get; set; } = null!;

    public string? Description { get; set; }

    public DateOnly? TargetDate { get; set; }

    public string? Status { get; set; }

    public int? ProgressPct { get; set; }

    public int? CreatedBy { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual User? CreatedByNavigation { get; set; }

    public virtual User User { get; set; } = null!;
}
