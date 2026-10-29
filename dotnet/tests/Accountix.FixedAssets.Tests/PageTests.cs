using System.Net;
using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.Core.Shared;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.FixedAssets.Tests;

/// <summary>Every page renders with the Python markup, the sidebar, the module gate and the section rights.</summary>
public class PageTests
{
    private static int B => Fa.CompanyB;

    [ReferenceFact]
    public async Task Every_page_renders()
    {
        int cat = await Fa.CategoryAsync(B);
        await using var host = await Fa.HostAsync();
        var c = host.ClientFor(Fa.OwnerB, B);
        string name = $"Page Laptop {Fa.Tag()}";
        await AssetRulesTests.CreateAsync(c, cat, name, ("purchase_cost", "150000"), ("location", "Head Office"));
        var a = await Fa.AssetAsync(B, name);
        var pages = new Dictionary<string, string[]>
        {
            ["/fixed-assets/"] = ["Assets in service", "Disposed", "stat-card", name],
            ["/fixed-assets/dashboard"] = ["Fixed Assets Dashboard", "Recent Assets"],
            ["/fixed-assets/assets/"] = ["+ New Asset", name, "name=\"q\""],
            [$"/fixed-assets/assets/?q={Uri.EscapeDataString(name)}&status=active&category_id={cat}"] = [name],
            ["/fixed-assets/assets/create"] = ["id=\"fa-category\"", "data-salvage-pct=", "Chart of Accounts Mapping", "Create Asset"],
            [$"/fixed-assets/assets/{a.Id}"] = ["Purchase Cost", "150,000", "Planned depreciation", "Dispose Asset", "Record a manual charge", "gleFab"],
            [$"/fixed-assets/assets/{a.Id}/edit"] = ["Update Asset", "value=\"150000.0\"", "Changes only through Dispose"],
            ["/fixed-assets/categories/"] = ["+ New Category", "Vehicles", "Declining Balance"],
            ["/fixed-assets/categories/create"] = ["Create Category"],
            [$"/fixed-assets/categories/{cat}/edit"] = ["Update Category", "name=\"is_active\""],
            ["/fixed-assets/depreciation/?as_of=2025-12-31"] = ["Post Selected Depreciation", "id=\"select-all\"", name],
            ["/fixed-assets/transfers/"] = ["Capitalise Stock as Fixed Asset"],
            ["/fixed-assets/transfers/capitalise"] = ["Save &amp; Approve", "name=\"source_product_id\""],
            [$"/fixed-assets/transfers/create?asset_id={a.Id}"] = ["Transfer to Inventory", "Journal Entry (on approve):", "name=\"new_product_name\""],
            ["/fixed-assets/reports/"] = ["Total Cost", "Summary by Category", "Summary by Status", "export-btn"],
            ["/fixed-assets/reports/movement"] = ["Fixed Asset Movement Schedule", "id=\"mv-from\""],
            ["/inventory/transfer-to-fa/"] = ["Transfers to Fixed Assets", "+ New Transfer"],
            ["/inventory/transfer-to-fa/create"] = ["New Transfer to Fixed Assets", "id=\"product-select\""],
        };
        foreach (var (url, expect) in pages)
        {
            var r = await c.GetAsync(url);
            Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url}: {(int)r.StatusCode}");
            string html = await r.Content.ReadAsStringAsync();
            foreach (string e in expect)
                Assert.True(html.Contains(e), $"{url} lacks {e}");
            // Inline scripts carry raw JavaScript: an HTML-encoded value there is a syntax error.
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(html, @"<script(?![^>]*\bsrc=)[^>]*>(.*?)</script>",
                         System.Text.RegularExpressions.RegexOptions.Singleline))
                Assert.True(!m.Groups[1].Value.Contains("&quot;") && !m.Groups[1].Value.Contains("&amp;") && !m.Groups[1].Value.Contains("&#x27;"),
                            $"{url}: encoded text inside <script>");
        }
        // Sidebar: the Python NAV["fixed_assets"] entries, the current one active.
        string list = await (await c.GetAsync("/fixed-assets/assets/")).Content.ReadAsStringAsync();
        foreach (string link in new[] { "/fixed-assets/dashboard", "/fixed-assets/transfers/", "/fixed-assets/depreciation/",
                                        "/fixed-assets/categories/", "/fixed-assets/reports/", "/fixed-assets/reports/movement" })
            Assert.Contains($"href=\"{link}\"", list);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/fixed-assets/assets/999999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/fixed-assets/reports/?format=docx")).StatusCode);
        var redirect = await c.GetAsync("/fixed-assets/transfers/create?asset_id=999999");
        Assert.Equal("/fixed-assets/assets/", Fa.Location(redirect));
    }

    [ReferenceFact]
    public async Task The_register_exports_what_python_exports()
    {
        int cat = await Fa.CategoryAsync(B, "Office Furniture");
        await using var host = await Fa.HostAsync();
        var c = host.ClientFor(Fa.OwnerB, B);
        string name = $"Desk {Fa.Tag()}";
        await AssetRulesTests.CreateAsync(c, cat, name, ("purchase_cost", "45000"), ("useful_life", "10"), ("salvage_value", "500"));
        var x = await c.GetAsync("/fixed-assets/reports/?format=excel");
        Assert.Equal(HttpStatusCode.OK, x.StatusCode);
        var (fmt, t) = FakeExport.Instance.Last!.Value;
        Assert.Equal("excel", fmt);
        Assert.Equal("Fixed Asset Register", t.Title);
        Assert.StartsWith("Assets in service as at ", t.Period);
        Assert.Equal(14, t.Columns.Count);
        Assert.Equal(ReportColumnKind.Integer, t.Columns[8].Kind);
        var row = t.Rows.Single(r => (string?)r.Cells[1] == name);
        Assert.Equal("Office Furniture", row.Cells[2]);
        Assert.Equal("Straight Line", row.Cells[7]);
        Assert.Equal(10, row.Cells[8]);
        Assert.Equal(45000.0, row.Cells[9]);
        Assert.Equal("Active", row.Cells[13]);
        Assert.Equal("grand", t.Rows[^1].Style);
        Assert.StartsWith("Total (", (string)t.Rows[^1].Cells[1]!);
        Assert.Single(t.ExtraSheets);
        Assert.Equal("By category", t.ExtraSheets[0].Name);
        await c.GetAsync("/fixed-assets/reports/?format=pdf");
        Assert.Empty(FakeExport.Instance.Last!.Value.Table.ExtraSheets);
    }

    [ReferenceFact]
    public async Task Categories_are_created_and_edited_with_python_refusals()
    {
        await using var host = await Fa.HostAsync();
        var c = host.ClientFor(Fa.OwnerB, B);
        string name = $"Solar Arrays {Fa.Tag()}";
        var r = await Fa.PostAsync(c, "/fixed-assets/categories/create", Fa.Form(("name", name), ("description", "PV"),
            ("default_useful_life", "20"), ("default_depreciation_method", "declining_balance"), ("default_salvage_value_pct", "7.5")),
            "/fixed-assets/categories/create");
        Assert.Equal("/fixed-assets/categories/", Fa.Location(r));
        var dup = await Fa.PostAsync(c, "/fixed-assets/categories/create", Fa.Form(("name", name)), "/fixed-assets/categories/create");
        Assert.Equal(HttpStatusCode.OK, dup.StatusCode);
        Assert.Contains("already exists.", await dup.Content.ReadAsStringAsync());
        int id;
        await using (var db = Fa.Db(B))
        {
            var cat = await db.Set<FaCategory>().SingleAsync(x => x.Name == name);
            Assert.Equal((20, "declining_balance", 7.5m, true), (cat.DefaultUsefulLife!.Value, cat.DefaultDepreciationMethod, cat.DefaultSalvageValuePct!.Value, cat.IsActive!.Value));
            id = cat.Id;
        }
        Assert.Contains("Default useful life must be a whole number.",
            await Fa.PostAndFollowAsync(c, $"/fixed-assets/categories/{id}/edit", Fa.Form(("name", name), ("default_useful_life", "x")),
                                        $"/fixed-assets/categories/{id}/edit"));
        await Fa.PostAsync(c, $"/fixed-assets/categories/{id}/edit", Fa.Form(("name", name + " B"), ("default_useful_life", "15"),
            ("default_depreciation_method", "straight_line"), ("default_salvage_value_pct", "0"), ("is_active", "0")), $"/fixed-assets/categories/{id}/edit");
        await using (var db = Fa.Db(B))
        {
            var cat = await db.Set<FaCategory>().SingleAsync(x => x.Id == id);
            Assert.Equal((name + " B", 15, false), (cat.Name, cat.DefaultUsefulLife!.Value, cat.IsActive!.Value));
        }
    }

    [ReferenceFact]
    public async Task The_module_gate_and_the_section_rights_refuse_as_python_does()
    {
        await using var host = await Fa.HostAsync();
        // owner_a is an employee in B without the Fixed Assets switch.
        var outsider = host.ClientFor(Fa.OwnerA, B);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync("/fixed-assets/")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync("/inventory/transfer-to-fa/")).StatusCode);

        // "restricted" (a manager in B) gets the module and only "view" on assets.
        int restricted = Reference.UserId(Reference.Email("restricted"));
        await using (var db = Fa.Db(B))
        {
            var m = await db.Memberships.IgnoreQueryFilters().SingleAsync(x => x.UserId == restricted && x.CompanyId == B);
            var flags = Modules.All.ToDictionary(x => x.Key, x => m.ModuleFlag(x.Key) ?? false);
            flags[Modules.FixedAssets] = true;
            m.SetModuleFlags(flags);
            var p = await db.Set<UserPermission>().SingleOrDefaultAsync(x => x.UserId == restricted && x.Resource == "fixed_assets_assets");
            if (p is null)
                db.Add(p = new UserPermission { UserId = restricted, Resource = "fixed_assets_assets" });
            (p.CanView, p.CanCreate, p.CanEdit, p.CanApprove, p.CanDelete) = (true, false, false, false, false);
            var rp = await db.Set<UserPermission>().SingleOrDefaultAsync(x => x.UserId == restricted && x.Resource == "fixed_assets_reports");
            if (rp is null)
                db.Add(rp = new UserPermission { UserId = restricted, Resource = "fixed_assets_reports" });
            (rp.CanView, rp.CanCreate, rp.CanEdit, rp.CanApprove, rp.CanDelete) = (false, false, false, false, false);
            await db.SaveChangesAsync();
        }
        var c = host.ClientFor(restricted, B);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/fixed-assets/assets/")).StatusCode);
        string list = await (await c.GetAsync("/fixed-assets/assets/")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("href=\"/fixed-assets/reports/\"", list);      // the sidebar hides what the rights refuse
        var create = await c.GetAsync("/fixed-assets/assets/create");
        Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await c.GetAsync("/fixed-assets/reports/")).StatusCode);
        int before;
        await using (var db = Fa.Db(B))
            before = await db.Set<AssetDepreciation>().CountAsync();
        var run = await Fa.PostAsync(c, "/fixed-assets/depreciation/post", Fa.Form(("entry_date", "2030-01-01")), "/fixed-assets/assets/");
        Assert.Equal("/fixed-assets/assets/", Fa.Location(run));
        // (The refusal's flash is lost: Web.Shared's Refusal.Right writes TempData from an authorization
        // filter, where MVC never saves it — reported to the coordinator.)
        await using (var db = Fa.Db(B))
            Assert.Equal(before, await db.Set<AssetDepreciation>().CountAsync());
        var json = await c.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/fixed-assets/assets/create") { Headers = { { "Accept", "application/json" } } });
        Assert.Equal(HttpStatusCode.Forbidden, json.StatusCode);
    }
}
