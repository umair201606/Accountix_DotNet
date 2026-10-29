using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Parties.Tests;

/// <summary>
/// The register's bulk print (/invoicing/registers/documents, /data?full=1) and its workbook (/documents/export),
/// rendered through Invoicing's print code and compared with what the Python app answered for the same invoices
/// (Golden/BulkPrintDocs.json, Golden/BulkWorkbook.json).
/// </summary>
public class BulkPrintTests
{
    private static Task<ModuleHost> HostAsync() =>
        ModuleHost.StartAsync(typeof(PartiesModule).Assembly, typeof(Accountix.Invoicing.InvoicingModule).Assembly,
                              typeof(Accountix.Accounting.AccountingModule).Assembly, typeof(Accountix.Finance.FinanceModule).Assembly);

    /// <summary>The golden invoices' day (in a fresh company of their own).</summary>
    private static readonly DateTime GoldenDay = new(2026, 2, 10);

    /// <summary>
    /// Three printable sales invoices on one day of their own (lines, a product, a charge, combined and per-line tax):
    /// in company B on a free day far back, or (golden) in <paramref name="companyId"/> on <see cref="GoldenDay"/> with fixed names.
    /// </summary>
    private static async Task<(DateTime Day, int[] Ids, string Tag)> InvoicesAsync(int? companyId = null)
    {
        string tag = companyId is null ? Scenario.NewTag() : "GOLD";
        await using var db = Scenario.Db(companyId);
        var day = GoldenDay;
        if (companyId is null)
        {
            // A day of its own, far back, so the register's range holds only these invoices.
            day = new DateTime(2019, 1, 1).AddDays(Random.Shared.Next(0, 3000));
            while (await db.Set<InvInvoice>().AnyAsync(i => i.InvoiceDate >= day && i.InvoiceDate < day.AddDays(1)))
                day = day.AddDays(1);
        }
        var (cid, _, _, _) = await Scenario.CustomerAsync(db, tag);
        var product = new InvProduct { Sku = $"BP-{tag}", Name = $"Panel {tag}", Unit = "pcs", IsActive = true, CreatedAt = DateTime.UtcNow };
        db.Add(product);
        await db.SaveChangesAsync();
        int charge = (await db.Set<ChartOfAccount>().Where(a => a.Level == 5 && a.Type == "Expense").OrderBy(a => a.Id).FirstOrDefaultAsync()
                      ?? await db.Set<ChartOfAccount>().Where(a => a.Level == 5).OrderBy(a => a.Id).FirstAsync()).Id;
        var ids = new List<int>();
        for (int n = 0; n < 3; n++)
        {
            int id = await Scenario.InvoiceAsync(db, cid, $"BP{n}-{tag}", 0m, day.AddHours(n), day.AddDays(30));
            var inv = await db.Set<InvInvoice>().SingleAsync(i => i.Id == id);
            inv.TaxMode = n == 1 ? "individual" : "general";
            inv.DiscountMode = "general";
            inv.ChargesMode = "general";
            inv.GlobalSalesTaxPct = 18;
            inv.Notes = n == 2 ? "Deliver <before> Friday & call" : "";
            var lines = new[]
            {
                new InvInvoiceItem { InvoiceId = id, ProductId = product.Id, Description = "Solar panel 550W", Quantity = 4, Unit = "pcs",
                    UnitPrice = 31250.5m, DiscountPct = 2.5m, DiscountAmount = 3125.05m, SalesTaxPct = n == 1 ? 18 : 0,
                    TotalBeforeDiscount = 125002m, TotalAfterDiscount = 121876.95m, Delivery = 0, Installation = 0 },
                new InvInvoiceItem { InvoiceId = id, Description = "Installation & wiring", Quantity = 1.5m, Unit = "job",
                    UnitPrice = 9999.99m, DiscountPct = 0, DiscountAmount = 0, SalesTaxPct = n == 1 ? 5 : 0,
                    TotalBeforeDiscount = 14999.99m, TotalAfterDiscount = 14999.99m, Delivery = 0, Installation = 0 },
            };
            db.AddRange(lines);
            inv.Subtotal = 140001.99m;
            inv.TotalDiscount = 3125.05m;
            inv.TotalTax = n == 1 ? 22687.85m : 24644.65m;
            inv.TotalCharges = 1500m;
            inv.TotalAmount = inv.Subtotal - inv.TotalDiscount + inv.TotalTax + inv.TotalCharges;
            inv.VoucherStatus = n == 0 ? "unapproved" : "approved";
            db.Add(new AdditionalCharge
            {
                DocType = "SI", DocId = id, ChargeAccountId = charge, Description = "Freight", Amount = 1500m, Scope = "general",
                Treatment = "bill", StTaxable = true, WhtTaxable = false, CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            ids.Add(id);
        }
        await Scenario.ReturnAsync(ids[2], cid, 999.99m, $"BPCN-{tag}", companyId, day.AddDays(2));
        return (day, [.. ids], tag);
    }

    private static string Range(DateTime day) => $"mode=custom&from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}";

    /// <summary>The documents in a documents page.</summary>
    private static List<string> PageDocs(string html) =>
        Regex.Matches(html, "<div class=\"doc\">(.*?)</div>\\s*(?=<div class=\"doc\">|</div>\\s*<div class=\"reg-progress)", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value).ToList();

    private static string[] DocUrls(DateTime day) =>
    [
        $"/invoicing/registers/data?full=1&doc=sales&{Range(day)}&limit=2&offset=0",
        $"/invoicing/registers/data?full=1&doc=sales&{Range(day)}&limit=2&offset=2&total=3",
        $"/invoicing/registers/data?full=1&doc=sales&{Range(day)}&status=approved",
        $"/invoicing/registers/documents?doc=sales&{Range(day)}",
        $"/invoicing/registers/documents?doc=sales&{Range(day)}&status=unapproved&view=1",
    ];

    /// <summary>
    /// Three /data?full=1 chunks and two documents pages as comparable values: every distinct document once (in the
    /// order first met), and each chunk and page as the list of the documents it carries. The documents hold no ids
    /// (fixed names and dates in a company of their own), so they are compared as they are, byte for byte.
    /// </summary>
    private static object DocsShape(IReadOnlyList<byte[]> bodies)
    {
        var documents = new List<string>();
        int Ref(string doc)
        {
            int i = documents.IndexOf(doc);
            if (i < 0)
            {
                documents.Add(doc);
                i = documents.Count - 1;
            }
            return i;
        }
        var chunks = bodies.Take(3).Select(b =>
        {
            var j = JsonDocument.Parse(b).RootElement;
            return new
            {
                total = j.GetProperty("total").GetInt32(), rows = j.GetProperty("rows").GetInt32(), offset = j.GetProperty("offset").GetInt32(),
                limit = j.GetProperty("limit").GetInt32(), docs = j.GetProperty("docs").EnumerateArray().Select(d => Ref(d.GetString()!)).ToList(),
            };
        }).ToList();
        var pages = bodies.Skip(3).Select(b => PageDocs(System.Text.Encoding.UTF8.GetString(b)).Select(Ref).ToList()).ToList();
        return new { chunks, pages, documents };
    }

    [ReferenceFact]
    public async Task Bulk_print_chunks_and_the_documents_page_equal_pythons()
    {
        int company = await Scenario.FreshCompanyAsync();
        var (day, _, tag) = await InvoicesAsync(company);
        string[] urls = DocUrls(day);
        await using var host = await HostAsync();
        var c = host.ClientFor(Scenario.OwnerB, company);
        var bodies = new List<byte[]>();
        foreach (string url in urls)
            bodies.Add(await (await c.GetAsync(url)).Content.ReadAsByteArrayAsync());
        Golden.Match("BulkPrintDocs", DocsShape(bodies));

        // The pages: the same count and toolbar.
        for (int i = 3; i < 5; i++)
        {
            string html = System.Text.Encoding.UTF8.GetString(bodies[i]);
            Assert.Equal(i == 3 ? 3 : 1, PageDocs(html).Count);
            Assert.Contains($"(<span id=\"docCount\">{(i == 3 ? 3 : 1)}</span>)", html);
            Assert.Contains("Sales Invoice Register", html);
        }
        string page = System.Text.Encoding.UTF8.GetString(bodies[3]);
        Assert.Contains($"BP2-{tag}", page);
        Assert.Contains("data-docs=\"done\"", page);
        Assert.Contains($"href=\"/invoicing/registers/documents/export?doc=sales&amp;mode=custom", page);
        // The script carries numbers, not encoded text.
        string script = page[page.LastIndexOf("<script>", StringComparison.Ordinal)..];
        Assert.Contains("var preloaded=3, total=3, got=preloaded;", script);
        Assert.DoesNotContain("&quot;", script);
        Assert.DoesNotContain("&amp;", script);
    }

    /// <summary>Every cell from a sheet's heading row down, as comparable text.</summary>
    private static List<string> Cells(XLWorkbook wb, string sheet, string firstHeader)
    {
        var ws = wb.Worksheet(sheet);
        var used = ws.RangeUsed()!;
        int head = Enumerable.Range(1, used.LastRow().RowNumber()).First(r => ws.Cell(r, 1).GetString() == firstHeader);
        var output = new List<string>();
        for (int r = head; r <= used.LastRow().RowNumber(); r++)
            output.Add(string.Join("|", Enumerable.Range(1, used.LastColumn().ColumnNumber()).Select(col =>
            {
                var v = ws.Cell(r, col).Value;
                return v.Type switch
                {
                    XLDataType.Number => v.GetNumber().ToString("0.####", CultureInfo.InvariantCulture),
                    XLDataType.DateTime => v.GetDateTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    XLDataType.Blank => "",
                    _ => v.ToString(CultureInfo.InvariantCulture),
                };
            })));
        return output;
    }

    /// <summary>The invoices' workbook as comparable values: its sheets and every cell of Invoices / Items / Charges.</summary>
    private static object WorkbookShape(byte[] xlsx)
    {
        using var wb = new XLWorkbook(new MemoryStream(xlsx));
        return new
        {
            sheets = wb.Worksheets.Select(w => w.Name).ToList(),
            cells = new[] { ("Invoices", "Date"), ("Items", "Invoice #"), ("Charges", "Invoice #") }
                .ToDictionary(x => x.Item1, x => Cells(wb, x.Item1, x.Item2)),
        };
    }

    [ReferenceFact]
    public async Task The_bulk_workbook_carries_what_pythons_carries()
    {
        int company = await Scenario.FreshCompanyAsync();
        var (day, _, tag) = await InvoicesAsync(company);
        string url = $"/invoicing/registers/documents/export?doc=sales&{Range(day)}&fmt=excel";
        await using var host = await HostAsync();
        var c = host.ClientFor(Scenario.OwnerB, company);
        var r = await c.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Bulk_Invoices", r.Content.Headers.ContentDisposition!.FileName);
        byte[] xlsx = await r.Content.ReadAsByteArrayAsync();
        Golden.Match("BulkWorkbook", WorkbookShape(xlsx));
        using (var wb = new XLWorkbook(new MemoryStream(xlsx)))
            Assert.Contains(Cells(wb, "Items", "Invoice #"), row => row.StartsWith($"BP1-{tag}|BP1-{tag}|") && row.Contains("|Installation & wiring|"));
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/invoicing/registers/documents/export?doc=sales&{Range(day)}&fmt=pdf")).StatusCode);
    }

    [ReferenceFact]
    public async Task The_purchase_side_prints_and_exports()
    {
        await using var host = await HostAsync();
        var c = host.ClientFor(Scenario.OwnerB, Scenario.CompanyB);
        var page = await c.GetAsync("/invoicing/registers/documents?doc=purchase&mode=year");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Purchase Invoice Register", await page.Content.ReadAsStringAsync());
        var data = await c.GetAsync("/invoicing/registers/data?full=1&doc=purchase&mode=year&limit=500");
        var json = JsonDocument.Parse(await data.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(100, json.GetProperty("limit").GetInt32());   // a full chunk is at most a hundred
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/invoicing/registers/documents/export?doc=purchase&mode=year")).StatusCode);
        var none = await c.GetAsync("/invoicing/registers/documents?doc=sales&mode=custom&from=1990-01-01&to=1990-01-02");
        Assert.Contains("No invoices found for this filter.", await none.Content.ReadAsStringAsync());
    }

    [ReferenceFact]
    public async Task A_bulk_chunk_runs_a_fixed_number_of_queries()
    {
        var (day, _, _) = await InvoicesAsync();
        await using var host = await HostAsync();
        var c = host.ClientFor(Scenario.OwnerB, Scenario.CompanyB);
        foreach (string url in new[]
                 {
                     $"/invoicing/registers/data?full=1&doc=sales&{Range(day)}", "/invoicing/registers/data?full=1&doc=sales&mode=year",
                     $"/invoicing/registers/documents?doc=sales&{Range(day)}", "/invoicing/registers/documents?doc=sales&mode=year&view=1",
                     "/invoicing/registers/documents/export?doc=sales&mode=year",
                 })
            (await host.MeasureAsync(c, url, runs: 1)).Within(maxMilliseconds: 10000, maxQueries: 40);
    }
}
