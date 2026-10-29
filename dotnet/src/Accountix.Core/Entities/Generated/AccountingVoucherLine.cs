using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class AccountingVoucherLine
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int VoucherId { get; set; }

    public int LineNo { get; set; }

    public int AccountId { get; set; }

    public string? Description { get; set; }

    public decimal? Debit { get; set; }

    public decimal? Credit { get; set; }

    public int? LabelId { get; set; }

    public virtual ChartOfAccount Account { get; set; } = null!;

    public virtual ProjectLabel? Label { get; set; }

    public virtual ICollection<PaymentAllocation> PaymentAllocations { get; set; } = new List<PaymentAllocation>();

    public virtual AccountingVoucher Voucher { get; set; } = null!;
}
