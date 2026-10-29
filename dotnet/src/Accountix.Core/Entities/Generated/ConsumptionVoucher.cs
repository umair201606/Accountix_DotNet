using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class ConsumptionVoucher
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string VoucherNumber { get; set; } = null!;

    public DateTime? Date { get; set; }

    public string? Department { get; set; }

    public string? Reason { get; set; }

    public int? ChargeAccountId { get; set; }

    public int? LabelId { get; set; }

    public string? Status { get; set; }

    public int CreatedBy { get; set; }

    public int? ApprovedBy { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? ApprovedAt { get; set; }

    public virtual User? ApprovedByNavigation { get; set; }

    public virtual ChartOfAccount? ChargeAccount { get; set; }

    public virtual ICollection<ConsumptionItem> ConsumptionItems { get; set; } = new List<ConsumptionItem>();

    public virtual User CreatedByNavigation { get; set; } = null!;

    public virtual ProjectLabel? Label { get; set; }
}
