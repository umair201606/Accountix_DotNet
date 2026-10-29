using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class ProjectTask
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int WorkPackageId { get; set; }

    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    public double? EstimatedHours { get; set; }

    public int? AssignedTo { get; set; }

    public DateOnly? DueDate { get; set; }

    public string? Status { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual User? AssignedToNavigation { get; set; }

    public virtual WorkPackage WorkPackage { get; set; } = null!;
}
