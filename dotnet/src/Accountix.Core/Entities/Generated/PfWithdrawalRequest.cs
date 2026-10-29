using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class PfWithdrawalRequest
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int UserId { get; set; }

    public decimal Amount { get; set; }

    public string Reason { get; set; } = null!;

    public string? Status { get; set; }

    public int? ApprovedBy { get; set; }

    public DateTime? ApprovedAt { get; set; }

    public DateTime? DisbursedAt { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual User? ApprovedByNavigation { get; set; }

    public virtual User User { get; set; } = null!;
}
