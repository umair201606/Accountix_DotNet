using Accountix.Fbr.Services;

namespace Accountix.Fbr.Controllers;

/// <summary>FBR settings (fbr_app/routes/settings.py, blueprint fbr_settings, /fbr/settings): company admins only.</summary>
[Area("Fbr")]
[Module(Modules.Fbr)]
[Route("fbr/settings")]
public sealed class FbrSettingsController(AccountixDbContext db, FbrConfigs configs, FbrClient client, CompanyClock clock) : Controller
{
    public const string Section = "fbr_settings";
    private const string Hub = "/dashboard/";

    private bool IsAdmin => HttpContext.Access()!.IsAdmin;

    [HttpGet("")]
    [Right(Section, Rights.View)]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        if (!IsAdmin)
        {
            this.Flash("Only administrators can access FBR settings.", "error");
            return Redirect(Hub);
        }
        return View("Index", await configs.GetAsync(ct));
    }

    [HttpPost("")]
    [Right(Section, Rights.Edit)]
    public async Task<IActionResult> Save(CancellationToken ct)
    {
        if (!IsAdmin)
        {
            this.Flash("Only administrators can access FBR settings.", "error");
            return Redirect(Hub);
        }
        var form = Request.Form;
        // request.form.get(name, default): the first value when the field was sent.
        string Field(string name, string fallback = "") => form.TryGetValue(name, out var v) && v.Count > 0 ? v[0] ?? "" : fallback;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var config = await configs.GetOrCreateAsync(ct);
        config.Environment = Field("environment", "sandbox");
        config.AccessToken = Field("access_token");
        config.SellerNtn = Field("seller_ntn");
        config.SellerStrn = Field("seller_strn");
        config.SellerCnic = Field("seller_cnic");
        config.SellerName = Field("seller_name");
        config.SellerBusinessName = Field("seller_business_name");
        config.SellerAddress = Field("seller_address");
        config.SellerPhone = Field("seller_phone");
        config.SellerEmail = Field("seller_email");
        config.IsActive = Field("is_active") == "on";
        // updated_at's onupdate: only when the row really changes.
        if (db.Entry(config).Properties.Any(p => p.IsModified))
            config.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash("FBR configuration saved.", "success");
        return Redirect("/fbr/settings/");
    }

    [HttpPost("test-connection")]
    [Right(Section, Rights.Edit)]
    public async Task<IActionResult> TestConnection(CancellationToken ct)
    {
        if (!IsAdmin)
            return FbrDashboardController.Answer(new PyDict { ["ok"] = false, ["error"] = "Admin only" }, 403);
        var (ok, message) = await client.HeartbeatAsync(await configs.GetAsync(ct));
        return FbrDashboardController.Answer(ok
            ? new PyDict { ["ok"] = true, ["message"] = "Connection successful: " + message }
            : new PyDict { ["ok"] = false, ["message"] = "Connection failed: " + message });
    }
}
