using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class PfLoanRequest
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int UserId { get; set; }

    public decimal Amount { get; set; }

    public int? InstallmentMonths { get; set; }

    public string Purpose { get; set; } = null!;

    public string? Status { get; set; }

    public int? ApprovedBy { get; set; }

    public DateTime? ApprovedAt { get; set; }

    public decimal? MonthlyInstallment { get; set; }

    public decimal? RemainingAmount { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual User? ApprovedByNavigation { get; set; }

    public virtual User User { get; set; } = null!;
}
