using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class PfConfig
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public decimal? EmployeeContributionPct { get; set; }

    public decimal? EmployerContributionPct { get; set; }

    public string? ContributionBase { get; set; }

    public decimal? MaxLoanPercentage { get; set; }

    public decimal? InterestRate { get; set; }

    public int? MinServiceMonthsForLoan { get; set; }

    public bool? IsActive { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
