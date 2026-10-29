using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class TimesheetEntry
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int WeekId { get; set; }

    public DateOnly Day { get; set; }

    public string? Project { get; set; }

    public string Task { get; set; } = null!;

    public double Hours { get; set; }

    public string? Description { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual TimesheetWeek Week { get; set; } = null!;
}
