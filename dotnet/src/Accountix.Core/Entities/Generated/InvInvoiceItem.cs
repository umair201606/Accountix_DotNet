using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class InvInvoiceItem
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int InvoiceId { get; set; }

    public int? ProductId { get; set; }

    public string? Description { get; set; }

    public decimal? Quantity { get; set; }

    public string? Unit { get; set; }

    public decimal? UnitPrice { get; set; }

    public int? LabelId { get; set; }

    public int? SourceOrderId { get; set; }

    public int? SourceOrderItemId { get; set; }

    public string? SourceOrderNumber { get; set; }

    public decimal? DiscountPct { get; set; }

    public decimal? DiscountAmount { get; set; }

    public decimal? Delivery { get; set; }

    public decimal? Installation { get; set; }

    public decimal? SalesTaxPct { get; set; }

    public decimal? TotalBeforeDiscount { get; set; }

    public decimal? TotalAfterDiscount { get; set; }

    public string? Comments { get; set; }

    public virtual InvInvoice Invoice { get; set; } = null!;

    public virtual ProjectLabel? Label { get; set; }

    public virtual InvProduct? Product { get; set; }

    public virtual InvSalesOrder? SourceOrder { get; set; }

    public virtual InvSalesOrderItem? SourceOrderItem { get; set; }
}
