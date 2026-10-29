using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class ChargeLedgerDefault
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int AccountId { get; set; }

    public string? Treatment { get; set; }

    public bool? StTaxable { get; set; }

    public bool? WhtTaxable { get; set; }

    public bool? ExtraTaxable { get; set; }

    public string? AppliesTo { get; set; }

    public virtual ChartOfAccount Account { get; set; } = null!;
}
