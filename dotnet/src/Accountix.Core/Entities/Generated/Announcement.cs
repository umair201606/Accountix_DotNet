using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class Announcement
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string Title { get; set; } = null!;

    public string Content { get; set; } = null!;

    public string? Priority { get; set; }

    public int AuthorId { get; set; }

    public bool? Pinned { get; set; }

    public DateOnly? ExpiresAt { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public virtual User Author { get; set; } = null!;
}
