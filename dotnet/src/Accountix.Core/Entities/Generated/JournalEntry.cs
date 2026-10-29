using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class JournalEntry
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string VoucherType { get; set; } = null!;

    public int VoucherId { get; set; }

    public string VoucherNumber { get; set; } = null!;

    public string? Description { get; set; }

    public DateTime EntryDate { get; set; }

    public int CreatedBy { get; set; }

    public DateTime? CreatedAt { get; set; }

    public bool? IsPosted { get; set; }

    public virtual ICollection<AssetDepreciation> AssetDepreciations { get; set; } = new List<AssetDepreciation>();

    public virtual User CreatedByNavigation { get; set; } = null!;

    public virtual ICollection<JournalLine> JournalLines { get; set; } = new List<JournalLine>();

    public virtual ICollection<StockCostAdjustment> StockCostAdjustments { get; set; } = new List<StockCostAdjustment>();
}
