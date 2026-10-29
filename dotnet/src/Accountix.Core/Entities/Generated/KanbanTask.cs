using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class KanbanTask
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int BoardId { get; set; }

    public string Title { get; set; } = null!;

    public string? Description { get; set; }

    public string? Status { get; set; }

    public string? Priority { get; set; }

    public int? AssigneeId { get; set; }

    public DateOnly? DueDate { get; set; }

    public int? Position { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public virtual User? Assignee { get; set; }

    public virtual KanbanBoard Board { get; set; } = null!;
}
