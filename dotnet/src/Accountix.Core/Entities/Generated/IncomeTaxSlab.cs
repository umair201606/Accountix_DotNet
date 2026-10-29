using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class IncomeTaxSlab
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public decimal MinIncome { get; set; }

    public decimal MaxIncome { get; set; }

    public decimal RatePct { get; set; }

    public decimal FixedAmount { get; set; }

    public bool? IsActive { get; set; }

    public DateTime? CreatedAt { get; set; }
}
