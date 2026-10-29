using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class PfProfitDistribution
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int Month { get; set; }

    public int Year { get; set; }

    public decimal TotalProfit { get; set; }

    public DateTime? DistributedAt { get; set; }

    public int? DistributedBy { get; set; }

    public string? Status { get; set; }

    public virtual User? DistributedByNavigation { get; set; }
}
