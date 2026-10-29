using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class BankReconciliation
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int AccountId { get; set; }

    public DateOnly StatementDate { get; set; }

    public decimal StatementBalance { get; set; }

    public string? Status { get; set; }

    public string? Notes { get; set; }

    public int? CreatedBy { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public virtual ChartOfAccount Account { get; set; } = null!;

    public virtual ICollection<BankRecItem> BankRecItems { get; set; } = new List<BankRecItem>();

    public virtual User? CreatedByNavigation { get; set; }
}
