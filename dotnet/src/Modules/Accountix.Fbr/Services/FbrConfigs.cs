namespace Accountix.Fbr.Services;

/// <summary>The company's FBR settings row (shared/models/fbr.py FBRConfig).</summary>
public sealed class FbrConfigs(AccountixDbContext db, CompanyClock clock)
{
    public const string SandboxUrl = "https://sandbox.fbr.gov.pk/di/api";
    public const string LiveUrl = "https://di.fbr.gov.pk/di/api";

    /// <summary>
    /// FBRConfig.get() for reading: the company's first row by id, or an
    /// unsaved row with the model's defaults. Python flushes that default row
    /// and never commits it on these paths, so nothing is kept either way.
    /// </summary>
    public async Task<FbrConfig> GetAsync(CancellationToken ct = default) =>
        await db.Set<FbrConfig>().OrderBy(c => c.Id).FirstOrDefaultAsync(ct) ?? Defaults();

    /// <summary>
    /// FBRConfig.get() for writing (inside the caller's transaction): the
    /// first row, else one made under serialize("first-use", "fbr_config",
    /// company) so two first requests make one row, as Python does.
    /// </summary>
    public async Task<FbrConfig> GetOrCreateAsync(CancellationToken ct = default)
    {
        var c = await db.Set<FbrConfig>().OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (c is not null)
            return c;
        await db.SerializeAsync("first-use", "fbr_config", db.Company.CompanyId);
        c = await db.Set<FbrConfig>().OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (c is not null)
            return c;
        c = Defaults();
        db.Add(c);
        await db.SaveChangesAsync(ct);
        return c;
    }

    private FbrConfig Defaults()
    {
        var now = clock.UtcNow;
        return new FbrConfig
        {
            Environment = "sandbox",
            AccessToken = "",
            SellerNtn = "",
            SellerStrn = "",
            SellerCnic = "",
            SellerName = "",
            SellerBusinessName = "",
            SellerAddress = "",
            SellerPhone = "",
            SellerEmail = "",
            IsActive = false,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>The routes' "is FBR set up" check: active and a token.</summary>
    public static bool Configured(FbrConfig c) => c.IsActive == true && !string.IsNullOrEmpty(c.AccessToken);

    public static bool IsSandbox(FbrConfig c) => c.Environment == "sandbox";

    public static string BaseUrl(FbrConfig c) => IsSandbox(c) ? SandboxUrl : LiveUrl;
}
