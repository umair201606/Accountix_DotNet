using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class InvCustomer
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

    public decimal? CreditLimit { get; set; }

    public string? Website { get; set; }

    public string? Notes { get; set; }

    public bool? IsActive { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual ICollection<InvInvoice> InvInvoices { get; set; } = new List<InvInvoice>();

    public virtual ICollection<InvSalesOrder> InvSalesOrders { get; set; } = new List<InvSalesOrder>();

    public virtual ICollection<InvSalesReturn> InvSalesReturns { get; set; } = new List<InvSalesReturn>();
}
