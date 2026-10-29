using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class AdditionalCharge
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string DocType { get; set; } = null!;

    public int DocId { get; set; }

    public int ChargeAccountId { get; set; }

    public string? Description { get; set; }

    public decimal? Amount { get; set; }

    public string? Scope { get; set; }

    public string? Distribution { get; set; }

    public string? ManualAllocations { get; set; }

    public string? Treatment { get; set; }

    public bool? StTaxable { get; set; }

    public bool? WhtTaxable { get; set; }

    public bool? ExtraTaxable { get; set; }

    public bool? Taxable { get; set; }

    public string? TaxBase { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual ChartOfAccount ChargeAccount { get; set; } = null!;
}
