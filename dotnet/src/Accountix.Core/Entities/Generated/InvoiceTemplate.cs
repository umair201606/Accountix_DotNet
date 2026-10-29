using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class InvoiceTemplate
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string Name { get; set; } = null!;

    public string Type { get; set; } = null!;

    public string BodyHtml { get; set; } = null!;

    public bool? IsDefault { get; set; }

    public string? Design { get; set; }

    public string? AccentColor { get; set; }

    public string? OptionsJson { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public virtual ICollection<ReportSetting> ReportSettingPurchaseTemplates { get; set; } = new List<ReportSetting>();

    public virtual ICollection<ReportSetting> ReportSettingSalesTemplates { get; set; } = new List<ReportSetting>();
}
