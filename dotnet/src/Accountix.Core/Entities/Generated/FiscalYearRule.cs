using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class FiscalYearRule
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int StartMonth { get; set; }

    public int StartDay { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
