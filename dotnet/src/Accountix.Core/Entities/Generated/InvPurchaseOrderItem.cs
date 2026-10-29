using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class InvPurchaseOrderItem
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int PoId { get; set; }

    public int ProductId { get; set; }

    public string? Description { get; set; }

    public string? Unit { get; set; }

    public decimal? Quantity { get; set; }

    public decimal? InvoicedQty { get; set; }

    public decimal? UnitPrice { get; set; }

    public decimal? SalesTaxPct { get; set; }

    public decimal? TotalBeforeDiscount { get; set; }

    public decimal? TotalAfterDiscount { get; set; }

    public decimal? TotalPrice { get; set; }

    public virtual ICollection<InvPurchaseInvoiceItem> InvPurchaseInvoiceItems { get; set; } = new List<InvPurchaseInvoiceItem>();

    public virtual InvPurchaseOrder Po { get; set; } = null!;

    public virtual InvProduct Product { get; set; } = null!;
}
