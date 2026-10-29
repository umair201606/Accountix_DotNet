using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class Role
{
    public int Id { get; set; }

    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    public virtual ICollection<ButtonPermission> ButtonPermissions { get; set; } = new List<ButtonPermission>();

    public virtual ICollection<CompanyInvitation> CompanyInvitations { get; set; } = new List<CompanyInvitation>();

    public virtual ICollection<CompanyMembership> CompanyMemberships { get; set; } = new List<CompanyMembership>();

    public virtual ICollection<Permission> Permissions { get; set; } = new List<Permission>();

    public virtual ICollection<User> Users { get; set; } = new List<User>();
}
