using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class ReportSetting
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string? PlStructureJson { get; set; }

    public int? PlDetailRows { get; set; }

    public string? InvoicePartyMode { get; set; }

    public string? PurchasePartyMode { get; set; }

    public string? SalesPartyMode { get; set; }

    public string? PurchaseTemplateText { get; set; }

    public string? SalesTemplateText { get; set; }

    public int? PurchaseTemplateId { get; set; }

    public int? SalesTemplateId { get; set; }

    public string? CashFlowMethod { get; set; }

    public decimal? TwcfCashFloor { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public virtual InvoiceTemplate? PurchaseTemplate { get; set; }

    public virtual InvoiceTemplate? SalesTemplate { get; set; }
}
