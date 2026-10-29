using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class InvPurchaseInvoice
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string InvoiceNumber { get; set; } = null!;

    public string VoucherNumber { get; set; } = null!;

    public DateTime? InvoiceDate { get; set; }

    public int SupplierId { get; set; }

    public int? PartyAccountId { get; set; }

    public int? LabelId { get; set; }

    public string? DriverName { get; set; }

    public string? DriverContact { get; set; }

    public string? VehicleNumber { get; set; }

    public string? GatePass { get; set; }

    public string? DiscountMode { get; set; }

    public string? ExpensesMode { get; set; }

    public string? TaxMode { get; set; }

    public decimal? GlobalDiscountPct { get; set; }

    public decimal? GlobalDiscountValue { get; set; }

    public decimal? GlobalCommission { get; set; }

    public decimal? GlobalFreight { get; set; }

    public decimal? GlobalLoading { get; set; }

    public decimal? GlobalSalesTaxPct { get; set; }

    public decimal? GlobalWithholdingTaxPct { get; set; }

    public decimal? FurtherTaxPct { get; set; }

    public bool? ApplyFurtherTax { get; set; }

    public bool? ApplyWithholdingTax { get; set; }

    public decimal? Subtotal { get; set; }

    public decimal? TotalDiscount { get; set; }

    public decimal? TotalExpenses { get; set; }

    public decimal? TotalTax { get; set; }

    public decimal? TotalFurtherTax { get; set; }

    public decimal? TotalWithholdingTax { get; set; }

    public decimal? NetPayable { get; set; }

    public decimal? TotalAmount { get; set; }

    public decimal? PaidAmount { get; set; }

    public string? PaymentStatus { get; set; }

    public int? PurchaseOrderId { get; set; }

    public string? Notes { get; set; }

    public string? Status { get; set; }

    public int? CreatedBy { get; set; }

    public int? ApprovedBy { get; set; }

    public DateTime? ApprovedAt { get; set; }

    public int? EditVersion { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual User? ApprovedByNavigation { get; set; }

    public virtual User? CreatedByNavigation { get; set; }

    public virtual ICollection<InvPurchaseInvoiceItem> InvPurchaseInvoiceItems { get; set; } = new List<InvPurchaseInvoiceItem>();

    public virtual ICollection<InvPurchaseReturn> InvPurchaseReturns { get; set; } = new List<InvPurchaseReturn>();

    public virtual ProjectLabel? Label { get; set; }

    public virtual ChartOfAccount? PartyAccount { get; set; }

    public virtual InvPurchaseOrder? PurchaseOrder { get; set; }

    public virtual InvSupplier Supplier { get; set; } = null!;
}
