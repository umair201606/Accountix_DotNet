using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class CompanyInvitation
{
    public int Id { get; set; }

    public int CompanyId { get; set; }

    public string Email { get; set; } = null!;

    public int RoleId { get; set; }

    public int? InvitedBy { get; set; }

    public string Status { get; set; } = null!;

    public DateTime? CreatedAt { get; set; }

    public virtual Company Company { get; set; } = null!;

    public virtual User? InvitedByNavigation { get; set; }

    public virtual Role Role { get; set; } = null!;
}
