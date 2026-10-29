using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class CompanyHoliday
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string Name { get; set; } = null!;

    public DateOnly HolidayDate { get; set; }

    public bool? IsRecurring { get; set; }

    public string? Department { get; set; }

    public string? Location { get; set; }

    public bool? IsActive { get; set; }

    public DateTime? CreatedAt { get; set; }
}
