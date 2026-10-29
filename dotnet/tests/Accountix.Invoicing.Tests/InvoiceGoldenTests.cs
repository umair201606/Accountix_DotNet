using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Invoicing.Tests;

/// <summary>
/// Sales and purchase invoices approved (and unapproved) through the .NET app, compared with what
/// the Python app wrote for the same steps on 29 Oct 2026 (Golden/*.json, captured by
/// Golden/capture/invoices.py on its own fresh parties): the documents, their lines and charges,
/// the stock rows, layers, consumptions and re-costings, the products' figures and every journal
/// line, to the paisa, after every step.
/// </summary>
public class InvoiceGoldenTests
{
    public static Dictionary<string, object?> PiStockPayload(Fx.Parties p, string action = "approve") => new()
    {
        ["supplier_id"] = p.Supplier, ["invoice_date"] = "2026-05-02", ["action"] = action,
        ["discount_mode"] = "general", ["expenses_mode"] = "general", ["tax_mode"] = "general",
        ["global_discount_pct"] = 0, ["global_discount_value"] = 0, ["global_sales_tax_pct"] = 0,
        ["global_withholding_tax_pct"] = 0, ["apply_withholding_tax"] = false,
        ["subtotal"] = 11000, ["total_discount"] = 0, ["notes"] = "Stock for parity",
        ["items"] = new object[]
        {
            new Dictionary<string, object?> { ["product_id"] = p.P1, ["description"] = "Panel", ["quantity"] = 100, ["unit"] = "pcs", ["unit_price"] = 40, ["total_before_discount"] = 4000, ["total_after_discount"] = 4000 },
            new Dictionary<string, object?> { ["product_id"] = p.P2, ["description"] = "Inverter", ["quantity"] = 100, ["unit"] = "pcs", ["unit_price"] = 70, ["total_before_discount"] = 7000, ["total_after_discount"] = 7000 },
        },
        ["charges"] = Array.Empty<object>(),
    };

    /// <summary>A combined-mode sale: discount, 18% tax, further tax, withholding, charges in all three treatments.</summary>
    public static Dictionary<string, object?> SiGeneral(Fx.Parties p, string action = "approve") => new()
    {
        ["customer_id"] = p.Customer, ["invoice_date"] = "2026-05-14", ["due_date"] = "2026-06-13", ["label_id"] = "",
        ["party_account_id"] = "", ["sales_order_id"] = "", ["action"] = action,
        ["discount_mode"] = "general", ["charges_mode"] = "general", ["tax_mode"] = "general",
        ["global_discount_pct"] = 5, ["global_discount_value"] = 0, ["global_delivery"] = 0, ["global_installation"] = 0,
        ["global_sales_tax_pct"] = 18, ["further_tax_pct"] = 3, ["apply_further_tax"] = true,
        ["withholding_tax_pct"] = 4.5, ["apply_withholding_tax"] = true, ["notes"] = "Parity sale",
        ["subtotal"] = 25501.5, ["total_discount"] = 1275.08, ["total_charges"] = 0, ["total_tax"] = 0,
        ["total_further_tax"] = 0, ["total_withholding_tax"] = 0, ["total_amount"] = 0,
        ["items"] = new object[]
        {
            new Dictionary<string, object?> { ["product_id"] = p.P1, ["description"] = "Panel 550W", ["quantity"] = 3, ["unit"] = "pcs", ["unit_price"] = 2500.5,
                ["discount_pct"] = 0, ["discount_amount"] = 0, ["delivery"] = 0, ["installation"] = 0, ["sales_tax_pct"] = 0,
                ["total_before_discount"] = 7501.5, ["total_after_discount"] = 7501.5, ["label_id"] = "" },
            new Dictionary<string, object?> { ["product_id"] = p.P2, ["description"] = "Inverter 6kW", ["quantity"] = 2, ["unit"] = "pcs", ["unit_price"] = 9000,
                ["discount_pct"] = 0, ["discount_amount"] = 0, ["delivery"] = 0, ["installation"] = 0, ["sales_tax_pct"] = 0,
                ["total_before_discount"] = 18000, ["total_after_discount"] = 18000, ["label_id"] = "" },
        },
        ["charges"] = new object[]
        {
            new Dictionary<string, object?> { ["charge_account_id"] = p.BillAccount, ["description"] = "Freight", ["amount"] = 350.25, ["scope"] = "general",
                ["treatment"] = "bill", ["st_taxable"] = true, ["wht_taxable"] = true, ["distribution"] = "pro_rata_value" },
            new Dictionary<string, object?> { ["charge_account_id"] = p.ExpenseAccount, ["description"] = "Packing", ["amount"] = 120, ["scope"] = "general",
                ["treatment"] = "absorb" },
            new Dictionary<string, object?> { ["charge_account_id"] = p.ExpenseAccount, ["description"] = "Unloading", ["amount"] = 80.4, ["scope"] = "general",
                ["treatment"] = "expense" },
        },
    };

    /// <summary>A per-line sale: two rates, per-line discounts, a per-item charge split by weight.</summary>
    public static Dictionary<string, object?> SiPerLine(Fx.Parties p) => new()
    {
        ["customer_id"] = p.Customer, ["invoice_date"] = "2026-05-15", ["action"] = "approve",
        ["discount_mode"] = "individual", ["charges_mode"] = "individual", ["tax_mode"] = "individual",
        ["global_discount_pct"] = 0, ["global_discount_value"] = 0, ["global_sales_tax_pct"] = 0,
        ["further_tax_pct"] = 0, ["apply_further_tax"] = false, ["withholding_tax_pct"] = 0, ["apply_withholding_tax"] = false,
        ["subtotal"] = 5000, ["total_discount"] = 350, ["total_tax"] = 836.32, ["notes"] = "",
        ["items"] = new object[]
        {
            new Dictionary<string, object?> { ["product_id"] = p.P1, ["description"] = "A", ["quantity"] = 2, ["unit_price"] = 1000, ["discount_amount"] = 200,
                ["sales_tax_pct"] = 18, ["total_before_discount"] = 2000, ["total_after_discount"] = 1800 },
            new Dictionary<string, object?> { ["product_id"] = p.P2, ["description"] = "B", ["quantity"] = 1, ["unit_price"] = 3000, ["discount_amount"] = 150,
                ["sales_tax_pct"] = 17, ["total_before_discount"] = 3000, ["total_after_discount"] = 2850 },
        },
        ["charges"] = new object[]
        {
            new Dictionary<string, object?> { ["charge_account_id"] = p.BillAccount, ["description"] = "Freight", ["amount"] = 100, ["scope"] = "general", ["treatment"] = "bill", ["st_taxable"] = true },
            new Dictionary<string, object?> { ["charge_account_id"] = p.BillAccount, ["description"] = "Labour", ["amount"] = 60, ["scope"] = "individual",
                ["distribution"] = "by_weight", ["treatment"] = "bill", ["st_taxable"] = true },
            new Dictionary<string, object?> { ["charge_account_id"] = p.BillAccount, ["description"] = "Manual", ["amount"] = 30, ["scope"] = "individual",
                ["distribution"] = "manual", ["manual"] = new object[] { 10, 20 }, ["treatment"] = "bill", ["st_taxable"] = false },
        },
    };

    /// <summary>A purchase with combined discount, absorbed carriage, per-item freight, billed and expense charges, 18% tax and withholding.</summary>
    public static Dictionary<string, object?> PiFull(Fx.Parties p, string action = "approve") => new()
    {
        ["supplier_id"] = p.Supplier, ["invoice_date"] = "2026-05-20", ["action"] = action, ["label_id"] = "",
        ["driver_name"] = "Aslam", ["driver_contact"] = "0300", ["vehicle_number"] = "LES-1234", ["gate_pass"] = "GP-7",
        ["discount_mode"] = "general", ["expenses_mode"] = "individual", ["tax_mode"] = "general",
        ["global_discount_pct"] = 2.5, ["global_discount_value"] = 0, ["global_commission"] = 0, ["global_freight"] = 0, ["global_loading"] = 0,
        ["global_sales_tax_pct"] = 18, ["global_withholding_tax_pct"] = 4.5, ["apply_withholding_tax"] = true,
        ["subtotal"] = 13333.33, ["total_discount"] = 333.33, ["total_expenses"] = 0, ["total_tax"] = 0, ["net_payable"] = 0,
        ["total_amount"] = 0, ["paid_amount"] = 0, ["payment_status"] = "unpaid", ["notes"] = "Parity purchase",
        ["items"] = new object[]
        {
            new Dictionary<string, object?> { ["product_id"] = p.P1, ["description"] = "Panel", ["quantity"] = 7, ["unit"] = "pcs", ["unit_price"] = 333.333,
                ["freight"] = 15.5, ["commission"] = 0, ["loading_unloading"] = 4.25, ["total_before_discount"] = 2333.33, ["total_after_discount"] = 2333.33 },
            new Dictionary<string, object?> { ["product_id"] = p.P2, ["description"] = "Inverter", ["quantity"] = 3, ["unit"] = "pcs", ["unit_price"] = 3666.6667,
                ["freight"] = 0, ["commission"] = 12, ["loading_unloading"] = 0, ["total_before_discount"] = 11000, ["total_after_discount"] = 11000 },
        },
        ["charges"] = new object[]
        {
            new Dictionary<string, object?> { ["charge_account_id"] = p.ExpenseAccount, ["description"] = "Carriage inward", ["amount"] = 211.11, ["treatment"] = "absorb" },
            new Dictionary<string, object?> { ["charge_account_id"] = p.BillAccount, ["description"] = "Supplier freight", ["amount"] = 150, ["treatment"] = "bill",
                ["st_taxable"] = true, ["wht_taxable"] = true },
            new Dictionary<string, object?> { ["charge_account_id"] = p.ExpenseAccount, ["description"] = "Octroi", ["amount"] = 77.77, ["treatment"] = "expense" },
        },
    };

    public static Dictionary<string, object?> Sale(Fx.Parties p, string date, double q1, double q2)
    {
        var d = SiGeneral(p);
        d["invoice_date"] = date;
        d["due_date"] = "";
        d["subtotal"] = q1 * 2500.5 + q2 * 9000;
        d["total_discount"] = Py(0.05 * (q1 * 2500.5 + q2 * 9000));
        d["items"] = new object[]
        {
            new Dictionary<string, object?> { ["product_id"] = p.P1, ["description"] = "Panel 550W", ["quantity"] = q1, ["unit"] = "pcs", ["unit_price"] = 2500.5,
                ["total_before_discount"] = q1 * 2500.5, ["total_after_discount"] = q1 * 2500.5 },
            new Dictionary<string, object?> { ["product_id"] = p.P2, ["description"] = "Inverter 6kW", ["quantity"] = q2, ["unit"] = "pcs", ["unit_price"] = 9000,
                ["total_before_discount"] = q2 * 9000, ["total_after_discount"] = q2 * 9000 },
        };
        return d;
        static double Py(double x) => Accountix.Invoicing.Services.Py.Round(x, 2);
    }

    private static async Task<int> NetSaveAsync(PageClient c, string url, string page, object payload)
    {
        var r = await c.PostJsonAsync(url, payload, page);
        string body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == HttpStatusCode.OK, body);
        using var j = JsonDocument.Parse(body);
        Assert.True(j.RootElement.GetProperty("ok").GetBoolean(), body);
        return j.RootElement.GetProperty("id").GetInt32();
    }

    private const string Pi = "/inventory/purchase-invoice", Si = "/inventory/invoices";

    /// <summary>One step of a scenario: save a payload, or unapprove the document made earlier under <paramref name="Role"/>.</summary>
    private sealed record Step(string Role, string Type, Func<Fx.Parties, Dictionary<string, object?>>? Payload = null, bool Unapprove = false)
    {
        public string Path => Type == "SI" ? Si : Pi;
        public object Input(Fx.Parties p) => Unapprove
            ? new Dictionary<string, object?> { ["role"] = Role, ["type"] = Type, ["unapprove"] = Path }
            : new Dictionary<string, object?> { ["role"] = Role, ["type"] = Type, ["url"] = Path + "/save", ["payload"] = Payload!(p) };
    }

    private static Step Save(string role, string type, Func<Fx.Parties, Dictionary<string, object?>> payload) => new(role, type, payload);
    private static Step Unapprove(string role, string type) => new(role, type, Unapprove: true);

    /// <summary>
    /// Runs the steps through the .NET app on fresh parties of company B and compares the snapshot after
    /// every step (with the step's answer) with the Python app's.
    /// </summary>
    private static async Task RunAsync(string golden, params Step[] steps)
    {
        var settings = await Fx.PinSettingsAsync();
        Golden.SameInputs(golden, new { settings, steps = steps.Select(s => s.Input(Fx.Sentinel)).ToList() });
        var p = await Fx.PartiesAsync();
        var scene = Scene.Of(p);
        await using var host = await Fx.HostAsync();
        var c = host.ClientFor(Fx.OwnerB, Fx.CompanyB);
        foreach (var step in steps)
        {
            HttpResponseMessage r;
            if (step.Unapprove)
            {
                int id = scene.Docs.Single(d => d.Role == step.Role).Id;
                r = await c.PostJsonAsync($"{step.Path}/unapprove/{id}", new { }, $"{step.Path}/{id}");
            }
            else
                r = await c.PostJsonAsync(step.Path + "/save", step.Payload!(p), step.Path + "/");
            string text = await r.Content.ReadAsStringAsync();
            Assert.True(r.StatusCode == HttpStatusCode.OK, $"{step.Role}: {(int)r.StatusCode} {text}");
            using var body = JsonDocument.Parse(text);
            if (!step.Unapprove)
                scene.Docs.Add((step.Type, body.RootElement.GetProperty("id").GetInt32(), step.Role));
            string message = body.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            var lines = new List<string> { $"answer|{(int)r.StatusCode}|{message.Replace(p.Tag, "TAG")}" };
            lines.AddRange(await Snapshot.TakeAsync(scene));
            Golden.Match(golden, lines, (step.Unapprove ? "unapprove " : "") + step.Role);
        }
    }

    [ReferenceFact]
    public Task Approving_a_combined_mode_sale_posts_what_python_posted() =>
        RunAsync("sale_combined", Save("PI-A", "PI", p => PiStockPayload(p)), Save("SI1", "SI", p => SiGeneral(p)));

    [ReferenceFact]
    public Task Approving_a_per_line_sale_posts_what_python_posted() =>
        RunAsync("sale_per_line", Save("PI-A", "PI", p => PiStockPayload(p)), Save("SI1", "SI", SiPerLine));

    [ReferenceFact]
    public Task Approving_a_purchase_posts_what_python_posted_and_receives_the_same_landed_cost() =>
        RunAsync("purchase_full", Save("PI", "PI", p => PiFull(p)));

    /// <summary>
    /// Two receipts at different prices, a sale, a BACK-DATED sale that re-costs the later one (CADJ),
    /// then unapproving the back-dated sale and the second receipt (re-costing back; the purchase
    /// correction carried short): the snapshot after every step is Python's.
    /// </summary>
    [ReferenceFact]
    public async Task A_back_dated_sale_and_unapprovals_cost_and_post_what_python_did()
    {
        await RunAsync("backdated_history",
            Save("PI-A", "PI", s => Fx.Receipt(s, "2026-05-01", 10, 40, 10, 70)),
            Save("PI-B", "PI", s => Fx.Receipt(s, "2026-05-15", 10, 60, 10, 90.5)),
            Save("SI1", "SI", s => Sale(s, "2026-05-20", 12, 3)),
            Save("SI2", "SI", s => Sale(s, "2026-05-10", 5, 4)),          // back-dated: SI1 is re-costed
            Unapprove("SI2", "SI"),
            Unapprove("PI-B", "PI"));
        var after = Golden.Expected("backdated_history", "SI2");
        Assert.Contains(after, r => r.StartsWith("cadj "));                  // the scenario still exercises the re-costing
        Assert.Contains(after, r => r.Contains(" CADJ-SI1|"));
    }

    // ── The dashboard on the test's own documents (a label of its own keeps everyone else's out) ──

    private static Dictionary<string, object?> Labelled(Dictionary<string, object?> d, int label)
    {
        d["label_id"] = label;
        return d;
    }

    private static object DashboardInputs(Fx.Parties p, int label, Dictionary<string, object?> settings) => new
    {
        settings,
        steps = new object[]
        {
            new Dictionary<string, object?> { ["url"] = Pi + "/save", ["payload"] = Labelled(Fx.Receipt(p, "2026-05-01", 100, 40, 100, 70), label) },
            new Dictionary<string, object?> { ["url"] = Si + "/save", ["payload"] = Labelled(SiGeneral(p), label) },
            new Dictionary<string, object?> { ["url"] = Pi + "/save", ["payload"] = Labelled(PiFull(p), label) },
        },
        // Only the year the documents are in: another year falls back to the company's latest, which other tests decide.
        pages = new[] { "/invoicing/?year=2026&label=<L1>" },
    };

    /// <summary>The performance band without its filter form (the form lists every label and year of the company), one tag per line.</summary>
    public static List<string> PerfLines(string html)
    {
        string section = Regex.Replace(Fx.RawSegment(html, "<section class=\"perf\">", "</section>"),
            "<form class=\"perf-filters\".*?</form>", "", RegexOptions.Singleline);
        var lines = Fx.Normalise(section).Replace("><", ">\n<").Split('\n').ToList();
        lines.Add(Fx.RawSegment(html, "<script id=\"perf-data\"", "</script>"));
        return lines;
    }

    [ReferenceFact]
    public async Task The_dashboard_shows_the_figures_python_showed()
    {
        var settings = await Fx.PinSettingsAsync();
        Golden.SameInputs("dashboard_label", DashboardInputs(Fx.Sentinel, Fx.SentinelLabel, settings));
        var p = await Fx.PartiesAsync();
        int label;
        await using (var db = Fx.Db())
        {
            var l = new ProjectLabel { Name = $"Dash {p.Tag}", IsActive = true, CreatedAt = DateTime.UtcNow };
            db.Add(l);
            await db.SaveChangesAsync();
            label = l.Id;
        }
        await using var host = await Fx.HostAsync();
        var c = host.ClientFor(Fx.OwnerB, Fx.CompanyB);
        var inputs = JsonSerializer.SerializeToElement(DashboardInputs(p, label, settings));
        foreach (var step in inputs.GetProperty("steps").EnumerateArray())
        {
            string url = step.GetProperty("url").GetString()!;
            var r = await c.PostJsonAsync(url, step.GetProperty("payload"), url.Replace("/save", "/"));
            Assert.True(r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        }
        foreach (string page in inputs.GetProperty("pages").EnumerateArray().Select(x => x.GetString()!))
        {
            var r = await c.GetAsync(page.Replace("<L1>", label.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            string html = await r.Content.ReadAsStringAsync();
            Assert.True(r.StatusCode == HttpStatusCode.OK, page);
            Golden.Match("dashboard_label", PerfLines(html), page);
        }
    }

    /// <summary>
    /// The editors of a saved draft and of a new document render, and the data the page's script reads is
    /// valid JavaScript (E2E finding F1: Razor's HTML-encoding inside &lt;script&gt; broke the page). The
    /// markup itself was compared with the Python template while both apps existed; it is no longer frozen.
    /// </summary>
    [ReferenceFact]
    public async Task The_editor_carries_its_document_in_valid_script()
    {
        var p = await Fx.PartiesAsync();
        await using var host = await Fx.HostAsync();
        var c = host.ClientFor(Fx.OwnerB, Fx.CompanyB);
        int id = await NetSaveAsync(c, Si + "/save", Si + "/", SiGeneral(p, action: "save"));
        int pid = await NetSaveAsync(c, Pi + "/save", Pi + "/", PiFull(p, action: "save"));
        foreach (var (url, what) in new[] { ($"{Si}/{id}", "SI"), ($"{Si}/", "SI new"), ($"{Pi}/{pid}", "PI"), ($"{Pi}/", "PI new") })
        {
            var r = await c.GetAsync(url);
            string html = await r.Content.ReadAsStringAsync();
            Assert.True(r.StatusCode == HttpStatusCode.OK, what);
            Assert.Contains("id=\"confirmOkBtn\">Yes, continue</button>", html);
            string scripts = Fx.RawSegment(html, "<input type=\"hidden\" id=\"editVersion\"", "js/doc_form.js\"></script>");
            Assert.DoesNotContain("&quot;", scripts);
            Assert.DoesNotContain("&#x", scripts);
            if (what is "SI")
            {
                Assert.Contains("Panel 550W", scripts);
                Assert.Contains("Freight", scripts);
            }
            if (what is "PI")
            {
                Assert.Contains("LES-1234", html);
                Assert.Contains("Carriage inward", scripts);
            }
        }
    }
}
