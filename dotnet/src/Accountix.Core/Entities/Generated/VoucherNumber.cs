using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class VoucherNumber
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string Prefix { get; set; } = null!;

    public int NextNumber { get; set; }
}
