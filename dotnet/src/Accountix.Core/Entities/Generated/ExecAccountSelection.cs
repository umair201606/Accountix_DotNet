using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class ExecAccountSelection
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int AccountId { get; set; }

    public bool IncludeChildren { get; set; }

    public int? CreatedBy { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual ChartOfAccount Account { get; set; } = null!;

    public virtual User? CreatedByNavigation { get; set; }
}
