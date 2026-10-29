using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class AuditLog
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public DateTime CreatedAt { get; set; }

    public int? UserId { get; set; }

    public string? UserName { get; set; }

    public string Action { get; set; } = null!;

    public string? Module { get; set; }

    public string? EntityType { get; set; }

    public int? EntityId { get; set; }

    public string? Reference { get; set; }

    public string? Summary { get; set; }

    public string? Changes { get; set; }

    public string? IpAddress { get; set; }

    public string? Path { get; set; }
}
