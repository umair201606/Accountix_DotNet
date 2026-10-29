using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class StockTake
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string Reference { get; set; } = null!;

    public DateTime? Date { get; set; }

    public string? Location { get; set; }

    public string? Status { get; set; }

    public int CreatedBy { get; set; }

    public int? ApprovedBy { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? ApprovedAt { get; set; }

    public int? AdjustmentVoucherId { get; set; }

    public virtual StockAdjustmentVoucher? AdjustmentVoucher { get; set; }

    public virtual User? ApprovedByNavigation { get; set; }

    public virtual User CreatedByNavigation { get; set; } = null!;

    public virtual ICollection<StockTakeItem> StockTakeItems { get; set; } = new List<StockTakeItem>();
}
