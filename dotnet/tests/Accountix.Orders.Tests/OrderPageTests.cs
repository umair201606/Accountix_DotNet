using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Accountix.TestSupport;

namespace Accountix.Orders.Tests;

/// <summary>
/// The order pages render what the Python templates rendered (ids, values, inline JSON, the inline script) and the
/// lookups answer alike: each golden test makes a company of its own, so lists and pickers hold only its rows.
/// </summary>
public partial class OrderPageTests
{
    private static Task<ModuleHost> HostAsync() => ModuleHost.StartAsync(typeof(OrdersModule).Assembly);

    [ReferenceFact]
    public async Task Lists_and_editors_render_with_the_sidebar_links()
    {
        await using var host = await HostAsync();
        var w = await World.SharedAsync();
        var c = w.Client(host);
        foreach (string url in new[]
                 {
                     "/inventory/sales/list", "/inventory/sales/list?status=approved", "/inventory/sales/", "/inventory/sales/99999999",
                     "/inventory/purchases/list", "/inventory/purchases/list?status=cancelled", "/inventory/purchases/",
                 })
        {
            var r = await c.GetAsync(url);
            Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url}: {(int)r.StatusCode}");
            string html = await r.Content.ReadAsStringAsync();
            Assert.Contains("href=\"/inventory/sales/list\"", html);
            Assert.Contains("href=\"/inventory/purchases/list\"", html);
        }
        string so = await (await c.GetAsync("/inventory/sales/")).Content.ReadAsStringAsync();
        Assert.Contains("<title>Unapproved Sales Order</title>", so);
        Assert.Contains("id=\"saveApproveBtn\"", so);
        Assert.Contains("value=\"(auto)\"", so);
        string list = await (await c.GetAsync("/inventory/purchases/list")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(">Receive<", list);
    }

    /// <summary>Golden/order_editors.json: new, draft and approved editors of both kinds.</summary>
    [ReferenceFact]
    public async Task The_editors_render_what_the_python_templates_rendered()
    {
        var w = await World.CreateAsync();
        await using var host = await HostAsync();
        var c = w.Client(host);
        string token = await c.TokenFromAsync("/inventory/sales/list");
        var draft = w.SalesJson("save", "form <&> \"quotes\"");
        draft["notes"] = 0;
        var steps = new (string Role, string Url, JsonObject Payload)[]
        {
            ("SO1", "/inventory/sales/save", draft),
            ("SO2", "/inventory/sales/save", w.SalesJson("approve", "approved form")),
            ("PO1", "/inventory/purchases/save", w.PurchaseJson("save", "po form")),
            ("PO2", "/inventory/purchases/save", w.PurchaseJson("approve", "po approved")),
        };
        var ids = new Dictionary<string, int>();
        foreach (var (role, url, payload) in steps)
        {
            var r = await w.SaveAsync(c, url, payload, token, role);
            Assert.True(r.Status == HttpStatusCode.OK, r.Raw);
            ids[role] = r.Body.GetProperty("id").GetInt32();
        }
        const string name = "order_editors";
        Golden.SameInputs(name, new { steps = steps.Select(x => new object[] { x.Role, x.Url, w.RoleJson(x.Payload) }) });
        foreach (var (key, url) in new[]
                 {
                     ("SO1", $"/inventory/sales/{ids["SO1"]}"), ("SO2", $"/inventory/sales/{ids["SO2"]}"), ("SO new", "/inventory/sales/"),
                     ("PO1", $"/inventory/purchases/{ids["PO1"]}"), ("PO2", $"/inventory/purchases/{ids["PO2"]}"), ("PO new", "/inventory/purchases/"),
                 })
        {
            string mine = await (await c.GetAsync(url)).Content.ReadAsStringAsync();
            Golden.Match(name, [.. w.EditorLines(Fingerprint(mine), key[..2]), World.ScriptBody(InlineScript(mine))], key);
            Assert.Contains("<link rel=\"stylesheet\" href=\"/static/css/order_editor.css\">", mine);
            // The editor's own data is JSON to the byte: nothing HTML-encoded inside the script.
            string data = string.Join("\n", InlineScript(mine).Split('\n').Take(4));
            Assert.DoesNotContain("&quot;", data);
            Assert.DoesNotContain("&amp;", data);
            Assert.DoesNotContain("&#", data);
        }
    }

    /// <summary>The parts of an editor the scripts depend on: every id, input values, textareas, inline JSON, buttons.</summary>
    private static List<string> Fingerprint(string html)
    {
        int start = html.IndexOf("<div class=\"wrap\">", StringComparison.Ordinal);
        int end = html.IndexOf("<script src=\"/static/js/app.js\"", start, StringComparison.Ordinal);
        int scripts = html.IndexOf("<input type=\"hidden\" id=\"editVersion\"", end, StringComparison.Ordinal);
        string body = html[start..end] + html[scripts..html.IndexOf("/static/js/theme.js", scripts, StringComparison.Ordinal)];
        var parts = new List<string>();
        parts.Add(Regex.Match(html, "<title>(.*?)</title>").Groups[1].Value.Trim());
        foreach (Match m in TagRe().Matches(body))
        {
            string tag = m.Groups[1].Value, attrs = m.Groups[2].Value;
            string? id = Attr(attrs, "id");
            if (id is null) continue;
            parts.Add($"{tag}#{id} value={Attr(attrs, "value")} class={Attr(attrs, "class")} " +
                      $"disabled={Regex.IsMatch(attrs, @"\sdisabled(\s|>|=|$)")} checked={Regex.IsMatch(attrs, @"\schecked(\s|>|=|$)")}");
        }
        foreach (Match m in Regex.Matches(body, "<textarea[^>]*id=\"([^\"]+)\"[^>]*>(.*?)</textarea>", RegexOptions.Singleline))
            parts.Add($"textarea#{m.Groups[1].Value}={System.Net.WebUtility.HtmlDecode(m.Groups[2].Value)}");
        foreach (string v in new[] { "allProducts", "existingOrderItems" })
            parts.Add(v + "=" + Regex.Match(body, $"var {v} = (.*?)(, rowCount|;\\n)").Groups[1].Value);
        parts.Add("editable=" + Regex.Match(body, "const editable = (\\w+);").Groups[1].Value);
        parts.Add("buttons=" + string.Join("|", Regex.Matches(body, "<button[^>]*>(.*?)</button>").Select(m => m.Groups[1].Value)));
        parts.Add("banner=" + System.Net.WebUtility.HtmlDecode(Regex.Match(body, "<div class=\"ap-b\">(.*?)</div>").Groups[1].Value));
        parts.Add("pills=" + string.Join("|", Regex.Matches(body, "class=\"pill-b ?(\\w*) ?\"").Select(m => m.Groups[1].Value)));
        parts.Add("styles=" + string.Join("|", Regex.Matches(body, "<th class=\"c-\\w+ num\" style=\"([^\"]*)\"").Select(m => m.Groups[1].Value)));
        return parts;
    }

    /// <summary>The editor's inline &lt;script&gt; (after edit_version.js).</summary>
    private static string InlineScript(string html)
    {
        int at = html.IndexOf("/static/js/edit_version.js\"></script>", StringComparison.Ordinal);
        int start = html.IndexOf("<script>", at, StringComparison.Ordinal) + "<script>".Length;
        return html[start..html.IndexOf("</script>", start, StringComparison.Ordinal)].Trim();
    }

    private static string? Attr(string attrs, string name)
    {
        var m = Regex.Match(attrs, $"(?:^|\\s){name}=\"([^\"]*)\"");
        return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value) : null;
    }

    [GeneratedRegex("<(input|div|span|button|select|table|tbody|textarea|a|h1|h3|p)\\b([^>]*)>")]
    private static partial Regex TagRe();

    /// <summary>Golden/order_lists.json: an order dated "2026-1-5" with an odd total, a cancelled purchase order, the filters.</summary>
    [ReferenceFact]
    public async Task The_lists_show_what_the_python_lists_showed()
    {
        var w = await World.CreateAsync();
        await using var host = await HostAsync();
        var c = w.Client(host);
        string token = await c.TokenFromAsync("/inventory/sales/list");
        var odd = w.SalesJson("save", "list");
        odd["order_date"] = "2026-1-5";
        odd["total_amount"] = 1234567.5;
        await w.SaveAsync(c, "/inventory/sales/save", odd, token, "SO1");
        var po = w.PurchaseJson("approve", "list");
        var (_, cancelled, _) = await w.SaveAsync(c, "/inventory/purchases/save", po, token, "PO1");
        var cancel = await c.PostFormWithTokenAsync($"/inventory/purchases/cancel/{cancelled.GetProperty("id").GetInt32()}", [], token);
        Assert.Equal(HttpStatusCode.Redirect, cancel.StatusCode);
        const string name = "order_lists";
        Golden.SameInputs(name, new { odd = w.RoleJson(odd), po = w.RoleJson(po) });
        foreach (string url in new[] { "/inventory/sales/list", "/inventory/sales/list?status=approved", "/inventory/purchases/list",
                                       "/inventory/purchases/list?status=cancelled", "/inventory/purchases/list?status=received" })
            Golden.Match(name, w.ListLines(ListBody(await (await c.GetAsync(url)).Content.ReadAsStringAsync())), url);
    }

    /// <summary>The list's filter and table, whitespace and entity spelling apart (the token field is .NET's antiforgery).</summary>
    private static string ListBody(string html)
    {
        int start = html.IndexOf("<div class=\"page-header\">", StringComparison.Ordinal);
        int end = html.IndexOf("<style>", start, StringComparison.Ordinal);
        string body = Regex.Replace(html[start..end], "<input name=\"__RequestVerificationToken\"[^>]*>", "");
        body = Regex.Replace(body, "selected(=\"selected\")?", "selected");
        body = System.Net.WebUtility.HtmlDecode(Regex.Replace(body, ">\\s+<", "><"));
        return Regex.Replace(Regex.Replace(body, "\\s+", " "), " >", ">").Trim();
    }

    /// <summary>Golden/order_lookups.json: products, customers, suppliers, accounts and a party's open orders.</summary>
    [ReferenceFact]
    public async Task The_lookups_answer_what_the_python_routes_answered()
    {
        var w = await World.CreateAsync();
        await using var host = await HostAsync();
        var c = w.Client(host);
        string token = await c.TokenFromAsync("/inventory/sales/list");
        var so = w.SalesJson("approve", "lookup");
        await w.SaveAsync(c, "/inventory/sales/save", so, token, "SO1");
        var po = w.PurchaseJson("approve", "lookup");
        await w.SaveAsync(c, "/inventory/purchases/save", po, token, "PO1");
        string[] urls =
        [
            "/inventory/sales/api/products?q=", "/inventory/sales/api/products?q=%20inv%20", "/inventory/sales/api/products?q=SOL",
            "/inventory/sales/api/customers?q=", "/inventory/sales/api/customers?q=solar", "/inventory/sales/api/accounts?q=",
            "/inventory/sales/api/accounts?q=cash", $"/inventory/sales/api/orders/{w.Customer}", "/inventory/sales/api/orders/999999",
            "/inventory/purchases/api/products?q=bat", "/inventory/purchases/api/suppliers?q=", "/inventory/purchases/api/accounts?q=1-01",
            $"/inventory/purchases/api/orders/{w.Supplier}",
        ];
        var lines = new List<string>();
        foreach (string url in urls)
        {
            var r = await c.GetAsync(url);
            string key = url.Replace($"/orders/{w.Customer}", "/orders/C1").Replace($"/orders/{w.Supplier}", "/orders/S1");
            lines.Add(key + " " + w.LookupJson((int)r.StatusCode, await r.Content.ReadAsStringAsync(), url.Contains("/purchases/") ? "S" : "C"));
        }
        const string name = "order_lookups";
        Golden.SameInputs(name, new { so = w.RoleJson(so), po = w.RoleJson(po) });
        Golden.Match(name, lines, "lookups");
    }

    [ReferenceFact]
    public async Task The_sidebar_follows_the_order_flow_settings()
    {
        await using var host = await HostAsync();
        var w = await World.SharedAsync();
        var c = w.Client(host);
        await using var db = w.Db();
        var s = db.Set<Accountix.Core.Entities.InventorySetting>().OrderBy(x => x.Id).First();
        string? before = s.SalesFlow;
        try
        {
            s.SalesFlow = "direct_invoice";
            await db.SaveChangesAsync();
            string html = await (await c.GetAsync("/inventory/purchases/list")).Content.ReadAsStringAsync();
            Assert.DoesNotContain("href=\"/inventory/sales/list\"", html);
            Assert.Contains("href=\"/inventory/purchases/list\"", html);
        }
        finally
        {
            s.SalesFlow = before;
            await db.SaveChangesAsync();
        }
    }
}
