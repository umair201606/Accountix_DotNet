using Accountix.Platform.Audit;
using Accountix.Platform.Provisioning;
using Accountix.Platform.Services;

namespace Accountix.Platform.Controllers;

/// <summary>
/// shared/routes/settings.py: the application's single settings module.
/// The page is open to every signed-in person; each section shows only when
/// the person has the matching module access (or is admin), and every write
/// re-checks the same predicate (<see cref="Require"/>).
///
/// One controller in several files: this one holds the section registry and
/// the index; each group of tabs (and its POST handlers) lives in its own
/// partial file, with its views under Views/Settings/Tabs.
/// </summary>
[Area("Platform")]
[Shell("settings")]
[SignedIn]
[Route("settings")]
public sealed partial class SettingsController(
    AccountixDbContext db, FirstUse firstUse, CompanySetup setup, Memberships memberships,
    AccountSecurity security, SignInService signIn, TimeProvider time) : Controller
{
    public sealed record Section(string Key, string Label, string Icon);

    /// <summary>SECTIONS: key, label, icon, predicate.</summary>
    private static readonly (string Key, string Label, string Icon, Func<EffectiveAccess, bool> Pred)[] Sections =
    [
        ("account", "My Account", "&#128100;", _ => true),
        ("company", "Company Profile", "&#127970;", a => a.CanOpenModule(Modules.Finance)),
        ("periods", "Financial Periods", "&#128197;", a => a.CanOpenModule(Modules.Finance)),
        ("reports", "Report Structure", "&#128200;", a => a.CanOpenModule(Modules.Finance)),
        ("cash_flow", "Cash Flow Method", "&#128181;", a => a.CanOpenModule(Modules.Finance)),
        ("labels", "Project Labels", "&#127991;", a => a.CanOpenModule(Modules.Finance)),
        ("inventory", "Inventory", "&#128230;", a => a.CanOpenModule(Modules.Inventory)),
        ("purchase", "Procurement", "&#128228;", a => a.CanOpenModule(Modules.Invoicing)),
        ("sales", "Sales", "&#128229;", a => a.CanOpenModule(Modules.Invoicing)),
        ("invoicing", "Invoice Defaults", "&#128207;", a => a.CanOpenModule(Modules.Invoicing)),
        ("templates", "Invoice Templates", "&#128196;", a => a.CanOpenModule(Modules.Invoicing)),
        ("rights", "Rights & Access", "&#128273;", a => a.IsAdmin),
        ("members", "Members & Roles", "&#128101;", a => a.IsAdmin),
        ("audit", "Audit Log", "&#128220;", a => a.IsAdmin),
        ("invites", "Invitations", "&#9993;", _ => true),
    ];

    private EffectiveAccess Me => HttpContext.Access()!;

    public static IReadOnlyList<Section> VisibleSections(EffectiveAccess a) =>
        Sections.Where(s => s.Pred(a)).Select(s => new Section(s.Key, s.Label, s.Icon)).ToList();

    private bool Allowed(string? section) =>
        section is not null && Sections.FirstOrDefault(s => s.Key == section).Pred is { } p && p(Me);

    /// <summary>_require(section): a write handler's gate; null when the person may proceed.</summary>
    private IActionResult? Require(string section)
    {
        if (Allowed(section)) return null;
        this.Flash("You don't have access to that setting.", "error");
        return Redirect("/settings/");
    }

    private RedirectResult ToTab(string tab) => Redirect($"/settings/?tab={tab}");

    /// <summary>The model of the settings page: the resolved tab and that tab's own model.</summary>
    public sealed record SettingsPage(string Tab, IReadOnlyList<Section> Sections, object? TabModel);

    [HttpGet("")]
    public async Task<IActionResult> Index(string? tab, CancellationToken ct)
    {
        var sections = VisibleSections(Me);
        string resolved = tab is { Length: > 0 } ? tab : sections.Count > 0 ? sections[0].Key : "account";
        if (!Allowed(resolved))
            // Deep link to a section this person can't see: fall back rather than 403.
            resolved = sections.Count > 0 ? sections[0].Key : "account";
        // The sidebar is tab-scoped: it must see the RESOLVED tab (g.settings_tab).
        if (Request.Query["tab"].ToString() != resolved)
        {
            var q = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(Request.QueryString.Value);
            q["tab"] = resolved;
            Request.QueryString = QueryString.Create(q);
        }
        object? model = resolved switch
        {
            "account" => await AccountTabAsync(ct),
            "company" => await CompanyTabAsync(ct),
            "periods" => await PeriodsTabAsync(ct),
            "reports" => await ReportsTabAsync(ct),
            "cash_flow" => await CashFlowTabAsync(ct),
            "labels" => await LabelsTabAsync(ct),
            "inventory" => await InventoryTabAsync(ct),
            "purchase" or "sales" => await DocumentsTabAsync(resolved, ct),
            "invoicing" => await InvoicingTabAsync(ct),
            "templates" => await TemplatesTabAsync(ct),
            "rights" => await RightsTabAsync(ct),
            "members" => await MembersTabAsync(ct),
            "invites" => await InvitesTabAsync(ct),
            "audit" => await AuditTabAsync(ct),
            _ => null,
        };
        return View("Index", new SettingsPage(resolved, sections, model));
    }
}
