using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class StockCostAdjustment
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int ProductId { get; set; }

    public int? LedgerId { get; set; }

    public string VoucherType { get; set; } = null!;

    public int VoucherId { get; set; }

    public string? VoucherNumber { get; set; }

    public decimal OldCost { get; set; }

    public decimal NewCost { get; set; }

    public decimal Delta { get; set; }

    public string? Trigger { get; set; }

    public int? JournalEntryId { get; set; }

    public DateOnly? EntryDate { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual JournalEntry? JournalEntry { get; set; }
}
