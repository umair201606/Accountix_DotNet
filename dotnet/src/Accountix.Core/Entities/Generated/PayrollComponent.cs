using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class PayrollComponent
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int ProfileId { get; set; }

    public string Name { get; set; } = null!;

    public string Type { get; set; } = null!;

    public string? CalculationMethod { get; set; }

    public decimal? Value { get; set; }

    public bool? IsTaxable { get; set; }

    public virtual PayrollProfile Profile { get; set; } = null!;
}
