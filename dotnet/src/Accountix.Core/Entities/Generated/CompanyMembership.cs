using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class CompanyMembership
{
    public int Id { get; set; }

    public int CompanyId { get; set; }

    public int UserId { get; set; }

    public int RoleId { get; set; }

    public string Status { get; set; } = null!;

    public string? EmployeeCode { get; set; }

    public DateTime? JoinedAt { get; set; }

    public DateOnly? DateOfLeaving { get; set; }

    public string? ModuleFlags { get; set; }

    public int? ManagerId { get; set; }

    public string? Designation { get; set; }

    public string? Department { get; set; }

    public virtual Company Company { get; set; } = null!;

    public virtual Role Role { get; set; } = null!;

    public virtual User User { get; set; } = null!;
}
