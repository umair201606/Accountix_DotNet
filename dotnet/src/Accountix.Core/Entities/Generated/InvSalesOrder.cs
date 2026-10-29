using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class InvSalesOrder
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string SoNumber { get; set; } = null!;

    public int CustomerId { get; set; }

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

    public int? CreatedBy { get; set; }

    public int? ApprovedBy { get; set; }

    public DateTime? ApprovedAt { get; set; }

    public int? EditVersion { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual User? ApprovedByNavigation { get; set; }

    public virtual User? CreatedByNavigation { get; set; }

    public virtual InvCustomer Customer { get; set; } = null!;

    public virtual ICollection<InvInvoiceItem> InvInvoiceItems { get; set; } = new List<InvInvoiceItem>();

    public virtual ICollection<InvInvoice> InvInvoices { get; set; } = new List<InvInvoice>();

    public virtual ICollection<InvSalesOrderItem> InvSalesOrderItems { get; set; } = new List<InvSalesOrderItem>();

    public virtual ChartOfAccount? PartyAccount { get; set; }
}
