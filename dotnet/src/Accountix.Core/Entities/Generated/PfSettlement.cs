using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class PfSettlement
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public int UserId { get; set; }

    public decimal? TotalEmployeeContrib { get; set; }

    public decimal? TotalEmployerContrib { get; set; }

    public decimal? TotalProfitDistributed { get; set; }

    public decimal? OutstandingLoan { get; set; }

    public decimal? NetSettlement { get; set; }

    public string? Status { get; set; }

    public int? SettledBy { get; set; }

    public DateTime? SettledAt { get; set; }

    public string? PdfStatement { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual User? SettledByNavigation { get; set; }

    public virtual User User { get; set; } = null!;
}
