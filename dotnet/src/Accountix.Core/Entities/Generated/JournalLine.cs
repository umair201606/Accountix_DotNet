using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class JournalLine
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int JournalEntryId { get; set; }

    public int AccountId { get; set; }

    public decimal? Debit { get; set; }

    public decimal? Credit { get; set; }

    public string? Description { get; set; }

    public int? LabelId { get; set; }

    public virtual ChartOfAccount Account { get; set; } = null!;

    public virtual ICollection<BankRecItem> BankRecItems { get; set; } = new List<BankRecItem>();

    public virtual JournalEntry JournalEntry { get; set; } = null!;

    public virtual ProjectLabel? Label { get; set; }
}
