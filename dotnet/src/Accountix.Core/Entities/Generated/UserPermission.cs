using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class UserPermission
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int UserId { get; set; }

    public string Resource { get; set; } = null!;

    public bool? CanView { get; set; }

    public bool? CanCreate { get; set; }

    public bool? CanEdit { get; set; }

    public bool? CanApprove { get; set; }

    public bool? CanDelete { get; set; }

    public virtual User User { get; set; } = null!;
}
