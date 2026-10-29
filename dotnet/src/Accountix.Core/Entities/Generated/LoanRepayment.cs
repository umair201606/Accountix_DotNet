using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class LoanRepayment
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int LoanId { get; set; }

    public decimal Amount { get; set; }

    public DateTime? PaidAt { get; set; }

    public int? PayrollRunId { get; set; }

    public string? Notes { get; set; }

    public virtual LoanAdvanceRequest Loan { get; set; } = null!;

    public virtual PayrollRun? PayrollRun { get; set; }
}
