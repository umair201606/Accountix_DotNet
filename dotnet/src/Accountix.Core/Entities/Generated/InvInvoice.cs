using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class InvInvoice
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string InvoiceNumber { get; set; } = null!;

    public string VoucherNumber { get; set; } = null!;

    public int? SalesOrderId { get; set; }

    public int CustomerId { get; set; }

    public int? PartyAccountId { get; set; }

    public int? LabelId { get; set; }

    public DateTime? InvoiceDate { get; set; }

    public DateTime? DueDate { get; set; }

    public string? VoucherStatus { get; set; }

    public string? PaymentStatus { get; set; }

    public string? DiscountMode { get; set; }

    public string? ChargesMode { get; set; }

    public string? TaxMode { get; set; }

    public decimal? GlobalDiscountPct { get; set; }

    public decimal? GlobalDiscountValue { get; set; }

    public decimal? GlobalDelivery { get; set; }

    public decimal? GlobalInstallation { get; set; }

    public decimal? GlobalSalesTaxPct { get; set; }

    public decimal? FurtherTaxPct { get; set; }

    public bool? ApplyFurtherTax { get; set; }

    public decimal? WithholdingTaxPct { get; set; }

    public bool? ApplyWithholdingTax { get; set; }

    public decimal? Subtotal { get; set; }

    public decimal? TotalDiscount { get; set; }

    public decimal? TotalCharges { get; set; }

    public decimal? TotalTax { get; set; }

    public decimal? TotalFurtherTax { get; set; }

    public decimal? TotalWithholdingTax { get; set; }

    public decimal? TotalAmount { get; set; }

    public decimal? PaidAmount { get; set; }

    public string? Notes { get; set; }

    public int? CreatedBy { get; set; }

    public int? ApprovedBy { get; set; }

    public DateTime? ApprovedAt { get; set; }

    public int? EditVersion { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual User? ApprovedByNavigation { get; set; }

    public virtual User? CreatedByNavigation { get; set; }

    public virtual InvCustomer Customer { get; set; } = null!;

    public virtual ICollection<FbrInvoiceRecord> FbrInvoiceRecords { get; set; } = new List<FbrInvoiceRecord>();

    public virtual ICollection<InvInvoiceItem> InvInvoiceItems { get; set; } = new List<InvInvoiceItem>();

    public virtual ICollection<InvSalesReturn> InvSalesReturns { get; set; } = new List<InvSalesReturn>();

    public virtual ProjectLabel? Label { get; set; }

    public virtual ChartOfAccount? PartyAccount { get; set; }

    public virtual InvSalesOrder? SalesOrder { get; set; }
}
