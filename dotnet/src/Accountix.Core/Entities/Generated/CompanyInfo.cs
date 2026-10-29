using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class CompanyInfo
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string? CompanyName { get; set; }

    public string? Address { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    public string? Country { get; set; }

    public string? Phone { get; set; }

    public string? Email { get; set; }

    public string? Website { get; set; }

    public string? TaxId { get; set; }

    public string? RegistrationNumber { get; set; }

    public string? LogoUrl { get; set; }

    public string? BankName { get; set; }

    public string? BankAccountTitle { get; set; }

    public string? BankAccountNumber { get; set; }

    public int? FiscalYearStartMonth { get; set; }

    public string? Currency { get; set; }

    public string? CurrencySymbol { get; set; }

    public string? DateFormat { get; set; }

    public string? NumberFormat { get; set; }

    public string? Timezone { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
