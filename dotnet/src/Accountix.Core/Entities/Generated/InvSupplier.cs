using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class InvSupplier
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string Name { get; set; } = null!;

    public string? ContactPerson { get; set; }

    public string? Email { get; set; }

    public string? Phone { get; set; }

    public string? Mobile { get; set; }

    public string? Address { get; set; }

    public string? City { get; set; }

    public string? TaxId { get; set; }

    public string? PaymentTerms { get; set; }

    public string? Website { get; set; }

    public string? Notes { get; set; }

    public bool? IsActive { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual ICollection<InvPurchaseInvoice> InvPurchaseInvoices { get; set; } = new List<InvPurchaseInvoice>();

    public virtual ICollection<InvPurchaseOrder> InvPurchaseOrders { get; set; } = new List<InvPurchaseOrder>();

    public virtual ICollection<InvPurchaseReturn> InvPurchaseReturns { get; set; } = new List<InvPurchaseReturn>();
}
