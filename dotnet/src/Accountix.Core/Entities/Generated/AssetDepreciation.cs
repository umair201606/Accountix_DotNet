using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class AssetDepreciation
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int AssetId { get; set; }

    public DateOnly EntryDate { get; set; }

    public decimal Amount { get; set; }

    public decimal AccumulatedAfter { get; set; }

    public decimal NetBookValueAfter { get; set; }

    public int? JournalEntryId { get; set; }

    public string? Notes { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual FixedAsset Asset { get; set; } = null!;

    public virtual JournalEntry? JournalEntry { get; set; }
}
