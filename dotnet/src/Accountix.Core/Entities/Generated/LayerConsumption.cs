using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class LayerConsumption
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int LayerId { get; set; }

    public int OutLedgerId { get; set; }

    public int ProductId { get; set; }

    public decimal Qty { get; set; }

    public decimal UnitCost { get; set; }

    public decimal TotalCost { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual StockLayer Layer { get; set; } = null!;

    public virtual StockLedger OutLedger { get; set; } = null!;
}
