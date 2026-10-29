using System.Text.RegularExpressions;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.SuperAdmin.Tests;

/// <summary>The platform-wide audit page: every company plus platform events, filters, 50 a page.</summary>
public class AuditPageTests
{
    /// <summary>What <see cref="SeedAsync"/> made: the search tag and the two companies its rows belong to.</summary>
    private sealed record Seeded(string Tag, int North, int South);

    /// <summary>
    /// Rows only this test's search finds: platform rows and rows of two fresh companies ("Audit North",
    /// "Audit South"), every kind of change payload, fixed times.
    /// </summary>
    private static async Task<Seeded> SeedAsync(int count = 60)
    {
        string tag = "AUD" + S.Tag();
        int north = await S.NewCompanyAsync("Audit North");
        int south = await S.NewCompanyAsync("Audit South");
        await using var db = S.Db();
        var t0 = new DateTime(2026, 10, 1, 8, 30, 0, DateTimeKind.Utc);
        string[] changes =
        [
            "[{\"field\": \"unit_price\", \"old\": 1500.0, \"new\": 1750.5}, {\"field\": \"is_active\", \"old\": true, \"new\": false}]",
            "[{\"field\": \"notes\", \"old\": null, \"new\": \"O'Brien said \\\"hi\\\" — ok\"}, {\"field\": \"qty\", \"old\": 2, \"new\": 1e+20}]",
            "{\"sku\": \"P-1\", \"tags\": [\"a\", 1, null], \"meta\": {\"k\": 2.5}}",
            "not json",
        ];
        for (int i = 0; i < count; i++)
            db.AuditLogs.Add(new AuditLog
            {
                CompanyId = i % 3 == 0 ? null : (i % 3 == 1 ? north : south),
                CreatedAt = t0.AddMinutes(i), UserId = i % 4 == 0 ? null : S.SuperAdmin, UserName = i % 4 == 0 ? null : "Audit Tester",
                Action = i % 2 == 0 ? "update" : "login_failed", Module = "admin", EntityType = i % 5 == 0 ? null : "Product",
                EntityId = i, Reference = $"{tag}-{i}", Summary = $"Row {i} for {tag}",
                Changes = i % 5 == 4 ? null : changes[i % 4], IpAddress = i % 2 == 0 ? $"10.0.0.{i}" : null, Path = "/x",
            });
        await db.SaveChangesAsync();
        return new Seeded(tag, north, south);
    }

    /// <summary>The page queries the golden test renders ({0} = tag, {1} = Audit South's id).</summary>
    private static readonly string[] GoldenQueries =
        ["q={0}", "company_id=&action=&q={0}&page=2", "q={0}&company_id=platform", "q={0}+&action=update&company_id={1}"];

    private static string QueryKey(string q) => string.Format(q, "TAG", "SOUTH");

    /// <summary>
    /// A rendered audit page as the golden compares it: the table rows (field changes included), the pager
    /// links, the count line, the action select and the chosen company, as normalised markup; the search tag
    /// as TAG and the two companies' ids as NORTH / SOUTH.
    /// </summary>
    private static Dictionary<string, object> PageShape(string html, Seeded s)
    {
        string N(string x) => x.Replace(s.Tag, "TAG")
            .Replace($"company_id={s.North}", "company_id=NORTH").Replace($"company_id={s.South}", "company_id=SOUTH")
            .Replace($"value=\"{s.North}\"", "value=\"NORTH\"").Replace($"value=\"{s.South}\"", "value=\"SOUTH\"");
        var chosen = Regex.Match(Markup.Between(html, "<select id=\"saCo\"", "</select>"), "<option value=\"([^\"]*)\" selected>([^<]*)</option>");
        return new Dictionary<string, object>
        {
            ["rows"] = N(Markup.Rows(html, "sa-tbl")),
            ["links"] = Links(html).Select(N).ToList(),
            ["count"] = N(Markup.Between(html, "<p style=\"font-size:12px", "</p>")),
            ["actions"] = Markup.Between(html, "<select id=\"saAct\"", "</select>"),
            ["company"] = chosen.Success ? N(chosen.Value) : "",
        };
    }

    [ReferenceFact]
    public async Task Filters_narrow_by_company_platform_action_and_search()
    {
        await using var host = await S.HostAsync();
        var seeded = await SeedAsync();
        string tag = seeded.Tag;
        var c = S.Super(host);
        async Task<int> Count(string q)
        {
            string html = await c.GetStringAsync("/superadmin/audit/?" + q);
            var m = Regex.Match(html, @">(\d+) entr(y|ies)");
            Assert.True(m.Success, html);
            return int.Parse(m.Groups[1].Value);
        }
        Assert.Equal(60, await Count($"q={tag}"));
        Assert.Equal(20, await Count($"q={tag}&company_id=platform"));
        Assert.Equal(20, await Count($"q={tag}&company_id={seeded.South}"));
        Assert.Equal(30, await Count($"q={tag}&action=update"));
        Assert.Equal(60, await Count($"q={tag}&action=bogus"));               // unknown actions are ignored
        Assert.Equal(0, await Count($"q={tag}&company_id=99999999999"));
        Assert.Equal(1, await Count($"q={tag}-7"));                            // reference
        Assert.Equal(10, await Count($"q={tag}&company_id=platform&action=update&page=0"));
        Assert.True(await Count("q=10.0.0.&company_id=&action=update") >= 30);                   // IP search
        string page2 = await c.GetStringAsync($"/superadmin/audit/?q={tag}&page=2");
        Assert.Contains("· page 2 of 2", S.Text(page2));
        Assert.Contains("No activity matches these filters.", await c.GetStringAsync($"/superadmin/audit/?q={tag}&page=9"));
    }

    /// <summary>
    /// Rows, field changes (Python's str() of every kind of value), pager, count line and the selects render as
    /// Python's page rendered the same rows on 29 Oct 2026 (Golden/AuditPage.json).
    /// </summary>
    [ReferenceFact]
    public async Task The_page_renders_rows_changes_and_pager_as_python_rendered_them()
    {
        await using var host = await S.HostAsync();
        var seeded = await SeedAsync();
        var c = S.Super(host);
        var net = new Dictionary<string, object>();
        foreach (string q in GoldenQueries)
            net[QueryKey(q)] = PageShape(await c.GetStringAsync("/superadmin/audit/?" + string.Format(q, seeded.Tag, seeded.South)), seeded);
        Assert.Contains("Audit South", (string)((Dictionary<string, object>)net[QueryKey(GoldenQueries[3])])["company"]);
        Golden.Match("AuditPage", net);
    }

    private static List<string> Links(string html) =>
        Regex.Matches(html, "href=\"(/superadmin/audit/[^\"]*)\"").Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();

    [ReferenceFact]
    public async Task The_audit_page_is_two_queries_plus_the_shell_whatever_the_filter()
    {
        await using var host = await S.HostAsync();
        string tag = (await SeedAsync(10)).Tag;
        var c = S.Super(host);
        int baseline = (await host.MeasureAsync(c, "/superadmin/audit/", runs: 1)).Within(5000, 40).Queries;
        foreach (string q in new[] { $"?q={tag}", "?company_id=platform&action=login", "?company_id=1&page=3" })
            Assert.Equal(baseline, (await host.MeasureAsync(c, "/superadmin/audit/" + q, runs: 1)).Within(5000, 40).Queries);
    }

    [ReferenceFact]
    public void Changes_decode_as_pythons_decode_changes()
    {
        var list = Controllers.ConsoleController.DecodeChanges("[{\"field\": \"unit_price\", \"old\": 1500.0, \"new\": 1e16}, {\"new\": \"x\"}]");
        Assert.Equal("unit price", list[0].Field);
        Assert.Equal("1500.0", Infrastructure.Py.Str(list[0].Old!.Value));
        Assert.Equal("1e+16", Infrastructure.Py.Str(list[0].New!.Value));
        Assert.Equal("", list[1].Field);
        Assert.Null(list[1].Old);
        var dict = Controllers.ConsoleController.DecodeChanges("{\"a_b\": [\"x\", true, null], \"n\": 3}");
        Assert.Equal(("a b", "['x', True, None]"), (dict[0].Field, Infrastructure.Py.Str(dict[0].New!.Value)));
        Assert.Equal("3", Infrastructure.Py.Str(dict[1].New!.Value));
        Assert.Empty(Controllers.ConsoleController.DecodeChanges("oops"));
        Assert.Empty(Controllers.ConsoleController.DecodeChanges("\"text\""));
    }
}
