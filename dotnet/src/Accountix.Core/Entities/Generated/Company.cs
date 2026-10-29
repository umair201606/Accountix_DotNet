using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class Company
{
    public int Id { get; set; }

    public string Name { get; set; } = null!;

    public string Slug { get; set; } = null!;

    public bool? IsActive { get; set; }

    public string? PlanName { get; set; }

    public int? MaxMembers { get; set; }

    public int? CreatedBy { get; set; }

    public bool? ModHrEnabled { get; set; }

    public bool? ModInventoryEnabled { get; set; }

    public bool? ModInvoicingEnabled { get; set; }

    public bool? ModFinanceEnabled { get; set; }

    public bool? ModAccountingEnabled { get; set; }

    public bool? ModFbrEnabled { get; set; }

    public bool? ModFixedAssetsEnabled { get; set; }

    public bool? ModExecutiveEnabled { get; set; }

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

    public virtual ICollection<CompanyInvitation> CompanyInvitations { get; set; } = new List<CompanyInvitation>();

    public virtual ICollection<CompanyMembership> CompanyMemberships { get; set; } = new List<CompanyMembership>();

    public virtual User? CreatedByNavigation { get; set; }
}
