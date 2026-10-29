using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class GlobalLimit
{
    public int Id { get; set; }

    public int MaxCompaniesPerUser { get; set; }

    public int DefaultMaxMembers { get; set; }

    public int? UpdatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public virtual User? UpdatedByNavigation { get; set; }
}
