using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class Permission
{
    public int Id { get; set; }

    public int RoleId { get; set; }

    public string Resource { get; set; } = null!;

    public bool? CanRead { get; set; }

    public bool? CanWrite { get; set; }

    public bool? CanDelete { get; set; }

    public virtual Role Role { get; set; } = null!;
}
