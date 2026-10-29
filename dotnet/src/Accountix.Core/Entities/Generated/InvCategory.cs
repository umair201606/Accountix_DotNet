using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class InvCategory
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    public int? ParentId { get; set; }

    public bool? IsActive { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual ICollection<CategoryRevenueAccount> CategoryRevenueAccounts { get; set; } = new List<CategoryRevenueAccount>();

    public virtual ICollection<InvProduct> InvProducts { get; set; } = new List<InvProduct>();

    public virtual ICollection<InvCategory> InverseParent { get; set; } = new List<InvCategory>();

    public virtual InvCategory? Parent { get; set; }
}
