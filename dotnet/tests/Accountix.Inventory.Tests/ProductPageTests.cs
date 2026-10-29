using System.Net;
using System.Text.Json;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using static Accountix.Inventory.Tests.MasterTestKit;

namespace Accountix.Inventory.Tests;

/// <summary>Products through the .NET pages (inventory_app/routes/products.py), company B.</summary>
public class ProductPageTests
{
    private static async Task<(ModuleHost Host, PageClient Client)> StartAsync()
    {
        await EnsureUnitAsync("Piece", "pcs");
        var host = await HostAsync();
        return (host, host.ClientFor(OwnerB, CompanyB));
    }

    private static Task<HttpResponseMessage> CreateAsync(PageClient c, List<KeyValuePair<string, string>> form) =>
        c.PostFormAsync("/inventory/products/create", form, "/inventory/products/create");

    [ReferenceFact]
    public async Task Every_product_page_renders()
    {
        var (host, c) = await StartAsync();
        await using var _ = host;
        string tag = Tag();
        await CreateAsync(c, ProductForm($"PR-{tag}", $"Render {tag}"));
        var p = (await ProductAsync($"PR-{tag}"))!;

        var list = await c.GetAsync("/inventory/products/");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        string html = await BodyAsync(list);
        Assert.Contains("+ New Product", html);
        Assert.Contains($"<strong>PR-{tag}</strong>", html);
        Assert.Contains($"href=\"/inventory/vouchers/product-ledger?product_id={p.Id}\"", html);
        Assert.Contains("class=\"table-responsive\"", html);

        string form = await BodyAsync(await c.GetAsync("/inventory/products/create"));
        Assert.Contains("id=\"sku\"", form);
        Assert.Contains("id=\"unitModal\"", form);
        Assert.Contains("var EXCLUDE = null;", form);
        Assert.Contains("name=\"opening_date\"", form);

        string edit = await BodyAsync(await c.GetAsync($"/inventory/products/edit/{p.Id}"));
        Assert.Contains($"var EXCLUDE = {p.Id};", edit);
        Assert.Contains("value=\"100.0\"", edit);          // Money columns print as Python floats
        Assert.Contains("value=\"1.5\"", edit);
        Assert.DoesNotContain("name=\"opening_date\"", edit);
        Assert.Contains("name=\"is_active\" checked", edit);

        string adjust = await BodyAsync(await c.GetAsync($"/inventory/products/adjust-stock/{p.Id}"));
        Assert.Contains("name=\"quantity\"", adjust);
        Assert.Contains($"Adjust Stock: Render {tag}", adjust);

        string batch = await BodyAsync(await c.GetAsync("/inventory/products/batch"));
        Assert.Contains("id=\"beBody\"", batch);
        Assert.Contains("name=\"opening_date\"", batch);

        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/inventory/products/edit/999999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/inventory/products/adjust-stock/999999")).StatusCode);

        // Search and category filter.
        string found = await BodyAsync(await c.GetAsync($"/inventory/products/?q=pr-{tag.ToLowerInvariant()}"));
        Assert.Contains($"<strong>PR-{tag}</strong>", found);
    }

    [ReferenceFact]
    public async Task Creating_a_product_makes_its_ledger_account()
    {
        var (host, c) = await StartAsync();
        await using var _ = host;
        string tag = Tag();
        var r = await CreateAsync(c, ProductForm($"  NEW-{tag} ", $"New   product {tag}", hs: ""));
        string page = Decode(await FollowAsync(c, r));
        Assert.Contains($"Product created — ledger account 'New product {tag} (NEW-{tag})' added under Inventory", page);

        var p = (await ProductAsync($"NEW-{tag}"))!;
        Assert.Equal($"New product {tag}", p.Name);         // single_line
        Assert.Equal("pcs", p.Unit);
        Assert.Equal(100m, p.UnitPrice);
        Assert.Equal(70m, p.CostPrice);
        Assert.Equal(5, p.ReorderLevel);
        Assert.Equal(0m, p.CurrentStock);
        Assert.Equal(1.5m, p.Weight);
        Assert.Equal("", p.HsCode);
        Assert.Equal("From the test", p.Description);
        Assert.True(p.IsActive);
        Assert.Null(p.CategoryId);

        await using var db = Db();
        var acct = await db.Set<ChartOfAccount>().SingleAsync(a => a.Code == $"1-01-04-01-{p.Id + 100:D4}");
        Assert.Equal($"New product {tag} (NEW-{tag})", acct.Name);
        Assert.Equal(5, acct.Level);
    }

    [ReferenceFact]
    public async Task The_form_refuses_what_python_refuses_in_its_words()
    {
        var (host, c) = await StartAsync();
        await using var _ = host;
        string tag = Tag();
        await CreateAsync(c, ProductForm($"DUP-{tag}", $"Dup {tag}"));

        async Task<string> Refused(List<KeyValuePair<string, string>> form)
        {
            var r = await CreateAsync(c, form);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);      // the form again, with what was typed
            return Decode(await BodyAsync(r));
        }

        string dup = await Refused(ProductForm($" dup-{tag.ToLowerInvariant()}  ", $"DUP  {tag.ToLowerInvariant()}"));
        Assert.Contains($"SKU 'dup-{tag.ToLowerInvariant()}' is already used by another product.", dup);
        Assert.Contains($"A product named 'DUP {tag.ToLowerInvariant()}' already exists.", dup);
        Assert.Contains($"value=\" dup-{tag.ToLowerInvariant()}  \"", dup);      // draft kept as typed

        string blank = await Refused(ProductForm("", " "));
        Assert.Contains("SKU is required. Name is required.", blank);

        string unit = await Refused(ProductForm($"U-{tag}", $"U {tag}", unit: "zz-none"));
        Assert.Contains("Unit 'zz-none' is not set up — add it under Units, or pick one.", unit);

        string numbers = await Refused(ProductForm($"N-{tag}", $"N {tag}", unitPrice: "abc", costPrice: "-1", reorder: "2.5"));
        Assert.Contains("Sale price must be a number (got 'abc'). Cost price cannot be negative. Reorder level must be a whole number.", numbers);

        string cost = await Refused(ProductForm($"C-{tag}", $"C {tag}", costPrice: "0", stock: "5"));
        Assert.Contains("Opening stock needs a cost price — stock brought on at zero cost would later be sold with no cost of sales.", cost);
        Assert.Contains("name=\"current_stock\" class=\"form-control focus-ring\" value=\"5\"", cost);

        string date = await Refused(ProductForm($"D-{tag}", $"D {tag}", stock: "5", openingDate: "31-12-2026"));
        Assert.Contains("'31-12-2026' is not a valid date (YYYY-MM-DD).", date);

        string cat = await Refused(ProductForm($"K-{tag}", $"K {tag}", category: "999999"));
        Assert.Contains("That category does not exist.", cat);

        string longSku = await Refused(ProductForm(new string('S', 51), $"L {tag}"));
        Assert.Contains("SKU is longer than 50 characters.", longSku);

        foreach (string sku in new[] { $"U-{tag}", $"N-{tag}", $"C-{tag}", $"D-{tag}", $"K-{tag}" })
            Assert.Null(await ProductAsync(sku));
    }

    [ReferenceFact]
    public async Task Editing_renames_the_account_and_locks_cost_once_stock_has_moved()
    {
        var (host, c) = await StartAsync();
        await using var _ = host;
        string tag = Tag();
        await CreateAsync(c, ProductForm($"ED-{tag}", $"Edit {tag}"));
        var p = (await ProductAsync($"ED-{tag}"))!;

        var form = ProductForm($"ED-{tag}", $"Edited {tag}", unitPrice: "120.5", costPrice: "80");
        form.Add(new("is_active", "on"));
        var r = await c.PostFormAsync($"/inventory/products/edit/{p.Id}", form, $"/inventory/products/edit/{p.Id}");
        Assert.Contains("Product updated", await FollowAsync(c, r));
        var edited = (await ProductAsync($"ED-{tag}"))!;
        Assert.Equal($"Edited {tag}", edited.Name);
        Assert.Equal(120.5m, edited.UnitPrice);
        Assert.Equal(80m, edited.CostPrice);
        Assert.True(edited.UpdatedAt >= p.UpdatedAt);
        await using (var db = Db())
            Assert.Equal($"Edited {tag} (ED-{tag})",
                (await db.Set<ChartOfAccount>().SingleAsync(a => a.Code == $"1-01-04-01-{p.Id + 100:D4}")).Name);

        // Unticked "Active" switches the product off.
        r = await c.PostFormAsync($"/inventory/products/edit/{p.Id}", ProductForm($"ED-{tag}", $"Edited {tag}", costPrice: "80"),
                                  $"/inventory/products/edit/{p.Id}");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.False((await ProductAsync($"ED-{tag}"))!.IsActive);

        // A stock history keeps the cost where the engine put it, and refuses the delete.
        int ledgerId = await AddStockRowAsync(p.Id);
        try
        {
            string page = await BodyAsync(await c.GetAsync($"/inventory/products/edit/{p.Id}"));
            Assert.Contains("readonly title=\"Kept at the current valuation cost by the stock ledger\"", page);
            r = await c.PostFormAsync($"/inventory/products/edit/{p.Id}",
                                      ProductForm($"ED-{tag}", $"Edited {tag}", costPrice: "999"), $"/inventory/products/edit/{p.Id}");
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Equal(80m, (await ProductAsync($"ED-{tag}"))!.CostPrice);

            r = await c.PostFormAsync($"/inventory/products/delete/{p.Id}", [], "/inventory/products/");
            Assert.Contains("Cannot delete a product with stock history — mark it inactive instead.", Decode(await FollowAsync(c, r)));
            Assert.NotNull(await ProductAsync($"ED-{tag}"));
        }
        finally
        {
            await using var db = Db();
            await db.Set<StockLedger>().Where(x => x.Id == ledgerId).ExecuteDeleteAsync();
        }

        r = await c.PostFormAsync($"/inventory/products/delete/{p.Id}", [], "/inventory/products/");
        Assert.Contains("Product deleted", await FollowAsync(c, r));
        Assert.Null(await ProductAsync($"ED-{tag}"));
    }

    /// <summary>A bare stock-ledger row (no layer, no journal): enough for the "has stock history" checks.</summary>
    private static async Task<int> AddStockRowAsync(int productId)
    {
        await using var db = Db();
        var row = new StockLedger
        {
            ProductId = productId, VoucherType = "TEST", VoucherId = 1, VoucherNumber = "TEST-1", TransactionType = "IN",
            Quantity = 1, UnitCost = 1, TotalCost = 1, RunningQty = 1, RunningCost = 1, RunningAvg = 1,
            ValuationMethod = "weighted_average", TxnDate = DateOnly.FromDateTime(DateTime.Today), CreatedAt = DateTime.UtcNow,
            CreatedBy = OwnerB, Notes = "master-data test",
        };
        db.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    [ReferenceFact]
    public async Task The_duplicate_check_answers_as_python_does()
    {
        var (host, c) = await StartAsync();
        await using var _ = host;
        string tag = Tag();
        await CreateAsync(c, ProductForm($"CK-{tag}", $"Check {tag}"));
        var p = (await ProductAsync($"CK-{tag}"))!;

        async Task<bool> Exists(string query)
        {
            using var doc = JsonDocument.Parse(await BodyAsync(await c.GetAsync("/inventory/products/check?" + query)));
            return doc.RootElement.GetProperty("exists").GetBoolean();
        }

        Assert.True(await Exists($"field=sku&value=%20ck-{tag}%20"));
        Assert.True(await Exists($"field=name&value=check%20%20{tag.ToLowerInvariant()}"));
        Assert.False(await Exists($"field=sku&value=CK-{tag}&exclude={p.Id}"));
        Assert.False(await Exists($"field=sku&value=nope-{tag}"));
        Assert.False(await Exists($"field=description&value=CK-{tag}"));
        Assert.False(await Exists("field=sku&value=%20"));
    }

    [ReferenceFact]
    public async Task Quantity_refusals_on_the_adjust_page()
    {
        var (host, c) = await StartAsync();
        await using var _ = host;
        string tag = Tag();
        await CreateAsync(c, ProductForm($"AQ-{tag}", $"Adjust {tag}"));
        var p = (await ProductAsync($"AQ-{tag}"))!;
        string url = $"/inventory/products/adjust-stock/{p.Id}";

        var r = await c.PostFormAsync(url, [new("quantity", "0"), new("date", ""), new("notes", "")], url);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Quantity must be non-zero", await BodyAsync(r));
        r = await c.PostFormAsync(url, [new("quantity", "lots"), new("date", ""), new("notes", "")], url);
        Assert.Contains("Quantity must be a number (got &#x27;lots&#x27;).", await BodyAsync(r));
        Assert.Equal(0m, (await ProductAsync($"AQ-{tag}"))!.CurrentStock);
    }

    [EngineFact]
    public async Task Adjusting_stock_posts_an_approved_adjustment_voucher()
    {
        var (host, c) = await StartAsync();
        await using var _ = host;
        string tag = Tag();
        await CreateAsync(c, ProductForm($"AS-{tag}", $"Adjust stock {tag}", stock: "10", costPrice: "50"));
        var p = (await ProductAsync($"AS-{tag}"))!;
        string url = $"/inventory/products/adjust-stock/{p.Id}";
        var r = await c.PostFormAsync(url, [new("quantity", "2.5"), new("date", ""), new("notes", "")], url);
        string page = Decode(await FollowAsync(c, r));
        Assert.Matches(@"Stock adjusted by 2\.5 \(ADJ-\d{5}\)\. New stock: 12\.5", page);
        await using var db = Db();
        var mv = await db.Set<InvStockMovement>().SingleAsync(m => m.ProductId == p.Id);
        Assert.Equal("adjustment_in", mv.Type);
        Assert.Equal(2.5m, mv.Quantity);
        Assert.Equal("Manual adjustment of 2.5", mv.Notes);
        var v = await db.Set<StockAdjustmentVoucher>().SingleAsync(x => x.Id == mv.ReferenceId);
        Assert.Equal("approved", v.Status);
        Assert.Equal("Manual adjustment of 2.5", v.Reason);
        Assert.Equal(12.5m, (await ProductAsync($"AS-{tag}"))!.CurrentStock);
    }

    [ReferenceFact]
    public async Task The_import_template_is_the_python_one()
    {
        var (host, c) = await StartAsync();
        await using var _ = host;
        var r = await c.GetAsync("/inventory/products/template");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", r.Content.Headers.ContentType!.MediaType);
        Assert.Contains("product_import_template.xlsx", r.Content.Headers.ContentDisposition!.ToString());
        using var wb = new XLWorkbook(new MemoryStream(await r.Content.ReadAsByteArrayAsync()));
        var ws = wb.Worksheet(1);
        Assert.Equal("Products", ws.Name);
        Assert.Equal(new[] { "SKU", "Name", "Description", "Category", "Unit Price", "Cost Price", "Reorder Level", "Opening Stock", "Unit", "HS Code" },
                     Enumerable.Range(1, 10).Select(i => ws.Cell(1, i).GetString()));
        Assert.Equal("20.5", ws.Cell(3, 8).GetString());
        Assert.Equal(XLDataType.Text, ws.Cell(2, 5).DataType);
        Assert.Equal(22, ws.Column(2).Width);
    }

    private static byte[] Sheet(params object?[][] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Products");
        string[] head = ["SKU", "Name", "Description", "Category", "Unit Price", "Cost Price", "Reorder Level", "Opening Stock", "Unit", "HS Code"];
        for (int i = 0; i < head.Length; i++) ws.Cell(1, i + 1).Value = head[i];
        for (int r = 0; r < rows.Length; r++)
            for (int i = 0; i < rows[r].Length; i++)
                ws.Cell(r + 2, i + 1).Value = rows[r][i] switch
                {
                    null => Blank.Value,
                    double d => d,
                    int n => n,
                    string s => s,
                    _ => rows[r][i]!.ToString(),
                };
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static async Task<HttpResponseMessage> UploadAsync(PageClient c, byte[] file, string url = "/inventory/products/upload-excel")
    {
        string token = await c.TokenFromAsync("/inventory/products/");
        var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(file), "file", "products.xlsx" },
            { new StringContent(token), "__RequestVerificationToken" },
        };
        return await c.PostAsync(url, content);
    }

    [ReferenceFact]
    public async Task An_uploaded_sheet_comes_back_for_review_with_its_problems_flagged()
    {
        var (host, c) = await StartAsync();
        await using var _ = host;
        string tag = Tag();
        var r = await UploadAsync(c, Sheet(
            [$"im-{tag}", $"Imported {tag}", "Desc", null, 100d, 70d, 10d, 50d, "pcs", "8541.4000"],
            [null, null, null, null, null, null, null, null, null, null],              // blank: skipped
            [$"IM-{tag}", $"Other {tag}", "", null, "abc", 0d, 5d, 0d, "nope", ""],
            [$"IM2-{tag}", $"Imported {tag}", "", "No such category", 1d, 1d, 1d, 2.5d, "PCS", ""]));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        string html = Decode(await BodyAsync(r));
        Assert.Contains("2 row(s) need attention", html);
        Assert.Contains($"value=\"IM-{tag}\"", html);                    // upper-cased SKU
        Assert.Contains("value=\"100.0\"", html);                         // a float, as Python prints it
        Assert.Contains("value=\"50\"", html);                            // a Decimal
        Assert.Contains($"Row 4: Sale price must be a number (got 'abc'). Unit 'nope' is not set up — add it under Units, or pick one. " +
                        $"'IM-{tag}' appears more than once in this sheet.", html);
        Assert.Contains("<option value=\"nope\" selected>nope (not set up)</option>", html);
        Assert.Contains($"Row 5: Category 'No such category' not found — add it under Categories first. 'Imported {tag}' appears more than once in this sheet.", html);
        Assert.Null(await ProductAsync($"IM-{tag}"));                     // nothing saved yet

        var none = await c.PostFormAsync("/inventory/products/upload-excel", [], "/inventory/products/");
        Assert.Contains("No file selected.", await FollowAsync(c, none));
        var bad = await UploadAsync(c, "not a workbook"u8.ToArray());
        Assert.Contains("That file could not be read as an Excel workbook (.xlsx). Download the template and fill it in.",
                        Decode(await FollowAsync(c, bad)));
        var empty = await UploadAsync(c, Sheet());
        Assert.Contains("The sheet has no products under its heading row.", Decode(await FollowAsync(c, empty)));
    }

    [ReferenceFact]
    public async Task The_batch_save_creates_good_rows_and_returns_the_rest()
    {
        var (host, c) = await StartAsync();
        await using var _ = host;
        string tag = Tag();
        string data = JsonSerializer.Serialize(new object[]
        {
            new { sku = $"BA-{tag}", name = $"Batch A {tag}", description = "", category_name = "", unit_price = "10", cost_price = "5",
                  reorder_level = "0", current_stock = "0", unit = "pcs", hs_code = "" },
            new { sku = $"ba-{tag}", name = $"Batch B {tag}", description = "", category_name = "", unit_price = "10", cost_price = "5",
                  reorder_level = "0", current_stock = "0", unit = "pcs", hs_code = "" },
            new { sku = "", name = "", _delete = false },
            new { sku = $"BC-{tag}", name = $"Batch C {tag}", _delete = true },
        });
        var r = await c.PostFormAsync("/inventory/products/batch", [new("data", data), new("opening_date", "")], "/inventory/products/batch");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        string html = Decode(await BodyAsync(r));
        Assert.Contains("1 product(s) saved. The rows below were not", html);
        Assert.Contains($"SKU 'BA-{tag}' is already used by another product.", html);
        Assert.NotNull(await ProductAsync($"BA-{tag}"));
        Assert.Null(await ProductAsync($"BC-{tag}"));

        string ok = JsonSerializer.Serialize(new object[]
        {
            new { sku = $"BD-{tag}", name = $"Batch D {tag}", unit_price = 3, cost_price = "2", reorder_level = "1", current_stock = "0", unit = "pcs" },
            new { sku = $"BE-{tag}", name = $"Batch E {tag}", unit_price = "4", cost_price = "2", reorder_level = "1", current_stock = "", unit = "pcs" },
        });
        r = await c.PostFormAsync("/inventory/products/batch", [new("data", ok), new("opening_date", "")], "/inventory/products/batch");
        Assert.Contains("2 product(s) created.", await FollowAsync(c, r));
        Assert.Equal(3m, (await ProductAsync($"BD-{tag}"))!.UnitPrice);

        r = await c.PostFormAsync("/inventory/products/batch", [new("data", "{not json")], "/inventory/products/batch");
        Assert.Contains("The rows sent were not readable — reload the import and try again.", Decode(await FollowAsync(c, r)));

        // A bad opening date keeps the rows on the screen.
        string stock = JsonSerializer.Serialize(new object[]
        {
            new { sku = $"BF-{tag}", name = $"Batch F {tag}", unit_price = "4", cost_price = "2", reorder_level = "1", current_stock = "3", unit = "pcs" },
        });
        r = await c.PostFormAsync("/inventory/products/batch", [new("data", stock), new("opening_date", "nope")], "/inventory/products/batch");
        html = Decode(await BodyAsync(r));
        Assert.Contains("'nope' is not a valid date (YYYY-MM-DD).", html);
        Assert.Contains($"value=\"BF-{tag}\"", html);
        Assert.Null(await ProductAsync($"BF-{tag}"));
    }

    [ReferenceFact]
    public async Task Product_pages_run_a_fixed_number_of_queries()
    {
        var (host, c) = await StartAsync();
        await using var _ = host;
        foreach (string url in new[] { "/inventory/products/", "/inventory/products/create", "/inventory/products/batch" })
            (await host.MeasureAsync(c, url, runs: 1)).Within(maxMilliseconds: 5000, maxQueries: 40);
        // Company 1 holds the demo products: the list must not query per row.
        var demo = host.ClientFor(AdminId(), 1);
        var t = (await host.MeasureAsync(demo, "/inventory/products/", runs: 1)).Within(5000, 40);
        Assert.Contains("badge", t.Body);
    }

    [PerfFact]
    public async Task The_product_list_is_quick_on_a_full_year()
    {
        await using var host = await ModuleHost.StartOnAsync(Perf.ConnectionString, typeof(InventoryModule).Assembly,
                                                             typeof(Accountix.Accounting.AccountingModule).Assembly);
        var c = host.ClientFor(Perf.AdminUserId, Perf.CompanyId);
        string[] urls = ["/inventory/products/", "/inventory/categories/", "/inventory/units/"];
        var net = new List<PageTiming>();
        foreach (string url in urls)
            net.Add((await host.MeasureAsync(c, url)).Within(800, 40));
        // Python's times for these pages (64-77 ms on the same data) are kept in docs/modules/inventory.md as history.
        foreach (var t in net)
            Console.WriteLine($"SPEED {t.Url}: .NET {t.Elapsed.TotalMilliseconds:F0} ms / {t.Queries} queries");
    }

    // ── Golden: the product the Python form made for the same input (Golden/product_*.json, Golden/capture/products.py) ──

    private static async Task<List<string>> CreateGoldenAsync(string name, List<KeyValuePair<string, string>> form, string tag, bool stock)
    {
        Golden.SameInputs(name, form.ToDictionary(f => f.Key, f => f.Value.Replace(tag, "TAG")));
        var (host, c) = await StartAsync();
        await using var _ = host;
        var r = await CreateAsync(c, form);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        int pid = (await ProductAsync(form.Single(f => f.Key == "sku").Value))!.Id;
        var dump = new GoldenDump(CompanyB, OwnerB, new DateOnly(2026, 4, 1)).Product(pid, "P").Text(tag, "TAG");
        var lines = await dump.ProductAsync(pid);
        if (stock)
            lines.AddRange(await dump.ProductStockAsync(pid));
        return lines;
    }

    [ReferenceFact]
    public async Task A_product_made_here_is_the_product_python_made()
    {
        string tag = Tag();
        Golden.Match("product_create", await CreateGoldenAsync("product_create",
            ProductForm($"PRD-{tag}", $"Parity {tag}", unitPrice: "1,250.50", costPrice: "70.125", reorder: "7", weight: "", hs: " 8541 4000 "),
            tag, stock: false));
    }

    [EngineFact]
    public async Task Opening_stock_posts_what_python_posted()
    {
        string tag = Tag();
        Golden.Match("product_opening_stock", await CreateGoldenAsync("product_opening_stock",
            ProductForm($"OS-{tag}", $"Opening {tag}", costPrice: "70.12345", stock: "12.5", openingDate: "2026-04-01"),
            tag, stock: true));
    }
}
