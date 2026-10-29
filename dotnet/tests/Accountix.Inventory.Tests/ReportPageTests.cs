using System.Net;
using System.Text.Json;
using Accountix.Core.Entities;
using Accountix.Core.Shared;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Inventory.Tests;

/// <summary>
/// Dashboard, stock lists, reports, product ledgers and the voucher preview
/// through the .NET app (ACCOUNTIX_TEST_DB=inventory), on report data of
/// their own in company B and on the seeded demo in company 1.
/// </summary>
public class ReportPageTests(ReportFixture f) : IClassFixture<ReportFixture>
{
    [ReferenceFact]
    public async Task The_dashboard_counts_and_brings_drifted_stock_back_to_the_ledger()
    {
        await using var host = await ReportKit.HostAsync();
        var c = host.ClientFor(f.User, f.Company);
        foreach (string url in new[] { "/inventory/", "/inventory/dashboard" })
        {
            string html = await ReportKit.PageAsync(c, url);
            Assert.Contains("stats-grid", html);
            Assert.Contains("Recent Products", html);
            Assert.Contains("href=\"/inventory/vouchers/stock-take\"", html);
        }
        await using var db = ReportKit.Db(f.Company);
        var stock = await db.Set<InvProduct>().AsNoTracking().Where(p => p.Id == f.P1 || p.Id == f.P2)
            .ToDictionaryAsync(p => p.Id, p => p.CurrentStock);
        Assert.Equal(8.5m, stock[f.P1]);
        Assert.Equal(0m, stock[f.P2]);          // was 7, the ledger says 0
        (await host.MeasureAsync(c, "/inventory/", runs: 1)).Within(5000, 40);
    }

    [ReferenceFact]
    public async Task Every_page_renders_on_the_demo_company_in_a_fixed_number_of_queries()
    {
        int cid = ReportKit.DefaultCompany;
        await using var host = await ReportKit.HostAsync();
        var c = host.ClientFor(ReportKit.Admin, cid);
        int pid;
        await using (var db = ReportKit.Db(cid))
            pid = await db.Set<StockLedger>().OrderBy(r => r.Id).Select(r => r.ProductId).FirstAsync();
        foreach (string url in new[]
                 {
                     "/inventory/", "/inventory/stock/", "/inventory/stock/?low_only=1&q=Sol", "/inventory/stock/movements",
                     "/inventory/reports/stock-ledger", $"/inventory/reports/stock-ledger?product_id={pid}",
                     "/inventory/reports/valuation", "/inventory/reports/valuation?as_of=2026-12-31&from=2026-01-01",
                     "/inventory/reports/low-stock", $"/inventory/reports/api/stock-ledger-json?product_id={pid}",
                     $"/inventory/vouchers/product-ledger?product_id={pid}", "/inventory/vouchers/product-ledger/list",
                 })
            (await host.MeasureAsync(c, url, runs: 1)).Within(5000, 40);
        string val = await ReportKit.PageAsync(c, "/inventory/reports/valuation?as_of=2026-12-31&from=2026-01-01");
        Assert.Contains("id=\"valuation-table\"", val);
        Assert.Contains("class=\"sv-wrap\"", val);       // the wide table scrolls in its own box on phones
        Assert.Contains("data-export=\"excel\"", val);
    }

    [ReferenceFact]
    public async Task Stock_list_searches_and_marks_low_stock()
    {
        await using var host = await ReportKit.HostAsync();
        var c = host.ClientFor(f.User, f.Company);
        string all = await ReportKit.PageAsync(c, $"/inventory/stock/?q=rpt-{f.Tag}");
        Assert.Contains($"RPT-{f.Tag}-A", all);
        Assert.Contains($"RPT-{f.Tag}-B", all);
        Assert.Contains($"RPT-{f.Tag}-C", all);
        Assert.Contains($"RPT Cat {f.Tag}", all);
        Assert.Contains($"href=\"/inventory/products/adjust-stock/{f.P1}\"", all);
        Assert.Contains("<span class=\"badge badge-danger\">8.5</span>", all);
        string low = await ReportKit.PageAsync(c, $"/inventory/stock/?q=RPT-{f.Tag}&low_only=1");
        Assert.Contains($"RPT-{f.Tag}-A", low);
        Assert.DoesNotContain($"RPT-{f.Tag}-B", low);     // reorder level 0: never "low" on this list
        Assert.Contains("checked=\"checked\"", low);
        string none = await ReportKit.PageAsync(c, "/inventory/stock/?q=no-such-product-zz");
        Assert.Contains("No products", none);
    }

    [ReferenceFact]
    public async Task Movements_list_the_latest_first_and_filter_by_product()
    {
        await using var host = await ReportKit.HostAsync();
        var c = host.ClientFor(f.User, f.Company);
        string html = await ReportKit.PageAsync(c, $"/inventory/stock/movements?product_id={f.P1}");
        string body = ReportKit.Text(html, "<tbody>", "</tbody>");
        Assert.StartsWith($"2026-03-15 11:00 Rpt {f.Tag} A adjustment_out 2.5 - #- -", body);
        Assert.Contains($"2026-03-01 09:05 Rpt {f.Tag} A adjustment_in 5 stock_adjustment #{f.AdjId} Count", body);
        Assert.Contains($"<option value=\"{f.P1}\" selected=\"selected\">", html);
    }

    [ReferenceFact]
    public async Task Stock_and_product_ledgers_list_rows_in_document_date_order()
    {
        await using var host = await ReportKit.HostAsync();
        var c = host.ClientFor(f.User, f.Company);
        string html = await ReportKit.PageAsync(c, $"/inventory/reports/stock-ledger?product_id={f.P1}");
        string body = ReportKit.Text(html, "<tbody>", "</tbody>");
        int pi = body.IndexOf($"RPTPI-{f.Tag}", StringComparison.Ordinal), cons = body.IndexOf($"RPTC-{f.Tag}", StringComparison.Ordinal);
        int adj = body.IndexOf($"RPTA-{f.Tag}", StringComparison.Ordinal), scrap = body.IndexOf($"RPTS-{f.Tag}", StringComparison.Ordinal);
        Assert.True(pi < cons && cons < adj && adj < scrap, body);      // the ADJ was keyed last but is dated 1 Mar
        Assert.Contains("10-Jan-2026", body);
        Assert.Contains("10.0000", body);
        Assert.Contains($"href=\"/inventory/vouchers/preview/CONS/{f.ConsId}\"", html);
        Assert.Contains("href=\"/inventory/reports/stock-ledger\" class=\"btn btn-outline\">Clear", html);

        string pl = await ReportKit.PageAsync(c, $"/inventory/vouchers/product-ledger?product_id={f.P1}");
        Assert.Contains($"SKU: RPT-{f.Tag}-A | Unit: pcs", pl);
        Assert.Contains($"href=\"/inventory/vouchers/preview/PI/{f.PiId}\"", pl);
        string other = await ReportKit.PageAsync(c, $"/inventory/vouchers/product-ledger?product_id={f.P2}");
        Assert.DoesNotContain("/inventory/vouchers/preview/OPENING", other);  // only document types with a preview link
        Assert.Contains($"OPEN-RPT-{f.Tag}-B", other);

        string empty = await ReportKit.PageAsync(c, "/inventory/vouchers/product-ledger");
        Assert.DoesNotContain("card-header\" style=\"display:flex", empty);
        Assert.DoesNotContain("data-export", empty);

        string list = await ReportKit.PageAsync(c, "/inventory/vouchers/product-ledger/list");
        Assert.Contains($"Rpt {f.Tag} A</td>", list);
        Assert.Contains("8.5 pcs", list);
        Assert.Contains("0.0 pcs", list);             // P2: float(0) as Python prints it
        Assert.Contains($"href=\"/inventory/vouchers/product-ledger?product_id={f.P1}\"", list);
    }

    [ReferenceFact]
    public async Task Valuation_waits_to_be_asked_then_values_by_document_date()
    {
        await using var host = await ReportKit.HostAsync();
        var c = host.ClientFor(f.User, f.Company);
        string wait = await ReportKit.PageAsync(c, "/inventory/reports/valuation");
        Assert.Contains("Choose the dates, then press Apply", wait);
        Assert.DoesNotContain("valuation-table", wait);
        Assert.DoesNotContain("data-export", wait);

        string html = await ReportKit.PageAsync(c, "/inventory/reports/valuation?from=2026-02-01&as_of=2026-03-31");
        Assert.Contains("value=\"2026-02-01\"", html);
        Assert.Contains("value=\"2026-03-31\"", html);
        Assert.Contains($"href=\"/inventory/vouchers/product-ledger?product_id={f.P1}\">Rpt {f.Tag} A", html);
        Assert.DoesNotContain($"Rpt {f.Tag} B", html);         // nothing of P2 by 31 Mar

        var r = await c.GetAsync("/inventory/reports/valuation?from=2026-02-01&as_of=2026-03-31&format=csv");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var t = CapturingReportExport.Last!;
        Assert.Equal("Stock Valuation", t.Title);
        Assert.Equal("Movement 01 Feb 2026 to 31 Mar 2026 · valued as at 31 Mar 2026", t.Period);
        Assert.Equal("as_at_2026-03-31", t.FilePeriod);
        var row = t.Rows.Single(x => (string?)x.Cells[0] == $"RPT-{f.Tag}-A");
        Assert.Equal(new object?[] { $"RPT-{f.Tag}-A", $"Rpt {f.Tag} A", "pcs", 10m, 1000m, 5m, 600m, 6.5m, 672.73m, 8.5m },
                     row.Cells.Take(10).Select(x => x is decimal d ? (object)decimal.Parse(d.ToString("0.####")) : x).ToArray());
        Assert.Equal(927.27m, (decimal)row.Cells[11]!);
        Assert.Equal(["grand", "total", "total"], t.Rows.TakeLast(3).Select(x => x.Style ?? "").ToArray());
        Assert.Equal(ReportColumnKind.Quantity, t.Columns[3].Kind);
        Assert.Equal(ReportColumnKind.Text, t.Columns[4].Kind);

        // A start after the as-of date is pulled back to it; a malformed date falls back to the default.
        string pulled = await ReportKit.PageAsync(c, "/inventory/reports/valuation?from=2026-05-01&as_of=2026-03-31");
        Assert.Contains("id=\"svFrom\" name=\"from\" value=\"2026-03-31\"", pulled);
        string bad = await ReportKit.PageAsync(c, "/inventory/reports/valuation?as_of=31/03/2026&run=1");
        Assert.Contains("valuation-table", bad);
    }

    [ReferenceFact]
    public async Task Low_stock_lists_products_at_or_below_their_reorder_level()
    {
        await using var host = await ReportKit.HostAsync();
        var c = host.ClientFor(f.User, f.Company);
        string html = await ReportKit.PageAsync(c, "/inventory/reports/low-stock");
        Assert.Contains($"<td>RPT-{f.Tag}-A</td>", html);
        Assert.DoesNotContain($"RPT-{f.Tag}-C", html);                 // inactive
        var r = await c.GetAsync("/inventory/reports/low-stock?format=excel");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var t = CapturingReportExport.Last!;
        Assert.Equal(["SKU", "Product", "Current Stock", "Reorder Level", "Short By", "Unit", "Status"], t.Columns.Select(x => x.Header));
        var a = t.Rows.Single(x => (string?)x.Cells[0] == $"RPT-{f.Tag}-A").Cells;
        Assert.Equal(new object?[] { 8.5, 10d, 1.5, "pcs", "Low" }, a.Skip(2).ToArray());
    }

    [ReferenceFact]
    public async Task Stock_ledger_json_carries_every_row_by_document_date()
    {
        await using var host = await ReportKit.HostAsync();
        var c = host.ClientFor(f.User, f.Company);
        using var doc = JsonDocument.Parse(await ReportKit.PageAsync(c, $"/inventory/reports/api/stock-ledger-json?product_id={f.P1}"));
        var rows = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(["PI", "CONS", "ADJ", "SCRAP"], rows.Select(x => x.GetProperty("voucher_type").GetString()));
        Assert.Equal("2026-03-01", rows[2].GetProperty("date").GetString());
        Assert.Equal(109.0906, rows[3].GetProperty("running_avg_cost").GetDouble());
        Assert.Equal(927.27, rows[3].GetProperty("running_value").GetDouble());
        Assert.Equal("[]", await ReportKit.PageAsync(c, "/inventory/reports/api/stock-ledger-json"));
    }

    [ReferenceFact]
    public async Task The_print_preview_shows_each_document_type()
    {
        await using var host = await ReportKit.HostAsync();
        var c = host.ClientFor(f.User, f.Company);
        string cons = await ReportKit.PageAsync(c, $"/inventory/vouchers/preview/CONS/{f.ConsId}");
        Assert.Contains($"<title>Consumption Voucher - RPTC-{f.Tag}</title>", cons);
        Assert.Contains("<span class=\"label\">Department:</span> <span class=\"value\">Workshop</span>", cons);
        Assert.Contains("<td>4.0000</td>", cons);
        Assert.Contains("Approved by: ", cons);
        Assert.Contains("05-Feb-2026 10:30", cons);
        Assert.Contains("05-Feb-2026", ReportKit.Text(cons, "class=\"voucher-meta\"", "<table>"));
        Assert.Contains("/static/css/app.css", cons);

        string scrap = await ReportKit.PageAsync(c, $"/inventory/vouchers/preview/SCRAP/{f.ScrapId}");
        Assert.Contains("<span class=\"label\">Reason:</span> <span class=\"value\">None</span>", scrap);
        Assert.Contains("status-unapproved", scrap);
        Assert.DoesNotContain("Approved by:", scrap);

        string adj = await ReportKit.PageAsync(c, $"/inventory/vouchers/preview/ADJ/{f.AdjId}");
        Assert.Contains("<th>System Qty</th><th>Physical Qty</th><th>Diff</th>", adj);
        Assert.Contains("<td>6.0000</td><td>11.0000</td><td>5.0000</td>", adj);
        Assert.Contains("colspan=\"6\"", adj);

        string pi = await ReportKit.PageAsync(c, $"/inventory/vouchers/preview/PI/{f.PiId}");
        Assert.Contains($"<div class=\"voucher-no\">RPTPI-{f.Tag}</div>", pi);
        Assert.Contains("<td>Freight line</td>", pi);
        Assert.Contains("<span class=\"value\">1000.5</span>", pi);
        Assert.Contains("<td>10.0</td>", pi);
        Assert.Contains("1,000.50", pi);                        // 10 x 100 (no discount total) + 0.50

        string pr = await ReportKit.PageAsync(c, $"/inventory/vouchers/preview/PR/{f.PrId}");
        Assert.Contains($"<title>Purchase Return - RPT-DN-{f.Tag}</title>", pr);
        Assert.Contains("<span class=\"label\">Net Return:</span> <span class=\"value\">200.0</span>", pr);
        Assert.Contains("status-new", pr);

        var unknown = await c.GetAsync($"/inventory/vouchers/preview/SI/{f.PiId}");
        Assert.Equal(HttpStatusCode.Redirect, unknown.StatusCode);
        Assert.Equal("/inventory/vouchers/consumption/list", unknown.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/inventory/vouchers/preview/CONS/987654321")).StatusCode);
    }

    [ReferenceFact]
    public async Task Exports_carry_the_tables_python_builds()
    {
        await using var host = await ReportKit.HostAsync();
        var c = host.ClientFor(f.User, f.Company);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/inventory/reports/stock-ledger?product_id={f.P1}&format=excel")).StatusCode);
        var t = CapturingReportExport.Last!;
        Assert.Equal($"Stock Ledger — Rpt {f.Tag} A", t.Title);
        Assert.Equal($"SKU RPT-{f.Tag}-A · unit pcs · document-date order", t.Period);
        Assert.Equal($"RPT-{f.Tag}-A", t.FilePeriod);
        Assert.Empty(t.ExtraSheets);                                 // the report page never sends the re-costing sheet
        Assert.Equal(["Date", "Voucher", "Type", "Qty In", "Qty Out", "Unit Cost", "Value", "Running Qty", "Running Value", "Avg Cost", "Notes"],
                     t.Columns.Select(x => x.Header));
        Assert.Equal(new object?[] { new DateOnly(2026, 2, 5), $"CONS RPTC-{f.Tag}", "Out", 0d, 4d, 100d, 400d, 6d, 600d, 100d, "Consumption: Repairs" },
                     t.Rows[1].Cells.ToArray());

        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/inventory/vouchers/product-ledger?product_id={f.P1}&format=excel")).StatusCode);
        var sheet = Assert.Single(CapturingReportExport.Last!.ExtraSheets);
        Assert.Equal("Re-costing history", sheet.Name);
        Assert.Equal(new object?[] { new DateOnly(2026, 2, 5), $"CONS RPTC-{f.Tag}", 380d, 400d, 20d, "reversal of RPTX" }, sheet.Rows[0].ToArray());
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/inventory/vouchers/product-ledger?product_id={f.P1}&format=pdf")).StatusCode);
        Assert.Empty(CapturingReportExport.Last!.ExtraSheets);          // Excel only

        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/inventory/vouchers/product-ledger/list?format=csv")).StatusCode);
        var sub = CapturingReportExport.Last!;
        Assert.Equal("Product Sub-Ledgers", sub.Title);
        Assert.Equal("grand", sub.Rows[^1].Style);
        Assert.Equal(Math.Round(sub.Rows.SkipLast(1).Sum(x => (double)x.Cells[5]!), 2), (double)sub.Rows[^1].Cells[5]!);

        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/inventory/reports/stock-ledger?product_id={f.P1}&format=docx")).StatusCode);
        // No product chosen: the format is ignored and the page renders, as Python.
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/inventory/reports/stock-ledger?format=excel")).StatusCode);
    }

    [ReferenceFact]
    public async Task Without_inventory_rights_every_page_is_refused()
    {
        // "restricted" is a manager in B with every inventory right unticked. Give them the
        // module so the section rights are what refuses (put back afterwards).
        string? flags;
        await using (var db = Reference.NewDb())
        {
            var m = await db.Memberships.SingleAsync(x => x.UserId == ReportKit.Restricted && x.CompanyId == f.Company);
            flags = m.ModuleFlags;
        }
        await using var host = await ReportKit.HostAsync();
        var c = host.ClientFor(ReportKit.Restricted, f.Company);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/inventory/reports/low-stock")).StatusCode);   // no module
        try
        {
            await using (var db = Reference.NewDb())
            {
                var m = await db.Memberships.SingleAsync(x => x.UserId == ReportKit.Restricted && x.CompanyId == f.Company);
                var dict = string.IsNullOrEmpty(flags) ? new Dictionary<string, bool>()
                    : JsonSerializer.Deserialize<Dictionary<string, bool>>(flags)!;
                dict["inventory"] = true;
                m.SetModuleFlags(dict);
                await db.SaveChangesAsync();
            }
            foreach (string url in new[]
                     {
                         "/inventory/", "/inventory/stock/", "/inventory/stock/movements", "/inventory/reports/stock-ledger",
                         "/inventory/reports/valuation?run=1", "/inventory/reports/low-stock", "/inventory/vouchers/product-ledger",
                         "/inventory/vouchers/product-ledger/list", $"/inventory/vouchers/preview/CONS/{f.ConsId}",
                         $"/inventory/vouchers/preview/PI/{f.PiId}",
                     })
            {
                var r = await c.GetAsync(url);
                Assert.True(r.StatusCode == HttpStatusCode.Redirect, $"{url}: {(int)r.StatusCode}");
            }
            var api = await c.GetAsync($"/inventory/reports/api/stock-ledger-json?product_id={f.P1}");
            Assert.Equal(HttpStatusCode.Forbidden, api.StatusCode);
            Assert.Contains("You don't have 'view' rights for this section.", await api.Content.ReadAsStringAsync());
        }
        finally
        {
            await using var db = Reference.NewDb();
            var m = await db.Memberships.SingleAsync(x => x.UserId == ReportKit.Restricted && x.CompanyId == f.Company);
            m.ModuleFlags = flags;
            await db.SaveChangesAsync();
        }
    }

    [ReferenceFact]
    public async Task The_old_inventory_sign_in_and_sign_out_hand_over_to_the_one_sign_in()
    {
        await using var host = await ReportKit.HostAsync();
        var anon = host.AnonymousClient();
        var login = await anon.GetAsync("/inventory/login");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/auth/login", login.Headers.Location!.OriginalString);
        var anonOut = await anon.GetAsync("/inventory/logout");                   // login_required first
        Assert.NotEqual(HttpStatusCode.OK, anonOut.StatusCode);
        Assert.NotEqual("/auth/logout", anonOut.Headers.Location?.OriginalString);
        var c = host.ClientFor(f.User, f.Company);
        var logout = await c.GetAsync("/inventory/logout");
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/auth/logout", logout.Headers.Location!.OriginalString);
    }

    [Fact]
    public void Every_action_of_the_report_controllers_declares_its_right()
    {
        var types = new[]
        {
            typeof(Controllers.DashboardController), typeof(Controllers.StockController), typeof(Controllers.ReportsController),
            typeof(Controllers.ProductLedgerController),
        };
        var missing = types.SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                                                         | System.Reflection.BindingFlags.DeclaredOnly))
            .Where(m => !m.IsSpecialName)
            .Where(m => !m.IsDefined(typeof(Accountix.Web.Infrastructure.RightAttribute), false)
                        && !m.IsDefined(typeof(Accountix.Web.Infrastructure.AnyRightAttribute), false)
                        && !m.IsDefined(typeof(Accountix.Web.Infrastructure.AnyMemberAttribute), false))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}").ToList();
        Assert.Empty(missing);
    }
}
