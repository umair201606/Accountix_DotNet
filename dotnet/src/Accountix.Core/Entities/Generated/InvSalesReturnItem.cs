using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class InvSalesReturnItem
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int ReturnId { get; set; }

    public int? ProductId { get; set; }

    public string? Description { get; set; }

    public decimal? OriginalQuantity { get; set; }

    public decimal? PreviouslyReturnedQty { get; set; }

    public decimal? MaxReturnableQty { get; set; }

    public decimal? CurrentReturnQty { get; set; }

    public string? Unit { get; set; }

    public decimal? UnitPrice { get; set; }

    public decimal? DiscountPct { get; set; }

    public decimal? DiscountAmount { get; set; }

    public decimal? Delivery { get; set; }

    public decimal? Installation { get; set; }

    public decimal? SalesTaxPct { get; set; }

    public decimal? TotalBeforeDiscount { get; set; }

    public decimal? TotalAfterDiscount { get; set; }

    public decimal? ProportionalDiscount { get; set; }

    public decimal? ProportionalSalesTax { get; set; }

    public decimal? ProportionalDelivery { get; set; }

    public decimal? ProportionalInstallation { get; set; }

    public decimal? NetReturnValue { get; set; }

    public decimal? CostBasis { get; set; }

    public decimal? TotalCostReturned { get; set; }

    public virtual InvProduct? Product { get; set; }

    public virtual InvSalesReturn Return { get; set; } = null!;
}
