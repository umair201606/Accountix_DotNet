using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class TeamEvent
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string Title { get; set; } = null!;

    public string? Description { get; set; }

    public DateOnly EventDate { get; set; }

    public TimeOnly? StartTime { get; set; }

    public TimeOnly? EndTime { get; set; }

    public string? Location { get; set; }

    public string? EventType { get; set; }

    public int CreatedBy { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual User CreatedByNavigation { get; set; } = null!;
}
