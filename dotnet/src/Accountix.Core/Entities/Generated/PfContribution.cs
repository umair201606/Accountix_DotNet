using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class PfContribution
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int UserId { get; set; }

    public int Month { get; set; }

    public int Year { get; set; }

    public decimal? EmployeeAmount { get; set; }

    public decimal? EmployerAmount { get; set; }

    public decimal? TotalAmount { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual User User { get; set; } = null!;
}
