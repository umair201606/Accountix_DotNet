using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class ProjectLabel
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string Name { get; set; } = null!;

    public bool? IsActive { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual ICollection<AccountingVoucherLine> AccountingVoucherLines { get; set; } = new List<AccountingVoucherLine>();

    public virtual ICollection<AccountingVoucher> AccountingVouchers { get; set; } = new List<AccountingVoucher>();

    public virtual ICollection<ConsumptionVoucher> ConsumptionVouchers { get; set; } = new List<ConsumptionVoucher>();

    public virtual ICollection<FixedAsset> FixedAssets { get; set; } = new List<FixedAsset>();

    public virtual ICollection<InvInvoiceItem> InvInvoiceItems { get; set; } = new List<InvInvoiceItem>();

    public virtual ICollection<InvInvoice> InvInvoices { get; set; } = new List<InvInvoice>();

    public virtual ICollection<InvPurchaseInvoiceItem> InvPurchaseInvoiceItems { get; set; } = new List<InvPurchaseInvoiceItem>();

    public virtual ICollection<InvPurchaseInvoice> InvPurchaseInvoices { get; set; } = new List<InvPurchaseInvoice>();

    public virtual ICollection<InventorySetting> InventorySettingDefaultInvoiceLabels { get; set; } = new List<InventorySetting>();

    public virtual ICollection<InventorySetting> InventorySettingDefaultVoucherLabels { get; set; } = new List<InventorySetting>();

    public virtual ICollection<JournalLine> JournalLines { get; set; } = new List<JournalLine>();

    public virtual ICollection<ScrapVoucher> ScrapVouchers { get; set; } = new List<ScrapVoucher>();

    public virtual ICollection<StockAdjustmentVoucher> StockAdjustmentVouchers { get; set; } = new List<StockAdjustmentVoucher>();
}
