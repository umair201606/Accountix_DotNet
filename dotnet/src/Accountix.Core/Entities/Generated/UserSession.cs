using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class UserSession
{
    public int Id { get; set; }

    public string TokenHash { get; set; } = null!;

    public int UserId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime LastSeenAt { get; set; }

    public DateTime? EndedAt { get; set; }

    public string? EndReason { get; set; }

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    public virtual User User { get; set; } = null!;
}
