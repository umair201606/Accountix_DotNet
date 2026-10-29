using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class PerformanceReview
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int UserId { get; set; }

    public int ReviewerId { get; set; }

    public string ReviewPeriod { get; set; } = null!;

    public double? OverallScore { get; set; }

    public int? ProductivityRating { get; set; }

    public int? QualityRating { get; set; }

    public int? TeamworkRating { get; set; }

    public int? PunctualityRating { get; set; }

    public string? Strengths { get; set; }

    public string? Improvements { get; set; }

    public string? Feedback { get; set; }

    public string? Status { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual User Reviewer { get; set; } = null!;

    public virtual User User { get; set; } = null!;
}
