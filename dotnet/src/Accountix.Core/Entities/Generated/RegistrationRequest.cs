using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class RegistrationRequest
{
    public int Id { get; set; }

    public string FullName { get; set; } = null!;

    public string Email { get; set; } = null!;

    public string? Phone { get; set; }

    public string PasswordHash { get; set; } = null!;

    public string? CompanyName { get; set; }

    public string? Notes { get; set; }

    public string Status { get; set; } = null!;

    public bool Seen { get; set; }

    public int? ReviewedBy { get; set; }

    public DateTime? ReviewedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual User? ReviewedByNavigation { get; set; }
}
