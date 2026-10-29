using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class StockLedger
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int ProductId { get; set; }

    public string VoucherType { get; set; } = null!;

    public int VoucherId { get; set; }

    public string VoucherNumber { get; set; } = null!;

    public string TransactionType { get; set; } = null!;

    public decimal Quantity { get; set; }

    public decimal UnitCost { get; set; }

    public decimal TotalCost { get; set; }

    public decimal RunningQty { get; set; }

    public decimal RunningCost { get; set; }

    public decimal RunningAvg { get; set; }

    public string? ValuationMethod { get; set; }

    public DateOnly? TxnDate { get; set; }

    public DateTime? CreatedAt { get; set; }

    public int CreatedBy { get; set; }

    public string? Notes { get; set; }

    public virtual User CreatedByNavigation { get; set; } = null!;

    public virtual ICollection<LayerConsumption> LayerConsumptions { get; set; } = new List<LayerConsumption>();

    public virtual ICollection<StockLayer> StockLayers { get; set; } = new List<StockLayer>();
}
