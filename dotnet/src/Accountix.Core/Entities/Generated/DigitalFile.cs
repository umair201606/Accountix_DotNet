using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class DigitalFile
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int UserId { get; set; }

    public int? CategoryId { get; set; }

    public string Title { get; set; } = null!;

    public string Filename { get; set; } = null!;

    public string OriginalName { get; set; } = null!;

    public int? FileSize { get; set; }

    public string? MimeType { get; set; }

    public string? Notes { get; set; }

    public DateOnly? ExpiryDate { get; set; }

    public bool? IsVerified { get; set; }

    public int? VerifiedBy { get; set; }

    public DateTime? VerifiedAt { get; set; }

    public DateTime? UploadedAt { get; set; }

    public virtual FileCategory? Category { get; set; }

    public virtual User User { get; set; } = null!;

    public virtual User? VerifiedByNavigation { get; set; }
}
