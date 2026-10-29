using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class AccountingPeriod
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string FiscalYear { get; set; } = null!;

    public string PeriodName { get; set; } = null!;

    public DateOnly StartDate { get; set; }

    public DateOnly EndDate { get; set; }

    public bool? IsOpen { get; set; }

    public bool? IsClosed { get; set; }

    public bool? IsActive { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? ClosedAt { get; set; }
}
