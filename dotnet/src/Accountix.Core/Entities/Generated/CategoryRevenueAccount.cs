using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class CategoryRevenueAccount
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int CategoryId { get; set; }

    public int AccountId { get; set; }

    public virtual ChartOfAccount Account { get; set; } = null!;

    public virtual InvCategory Category { get; set; } = null!;
}
