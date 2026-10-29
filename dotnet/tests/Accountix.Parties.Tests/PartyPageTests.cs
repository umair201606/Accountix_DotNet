using System.Net;
using System.Text.Json;
using Accountix.Core.Entities;
using Accountix.Parties.Services;
using Accountix.TestSupport;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Parties.Tests;

/// <summary>Customers and suppliers: forms, guards, Excel import and batch save (tests/e2e/test_master_data_imports.py).</summary>
public class PartyPageTests
{
    private static Task<ModuleHost> HostAsync() =>
        ModuleHost.StartAsync(typeof(PartiesModule).Assembly, typeof(Accountix.Accounting.AccountingModule).Assembly);

    private static List<KeyValuePair<string, string>> CustomerForm(string name, string credit = "50000", string email = "ali@example.com") =>
    [
        new("name", name), new("contact_person", "Ali"), new("email", email), new("phone", "021-1"), new("mobile", ""),
        new("city", "Lahore"), new("tax_id", "NTN-1"), new("payment_terms", "Net 30"), new("credit_limit", credit),
        new("website", ""), new("address", "1 Mall Road"), new("notes", ""),
    ];

    private static async Task<string> FlashAfter(PageClient c, HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        return WebUtility.HtmlDecode(await (await c.GetAsync(r.Headers.Location!.OriginalString)).Content.ReadAsStringAsync());
    }

    [ReferenceFact]
    public async Task A_customer_is_created_with_its_ledger_account_renamed_on_edit_and_guarded_on_delete()
    {
        await using var host = await HostAsync();
        var c = host.ClientFor(Scenario.OwnerB, Scenario.CompanyB);
        string name = $"Form Customer {Scenario.NewTag()}";
        var r = await c.PostFormAsync("/inventory/customers/create", CustomerForm("  " + name.Replace(" ", "  ") + " "), "/inventory/customers/create");
        string list = await FlashAfter(c, r);
        Assert.Contains($"Customer created — ledger account '{name}' added under Trade Debtors", list);

        await using var db = Scenario.Db();
        var cust = await db.Set<InvCustomer>().AsNoTracking().SingleAsync(x => x.Name == name);
        Assert.Equal((50000m, true, "", "Net 30"), (cust.CreditLimit!.Value, cust.IsActive!.Value, cust.Mobile, cust.PaymentTerms));
        var acct = await db.Set<ChartOfAccount>().AsNoTracking().SingleAsync(a => a.Code == TrackingRules.EntityCode("customer", cust.Id));
        Assert.Equal((name, 5), (acct.Name, acct.Level));

        // Case and spacing do not make a new name; a negative limit and a bad email are refused, with what was typed kept.
        var dup = await c.PostFormAsync("/inventory/customers/create", CustomerForm(name.ToUpperInvariant(), "-5", "nope"), "/inventory/customers/create");
        Assert.Equal(HttpStatusCode.OK, dup.StatusCode);
        string page = WebUtility.HtmlDecode(await dup.Content.ReadAsStringAsync());
        Assert.Contains($"'{name.ToUpperInvariant()}' already exists. Email 'nope' does not look like an email address. Credit limit cannot be negative.", page);
        Assert.Contains("value=\"-5\"", page);
        Assert.Equal(1, await db.Set<InvCustomer>().CountAsync(x => x.Name.ToLower() == name.ToLower()));

        var check = JsonDocument.Parse(await (await c.GetAsync($"/inventory/customers/check?value={Uri.EscapeDataString(name.ToLower())}")).Content.ReadAsStringAsync());
        Assert.True(check.RootElement.GetProperty("exists").GetBoolean());
        var self = JsonDocument.Parse(await (await c.GetAsync($"/inventory/customers/check?value={Uri.EscapeDataString(name)}&exclude={cust.Id}")).Content.ReadAsStringAsync());
        Assert.False(self.RootElement.GetProperty("exists").GetBoolean());

        string edit = await (await c.GetAsync($"/inventory/customers/edit/{cust.Id}")).Content.ReadAsStringAsync();
        Assert.Contains("value=\"50000.0\"", edit);   // the float column, as Python prints it
        var renamed = await c.PostFormAsync($"/inventory/customers/edit/{cust.Id}", [.. CustomerForm(name + " Renamed"), new("is_active", "on")],
                                            $"/inventory/customers/edit/{cust.Id}");
        Assert.Contains("Customer updated", await FlashAfter(c, renamed));
        await using (var db2 = Scenario.Db())
            Assert.Equal(name + " Renamed", (await db2.Set<ChartOfAccount>().AsNoTracking().SingleAsync(a => a.Id == acct.Id)).Name);

        int inv = await Scenario.InvoiceAsync(db, cust.Id, $"DEL-GUARD-{cust.Id}", 10, DateTime.UtcNow, null);
        var refused = await c.PostFormAsync($"/inventory/customers/delete/{cust.Id}", [], "/inventory/customers/");
        Assert.Contains("Cannot delete a customer with sales history or ledger entries", await FlashAfter(c, refused));
        await db.Set<InvInvoice>().Where(i => i.Id == inv).ExecuteDeleteAsync();
        var deleted = await c.PostFormAsync($"/inventory/customers/delete/{cust.Id}", [], "/inventory/customers/");
        Assert.Contains("Customer deleted", await FlashAfter(c, deleted));
        Assert.False(await db.Set<InvCustomer>().AnyAsync(x => x.Id == cust.Id));
    }

    /// <summary>A saved customer and its ledger account as comparable values (the account's parent as its code).</summary>
    private static async Task<object> ShapeAsync(string name)
    {
        await using var db = Scenario.Db();
        var x = await db.Set<InvCustomer>().AsNoTracking().SingleAsync(k => k.Name == name);
        var a = await db.Set<ChartOfAccount>().AsNoTracking().SingleAsync(k => k.Code == TrackingRules.EntityCode("customer", x.Id));
        string? parent = await db.Set<ChartOfAccount>().AsNoTracking().Where(p => p.Id == a.ParentId).Select(p => p.Code).SingleOrDefaultAsync();
        return new
        {
            x.ContactPerson, x.Email, x.Phone, x.Mobile, x.City, x.TaxId, x.PaymentTerms, Credit = x.CreditLimit?.ToString("0.00"), x.Website,
            x.Address, x.Notes, x.IsActive, Account = new { a.Type, a.Level, Parent = parent, a.IsActive, a.IsFixed }, NameIsLedger = a.Name == x.Name,
        };
    }

    [ReferenceFact]
    public async Task The_customer_form_saves_what_the_python_form_saved()
    {
        string tag = Scenario.NewTag();
        await using var host = await HostAsync();
        var c = host.ClientFor(Scenario.OwnerB, Scenario.CompanyB);
        var r = await c.PostFormAsync("/inventory/customers/create", CustomerForm($"Parity NET {tag}", "1250.5"), "/inventory/customers/create");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Golden.Match("CustomerForm", await ShapeAsync($"Parity NET {tag}"));
    }

    private static byte[] Sheet(params string[][] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Customers");
        string[] head = ["Name", "Contact Person", "Email", "Phone", "Mobile", "City", "Tax ID", "Payment Terms", "Credit Limit", "Website", "Address", "Notes"];
        for (int i = 0; i < head.Length; i++) ws.Cell(1, i + 1).Value = head[i];
        for (int r = 0; r < rows.Length; r++)
            for (int i = 0; i < rows[r].Length; i++)
                if (double.TryParse(rows[r][i], out double n) && i is 3 or 8) ws.Cell(r + 2, i + 1).Value = n;
                else ws.Cell(r + 2, i + 1).Value = rows[r][i];
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    [ReferenceFact]
    public async Task An_import_flags_problem_rows_on_the_review_screen_and_the_batch_saves_the_good_ones()
    {
        string tag = Scenario.NewTag();
        await using var host = await HostAsync();
        var c = host.ClientFor(Scenario.OwnerB, Scenario.CompanyB);
        var template = await c.GetAsync("/inventory/customers/template");
        Assert.Equal("customer_import_template.xlsx", template.Content.Headers.ContentDisposition!.FileName?.Trim('"'));
        using (var wb = new XLWorkbook(new MemoryStream(await template.Content.ReadAsByteArrayAsync())))
            Assert.Equal("Credit Limit", wb.Worksheet("Customers").Cell(1, 9).GetString());

        // Sixty rows: well past what a 4 KB cookie could have carried to the review screen.
        var rows = Enumerable.Range(1, 60).Select(i => new[] { $"Imp {tag} {i}", "", $"imp{i}@x.com", "3001234567", "", "Lahore", "", "Net 30", "1000" }).ToList();
        rows.Add([$"Imp {tag} 1", "", "", "", "", "", "", "", ""]);                   // repeats row 2
        rows.Add([$"Imp {tag} bad", "", "not-an-email", "", "", "", "", "", "abc"]);
        string token = await c.TokenFromAsync("/inventory/customers/");
        var form = new MultipartFormDataContent { { new ByteArrayContent(Sheet([.. rows])), "file", "customers.xlsx" }, { new StringContent(token), "__RequestVerificationToken" } };
        var up = await c.PostAsync("/inventory/customers/upload-excel", form);
        string review = WebUtility.HtmlDecode(await up.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);
        Assert.Contains("2 row(s) need attention", review);
        Assert.Contains($"Row 62: 'Imp {tag} 1' appears more than once in this sheet.", review);
        Assert.Contains("Email 'not-an-email' does not look like an email address. Credit limit must be a number (got 'abc').", review);
        Assert.Contains("value=\"3001234567\"", review);   // a phone typed as a number keeps no ".0"
        Assert.Contains("value=\"1000\"", review);

        string data = JsonSerializer.Serialize(new object[]
        {
            new { name = $"Batch {tag} A", email = "a@x.com", credit_limit = "250" },
            new { name = $"Batch {tag} B", email = "bad", credit_limit = 5 },
            new { name = $"Batch {tag} C", _delete = true },
            new { name = "", notes = "" },
        });
        var saved = await c.PostFormAsync("/inventory/customers/batch", [new("data", data)], "/inventory/customers/batch");
        string after = WebUtility.HtmlDecode(await saved.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Contains("1 customer(s) saved. The rows below were not", after);
        Assert.Contains("does not look like an email address", after);
        await using var db = Scenario.Db();
        var a = await db.Set<InvCustomer>().AsNoTracking().SingleAsync(x => x.Name == $"Batch {tag} A");
        Assert.Equal(250m, a.CreditLimit);
        Assert.True(await db.Set<ChartOfAccount>().AnyAsync(x => x.Code == TrackingRules.EntityCode("customer", a.Id)));
        Assert.False(await db.Set<InvCustomer>().AnyAsync(x => x.Name == $"Batch {tag} B" || x.Name == $"Batch {tag} C"));

        var junk = await c.PostFormAsync("/inventory/customers/batch", [new("data", "{not json")], "/inventory/customers/batch");
        Assert.Contains("The rows sent were not readable", await FlashAfter(c, junk));
        var notXlsx = new MultipartFormDataContent { { new ByteArrayContent("hello"u8.ToArray()), "file", "x.xlsx" }, { new StringContent(token), "__RequestVerificationToken" } };
        Assert.Contains("could not be read as an Excel workbook", await FlashAfter(c, await c.PostAsync("/inventory/customers/upload-excel", notXlsx)));
    }

    [ReferenceFact]
    public async Task Suppliers_import_form_list_and_delete_guard()
    {
        string tag = Scenario.NewTag();
        await using var host = await HostAsync();
        var c = host.ClientFor(Scenario.OwnerB, Scenario.CompanyB);
        var r = await c.PostFormAsync("/inventory/suppliers/create",
            [new("name", $"Supplier {tag}"), new("email", ""), new("payment_terms", "Net 45"), new("credit_limit", "-9")], "/inventory/suppliers/create");
        Assert.Contains($"Supplier created — ledger account 'Supplier {tag}' added under Trade Creditors", await FlashAfter(c, r));
        string list = WebUtility.HtmlDecode(await (await c.GetAsync($"/inventory/suppliers/?q={tag}")).Content.ReadAsStringAsync());
        Assert.Contains($"Supplier {tag}", list);
        Assert.Contains("<th>Contact</th>", list);
        Assert.DoesNotContain("Credit Limit", list);
        await using var db = Scenario.Db();
        var sup = await db.Set<InvSupplier>().AsNoTracking().SingleAsync(x => x.Name == $"Supplier {tag}");
        Assert.True(await db.Set<ChartOfAccount>().AnyAsync(x => x.Code == TrackingRules.EntityCode("supplier", sup.Id)));
        var form = await (await c.GetAsync($"/inventory/suppliers/edit/{sup.Id}")).Content.ReadAsStringAsync();
        Assert.Contains("Company Name *", form);
        Assert.DoesNotContain("credit_limit", form);
        var del = await c.PostFormAsync($"/inventory/suppliers/delete/{sup.Id}", [], "/inventory/suppliers/");
        Assert.Contains("Supplier deleted", await FlashAfter(c, del));
    }

    [ReferenceFact]
    public async Task Without_customer_rights_the_list_and_the_forms_refuse()
    {
        await using var host = await HostAsync();
        var c = host.ClientFor(Reference.UserId(Reference.Email("restricted")), Scenario.CompanyB);
        Assert.NotEqual(HttpStatusCode.OK, (await c.GetAsync("/inventory/customers/")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await c.GetAsync("/inventory/suppliers/create")).StatusCode);
    }
}
