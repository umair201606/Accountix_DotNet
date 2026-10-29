using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class EmailLog
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string Recipient { get; set; } = null!;

    public string Subject { get; set; } = null!;

    public string? Body { get; set; }

    public string? Module { get; set; }

    public string? Status { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTime? SentAt { get; set; }
}
