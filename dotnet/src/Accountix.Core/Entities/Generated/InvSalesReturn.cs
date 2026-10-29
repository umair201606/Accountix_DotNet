using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class InvSalesReturn
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string ReturnNumber { get; set; } = null!;

    public int OriginalInvoiceId { get; set; }

    public int CustomerId { get; set; }

    public int? PartyAccountId { get; set; }

    public DateTime? ReturnDate { get; set; }

    public string? Notes { get; set; }

    public decimal? GrossReturnValue { get; set; }

    public decimal? TotalDiscount { get; set; }

    public decimal? TotalCharges { get; set; }

    public decimal? TotalTax { get; set; }

    public decimal? NetReturnAmount { get; set; }

    public decimal? TotalCostReturned { get; set; }

    public bool? ReverseCharges { get; set; }

    public string? Status { get; set; }

    public int? CreatedBy { get; set; }

    public int? ApprovedBy { get; set; }

    public DateTime? ApprovedAt { get; set; }

    public int? EditVersion { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual User? ApprovedByNavigation { get; set; }

    public virtual User? CreatedByNavigation { get; set; }

    public virtual InvCustomer Customer { get; set; } = null!;

    public virtual ICollection<InvSalesReturnItem> InvSalesReturnItems { get; set; } = new List<InvSalesReturnItem>();

    public virtual InvInvoice OriginalInvoice { get; set; } = null!;

    public virtual ChartOfAccount? PartyAccount { get; set; }
}
