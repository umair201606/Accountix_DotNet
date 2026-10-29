using Accountix.Platform.Services;
using Accountix.SuperAdmin.Infrastructure;
using Accountix.SuperAdmin.Services;

namespace Accountix.SuperAdmin.Controllers;

/// <summary>
/// Manage Companies (superadmin.companies, company_edit) and member blocking
/// (superadmin.member_block).
/// </summary>
[Area("SuperAdmin")]
[Route("superadmin")]
[SuperAdminOnly]
public sealed class CompaniesController(AccountixDbContext db, FirstUse firstUse, ConsoleQueries queries,
                                        ConsoleAccounts accounts) : Controller
{
    private User Me => HttpContext.Access()!.User;

    /// <summary>Company.MODULE_COLUMNS: module key → entitlement column, in Python's order.</summary>
    public static readonly IReadOnlyList<(string Key, Action<Company, bool> Set)> ModuleColumns =
    [
        (Modules.Hr, (c, v) => c.ModHrEnabled = v),
        (Modules.Inventory, (c, v) => c.ModInventoryEnabled = v),
        (Modules.Invoicing, (c, v) => c.ModInvoicingEnabled = v),
        (Modules.Finance, (c, v) => c.ModFinanceEnabled = v),
        (Modules.Accounting, (c, v) => c.ModAccountingEnabled = v),
        (Modules.Fbr, (c, v) => c.ModFbrEnabled = v),
        (Modules.FixedAssets, (c, v) => c.ModFixedAssetsEnabled = v),
        (Modules.Executive, (c, v) => c.ModExecutiveEnabled = v),
    ];

    private RedirectResult ToList(string message)
    {
        this.Flash(message, "error");
        return Redirect("/superadmin/companies/");
    }

    [HttpGet("companies")]
    public async Task<IActionResult> Companies(CancellationToken ct)
    {
        ViewData["Nav"] = "companies";
        return View("Companies", await queries.CompaniesAsync(Me.Id, ct));
    }

    /// <summary>
    /// Creates the company and makes the given email its admin (and owner);
    /// a new email becomes a new account with every module on, its password
    /// typed or random and shown once. The books are provisioned as a
    /// portal-made company's are.
    /// </summary>
    [HttpPost("companies")]
    public async Task<IActionResult> CompaniesPost(CancellationToken ct)
    {
        var form = Request.Form;
        string name = Py.Strip(Py.Form(form, "name"));
        string slug = Py.Strip(Py.Form(form, "slug")).ToLowerInvariant();
        string plan = Py.Strip(Py.Form(form, "plan_name"));
        if (plan.Length == 0) plan = "free";
        string email = Py.Strip(Py.Form(form, "admin_email")).ToLowerInvariant();
        if (name.Length == 0 || slug.Length == 0 || email.Length == 0)
            return ToList("Company name, slug and admin email are required.");
        if (!Py.SlugRe().IsMatch(slug))
            return ToList("Company address must be 2-62 characters: lowercase letters, digits and hyphens only.");
        if (await db.Companies.AnyAsync(c => c.Slug == slug, ct))
            return ToList($"A company with slug '{slug}' already exists.");
        if (await accounts.RoleIdAsync(Role.Admin, ct) is not int adminRole)
            return ToList("Admin role is not seeded yet.");

        var admin = await db.Users.Where(u => u.Email.ToLower() == email).OrderBy(u => u.Id).FirstOrDefaultAsync(ct);
        string adminName = "";
        string? typed = null;
        if (admin is null)
        {
            typed = Py.Strip(Py.Form(form, "password"));
            string? problem = typed.Length > 0 ? AccountSecurity.PasswordProblem(typed) : null;
            if (problem is not null || !email.Contains('@'))
                return ToList(problem ?? "Enter a valid admin email.");
            if (await db.Users.AnyAsync(u => u.LoginId!.ToLower() == email, ct))
                return ToList($"The login id {email} is already taken by another account.");
            adminName = Py.Strip(Py.Form(form, "admin_name"));
            if (adminName.Length == 0) adminName = Py.Before(email, '@');
        }

        var made = await accounts.CreateConsoleCompanyAsync(name, slug, plan, admin, email, adminName, typed, adminRole, ct);
        string msg = $"Company '{name}' created. Admin: {email}.";
        if (made.CreatedUser)
            msg += $" New user — password: {made.Password}";
        this.Flash(msg, "success");
        return Redirect("/superadmin/companies/");
    }

    public sealed record EditModel(Company Company, IReadOnlyList<ConsoleQueries.MemberRow> Members, GlobalLimit Limits, int ActiveMembers);

    [HttpGet("companies/{companyId:int}")]
    public async Task<IActionResult> CompanyEdit(int companyId, CancellationToken ct)
    {
        var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId, ct);
        if (company is null)
            return ConsolePages.Error(404);
        ViewData["Nav"] = "companies";
        return View("CompanyEdit", new EditModel(company,
            await queries.MembersAsync(companyId, ct),
            await firstUse.GlobalLimitsAsync(ct),
            await queries.ActiveMemberCountAsync(companyId, ct)));
    }

    [HttpPost("companies/{companyId:int}")]
    public async Task<IActionResult> CompanyEditPost(int companyId, CancellationToken ct)
    {
        var company = await db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct);
        if (company is null)
            return ConsolePages.Error(404);
        var form = Request.Form;
        string Or(string field, string? fallback) => Py.Form(form, field) is { Length: > 0 } v ? v : fallback!;

        string mm = Py.Strip(Py.Form(form, "max_members"));
        int? maxMembers = null;
        if (mm.Length > 0)
        {
            if (!Py.TryInt(mm, out int n))
            {
                this.Flash("Max members must be a whole number.", "error");
                return Redirect($"/superadmin/companies/{company.Id}/");
            }
            maxMembers = n;
        }
        if (Py.Strip(Py.Form(form, "name")) is { Length: > 0 } name)
            company.Name = name;
        company.PlanName = Py.Strip(Py.Form(form, "plan_name")) is { Length: > 0 } plan ? plan : "free";
        company.MaxMembers = maxMembers;
        company.IsActive = Py.Form(form, "is_active") == "on";
        company.Address = Or("address", null);
        company.City = Or("city", null);
        company.Country = Or("country", "Pakistan");
        company.Phone = Or("phone", null);
        company.Email = Or("email", null);
        company.Website = Or("website", null);
        company.TaxId = Or("tax_id", null);
        company.RegistrationNumber = Or("registration_number", null);
        company.LogoUrl = Or("logo_url", null);
        company.Currency = Or("currency", "PKR");
        company.CurrencySymbol = Or("currency_symbol", "Rs.");
        // Module entitlement. An absent checkbox is off, so this form is the
        // whole truth for the company's modules every time it is saved.
        foreach (var (key, set) in ModuleColumns)
            set(company, Py.Form(form, $"mod_{key}") == "on");
        db.TouchIfChanged(company, () => company.UpdatedAt = accounts.UtcNow);
        await db.SaveChangesAsync(ct);
        this.Flash("Company updated.", "success");
        return Redirect($"/superadmin/companies/{company.Id}/");
    }

    /// <summary>
    /// Block or unblock one person's access to one company. The membership is
    /// kept (role, employee code, history) and only its status flips; every
    /// gate admits ACTIVE only. Queued per company (serialize("members", id),
    /// the key the Python app takes), so the owner / last-admin guards hold
    /// when several people act at once.
    /// </summary>
    [HttpPost("companies/{companyId:int}/members/{membershipId:int}/block")]
    public async Task<IActionResult> MemberBlock(int companyId, int membershipId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SerializeAsync("members", companyId);
        var m = await db.Memberships.FirstOrDefaultAsync(x => x.Id == membershipId, ct);
        if (m is null || m.CompanyId != companyId)
            return ConsolePages.Error(404);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == m.UserId, ct);
        string back = $"/superadmin/companies/{companyId}/";
        string msg;
        if (m.Status == CompanyMembership.Blocked)
        {
            m.Status = CompanyMembership.StatusActive;
            msg = "unblocked";
        }
        else if (m.Status == CompanyMembership.StatusActive)
        {
            // Blocking the owner or the last active admin would orphan the
            // company (member_remove refuses both for the same reason).
            var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId, ct);
            if (company is not null && m.UserId == company.CreatedBy)
            {
                this.Flash("The company owner cannot be blocked.", "error");
                return Redirect(back);
            }
            if (await accounts.RoleIdAsync(Role.Admin, ct) is int adminRole && m.RoleId == adminRole)
            {
                int activeAdmins = await db.Memberships.CountAsync(
                    x => x.CompanyId == m.CompanyId && x.Status == CompanyMembership.StatusActive && x.RoleId == adminRole, ct);
                if (activeAdmins <= 1)
                {
                    this.Flash("Cannot block the last active admin of the company.", "error");
                    return Redirect(back);
                }
            }
            m.Status = CompanyMembership.Blocked;
            msg = "blocked";
        }
        else
        {
            this.Flash("Only an active or blocked membership can be toggled.", "error");
            return Redirect(back);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"{(user is not null ? user.FullName : "Member")} {msg}.", "success");
        return Redirect(back);
    }
}
