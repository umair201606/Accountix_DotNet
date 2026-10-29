using System.Net;
using System.Text.RegularExpressions;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Executive.Tests;

/// <summary>The /executive pages through the module host: what they show, who may open them, what they cost.</summary>
public class PageTests
{
    private static int B => Exec.Company;

    private static readonly string[] DashboardClasses =
    [
        "exr-band-title", "exr-kpi-lbl", "exr-kpi-val", "exr-kpi-flag", "exr-delta", "exr-delta-base", "lbl", "val", "sub",
        "exr-legend-val", "exr-bar-label", "exr-bar-amt", "exr-tip", "exr-fact", "exr-top-name", "exr-top-amt", "num",
        "exr-axis", "exr-pick-empty",
    ];
    private static readonly string[] DashboardAttrs = ["points", "style", "href", "x", "y", "y1", "width", "cx", "cy", "stroke-dasharray", "viewBox"];
    private static readonly string[] PageClasses =
        ["lbl", "val", "sub", "exr-asof", "exr-count", "exr-link", "exr-code", "exr-group", "num", "strong", "bi-status", "bi-detail", "bi-intro", "exr-empty"];

    /// <summary>The executive content of a page (from its first exr block), account ids in links as codes.</summary>
    private static string Content(string html, IReadOnlyDictionary<int, string> codes)
    {
        int i = html.IndexOf("<div class=\"exr", StringComparison.Ordinal);
        Assert.True(i >= 0, "page has no executive content");
        return Exec.LinkCodes(html[i..], codes);
    }

    private static string Title(string html) => Regex.Match(html, "<title>(.*?)</title>").Groups[1].Value.Trim();

    /// <summary>What the comparison reads off a page: each class's texts, each attribute's values, the title.</summary>
    private static object Read(string content, string title, bool dashboard) => new
    {
        title,
        classes = (dashboard ? DashboardClasses : PageClasses).ToDictionary(c => c, c => Hosts.Texts(content, c)),
        attrs = (dashboard ? DashboardAttrs : ["href"]).ToDictionary(a => a, a => Hosts.Attrs(content, a)),
    };

    /// <summary>
    /// Every figure, label, tooltip, bar width, SVG coordinate, link and title of the dashboard, the registers,
    /// the party ledger and Books Integrity equals the Python page's for the same data (Golden/Pages.json:
    /// Python's pages captured on the golden company, its clock stopped at <see cref="Exec.Now"/> as this host's is).
    /// </summary>
    [ReferenceFact]
    public async Task Pages_show_what_the_python_pages_show()
    {
        await Exec.SeedAsync();
        var codes = await Exec.CodesAsync(B);
        int c7 = await Exec.AccountIdAsync("1-01-02-01-9107", B);
        var golden = Golden.Load("Pages");
        await using var host = await Hosts.ExecutiveAsync();
        var c = host.ClientFor(Exec.OwnerB, B);
        foreach (string url in new[]
                 {
                     "/executive/?as_of=2026-06-30", "/executive/",
                     "/executive/receivables", "/executive/payables?as_of=2026-06-30", "/executive/receivables?q=customer%202",
                     $"/executive/party/{c7}", $"/executive/party/{c7}?as_of=2025-11-30", "/executive/integrity", "/executive/integrity?run=1",
                 })
        {
            string key = Exec.LinkCodes(url, codes);
            bool dashboard = url.StartsWith("/executive/?", StringComparison.Ordinal) || url == "/executive/";
            var py = golden[key] ?? throw new InvalidOperationException($"no golden page for {key}");
            string net = await c.HtmlAsync(url);
            Golden.Equal(Golden.Node(Read(py["html"]!.GetValue<string>(), py["title"]!.GetValue<string>(), dashboard)),
                         Read(Content(net, codes), Title(net), dashboard), key);
            if (dashboard)
            {
                Assert.Contains("Aging profile", net);
                Assert.Contains("Largest exposures", net);
            }
        }
    }

    [ReferenceFact]
    public async Task Pages_render_and_run_a_fixed_number_of_queries()
    {
        await Exec.SeedAsync();
        int c2 = await Exec.AccountIdAsync("1-01-02-01-9102", B);
        await using var host = await Hosts.ExecutiveAsync();
        var c = host.ClientFor(Exec.OwnerB, B);
        foreach (string url in new[]
                 {
                     "/executive/", "/executive/?as_of=2026-06-30", "/executive/receivables", "/executive/payables?as_of=2026-05-01",
                     $"/executive/party/{c2}", "/executive/settings", "/executive/integrity", "/executive/integrity?run=1",
                 })
            (await host.MeasureAsync(c, url, runs: 1)).Within(maxMilliseconds: 5000, maxQueries: 40);
    }

    [ReferenceFact]
    public async Task Without_a_selection_the_registers_ask_for_one()
    {
        var (td, tc) = await Exec.SeedAsync();
        try
        {
            await Exec.ResetSelectionAsync();
            await using var host = await Hosts.ExecutiveAsync();
            var c = host.ClientFor(Exec.OwnerB, B);
            string dash = await c.HtmlAsync("/executive/");
            Assert.Contains("Receivables and payables need accounts selected", dash);
            Assert.Contains("href=\"/executive/settings\" class=\"btn btn-primary\">Select accounts", dash);
            Assert.Contains("Profitability ·", dash);           // profit and liquidity need no selection
            string reg = await c.HtmlAsync("/executive/receivables");
            Assert.Contains("No accounts selected yet", reg);
            Assert.DoesNotContain("export-group", reg);         // no exports until configured
            var r = await c.GetAsync($"/executive/party/{td}");
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Equal("/executive/receivables", r.Headers.Location!.OriginalString);
            Assert.Contains("not in the executive report scope", await c.HtmlAsync("/executive/receivables"));
        }
        finally
        {
            await Exec.ResetSelectionAsync(td, tc);
        }
    }

    [ReferenceFact]
    public async Task Settings_replace_the_selection_and_python_would_read_the_same_scope()
    {
        var (td, tc) = await Exec.SeedAsync();
        try
        {
            await using var host = await Hosts.ExecutiveAsync();
            var c = host.ClientFor(Exec.OwnerB, B);
            string page = await c.HtmlAsync("/executive/settings");
            // The tree's data is valid JavaScript (no HTML entities inside the script).
            string script = Regex.Match(page, "var ROWS = (.*?);\\n", RegexOptions.Singleline).Groups[1].Value;
            Assert.StartsWith("[{\"id\":", script);
            Assert.DoesNotContain("&quot;", script);
            Assert.DoesNotContain("&amp;", script);
            Assert.Contains($"id=\"acct{td}\" checked", page.Replace("\n", " ").Replace("  ", " "));
            // The section counter's total sits where the script reads it (it read "1 of NaN selected").
            Assert.Matches("class=\"exr-section-head\" aria-expanded=\"true\" data-total=\"\\d+\"", page);

            var r = await c.PostFormAsync("/executive/settings",
                [new("account", td.ToString()), new("account", tc.ToString()), new("account", "x"), new("deep", tc.ToString())], "/executive/settings");
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Equal("/executive/settings", r.Headers.Location!.OriginalString);
            Assert.Contains("2 accounts now feed the executive reports.", await c.HtmlAsync("/executive/settings"));
            await using (var db = Exec.Db(B))
            {
                var rows = await db.Set<ExecAccountSelection>().OrderBy(s => s.AccountId).Select(s => new { s.AccountId, s.IncludeChildren }).ToListAsync();
                Assert.Equal([(td, false), (tc, true)], rows.Select(x => (x.AccountId, x.IncludeChildren)).OrderBy(x => x.AccountId == td ? 0 : 1));
            }
            // The scope .NET saved is the one Python read for the same selection (Golden/Scope.json):
            // Trade Debtors alone (no balance there), every creditor.
            var codes = await Exec.CodesAsync(B);
            await using (var db = Exec.Db(B))
                Golden.Equal(Golden.Load("Scope")["td_alone_tc_deep"],
                             (await Exec.Reports(db).ScopeAsync()).Keys.Select(k => codes[k]).Order(StringComparer.Ordinal), "scope after the save");
        }
        finally
        {
            await Exec.ResetSelectionAsync(td, tc);
        }
    }

    [ReferenceFact]
    public async Task Without_the_executive_module_or_its_rights_the_pages_are_refused()
    {
        // The reference people live in company B (restricted: manager, owner_a: employee); give B a selection first.
        int B = Exec.CompanyB;
        await Exec.ResetSelectionInAsync(B, await Exec.AccountIdAsync("1-01-02-01", B), await Exec.AccountIdAsync("2-01-01-01", B));
        await using var host = await Hosts.ExecutiveAsync();
        // "restricted" (manager in B) has the module switched off; owner_a (employee in B) too.
        foreach (int uid in new[] { Exec.Restricted, Exec.OwnerA })
        {
            var r = await host.ClientFor(uid, B).GetAsync("/executive/");
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        }
        // A person with the module but view rights withdrawn is sent back with Python's words.
        await using var db = Exec.Db(B);
        var membership = await db.Memberships.SingleAsync(m => m.CompanyId == B && m.UserId == Exec.Restricted);
        string flags = membership.ModuleFlags!;
        await db.Database.ExecuteSqlRawAsync("UPDATE company_memberships SET module_flags = REPLACE(module_flags, '\"executive\": false', '\"executive\": true') WHERE id = {0}", membership.Id);
        var right = await db.Set<UserPermission>().SingleAsync(p => p.CompanyId == B && p.UserId == Exec.Restricted && p.Resource == "executive_reports");
        int rightId = right.Id;
        bool? wasView = right.CanView, wasEdit = right.CanEdit;
        try
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE user_permissions SET can_view = false, can_edit = false WHERE id = {0}", rightId);
            var c = host.ClientFor(Exec.Restricted, B);
            var r = await c.GetAsync("/executive/receivables");
            // rights_guard.refusal: flash, then away from the page (no referrer, nothing visible in the module: the hub).
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Equal("/hub", r.Headers.Location!.OriginalString);
            // View without edit: the settings page is read-only and saving is refused.
            await db.Database.ExecuteSqlRawAsync("UPDATE user_permissions SET can_view = true WHERE id = {0}", rightId);
            string page = await c.HtmlAsync("/executive/settings");
            Assert.Contains("class=\"exr is-ro\"", page);
            Assert.DoesNotContain("Save selection", page);
            var post = await c.PostFormAsync("/executive/settings", [new("account", "1")], "/executive/settings");
            Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
            await using var db2 = Exec.Db(B);
            Assert.True(await db2.Set<ExecAccountSelection>().AnyAsync(), "the refused save changed nothing");
            // Managers see the Settings link; the dashboard offers no "Select accounts" button without edit.
            Assert.Contains("href=\"/executive/settings\"", await c.HtmlAsync("/executive/"));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE user_permissions SET can_view = {0}, can_edit = {1} WHERE id = {2}", wasView!, wasEdit!, rightId);
            await db.Database.ExecuteSqlRawAsync("UPDATE company_memberships SET module_flags = {0} WHERE id = {1}", flags, membership.Id);
        }
    }

    [ReferenceFact]
    public async Task The_sidebar_lists_the_executive_pages()
    {
        await using var host = await Hosts.ExecutiveAsync();
        string html = await host.ClientFor(Exec.OwnerB, B).HtmlAsync("/executive/receivables");
        foreach (string href in new[] { "/executive/", "/executive/receivables", "/executive/payables", "/executive/integrity", "/executive/settings" })
            Assert.Contains($"href=\"{href}\"", html);
    }
}
