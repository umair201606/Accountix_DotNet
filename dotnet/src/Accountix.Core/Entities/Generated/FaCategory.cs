using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class FaCategory
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    public int? DefaultUsefulLife { get; set; }

    public string? DefaultDepreciationMethod { get; set; }

    public decimal? DefaultSalvageValuePct { get; set; }

    public bool? IsActive { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual ICollection<FixedAsset> FixedAssets { get; set; } = new List<FixedAsset>();
}
