using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class InvPurchaseOrder
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string PoNumber { get; set; } = null!;

    public int SupplierId { get; set; }

    public int? PartyAccountId { get; set; }

    public DateOnly? OrderDate { get; set; }

    public DateOnly? ExpectedDate { get; set; }

    public string? Status { get; set; }

    public string? FulfilmentStatus { get; set; }

    public string? TaxMode { get; set; }

    public decimal? GlobalSalesTaxPct { get; set; }

    public decimal? Subtotal { get; set; }

    public decimal? TotalTax { get; set; }

    public decimal? TotalAmount { get; set; }

    public string? Notes { get; set; }

    public string? DriverName { get; set; }

    public string? DriverContact { get; set; }

    public string? VehicleNumber { get; set; }

    public string? GatePass { get; set; }

    public int? CreatedBy { get; set; }

    public int? ApprovedBy { get; set; }

    public DateTime? ApprovedAt { get; set; }

    public int? EditVersion { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual User? ApprovedByNavigation { get; set; }

    public virtual User? CreatedByNavigation { get; set; }

    public virtual ICollection<InvPurchaseInvoiceItem> InvPurchaseInvoiceItems { get; set; } = new List<InvPurchaseInvoiceItem>();

    public virtual ICollection<InvPurchaseInvoice> InvPurchaseInvoices { get; set; } = new List<InvPurchaseInvoice>();

    public virtual ICollection<InvPurchaseOrderItem> InvPurchaseOrderItems { get; set; } = new List<InvPurchaseOrderItem>();

    public virtual ChartOfAccount? PartyAccount { get; set; }

    public virtual InvSupplier Supplier { get; set; } = null!;
}
