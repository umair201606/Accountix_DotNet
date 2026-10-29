using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class PaymentAllocation
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int VoucherId { get; set; }

    public int VoucherLineId { get; set; }

    public string DocType { get; set; } = null!;

    public int DocId { get; set; }

    public string PartyType { get; set; } = null!;

    public int PartyId { get; set; }

    public decimal Amount { get; set; }

    public string? Note { get; set; }

    public int? CreatedBy { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual User? CreatedByNavigation { get; set; }

    public virtual AccountingVoucher Voucher { get; set; } = null!;

    public virtual AccountingVoucherLine VoucherLine { get; set; } = null!;
}
