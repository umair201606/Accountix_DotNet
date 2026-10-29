using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class StockLayer
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int ProductId { get; set; }

    public int? SourceLedgerId { get; set; }

    public decimal UnitCost { get; set; }

    public decimal QtyOriginal { get; set; }

    public decimal QtyRemaining { get; set; }

    public decimal ValueRemaining { get; set; }

    public string Method { get; set; } = null!;

    public bool? IsRevaluation { get; set; }

    public DateTime? CreatedAt { get; set; }

    public string? Notes { get; set; }

    public virtual ICollection<LayerConsumption> LayerConsumptions { get; set; } = new List<LayerConsumption>();

    public virtual StockLedger? SourceLedger { get; set; }
}
