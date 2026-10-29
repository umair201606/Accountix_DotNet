using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class InvProduct
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string Sku { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    public int? CategoryId { get; set; }

    public decimal? UnitPrice { get; set; }

    public decimal? CostPrice { get; set; }

    public int? ReorderLevel { get; set; }

    public decimal? CurrentStock { get; set; }

    public string? Unit { get; set; }

    public string? HsCode { get; set; }

    public decimal? Weight { get; set; }

    public bool? IsActive { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public virtual InvCategory? Category { get; set; }

    public virtual ICollection<InvInvoiceItem> InvInvoiceItems { get; set; } = new List<InvInvoiceItem>();

    public virtual ICollection<InvPurchaseInvoiceItem> InvPurchaseInvoiceItems { get; set; } = new List<InvPurchaseInvoiceItem>();

    public virtual ICollection<InvPurchaseOrderItem> InvPurchaseOrderItems { get; set; } = new List<InvPurchaseOrderItem>();

    public virtual ICollection<InvPurchaseReturnItem> InvPurchaseReturnItems { get; set; } = new List<InvPurchaseReturnItem>();

    public virtual ICollection<InvSalesOrderItem> InvSalesOrderItems { get; set; } = new List<InvSalesOrderItem>();

    public virtual ICollection<InvSalesReturnItem> InvSalesReturnItems { get; set; } = new List<InvSalesReturnItem>();

    public virtual ICollection<InvStockMovement> InvStockMovements { get; set; } = new List<InvStockMovement>();
}
