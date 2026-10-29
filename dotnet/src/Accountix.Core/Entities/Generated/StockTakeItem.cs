using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class StockTakeItem
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int StockTakeId { get; set; }

    public int ProductId { get; set; }

    public string? ProductName { get; set; }

    public decimal? SystemQty { get; set; }

    public decimal? PhysicalQty { get; set; }

    public decimal? Difference { get; set; }

    public decimal? UnitCost { get; set; }

    public virtual StockTake StockTake { get; set; } = null!;
}
