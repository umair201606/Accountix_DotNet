using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class TwcfLine
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string Direction { get; set; } = null!;

    public string Category { get; set; } = null!;

    public string Description { get; set; } = null!;

    public decimal Amount { get; set; }

    public DateOnly StartDate { get; set; }

    public string Frequency { get; set; } = null!;

    public int? DayOfWeek { get; set; }

    public int? DayOfMonth { get; set; }

    public int? Month { get; set; }

    public DateTime? CreatedAt { get; set; }
}
