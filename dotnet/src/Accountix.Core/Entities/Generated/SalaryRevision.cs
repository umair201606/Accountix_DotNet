using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class SalaryRevision
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int UserId { get; set; }

    public decimal? PreviousBasic { get; set; }

    public decimal NewBasic { get; set; }

    public string? Reason { get; set; }

    public int? ApprovedBy { get; set; }

    public DateOnly EffectiveFrom { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual User? ApprovedByNavigation { get; set; }

    public virtual User User { get; set; } = null!;
}
