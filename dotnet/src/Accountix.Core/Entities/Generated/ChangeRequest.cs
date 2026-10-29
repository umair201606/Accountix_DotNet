using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class ChangeRequest
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int UserId { get; set; }

    public string FieldName { get; set; } = null!;

    public string? OldValue { get; set; }

    public string NewValue { get; set; } = null!;

    public string? Status { get; set; }

    public int? ReviewedBy { get; set; }

    public string? ReviewNotes { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? ReviewedAt { get; set; }

    public virtual User? ReviewedByNavigation { get; set; }

    public virtual User User { get; set; } = null!;
}
