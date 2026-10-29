using System.Text.Json;
using Accountix.Platform.Services;
using Accountix.SuperAdmin.Infrastructure;
using Accountix.SuperAdmin.Services;

namespace Accountix.SuperAdmin.Controllers;

/// <summary>
/// The console's dashboard and Platform limits (superadmin.index), the super
/// admin's own books (superadmin.my_companies) and the platform-wide audit
/// trail (superadmin.audit).
/// </summary>
[Area("SuperAdmin")]
[Route("superadmin")]
[SuperAdminOnly]
public sealed class ConsoleController(AccountixDbContext db, FirstUse firstUse, ConsoleQueries queries,
                                      ConsoleAccounts accounts, SignInService signIn) : Controller
{
    private User Me => HttpContext.Access()!.User;

    // ── Dashboard + global limits ───────────────────────────────────────

    public sealed record IndexModel(GlobalLimit Limits, int CompanyCount, int UserCount, int MembershipCount);

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var limits = await firstUse.GlobalLimitsAsync(ct);
        ViewData["Nav"] = "index";
        return View("Index", new IndexModel(limits,
            await db.Companies.CountAsync(ct),
            await db.Users.CountAsync(ct),
            await db.Memberships.CountAsync(ct)));
    }

    [HttpPost("")]
    public async Task<IActionResult> IndexPost(CancellationToken ct)
    {
        var form = Request.Form;
        // int(request.form.get(name) or default): a blank field takes the default.
        static bool Read(IFormCollection f, string name, int fallback, out int value)
        {
            string raw = Py.Form(f, name);
            if (raw.Length == 0) { value = fallback; return true; }
            return Py.TryInt(raw, out value);
        }
        if (!Read(form, "max_companies_per_user", 3, out int perUser) || !Read(form, "default_max_members", 25, out int members))
        {
            this.Flash("Limits must be whole numbers.", "error");
            return Redirect("/superadmin/");
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var limits = await firstUse.GlobalLimitsAsync(ct);
        limits.MaxCompaniesPerUser = Math.Max(1, perUser);
        limits.DefaultMaxMembers = Math.Max(1, members);
        limits.UpdatedBy = Me.Id;
        db.TouchIfChanged(limits, () => limits.UpdatedAt = accounts.UtcNow);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash("Global limits updated.", "success");
        return Redirect("/superadmin/");
    }

    // ── My Books ────────────────────────────────────────────────────────

    [HttpGet("my-companies")]
    public async Task<IActionResult> MyCompanies(CancellationToken ct)
    {
        var limits = await firstUse.GlobalLimitsAsync(ct);
        ViewData["Nav"] = "my";
        return View("MyCompanies", await queries.MyCompaniesAsync(Me, limits, ct));
    }

    [HttpPost("my-companies")]
    public async Task<IActionResult> MyCompaniesPost(CancellationToken ct)
    {
        var form = Request.Form;
        string name = Py.Strip(Py.Form(form, "name"));
        string slug = Py.Strip(Py.Form(form, "slug")).ToLowerInvariant();
        if (name.Length == 0)
            return Refuse("Company name is required.");
        if (slug.Length == 0 || !Py.SlugRe().IsMatch(slug))
            return Refuse("Company address must be 2-62 characters: lowercase letters, digits and hyphens only.");
        if (await db.Companies.AnyAsync(c => c.Slug == slug, ct))
            return Refuse("That company address is already taken.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Queued per person (serialize("own-companies", user), as Python's
        // my_companies and portal create): two creations at once cannot both
        // pass the count.
        await db.SerializeAsync("own-companies", Me.Id);
        var limits = await firstUse.GlobalLimitsAsync(ct);
        int cap = limits.CompanyLimitFor(Me);
        if (await db.Companies.CountAsync(c => c.CreatedBy == Me.Id, ct) >= cap)
            return Refuse($"You have reached your company creation limit ({cap}).");

        var company = await accounts.CreateOwnCompanyAsync(name, slug, Me.Id, ct);
        signIn.SetCompany(HttpContext, company.Id);
        this.Flash($"Company '{company.Name}' created — its books are ready.", "success");
        return Redirect("/dashboard/");
    }

    private RedirectResult Refuse(string message)
    {
        this.Flash(message, "error");
        return Redirect("/superadmin/my-companies/");
    }

    // ── Audit trail ─────────────────────────────────────────────────────

    /// <summary>shared/audit.py ACTIONS.</summary>
    public static readonly IReadOnlyList<string> Actions =
        ["create", "update", "delete", "approve", "unapprove", "post", "unpost", "refused", "login", "login_failed", "logout"];

    public const int PageSize = 50;

    public sealed record Change(string Field, JsonElement? Old, JsonElement? New);

    public sealed record AuditRowView(AuditLog Row, IReadOnlyList<Change> Changes);

    public sealed record AuditModel(
        IReadOnlyList<AuditRowView> Rows, int Total, int Page, int Pages,
        IReadOnlyList<(int Id, string Name)> Companies, IReadOnlyDictionary<int, string> CompanyNames,
        IReadOnlyList<KeyValuePair<string, string>> Args)
    {
        public string Arg(string key) => Args.FirstOrDefault(a => a.Key == key).Value ?? "";
        public bool HasArg(string key) => Args.Any(a => a.Key == key);
    }

    /// <summary>
    /// Every company's activity plus platform events (console sign-ins,
    /// failed sign-ins) that belong to no company: 50 a page, newest first,
    /// filtered by company / "platform", action and a search over reference,
    /// summary, user and IP.
    /// </summary>
    [HttpGet("audit")]
    public async Task<IActionResult> Audit(CancellationToken ct)
    {
        var query = Request.Query;
        string? First(string key) => query.TryGetValue(key, out var v) && v.Count > 0 ? v[0] : null;

        var companies = await db.Companies.AsNoTracking().OrderBy(c => c.Name)
            .Select(c => new { c.Id, c.Name }).ToListAsync(ct);
        IQueryable<AuditLog> q = db.AuditLogs.AsNoTracking();
        string cid = First("company_id") ?? "";
        if (cid == "platform")
            q = q.Where(a => a.CompanyId == null);
        else if (cid.Length > 0 && cid.All(char.IsAsciiDigit))
        {
            if (int.TryParse(cid, out int id))
                q = q.Where(a => a.CompanyId == id);
            else
                q = q.Where(a => false);         // a number no company can have
        }
        if (First("action") is { } action && Actions.Contains(action))
            q = q.Where(a => a.Action == action);
        string term = Py.Strip(First("q"));
        if (term.Length > 0)
        {
            string like = $"%{term}%";
            q = q.Where(a => EF.Functions.ILike(a.Reference!, like) || EF.Functions.ILike(a.Summary!, like)
                             || EF.Functions.ILike(a.UserName!, like) || EF.Functions.ILike(a.IpAddress!, like));
        }
        // args.get("page", 1, type=int) or 1, at least 1
        int page = Py.TryInt(First("page"), out int p) && p != 0 ? p : 1;
        page = Math.Max(page, 1);

        int total = await q.CountAsync(ct);
        long skip = (long)(page - 1) * PageSize;
        var rows = skip > int.MaxValue ? [] : await q.OrderByDescending(a => a.Id).Skip((int)skip).Take(PageSize).ToListAsync(ct);

        // request.args.to_dict(): first value of each key, in the request's order.
        var args = new List<KeyValuePair<string, string>>();
        foreach (var (k, v) in query)
            args.Add(new(k, v.Count > 0 ? v[0] ?? "" : ""));

        ViewData["Nav"] = "audit";
        return View("Audit", new AuditModel(
            rows.Select(r => new AuditRowView(r, DecodeChanges(r.Changes))).ToList(),
            total, page, Math.Max((total + PageSize - 1) / PageSize, 1),
            companies.Select(c => (c.Id, c.Name)).ToList(),
            companies.ToDictionary(c => c.Id, c => c.Name),
            args));
    }

    /// <summary>shared/audit.py decode_changes: [{field, old, new}] (an update) or one entry per (field, value).</summary>
    public static IReadOnlyList<Change> DecodeChanges(string? changes)
    {
        if (string.IsNullOrEmpty(changes)) return [];
        JsonElement data;
        try
        {
            using var doc = JsonDocument.Parse(changes);
            data = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return [];
        }
        static string Field(JsonElement? f) =>
            (f is { } v ? Py.Str(v) : "").Replace("_", " ");
        static JsonElement? Get(JsonElement o, string key) =>
            o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v) ? v : null;
        if (data.ValueKind == JsonValueKind.Array)
            return data.EnumerateArray()
                .Select(c => new Change(Field(Get(c, "field")), Get(c, "old"), Get(c, "new")))
                .ToList();
        if (data.ValueKind == JsonValueKind.Object)
            return data.EnumerateObject().Select(p => new Change(p.Name.Replace("_", " "), null, p.Value)).ToList();
        return [];
    }
}
