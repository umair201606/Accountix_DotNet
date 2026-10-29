using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class StockAdjustmentItem
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int VoucherId { get; set; }

    public int ProductId { get; set; }

    public string? ProductName { get; set; }

    public decimal? SystemQty { get; set; }

    public decimal? PhysicalQty { get; set; }

    public decimal? Difference { get; set; }

    public decimal? UnitCost { get; set; }

    public decimal? TotalCost { get; set; }

    public virtual StockAdjustmentVoucher Voucher { get; set; } = null!;
}
