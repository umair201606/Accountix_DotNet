using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class PayrollRun
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int Month { get; set; }

    public int Year { get; set; }

    public DateTime? RunDate { get; set; }

    public string? Status { get; set; }

    public decimal? TotalGross { get; set; }

    public decimal? TotalDeductions { get; set; }

    public decimal? TotalNet { get; set; }

    public int? EmployeeCount { get; set; }

    public int? ProcessedBy { get; set; }

    public int? ApprovedBy { get; set; }

    public DateTime? ApprovedAt { get; set; }

    public string? Notes { get; set; }

    public DateTime? PaidAt { get; set; }

    public virtual User? ApprovedByNavigation { get; set; }

    public virtual ICollection<LoanRepayment> LoanRepayments { get; set; } = new List<LoanRepayment>();

    public virtual ICollection<PayrollAuditLog> PayrollAuditLogs { get; set; } = new List<PayrollAuditLog>();

    public virtual ICollection<PayrollSlip> PayrollSlips { get; set; } = new List<PayrollSlip>();

    public virtual User? ProcessedByNavigation { get; set; }
}
