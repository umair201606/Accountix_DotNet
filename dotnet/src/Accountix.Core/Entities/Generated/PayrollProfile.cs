using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class PayrollProfile
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int UserId { get; set; }

    public decimal? BasicSalary { get; set; }

    public DateOnly EffectiveFrom { get; set; }

    public string? PaymentMethod { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public virtual ICollection<PayrollComponent> PayrollComponents { get; set; } = new List<PayrollComponent>();

    public virtual User User { get; set; } = null!;
}
