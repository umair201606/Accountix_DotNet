using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class InventorySetting
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string? ValuationMethod { get; set; }

    public bool? AllowNegativeStock { get; set; }

    public int? DecimalPlaces { get; set; }

    public bool? AutoGenerateVouchers { get; set; }

    public string? PurchaseFlow { get; set; }

    public string? SalesFlow { get; set; }

    public int? DefaultCogsAccountId { get; set; }

    public int? DefaultInventoryAccountId { get; set; }

    public int? DefaultReturnAccountId { get; set; }

    public bool? PerLineLabelingVoucher { get; set; }

    public bool? PerLineLabelingInvoice { get; set; }

    public int? DefaultVoucherLabelId { get; set; }

    public int? DefaultInvoiceLabelId { get; set; }

    public virtual ChartOfAccount? DefaultCogsAccount { get; set; }

    public virtual ChartOfAccount? DefaultInventoryAccount { get; set; }

    public virtual ProjectLabel? DefaultInvoiceLabel { get; set; }

    public virtual ChartOfAccount? DefaultReturnAccount { get; set; }

    public virtual ProjectLabel? DefaultVoucherLabel { get; set; }
}
