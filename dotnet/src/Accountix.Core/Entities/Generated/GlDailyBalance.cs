using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class GlDailyBalance
{
    public int Id { get; set; }

    public int CompanyId { get; set; }

    public int AccountId { get; set; }

    public int LabelId { get; set; }

    public DateOnly Day { get; set; }

    public decimal Debit { get; set; }

    public decimal Credit { get; set; }

    public int LineCount { get; set; }
}
