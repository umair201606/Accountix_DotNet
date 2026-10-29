using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class PayrollSlip
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int PayrollRunId { get; set; }

    public int UserId { get; set; }

    public decimal? BasicSalary { get; set; }

    public decimal? Allowances { get; set; }

    public decimal? Deductions { get; set; }

    public decimal? GrossPay { get; set; }

    public decimal? TotalDeductions { get; set; }

    public decimal? NetPay { get; set; }

    public decimal? TaxablePay { get; set; }

    public string? ComponentsJson { get; set; }

    public string? PdfFilename { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual PayrollRun PayrollRun { get; set; } = null!;

    public virtual User User { get; set; } = null!;
}
