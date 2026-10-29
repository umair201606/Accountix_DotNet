using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class PayrollSetting
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public decimal? OvertimeMultiplier { get; set; }

    public decimal? OvertimeHoursBasis { get; set; }

    public bool? EobiEnabled { get; set; }

    public decimal? EobiWageBase { get; set; }

    public decimal? EobiEmployeePct { get; set; }

    public decimal? EobiEmployerPct { get; set; }

    public decimal? SurchargeThreshold { get; set; }

    public decimal? SurchargePct { get; set; }

    public bool? GratuityEnabled { get; set; }

    public string? GratuityBasis { get; set; }

    public decimal? GratuityDaysPerYear { get; set; }

    public bool? LeaveEncashmentEnabled { get; set; }
}
