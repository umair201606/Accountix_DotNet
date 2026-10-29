using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class AssetTransfer
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string VoucherNumber { get; set; } = null!;

    public string Direction { get; set; } = null!;

    public int AssetId { get; set; }

    public int? ProductId { get; set; }

    public string? NewProductName { get; set; }

    public int? SourceProductId { get; set; }

    public decimal? TransferAmount { get; set; }

    public decimal? Quantity { get; set; }

    public string? Description { get; set; }

    public DateOnly? TransferDate { get; set; }

    public string? Status { get; set; }

    public int CreatedBy { get; set; }

    public int? ApprovedBy { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? ApprovedAt { get; set; }

    public virtual User? ApprovedByNavigation { get; set; }

    public virtual FixedAsset Asset { get; set; } = null!;

    public virtual User CreatedByNavigation { get; set; } = null!;
}
