using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class FixedAsset
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string AssetCode { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    public int CategoryId { get; set; }

    public DateOnly PurchaseDate { get; set; }

    public decimal PurchaseCost { get; set; }

    public int UsefulLife { get; set; }

    public string DepreciationMethod { get; set; } = null!;

    public decimal? SalvageValue { get; set; }

    public decimal? AccumulatedDepreciation { get; set; }

    public decimal CurrentBookValue { get; set; }

    public string? Status { get; set; }

    public string? Location { get; set; }

    public string? AssignedTo { get; set; }

    public string? Vendor { get; set; }

    public string? SerialNumber { get; set; }

    public int? FixedAssetAccountId { get; set; }

    public int? AccumDepAccountId { get; set; }

    public int? DepExpenseAccountId { get; set; }

    public int? AcquisitionCreditAccountId { get; set; }

    public string? Notes { get; set; }

    public int? LabelId { get; set; }

    public bool? IsActive { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public virtual ChartOfAccount? AccumDepAccount { get; set; }

    public virtual ChartOfAccount? AcquisitionCreditAccount { get; set; }

    public virtual ICollection<AssetDepreciation> AssetDepreciations { get; set; } = new List<AssetDepreciation>();

    public virtual ICollection<AssetTransfer> AssetTransfers { get; set; } = new List<AssetTransfer>();

    public virtual FaCategory Category { get; set; } = null!;

    public virtual ChartOfAccount? DepExpenseAccount { get; set; }

    public virtual ChartOfAccount? FixedAssetAccount { get; set; }

    public virtual ProjectLabel? Label { get; set; }
}
