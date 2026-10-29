using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class InvStockMovement
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int ProductId { get; set; }

    public string Type { get; set; } = null!;

    public decimal Quantity { get; set; }

    public string? ReferenceType { get; set; }

    public int? ReferenceId { get; set; }

    public string? Notes { get; set; }

    public int? CreatedBy { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual User? CreatedByNavigation { get; set; }

    public virtual InvProduct Product { get; set; } = null!;
}
