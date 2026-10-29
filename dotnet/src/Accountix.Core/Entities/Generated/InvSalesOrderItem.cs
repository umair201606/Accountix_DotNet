using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class InvSalesOrderItem
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int SoId { get; set; }

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

    public virtual ICollection<InvInvoiceItem> InvInvoiceItems { get; set; } = new List<InvInvoiceItem>();

    public virtual InvProduct Product { get; set; } = null!;

    public virtual InvSalesOrder So { get; set; } = null!;
}
