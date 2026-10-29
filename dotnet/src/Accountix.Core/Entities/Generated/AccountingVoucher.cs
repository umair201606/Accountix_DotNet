using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class AccountingVoucher
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string VoucherType { get; set; } = null!;

    public string VoucherNumber { get; set; } = null!;

    public DateTime VoucherDate { get; set; }

    public int? CashBankAccountId { get; set; }

    public int? LabelId { get; set; }

    public string? Notes { get; set; }

    public string? Status { get; set; }

    public int CreatedBy { get; set; }

    public int? ApprovedBy { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? ApprovedAt { get; set; }

    public virtual ICollection<AccountingVoucherLine> AccountingVoucherLines { get; set; } = new List<AccountingVoucherLine>();

    public virtual User? ApprovedByNavigation { get; set; }

    public virtual ChartOfAccount? CashBankAccount { get; set; }

    public virtual User CreatedByNavigation { get; set; } = null!;

    public virtual ProjectLabel? Label { get; set; }

    public virtual ICollection<PaymentAllocation> PaymentAllocations { get; set; } = new List<PaymentAllocation>();
}
