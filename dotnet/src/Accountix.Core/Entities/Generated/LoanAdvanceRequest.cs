using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class LoanAdvanceRequest
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int UserId { get; set; }

    public string RequestType { get; set; } = null!;

    public decimal Amount { get; set; }

    public string Purpose { get; set; } = null!;

    public int? InstallmentMonths { get; set; }

    public decimal? MonthlyInstallment { get; set; }

    public decimal? RemainingAmount { get; set; }

    public string? Status { get; set; }

    public int? ApprovedBy { get; set; }

    public DateTime? ApprovedAt { get; set; }

    public DateTime? DisbursedAt { get; set; }

    public bool? QueueForPayroll { get; set; }

    public string? Notes { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual User? ApprovedByNavigation { get; set; }

    public virtual ICollection<LoanRepayment> LoanRepayments { get; set; } = new List<LoanRepayment>();

    public virtual User User { get; set; } = null!;
}
