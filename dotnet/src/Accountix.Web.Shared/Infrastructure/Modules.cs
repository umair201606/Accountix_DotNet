using Accountix.Core.Access;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Accountix.Web.Infrastructure;

/// <summary>
/// A module of the app (one project under src/Modules). The host finds every
/// implementation in the referenced Accountix.* assemblies at start-up, calls
/// <see cref="Register"/>, and the app shell renders <see cref="Navigation"/>.
/// </summary>
public interface IAccountixModule
{
    /// <summary>Module key (Modules.Accounting ...), or "settings" / "platform".</summary>
    string Key { get; }

    void Register(IServiceCollection services, IConfiguration config);

    /// <summary>The module's sidebar (shared/navigation.py NAV[key]); null when it adds none.</summary>
    ModuleNav? Navigation { get; }
}

/// <summary>A sidebar link. URLs are the Python app's URLs, so links work across both apps during the move.</summary>
public sealed record NavItem(string Label, string Url, string Icon = "&#9632;")
{
    /// <summary>Highlighted when the request path starts with one of these (default: equals Url).</summary>
    public string[] ActivePrefixes { get; init; } = [];
    public string[] ExcludePrefixes { get; init; } = [];
    /// <summary>Settings tab key: active only while ?tab= matches.</summary>
    public string? Tab { get; init; }
    /// <summary>Shown only when this returns true (admin, manager, flow setting...).</summary>
    public Func<EffectiveAccess, HttpContext, bool>? Gate { get; init; }
    /// <summary>Hidden when the person lacks this right (the Python sidebar hides links the rights guard would refuse).</summary>
    public (string Section, string Action)? Right { get; init; }
    /// <summary>Label that depends on settings (e.g. "Invoice Against SO").</summary>
    public Func<HttpContext, string>? LabelFor { get; init; }
    /// <summary>SVG icon name (Icons: grid, doc, user, building, cart, calendar, return, package, tag, activity, trend-up, trend-dn, cog, hub, ...). Default "doc".</summary>
    public string Svg { get; init; } = "doc";
    /// <summary>Place inside a group that several projects fill (stable: equal orders keep registration order).</summary>
    public int Order { get; init; }
}

public sealed record NavGroup(string Name, string Icon, IReadOnlyList<NavItem> Items)
{
    /// <summary>
    /// Where the group sits among the module's groups when several projects contribute to one key
    /// (Python's NAV order; e.g. invoicing: Procurement 10, Sales 20, Invoice Tracking 30). Groups of one
    /// name merge; the lowest non-zero Order of the copies wins; 0 keeps first-registered order.
    /// </summary>
    public int Order { get; init; }
}

public sealed class ModuleNav
{
    public IReadOnlyList<NavItem> Flat { get; init; } = [];
    public IReadOnlyList<NavGroup> Groups { get; init; } = [];
    public IReadOnlyList<NavItem> FlatAfter { get; init; } = [];
    public IReadOnlyList<NavGroup> GroupsAfter { get; init; } = [];
}

/// <summary>Presentation per module (shared/navigation.py MODULE_META).</summary>
public sealed record ModuleMeta(string Label, string Badge, string Letter, string Brand, string BadgeBg, string BadgeFg, string Home)
{
    public static readonly IReadOnlyDictionary<string, ModuleMeta> All = new Dictionary<string, ModuleMeta>
    {
        ["hr"] = new("Accountix ERP", "HR", "A", "linear-gradient(135deg,#0f5257,#1a7a7a)", "#e0f2f1", "#0f5257", "/dashboard"),
        ["inventory"] = new("Accountix Inventory", "INVENTORY", "I", "linear-gradient(135deg,#7c3aed,#8b5cf6)", "#f3e8ff", "#7c3aed", "/inventory/"),
        ["invoicing"] = new("Accountix Invoicing", "INVOICING", "V", "linear-gradient(135deg,#0d9488,#14b8a6)", "#ccfbf1", "#0d9488", "/invoicing/"),
        ["finance"] = new("Accountix Finance", "FINANCE", "F", "linear-gradient(135deg,#1d4ed8,#3b82f6)", "#dbeafe", "#1d4ed8", "/finance/"),
        ["accounting"] = new("Accountix Accounting", "ACCOUNTING", "A", "linear-gradient(135deg,#0f5257,#1a7a7a)", "#e0f2f1", "#0f5257", "/accounting/"),
        ["fbr"] = new("FBR Digital Invoicing", "FBR", "F", "linear-gradient(135deg,#b5790a,#d4952e)", "#faf1de", "#b5790a", "/fbr/"),
        ["settings"] = new("Accountix Settings", "SETTINGS", "A", "linear-gradient(135deg,#475569,#64748b)", "#f1f5f9", "#475569", "/settings/"),
        ["fixed_assets"] = new("Accountix Fixed Assets", "FA", "F", "linear-gradient(135deg,#b91c1c,#dc2626)", "#fef2f2", "#b91c1c", "/fixed-assets/"),
        ["executive"] = new("Accountix Executive", "EXECUTIVE", "E", "linear-gradient(135deg,#065f46,#047857)", "#ecfdf5", "#047857", "/executive/"),
    };

    public static ModuleMeta For(string? key) => key is not null && All.TryGetValue(key, out var m) ? m : All["hr"];
}

/// <summary>Resolves the sidebar for the current request (gates, rights, active item).</summary>
public sealed class NavigationService(IEnumerable<IAccountixModule> modules)
{
    public sealed record Link(string Label, string Url, string Icon, string Svg, bool Active);
    public sealed record Block(bool IsGroup, string? Name, string? Icon, string? Slug, bool Expanded, IReadOnlyList<Link> Items);

    public IReadOnlyList<Block> For(string? moduleKey, HttpContext http)
    {
        var access = http.Access();
        if (access is null || moduleKey is null)
            return [];
        // Several projects may contribute to one module key (e.g. HR and Payroll): merged in registration order.
        var specs = modules.Where(m => m.Key == moduleKey && m.Navigation is not null).Select(m => m.Navigation!).ToList();
        var blocks = new List<Block>();
        foreach (var flat in specs.Select(s => s.Flat)) AddFlat(flat);
        AddGroups(Merge(specs.SelectMany(s => s.Groups)));
        foreach (var flat in specs.Select(s => s.FlatAfter)) AddFlat(flat);
        AddGroups(Merge(specs.SelectMany(s => s.GroupsAfter)));
        return blocks;

        void AddFlat(IReadOnlyList<NavItem> items)
        {
            var links = Resolve(items);
            if (links.Count > 0) blocks.Add(new Block(false, null, null, null, false, links));
        }

        // Groups of one name from several projects (HR + Payroll "Administration") are one group.
        // Group order is explicit (NavGroup.Order) so it never depends on which assembly registered first.
        static List<NavGroup> Merge(IEnumerable<NavGroup> groups) => groups
            .GroupBy(g => g.Name)
            .Select((g, first) => (Order: g.Select(x => x.Order).Where(o => o != 0).DefaultIfEmpty(0).Min(), First: first,
                Group: new NavGroup(g.Key, g.First().Icon, g.SelectMany(x => x.Items).OrderBy(i => i.Order).ToList())))
            .OrderBy(t => t.Order == 0 ? int.MaxValue : t.Order).ThenBy(t => t.First)
            .Select(t => t.Group with { Order = t.Order })
            .ToList();

        void AddGroups(IReadOnlyList<NavGroup> groups)
        {
            foreach (var g in groups)
            {
                var links = Resolve(g.Items);
                if (links.Count > 0)
                    blocks.Add(new Block(true, g.Name, g.Icon, g.Name.ToLowerInvariant().Replace(' ', '-'),
                                         links.Any(l => l.Active), links));
            }
        }

        List<Link> Resolve(IReadOnlyList<NavItem> items)
        {
            string path = http.Request.Path.Value ?? "/";
            string? tab = http.Request.Query["tab"].FirstOrDefault();
            var output = new List<Link>();
            foreach (var item in items)
            {
                if (item.Gate is not null && !item.Gate(access, http)) continue;
                if (item.Right is { } r && !access.Can(r.Section, r.Action)) continue;
                string url = item.Tab is null ? item.Url : $"{item.Url}?tab={item.Tab}";
                bool active = item.ActivePrefixes.Length == 0
                    ? string.Equals(path.TrimEnd('/'), item.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                    : item.ActivePrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                if (active && item.ExcludePrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    active = false;
                if (active && item.Tab is not null)
                    active = tab == item.Tab;
                output.Add(new Link(item.LabelFor?.Invoke(http) ?? item.Label, url, item.Icon, item.Svg, active));
            }
            return output;
        }
    }
}

/// <summary>Flash messages (Flask's flash): categories success / error / danger / info / warning.</summary>
public static class Flashes
{
    private const string Key = "_flashes";

    public static void Add(ITempDataDictionary tempData, string message, string category = "info")
    {
        var list = tempData.Peek(Key) as string[] ?? [];
        tempData[Key] = list.Append(category + "\u001f" + message).ToArray();
    }

    public static void Flash(this Controller c, string message, string category = "info") => Add(c.TempData, message, category);

    public static IReadOnlyList<(string Category, string Message)> Take(ITempDataDictionary tempData)
    {
        if (tempData[Key] is not string[] list) return [];
        return list.Select(s => s.Split('\u001f', 2)).Select(p => (p[0], p.Length > 1 ? p[1] : "")).ToList();
    }
}

/// <summary>Local redirect targets only (shared/security.safe_local_url).</summary>
public static class SafeUrl
{
    public static string? Local(string? target)
    {
        if (string.IsNullOrEmpty(target) || target.Contains('\\')) return null;
        if (!target.StartsWith('/') || target.StartsWith("//")) return null;
        return Uri.TryCreate(target, UriKind.Relative, out _) ? target : null;
    }

    /// <summary>The referring page when it is this app's, else the fallback.</summary>
    public static string Back(HttpRequest request, string fallback = "/hub")
    {
        string? referer = request.Headers.Referer.ToString();
        if (Uri.TryCreate(referer, UriKind.Absolute, out var uri)
            && string.Equals(uri.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase))
            return Local(uri.PathAndQuery) ?? fallback;
        return fallback;
    }
}

/// <summary>Model of the shared _GlEntryButton partial (gl_entry_button macro).</summary>
public sealed record GlEntryButton(string Type, int? Id = null, string? IdFrom = null)
{
    public IReadOnlyList<string> Also { get; init; } = [];
}

/// <summary>Model of the shared _ExportButtons partial (export_buttons macro).</summary>
public sealed record ExportButtons(params string[] Formats)
{
    public string Param { get; init; } = "format";
    public bool Print { get; init; } = true;
    /// <summary>Another page's path (the macro's endpoint=); null: this page.</summary>
    public string? Path { get; init; }
    public static ExportButtons Default => new("excel", "pdf");
}
