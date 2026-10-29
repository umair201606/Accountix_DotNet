using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class FbrConfig
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string? Environment { get; set; }

    public string? AccessToken { get; set; }

    public DateTime? TokenExpiry { get; set; }

    public string? SellerNtn { get; set; }

    public string? SellerStrn { get; set; }

    public string? SellerCnic { get; set; }

    public string? SellerName { get; set; }

    public string? SellerBusinessName { get; set; }

    public string? SellerAddress { get; set; }

    public string? SellerPhone { get; set; }

    public string? SellerEmail { get; set; }

    public bool? IsActive { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
