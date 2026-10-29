using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class WorkPackage
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int ProjectId { get; set; }

    public string Name { get; set; } = null!;

    public string? Code { get; set; }

    public string? Description { get; set; }

    public double? EstimatedHours { get; set; }

    public string? Status { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual Project Project { get; set; } = null!;

    public virtual ICollection<ProjectTask> ProjectTasks { get; set; } = new List<ProjectTask>();
}
