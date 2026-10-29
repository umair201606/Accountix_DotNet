using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class KanbanBoard
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string Title { get; set; } = null!;

    public string? Description { get; set; }

    public int CreatedBy { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual User CreatedByNavigation { get; set; } = null!;

    public virtual ICollection<KanbanTask> KanbanTasks { get; set; } = new List<KanbanTask>();
}
