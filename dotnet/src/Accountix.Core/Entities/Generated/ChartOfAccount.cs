using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class ChartOfAccount
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string Type { get; set; } = null!;

    public int? ParentId { get; set; }

    public int Level { get; set; }

    public bool? IsFixed { get; set; }

    public bool? IsActive { get; set; }

    public string? Description { get; set; }

    public string? CashFlowActivity { get; set; }

    public string? PlSection { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual ICollection<AccountingVoucherLine> AccountingVoucherLines { get; set; } = new List<AccountingVoucherLine>();

    public virtual ICollection<AccountingVoucher> AccountingVouchers { get; set; } = new List<AccountingVoucher>();

    public virtual ICollection<AdditionalCharge> AdditionalCharges { get; set; } = new List<AdditionalCharge>();

    public virtual ICollection<BankReconciliation> BankReconciliations { get; set; } = new List<BankReconciliation>();

    public virtual ICollection<CategoryRevenueAccount> CategoryRevenueAccounts { get; set; } = new List<CategoryRevenueAccount>();

    public virtual ICollection<ChargeLedgerDefault> ChargeLedgerDefaults { get; set; } = new List<ChargeLedgerDefault>();

    public virtual ICollection<ConsumptionVoucher> ConsumptionVouchers { get; set; } = new List<ConsumptionVoucher>();

    public virtual ICollection<ExecAccountSelection> ExecAccountSelections { get; set; } = new List<ExecAccountSelection>();

    public virtual ICollection<FixedAsset> FixedAssetAccumDepAccounts { get; set; } = new List<FixedAsset>();

    public virtual ICollection<FixedAsset> FixedAssetAcquisitionCreditAccounts { get; set; } = new List<FixedAsset>();

    public virtual ICollection<FixedAsset> FixedAssetDepExpenseAccounts { get; set; } = new List<FixedAsset>();

    public virtual ICollection<FixedAsset> FixedAssetFixedAssetAccounts { get; set; } = new List<FixedAsset>();

    public virtual ICollection<InvInvoice> InvInvoices { get; set; } = new List<InvInvoice>();

    public virtual ICollection<InvPurchaseInvoice> InvPurchaseInvoices { get; set; } = new List<InvPurchaseInvoice>();

    public virtual ICollection<InvPurchaseOrder> InvPurchaseOrders { get; set; } = new List<InvPurchaseOrder>();

    public virtual ICollection<InvSalesOrder> InvSalesOrders { get; set; } = new List<InvSalesOrder>();

    public virtual ICollection<InvSalesReturn> InvSalesReturns { get; set; } = new List<InvSalesReturn>();

    public virtual ICollection<InventorySetting> InventorySettingDefaultCogsAccounts { get; set; } = new List<InventorySetting>();

    public virtual ICollection<InventorySetting> InventorySettingDefaultInventoryAccounts { get; set; } = new List<InventorySetting>();

    public virtual ICollection<InventorySetting> InventorySettingDefaultReturnAccounts { get; set; } = new List<InventorySetting>();

    public virtual ICollection<ChartOfAccount> InverseParent { get; set; } = new List<ChartOfAccount>();

    public virtual ICollection<JournalLine> JournalLines { get; set; } = new List<JournalLine>();

    public virtual ChartOfAccount? Parent { get; set; }

    public virtual ICollection<ScrapVoucher> ScrapVouchers { get; set; } = new List<ScrapVoucher>();

    public virtual ICollection<TaxRateAccount> TaxRateAccounts { get; set; } = new List<TaxRateAccount>();
}
