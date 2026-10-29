using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class Project
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string Name { get; set; } = null!;

    public string? Code { get; set; }

    public string? Description { get; set; }

    public string? Department { get; set; }

    public int? ProjectManagerId { get; set; }

    public DateOnly? StartDate { get; set; }

    public DateOnly? EndDate { get; set; }

    public string? Status { get; set; }

    public bool? IsBillable { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual User? ProjectManager { get; set; }

    public virtual ICollection<WorkPackage> WorkPackages { get; set; } = new List<WorkPackage>();
}
