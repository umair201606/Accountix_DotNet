using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class InvoiceSetting
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public decimal? DefaultSalesTaxPct { get; set; }

    public decimal? DefaultFurtherTaxPct { get; set; }

    public decimal? DefaultWithholdingTaxPct { get; set; }

    public decimal? DefaultDiscountPct { get; set; }

    public string? DefaultChargesMode { get; set; }

    public string? DefaultDiscountMode { get; set; }

    public string? DefaultTaxMode { get; set; }

    public bool? ShowDiscountColumn { get; set; }

    public bool? ShowChargesColumn { get; set; }

    public bool? ShowTaxColumn { get; set; }

    public bool? AutoAddLine { get; set; }

    public bool? RequireApproval { get; set; }

    public bool? AllowPartialPayment { get; set; }

    public string? DefaultPartyMode { get; set; }

    public decimal? OverInvoiceTolerancePct { get; set; }

    public string? WithholdingBase { get; set; }

    public bool? ShowFurtherTax { get; set; }

    public bool? ShowWithholdingTax { get; set; }

    public bool? ShowTransportBlock { get; set; }

    public bool? CreateFromOrdersEnabled { get; set; }

    public bool? PerLineDiscountEnabled { get; set; }

    public bool? PerLineTaxEnabled { get; set; }
}
