using System.Net;
using System.Text.Json;
using Accountix.Core.Entities;
using Accountix.Invoicing.Services;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Platform.Tests;

/// <summary>The invoice-template designer: build_body, the preview and the editor, byte for byte as Python
/// (golden: Golden/TemplateBodies.json and Golden/TemplateDesigner.json, captured 29 Oct 2026).</summary>
public class InvoiceTemplateTests
{
    private static readonly (string Name, Dictionary<string, object> Changes)[] OptionSets =
    [
        ("defaults", new()),
        ("per_line", new() { ["discount_display"] = "per_line", ["tax_display"] = "per_line", ["charges_display"] = "per_line" }),
        ("sparse", new() { ["show_logo"] = false, ["show_company_tax_id"] = false, ["show_status"] = true, ["show_due_date"] = false,
                           ["show_party_address"] = false, ["show_party_contact"] = false, ["show_party_tax_id"] = false,
                           ["show_notes"] = false, ["show_signature"] = false, ["show_thanks"] = false, ["show_bank_details"] = false }),
    ];

    private static Dictionary<string, object> Opts(string docType, Dictionary<string, object> changes)
    {
        var o = InvoiceTemplates.DefaultOptions(docType);
        foreach (var (k, v) in changes) if (o.ContainsKey(k)) o[k] = v;
        return o;
    }

    /// <summary>Every design × document type × option set the test renders (the order of the golden fixture).</summary>
    private static List<(string Design, string Doc, string Accent, string Set)> BodyCases()
    {
        var cases = new List<(string Design, string Doc, string Accent, string Set)>();
        foreach (var (design, _, _) in InvoiceDesigns.Designs)
            foreach (string doc in new[] { "sales", "purchase" })
                foreach (var (set, _) in OptionSets)
                    cases.Add((design, doc, design == "bold" ? "#b91c1c" : "#0f766e", set));
        return cases;
    }

    [ReferenceFact]
    public void Build_body_is_byte_identical_to_python_for_every_design()
    {
        var cases = BodyCases();
        var expected = Golden.Load("TemplateBodies").AsArray();
        Assert.Equal(cases.Count, expected.Count);
        var net = cases.Select(c => new
        {
            design = c.Design, doc = c.Doc, accent = c.Accent, options = c.Set,
            body = InvoiceDesigns.BuildBody(c.Design, c.Doc, c.Accent, Opts(c.Doc, OptionSets.Single(s => s.Name == c.Set).Changes)),
        }).ToList();
        for (int i = 0; i < cases.Count; i++)
        {
            string want = (string)expected[i]!["body"]!, got = net[i].body;
            if (want != got)
            {
                int at = Enumerable.Range(0, Math.Min(got.Length, want.Length)).FirstOrDefault(k => got[k] != want[k]);
                Assert.Fail($"{cases[i].Design}/{cases[i].Doc}/{cases[i].Set} differs at {at}:\nPY:  {Snip(want, at)}\nNET: {Snip(got, at)}");
            }
        }
        Golden.Match("TemplateBodies", net);
        // The seed bodies are the builder's own output too.
        foreach (var row in Accountix.Platform.Templates.DefaultTemplates.Rows)
            Assert.Equal(row.Body, InvoiceDesigns.BuildBody("classic", row.Type, row.Accent, InvoiceTemplates.Options(row.Type, row.OptionsJson)));
    }

    private static string Snip(string s, int at) => s.Substring(Math.Max(0, at - 60), Math.Min(160, s.Length - Math.Max(0, at - 60)));

    private static (string, string)[] DesignerForm(string type, string name, string design, string accent, bool perLine) =>
    [
        ("type", type), ("name", name), ("design", design), ("accent_color", accent), ("is_default", "on"),
        ("show_logo", "on"), ("show_company_tax_id", "on"), ("show_due_date", "on"), ("show_party_address", "on"),
        ("show_party_tax_id", "on"), ("show_discount", "on"), ("show_tax", "on"), ("show_delivery", "on"),
        ("show_commission", "on"), ("show_freight", "on"), ("show_notes", "on"), ("show_thanks", "on"),
        ("discount_display", perLine ? "per_line" : "combined"), ("tax_display", "combined"),
        ("charges_display", perLine ? "per_line" : "combined"), ("body_html", ""),
    ];

    private static string TemplateShape(InvoiceTemplate t) => $"{t.Name}|{t.Type}|{t.Design}|{t.AccentColor}|{t.IsDefault}|{t.OptionsJson}|{t.BodyHtml}";

    /// <summary>The preview HTML made comparable: the company's tag and today's date (the sample invoice is dated today).</summary>
    private static string PreviewShape(string html, string tag)
    {
        var today = DateTime.Now;
        foreach (string f in new[] { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd MMM yyyy", "d MMM yyyy", "MMM dd, yyyy", "MMMM d, yyyy", "dd-MM-yyyy", "dd-MMM-yyyy" })
            html = html.Replace(today.ToString(f, System.Globalization.CultureInfo.InvariantCulture), "TODAY");
        return P.Norm(html, tag);
    }

    /// <summary>Preview, create and edit through the designer as an admin; returns the golden shape.</summary>
    private static async Task<object> DesignerAsync(int cid, Func<string, (string, string)[], Task<string>> post)
    {
        string preview = await post("/settings/templates/preview", DesignerForm("sales", "Preview", "modern", "#1d4ed8", true));
        await post("/settings/templates/create", DesignerForm("purchase", "Our design", "letterhead", "#6d28d9", false));
        int id;
        await using (var db = P.Db(cid)) id = await db.Set<InvoiceTemplate>().Where(t => t.Name == "Our design").Select(t => t.Id).SingleAsync();
        await post($"/settings/templates/edit/{id}", DesignerForm("purchase", "Our design v2", "bold", "#c2410c", true));
        await using var check = P.Db(cid);
        return new
        {
            preview = PreviewShape(preview, await P.TagOfAsync(cid)),
            templates = (await check.Set<InvoiceTemplate>().AsNoTracking().OrderBy(t => t.Name).ToListAsync()).Select(TemplateShape).ToList(),
        };
    }

    [ReferenceFact]
    public async Task Preview_create_and_edit_write_what_python_wrote()
    {
        await using var host = await P.HostAsync();
        var (net, netAdmin) = await P.FreshCompanyAsync(host);
        var c = host.ClientFor(netAdmin, net);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/settings/templates/create?type=sales")).StatusCode);
        var flashes = new List<string>();
        var shape = await DesignerAsync(net, async (url, form) =>
        {
            var r = await P.PostAsync(c, url, "/settings/templates/create?type=sales", form);
            if (url.EndsWith("/preview"))
                return await r.Content.ReadAsStringAsync();
            flashes.Add(await P.FollowAsync(c, r));
            return ((int)r.StatusCode).ToString();
        });
        Assert.Contains("Template 'Our design' created.", flashes[0]);
        Assert.Contains("updated.", flashes[1]);
        Golden.Match("TemplateDesigner", shape);

        // A custom template keeps its own HTML.
        await using var check = P.Db(net);
        int netId = await check.Set<InvoiceTemplate>().Where(t => t.Name == "Our design v2").Select(t => t.Id).SingleAsync();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/settings/templates/edit/{netId}")).StatusCode);
        var custom = DesignerForm("sales", "Hand made", "custom", "#0f766e", false).Select(f => f.Item1 == "body_html" ? (f.Item1, "<p>{{invoice_no}}</p>") : f).ToArray();
        await P.PostAsync(c, "/settings/templates/create", "/settings/templates/create?type=sales", custom);
        Assert.Equal("<p>{{invoice_no}}</p>", (await check.Set<InvoiceTemplate>().AsNoTracking().SingleAsync(t => t.Name == "Hand made")).BodyHtml);
        string editPage = await (await c.GetAsync($"/settings/templates/edit/{netId}")).Content.ReadAsStringAsync();
        AuthPortalTests.AssertScriptsClean("template editor", editPage);
    }
}
