using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class TaxRateAccount
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public decimal RatePct { get; set; }

    public int AccountId { get; set; }

    public virtual ChartOfAccount Account { get; set; } = null!;
}
