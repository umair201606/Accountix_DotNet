using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class FbrInvoiceRecord
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int InvInvoiceId { get; set; }

    public string? FbrInvoiceNumber { get; set; }

    public string? FbrSubmissionId { get; set; }

    public string? Status { get; set; }

    public string? RequestJson { get; set; }

    public string? ResponseJson { get; set; }

    public string? ErrorMessage { get; set; }

    public string? InvoiceType { get; set; }

    public int? AttemptCount { get; set; }

    public DateTime? SubmittedAt { get; set; }

    public int? SubmittedBy { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual InvInvoice InvInvoice { get; set; } = null!;

    public virtual User? SubmittedByNavigation { get; set; }
}
