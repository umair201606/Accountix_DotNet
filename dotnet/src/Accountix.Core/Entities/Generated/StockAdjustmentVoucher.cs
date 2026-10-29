using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class StockAdjustmentVoucher
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string VoucherNumber { get; set; } = null!;

    public DateTime? Date { get; set; }

    public string? Reason { get; set; }

    public int? LabelId { get; set; }

    public string? Status { get; set; }

    public int CreatedBy { get; set; }

    public int? ApprovedBy { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? ApprovedAt { get; set; }

    public virtual User? ApprovedByNavigation { get; set; }

    public virtual User CreatedByNavigation { get; set; } = null!;

    public virtual ProjectLabel? Label { get; set; }

    public virtual ICollection<StockAdjustmentItem> StockAdjustmentItems { get; set; } = new List<StockAdjustmentItem>();

    public virtual ICollection<StockTake> StockTakes { get; set; } = new List<StockTake>();
}
