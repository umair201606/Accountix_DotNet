using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class PfLedger
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int UserId { get; set; }

    public DateOnly TransactionDate { get; set; }

    public string TransactionType { get; set; } = null!;

    public string? Description { get; set; }

    public decimal? Debit { get; set; }

    public decimal? Credit { get; set; }

    public decimal? Balance { get; set; }

    public int? ReferenceId { get; set; }

    public int? PayrollRunId { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual User User { get; set; } = null!;
}
