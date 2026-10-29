using System.Globalization;
using System.Net;
using System.Text.Json;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Parties.Tests;

/// <summary>The invoice register (invoicing_app/routes/registers.py): page, chunked data, exports, and the totals Python gave
/// for the same book (Golden/RegisterData.json).</summary>
public class RegisterTests
{
    private static Task<ModuleHost> HostAsync() =>
        ModuleHost.StartAsync(typeof(PartiesModule).Assembly, typeof(Accountix.Accounting.AccountingModule).Assembly,
                              typeof(Accountix.Finance.FinanceModule).Assembly);

    private static string Range()
    {
        var today = DateTime.UtcNow.Date;
        return $"mode=custom&from={today.AddDays(-120):yyyy-MM-dd}&to={today.AddDays(1):yyyy-MM-dd}";
    }

    [ReferenceFact]
    public async Task The_register_waits_for_view_then_lists_with_the_returned_column()
    {
        var s = await Scenario.CreateAsync();
        await Scenario.ReturnAsync(s.InvA, s.CustomerId, 250, $"REG-CN-{s.Tag}");
        await using var host = await HostAsync();
        var c = host.ClientFor(Scenario.OwnerB, Scenario.CompanyB);
        string blank = await (await c.GetAsync("/invoicing/registers/")).Content.ReadAsStringAsync();
        Assert.Contains("id=\"regLoaded\" style=\"display:none\"", blank);
        Assert.DoesNotContain(s.NumA, blank);
        var page = await c.GetAsync($"/invoicing/registers/?doc=sales&{Range()}&status=approved&view=1");
        string html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("<th>Returned</th>", html);
        Assert.Contains(s.NumA, html);
        Assert.Contains($"href=\"/inventory/invoices/{s.InvA}\"", html);
        await c.GetAsync($"/invoicing/registers/?doc=purchase&{Range()}&view=1");
    }

    /// <summary>The day the golden register is built around.</summary>
    private static readonly DateTime T = new(2026, 6, 30);

    private static string GoldenRange => $"mode=custom&from={T.AddDays(-120):yyyy-MM-dd}&to={T.AddDays(1):yyyy-MM-dd}";

    /// <summary>
    /// A fresh company holding only this register: two sales invoices with their tax columns filled, one partly
    /// returned and one paid, an unapproved one; a purchase invoice partly paid and partly returned and an unapproved one.
    /// </summary>
    private static async Task<int> RegisterBookAsync()
    {
        int company = await Scenario.FreshCompanyAsync();
        var s = await Scenario.CreateAsync(companyId: company, tag: "GOLD", today: T);
        await Scenario.ReturnAsync(s.InvB, s.CustomerId, 400, "REG-CN2-GOLD", company, T);
        await Scenario.Engine(company).Pt.AllocateAsync(s.LineId, [new(s.InvA, 3000m)], Scenario.OwnerB);
        await using var db = Scenario.Db(company);
        var a = await db.Set<InvInvoice>().SingleAsync(i => i.Id == s.InvA);
        (a.Subtotal, a.TotalDiscount, a.TotalTax, a.TotalFurtherTax, a.TotalWithholdingTax) = (2700m, 200m, 459m, 41m, 0m);
        var b = await db.Set<InvInvoice>().SingleAsync(i => i.Id == s.InvB);
        (b.Subtotal, b.TotalDiscount, b.TotalTax, b.TotalFurtherTax, b.TotalWithholdingTax) = (3389.83m, 0m, 610.17m, 0m, 135.59m);
        await db.SaveChangesAsync();
        await Scenario.InvoiceAsync(db, s.CustomerId, "REG-U-GOLD", 1234.56m, T.AddDays(-20), null, status: "unapproved");

        var sup = new InvSupplier { Name = "Golden Supplier", IsActive = true, CreatedAt = DateTime.UtcNow };
        db.Add(sup);
        await db.SaveChangesAsync();
        var p1 = new InvPurchaseInvoice
        {
            InvoiceNumber = "PI-GOLD-1", VoucherNumber = "PI-GOLD-1", InvoiceDate = T.AddDays(-15), SupplierId = sup.Id, Subtotal = 10000m,
            TotalDiscount = 500m, TotalExpenses = 300m, TotalTax = 1700m, TotalWithholdingTax = 100m, NetPayable = 11400m, TotalAmount = 11500m,
            PaidAmount = 5000m, PaymentStatus = "partial", Status = "approved", CreatedBy = Scenario.OwnerB, CreatedAt = DateTime.UtcNow, EditVersion = 1,
        };
        var p2 = new InvPurchaseInvoice
        {
            InvoiceNumber = "PI-GOLD-2", VoucherNumber = "PI-GOLD-2", InvoiceDate = T.AddDays(-2), SupplierId = sup.Id, Subtotal = 2000m,
            TotalDiscount = 0m, TotalExpenses = 0m, TotalTax = 0m, TotalWithholdingTax = 0m, NetPayable = 2000m, TotalAmount = 2000m,
            PaidAmount = 0m, PaymentStatus = "unpaid", Status = "unapproved", CreatedBy = Scenario.OwnerB, CreatedAt = DateTime.UtcNow, EditVersion = 1,
        };
        db.AddRange(p1, p2);
        await db.SaveChangesAsync();
        db.Add(new InvPurchaseReturn
        {
            ReturnNumber = "REG-DN-GOLD", OriginalInvoiceId = p1.Id, SupplierId = sup.Id, ReturnDate = T.AddDays(-10), NetReturnAmount = 1000m,
            Status = "approved", CreatedBy = Scenario.OwnerB, CreatedAt = DateTime.UtcNow, EditVersion = 1,
        });
        await db.SaveChangesAsync();
        return company;
    }

    private static readonly string[] GoldenUrls =
    [
        $"/invoicing/registers/data?doc=sales&{GoldenRange}&limit=3&offset=0",
        $"/invoicing/registers/data?doc=sales&{GoldenRange}&status=approved&limit=500",
        $"/invoicing/registers/data?doc=sales&{GoldenRange}&status=unapproved",
        $"/invoicing/registers/data?doc=purchase&{GoldenRange}",
        $"/invoicing/registers/data?doc=purchase&{GoldenRange}&status=approved&limit=1&offset=0",
    ];

    /// <summary>One /data answer as comparable values: counts, totals, labels and the text of every cell.</summary>
    private static object Shape(JsonElement r)
    {
        static IEnumerable<string> Cells(string html) =>
            System.Text.RegularExpressions.Regex.Matches(html, "<td[^>]*>(.*?)</td>", System.Text.RegularExpressions.RegexOptions.Singleline)
                .Select(m => System.Text.RegularExpressions.Regex.Replace(WebUtility.HtmlDecode(m.Groups[1].Value), "<[^>]+>|\\s+", ""));
        return new
        {
            total = r.GetProperty("total").GetInt32(),
            rows = r.GetProperty("rows").GetInt32(),
            totals = r.GetProperty("totals").EnumerateArray().Select(v => v.GetDecimal().ToString("0.00", CultureInfo.InvariantCulture)),
            labels = new[] { "grand", "paid", "returned", "balance" }.ToDictionary(k => k, k => r.GetProperty("labels").GetProperty(k).GetString()),
            cells = Cells(r.GetProperty("html").GetString()!),
            tfoot = Cells(r.GetProperty("tfoot").GetString()!),
        };
    }

    [ReferenceFact]
    public async Task Data_chunks_and_totals_equal_pythons()
    {
        int company = await RegisterBookAsync();
        await using var host = await HostAsync();
        var c = host.ClientFor(Scenario.OwnerB, company);
        var net = new List<object>();
        foreach (string url in GoldenUrls)
            net.Add(Shape(JsonDocument.Parse(await (await c.GetAsync(url)).Content.ReadAsStringAsync()).RootElement));
        Golden.Match("RegisterData", net);
    }

    [ReferenceFact]
    public async Task Exports_answer_excel_pdf_and_csv_and_refuse_other_formats()
    {
        await Scenario.CreateAsync();
        await using var host = await HostAsync();
        var c = host.ClientFor(Scenario.OwnerB, Scenario.CompanyB);
        foreach (var (fmt, type) in new[] { ("excel", "spreadsheetml"), ("pdf", "pdf"), ("csv", "csv") })
        {
            var r = await c.GetAsync($"/invoicing/registers/export?doc=sales&{Range()}&fmt={fmt}");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Contains(type, r.Content.Headers.ContentType!.MediaType);
            Assert.True((await r.Content.ReadAsByteArrayAsync()).Length > 200);
        }
        var csv = await (await c.GetAsync($"/invoicing/registers/export?doc=sales&{Range()}&fmt=csv")).Content.ReadAsStringAsync();
        Assert.Contains("Returned", csv);
        Assert.Contains("TOTAL", csv);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/invoicing/registers/export?doc=sales&{Range()}&fmt=docx")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/invoicing/registers/export?doc=purchase&{Range()}&fmt=excel")).StatusCode);
    }

    [ReferenceFact]
    public async Task The_purchase_register_needs_purchase_invoice_rights()
    {
        await using var host = await HostAsync();
        var c = host.ClientFor(Reference.UserId(Reference.Email("restricted")), Scenario.CompanyB);
        Assert.NotEqual(HttpStatusCode.OK, (await c.GetAsync("/invoicing/registers/?doc=purchase&view=1")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await c.GetAsync("/invoicing/registers/data?doc=sales")).StatusCode);
    }
}
