using System.Text.RegularExpressions;
using Accountix.Platform.Provisioning;
using Accountix.Platform.Services;

namespace Accountix.Platform.Controllers;

/// <summary>
/// shared/routes/portal.py: the companies a person created, the companies
/// where they hold a role, the invitations waiting for them; creating a company.
/// Platform tables only (companies, memberships, invitations, limits).
/// </summary>
[Area("Platform")]
[SignedIn]
[Route("portal")]
public sealed partial class PortalController(AccountixDbContext db, FirstUse firstUse, CompanySetup setup,
                                             Services.SignInService signIn, TimeProvider time) : Controller
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,60}$")]
    public static partial Regex SlugRe();

    public sealed record PortalModel(
        string UserName, IReadOnlyList<Company> Owned,
        IReadOnlyList<(CompanyMembership M, Company? C, string Role)> Memberships,
        IReadOnlyList<(CompanyInvitation I, Company? C, string Role)> Invites,
        int QuotaUsed, int QuotaTotal);

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var me = HttpContext.Access()!.User;
        string email = (me.Email ?? "").ToLowerInvariant();
        var owned = await db.Companies.AsNoTracking().Where(c => c.CreatedBy == me.Id).OrderBy(c => c.Name).ToListAsync(ct);
        var memberships = await db.Memberships.AsNoTracking().Include(m => m.Company).Include(m => m.Role)
            .Where(m => m.UserId == me.Id).OrderBy(m => m.Status).ThenBy(m => m.Id).ToListAsync(ct);
        var invites = await db.Invitations.AsNoTracking().Include(i => i.Company).Include(i => i.Role)
            .Where(i => i.Email.ToLower() == email && i.Status == CompanyInvitation.Sent).OrderBy(i => i.Id).ToListAsync(ct);
        GlobalLimit limits;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            limits = await firstUse.GlobalLimitsAsync(ct);
            await tx.CommitAsync(ct);
        }
        return View("Index", new PortalModel(
            string.IsNullOrEmpty(me.FullName) ? me.Email ?? "" : me.FullName, owned,
            memberships.Select(m => (m, (Company?)m.Company, m.Role?.Name ?? "")).ToList(),
            invites.Select(i => (i, (Company?)i.Company, i.Role?.Name ?? "")).ToList(),
            owned.Count, limits.CompanyLimitFor(me)));
    }

    [HttpPost("create")]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        var me = HttpContext.Access()!.User;
        string name = Request.Form["name"].ToString().Trim();
        string slug = Request.Form["slug"].ToString().Trim().ToLowerInvariant();
        if (name.Length == 0)
            return Back("Company name is required.");
        if (slug.Length == 0 || !SlugRe().IsMatch(slug))
            return Back("Company address must be 2-62 characters: lowercase letters, digits and hyphens only.");
        if (await db.Companies.AnyAsync(c => c.Slug == slug, ct))
            return Back("That company address is already taken.");

        Company company;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.SerializeAsync("own-companies", me.Id);
            var limits = await firstUse.GlobalLimitsAsync(ct);
            int cap = limits.CompanyLimitFor(me);
            if (await db.Companies.CountAsync(c => c.CreatedBy == me.Id, ct) >= cap)
                return Back($"You have reached your company creation limit ({cap}).");
            var now = FirstUse.Micro(time.GetUtcNow().UtcDateTime);
            company = new Company
            {
                Name = name, Slug = slug, IsActive = true, PlanName = "free", CreatedBy = me.Id,
                ModHrEnabled = true, ModInventoryEnabled = true, ModInvoicingEnabled = true, ModFinanceEnabled = true,
                ModAccountingEnabled = true, ModFbrEnabled = true, ModFixedAssetsEnabled = true, ModExecutiveEnabled = true,
                Country = "Pakistan", FiscalYearStartMonth = 1, Currency = "PKR", CurrencySymbol = "Rs.",
                DateFormat = "Y-m-d", NumberFormat = "en", Timezone = "Asia/Karachi", CreatedAt = now, UpdatedAt = now,
            };
            db.Companies.Add(company);
            await db.SaveChangesAsync(ct);
            int? adminRole = await db.Roles.Where(r => r.Name == Role.Admin).Select(r => (int?)r.Id).FirstOrDefaultAsync(ct);
            if (adminRole is int rid)
                await HttpContext.RequestServices.GetRequiredService<Memberships>()
                    .AddAsync(company.Id, me.Id, rid, CompanyMembership.StatusActive, ct: ct);
            await tx.CommitAsync(ct);
        }
        await setup.ProvisionAsync(company.Id, ct);
        signIn.SetCompany(HttpContext, company.Id);
        this.Flash($"Company '{company.Name}' created — its books are ready.", "success");
        return Redirect("/dashboard/");
    }

    private RedirectResult Back(string message)
    {
        this.Flash(message, "error");
        return Redirect("/portal/");
    }
}
