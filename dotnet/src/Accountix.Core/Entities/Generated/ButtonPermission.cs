using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class ButtonPermission
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int RoleId { get; set; }

    public string ButtonCode { get; set; } = null!;

    public bool? IsGranted { get; set; }

    public string Module { get; set; } = null!;

    public string? Description { get; set; }

    public virtual Role Role { get; set; } = null!;
}
