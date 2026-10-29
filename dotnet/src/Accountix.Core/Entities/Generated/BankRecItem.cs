using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class BankRecItem
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int ReconciliationId { get; set; }

    public int JournalLineId { get; set; }

    public virtual JournalLine JournalLine { get; set; } = null!;

    public virtual BankReconciliation Reconciliation { get; set; } = null!;
}
