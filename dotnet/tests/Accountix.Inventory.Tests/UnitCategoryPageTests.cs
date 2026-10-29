using System.Net;
using System.Text.Json;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using static Accountix.Inventory.Tests.MasterTestKit;

namespace Accountix.Inventory.Tests;

/// <summary>Units and categories through the .NET pages (inventory_app/routes/units.py, categories.py), company B.</summary>
public class UnitCategoryPageTests
{
    private static List<KeyValuePair<string, string>> UnitForm(string name, string abbr, string expl = "", bool active = true)
    {
        var f = new List<KeyValuePair<string, string>> { new("name", name), new("abbreviation", abbr), new("explanation", expl) };
        if (active) f.Add(new("is_active", "on"));
        return f;
    }

    private static async Task<InvUnit?> UnitAsync(string abbr)
    {
        await using var db = Db();
        return await db.Set<InvUnit>().AsNoTracking().FirstOrDefaultAsync(u => u.Abbreviation == abbr);
    }

    [ReferenceFact]
    public async Task Unit_pages_render_and_units_are_made_and_refused_as_in_python()
    {
        await using var host = await HostAsync();
        var c = host.ClientFor(OwnerB, CompanyB);
        string tag = Tag();
        string list = await BodyAsync(await c.GetAsync("/inventory/units/"));
        Assert.Contains("+ New Unit", list);
        string create = await BodyAsync(await c.GetAsync("/inventory/units/create"));
        Assert.Contains("id=\"fieldName\"", create);
        Assert.Contains("data-exclude=\"\"", create);
        Assert.Contains("id=\"beBody\"", await BodyAsync(await c.GetAsync("/inventory/units/batch")));

        var r = await c.PostFormAsync("/inventory/units/create", UnitForm($"  Kilo   {tag} ", $"k{tag}", ""), "/inventory/units/create");
        Assert.Contains($"Unit 'Kilo {tag}' created.", Decode(await FollowAsync(c, r)));
        var u = (await UnitAsync($"k{tag}"))!;
        Assert.Equal($"Kilo {tag}", u.Name);
        Assert.Null(u.Explanation);
        Assert.True(u.IsActive);

        r = await c.PostFormAsync("/inventory/units/create", UnitForm($"kilo {tag.ToLowerInvariant()}", $"K{tag}"), "/inventory/units/create");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        string refused = Decode(await BodyAsync(r));
        Assert.Contains($"Unit 'kilo {tag.ToLowerInvariant()}' already exists. Abbreviation 'K{tag}' already exists.", refused);
        Assert.Contains($"value=\"kilo {tag.ToLowerInvariant()}\"", refused);

        r = await c.PostFormAsync("/inventory/units/create", UnitForm(" ", ""), "/inventory/units/create");
        Assert.Contains("Name is required. Abbreviation is required.", Decode(await BodyAsync(r)));
        r = await c.PostFormAsync("/inventory/units/create", UnitForm($"Long {tag}", new string('a', 21)), "/inventory/units/create");
        Assert.Contains("Abbreviation is longer than 20 characters.", Decode(await BodyAsync(r)));

        string edit = await BodyAsync(await c.GetAsync($"/inventory/units/edit/{u.Id}"));
        Assert.Contains($"data-exclude=\"{u.Id}\"", edit);
        Assert.Contains("name=\"is_active\" checked", edit);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/inventory/units/edit/999999")).StatusCode);
    }

    [ReferenceFact]
    public async Task Renaming_an_abbreviation_moves_its_products_and_a_unit_in_use_cannot_be_deleted()
    {
        await using var host = await HostAsync();
        var c = host.ClientFor(OwnerB, CompanyB);
        string tag = Tag();
        await c.PostFormAsync("/inventory/units/create", UnitForm($"Metre {tag}", $"m{tag}"), "/inventory/units/create");
        var u = (await UnitAsync($"m{tag}"))!;
        var r = await c.PostFormAsync("/inventory/products/create", ProductForm($"UM-{tag}", $"Unit moved {tag}", unit: $"Metre {tag}"),
                                      "/inventory/products/create");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal($"m{tag}", (await ProductAsync($"UM-{tag}"))!.Unit);     // a unit name resolves to its abbreviation

        r = await c.PostFormAsync($"/inventory/units/delete/{u.Id}", [], "/inventory/units/");
        Assert.Contains($"1 product(s) use 'm{tag}' — move them to another unit first, or switch this unit off instead of deleting it.",
                        Decode(await FollowAsync(c, r)));

        r = await c.PostFormAsync($"/inventory/units/edit/{u.Id}", UnitForm($"Metre {tag}", $"M{tag}", "Length", active: false),
                                  $"/inventory/units/edit/{u.Id}");
        Assert.Contains($"Unit 'Metre {tag}' updated. 1 product(s) moved to 'M{tag}'.", Decode(await FollowAsync(c, r)));
        Assert.Equal($"M{tag}", (await ProductAsync($"UM-{tag}"))!.Unit);
        var moved = (await UnitAsync($"M{tag}"))!;
        Assert.False(moved.IsActive);
        Assert.Equal("Length", moved.Explanation);

        // The product's own unit stays offered on its form though the unit is now off.
        var p = (await ProductAsync($"UM-{tag}"))!;
        Assert.Contains($"<option value=\"M{tag}\" selected=\"selected\">Metre {tag} (M{tag})</option>",
                        await BodyAsync(await c.GetAsync($"/inventory/products/edit/{p.Id}")));

        await c.PostFormAsync($"/inventory/products/delete/{p.Id}", [], "/inventory/products/");
        r = await c.PostFormAsync($"/inventory/units/delete/{u.Id}", [], "/inventory/units/");
        Assert.Contains($"Unit 'Metre {tag}' deleted.", Decode(await FollowAsync(c, r)));
        Assert.Null(await UnitAsync($"M{tag}"));
    }

    [ReferenceFact]
    public async Task Quick_create_and_the_duplicate_check_answer_json()
    {
        await using var host = await HostAsync();
        var c = host.ClientFor(OwnerB, CompanyB);
        string tag = Tag();
        var r = await c.PostFormAsync("/inventory/units/quick-create", UnitForm($"Quick {tag}", $"q{tag}"), "/inventory/products/create");
        using (var doc = JsonDocument.Parse(await BodyAsync(r)))
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal($"q{tag}", doc.RootElement.GetProperty("abbreviation").GetString());
            Assert.Equal($"Quick {tag}", doc.RootElement.GetProperty("name").GetString());
            Assert.True(doc.RootElement.GetProperty("id").GetInt32() > 0);
        }
        r = await c.PostFormAsync("/inventory/units/quick-create", UnitForm($"QUICK {tag}", ""), "/inventory/products/create");
        using (var doc = JsonDocument.Parse(await BodyAsync(r)))
        {
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal($"Unit 'QUICK {tag}' already exists. Abbreviation is required.", doc.RootElement.GetProperty("error").GetString());
        }

        async Task<bool> Exists(string q)
        {
            using var doc = JsonDocument.Parse(await BodyAsync(await c.GetAsync("/inventory/units/check?" + q)));
            return doc.RootElement.GetProperty("exists").GetBoolean();
        }
        var u = (await UnitAsync($"q{tag}"))!;
        Assert.True(await Exists($"field=abbreviation&value=Q{tag}"));
        Assert.True(await Exists($"field=name&value=quick%20{tag}"));
        Assert.False(await Exists($"field=name&value=Quick%20{tag}&exclude={u.Id}"));
        Assert.False(await Exists($"field=explanation&value=x"));
    }

    [ReferenceFact]
    public async Task Units_import_from_the_template_through_review_and_batch()
    {
        await using var host = await HostAsync();
        var c = host.ClientFor(OwnerB, CompanyB);
        string tag = Tag();
        var t = await c.GetAsync("/inventory/units/template");
        Assert.Contains("unit_import_template.xlsx", t.Content.Headers.ContentDisposition!.ToString());
        using (var wb = new XLWorkbook(new MemoryStream(await t.Content.ReadAsByteArrayAsync())))
        {
            var ws = wb.Worksheet(1);
            Assert.Equal("Units", ws.Name);
            Assert.Equal(new[] { "Name", "Abbreviation", "Explanation" }, Enumerable.Range(1, 3).Select(i => ws.Cell(1, i).GetString()));
            Assert.Equal("pcs", ws.Cell(4, 2).GetString());
            Assert.Equal(24, ws.Column(1).Width);
        }

        byte[] sheet;
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Units");
            ws.Cell(1, 1).Value = "Name"; ws.Cell(1, 2).Value = "Abbreviation"; ws.Cell(1, 3).Value = "Explanation";
            ws.Cell(2, 1).Value = $"Gram {tag}"; ws.Cell(2, 2).Value = $"g{tag}"; ws.Cell(2, 3).Value = 12;
            ws.Cell(3, 1).Value = $"gram {tag}"; ws.Cell(3, 2).Value = $"gg{tag}";
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            sheet = ms.ToArray();
        }
        string token = await c.TokenFromAsync("/inventory/units/");
        var r = await c.PostAsync("/inventory/units/upload-excel", new MultipartFormDataContent
        {
            { new ByteArrayContent(sheet), "file", "units.xlsx" }, { new StringContent(token), "__RequestVerificationToken" },
        });
        string html = Decode(await BodyAsync(r));
        Assert.Contains("1 row(s) need attention", html);
        Assert.Contains($"Row 3: 'gram {tag}' appears more than once in this sheet.", html);
        Assert.Contains("value=\"12\"", html);              // a whole number reads without ".0"
        Assert.Null(await UnitAsync($"g{tag}"));

        string data = JsonSerializer.Serialize(new object[]
        {
            new { name = $"Gram {tag}", abbreviation = $"g{tag}", explanation = "12" },
            new { name = $"gram {tag}", abbreviation = $"gg{tag}", explanation = "" },
        });
        r = await c.PostFormAsync("/inventory/units/batch", [new("data", data)], "/inventory/units/batch");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        html = Decode(await BodyAsync(r));
        Assert.Contains("1 unit(s) saved.", html);
        Assert.Contains($"Unit 'gram {tag}' already exists.", html);
        Assert.Equal("12", (await UnitAsync($"g{tag}"))!.Explanation);

        r = await c.PostFormAsync("/inventory/units/batch",
                                  [new("data", JsonSerializer.Serialize(new[] { new { name = $"Ton {tag}", abbreviation = $"t{tag}" } }))],
                                  "/inventory/units/batch");
        Assert.Contains("1 unit(s) created.", await FollowAsync(c, r));
    }

    [ReferenceFact]
    public async Task Categories_are_listed_made_edited_and_deleted_as_in_python()
    {
        await using var host = await HostAsync();
        var c = host.ClientFor(OwnerB, CompanyB);
        string tag = Tag();
        var r = await c.PostFormAsync("/inventory/categories/create", [new("name", $"Panels {tag}"), new("description", ""), new("parent_id", "")],
                                      "/inventory/categories/create");
        Assert.Contains("Category created", await FollowAsync(c, r));
        await using var db = Db();
        var parent = await db.Set<InvCategory>().AsNoTracking().SingleAsync(x => x.Name == $"Panels {tag}");
        r = await c.PostFormAsync("/inventory/categories/create",
                                  [new("name", $"Mono {tag}"), new("description", "Mono panels"), new("parent_id", parent.Id.ToString())],
                                  "/inventory/categories/create");
        var child = await db.Set<InvCategory>().AsNoTracking().SingleAsync(x => x.Name == $"Mono {tag}");
        Assert.Equal(parent.Id, child.ParentId);
        Assert.True(child.IsActive);

        r = await c.PostFormAsync("/inventory/products/create", ProductForm($"CAT-{tag}", $"In category {tag}", category: child.Id.ToString()),
                                  "/inventory/products/create");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);

        string list = await BodyAsync(await c.GetAsync("/inventory/categories/"));
        Assert.Matches($@"<td>Mono {tag}</td>\s*<td>Mono panels</td>\s*<td>Panels {tag}</td>[\s\S]*?<td>1</td>", list);

        string form = await BodyAsync(await c.GetAsync($"/inventory/categories/edit/{child.Id}"));
        Assert.Contains($"<option value=\"{parent.Id}\" selected=\"selected\">Panels {tag}</option>", form);
        Assert.DoesNotContain($"<option value=\"{child.Id}\"", form);
        Assert.Contains("<textarea name=\"description\" class=\"form-control focus-ring\" rows=\"3\">Mono panels</textarea>", form);

        r = await c.PostFormAsync($"/inventory/categories/delete/{child.Id}", [], "/inventory/categories/");
        Assert.Contains("Cannot delete category with products", await FollowAsync(c, r));

        r = await c.PostFormAsync($"/inventory/categories/edit/{child.Id}",
                                  [new("name", $"Mono {tag} b"), new("description", "x"), new("parent_id", "")], $"/inventory/categories/edit/{child.Id}");
        Assert.Contains("Category updated", await FollowAsync(c, r));
        var edited = await db.Set<InvCategory>().AsNoTracking().SingleAsync(x => x.Id == child.Id);
        Assert.Equal($"Mono {tag} b", edited.Name);
        Assert.Null(edited.ParentId);
        Assert.False(edited.IsActive);

        // The parent goes; a child pointing at it is let go (SQLAlchemy nulls it), not refused.
        await c.PostFormAsync($"/inventory/categories/edit/{child.Id}",
                              [new("name", $"Mono {tag} b"), new("description", "x"), new("parent_id", parent.Id.ToString())],
                              $"/inventory/categories/edit/{child.Id}");
        r = await c.PostFormAsync($"/inventory/categories/delete/{parent.Id}", [], "/inventory/categories/");
        Assert.Contains("Category deleted", await FollowAsync(c, r));
        Assert.Null((await db.Set<InvCategory>().AsNoTracking().SingleAsync(x => x.Id == child.Id)).ParentId);

        r = await c.PostFormAsync("/inventory/categories/create", [new("description", "no name")], "/inventory/categories/create");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/inventory/categories/edit/999999")).StatusCode);
    }

    [ReferenceFact]
    public async Task Unit_and_category_lists_run_a_fixed_number_of_queries()
    {
        await using var host = await HostAsync();
        var demo = host.ClientFor(AdminId(), 1);
        foreach (string url in new[] { "/inventory/units/", "/inventory/categories/", "/inventory/categories/create", "/inventory/units/create" })
            (await host.MeasureAsync(demo, url, runs: 1)).Within(maxMilliseconds: 5000, maxQueries: 40);
        // Grouped product counts: the query count does not depend on how many categories there are.
        host.Queries.Reset();
        await demo.GetAsync("/inventory/categories/");
        Assert.DoesNotContain(host.Queries.Repeated(), q => q.StartsWith("8×") || q.StartsWith("9×"));
    }

    [ReferenceFact]
    public async Task Section_rights_govern_the_master_data_pages()
    {
        int restricted = Reference.UserId(Reference.Email("restricted"));
        await using var db = Db();
        var membership = await db.Set<CompanyMembership>().SingleAsync(m => m.UserId == restricted && m.CompanyId == CompanyB);
        string? flags = membership.ModuleFlags;
        var rows = await db.Set<UserPermission>().Where(p => p.UserId == restricted && (p.Resource == "products" || p.Resource == "categories"))
            .ToListAsync();
        var saved = rows.Select(p => (p.Resource, p.CanView, p.CanCreate, p.CanEdit, p.CanApprove, p.CanDelete)).ToList();
        try
        {
            var current = new Dictionary<string, bool>();
            if (!string.IsNullOrEmpty(flags))
                foreach (var kv in JsonDocument.Parse(flags).RootElement.EnumerateObject())
                    current[kv.Name] = kv.Value.ValueKind == JsonValueKind.True;
            current["inventory"] = true;
            membership.SetModuleFlags(current);
            foreach (string res in new[] { "products", "categories" })
            {
                var row = rows.FirstOrDefault(p => p.Resource == res);
                if (row is null)
                {
                    row = new UserPermission { UserId = restricted, Resource = res };
                    db.Add(row);
                    rows.Add(row);
                }
                row.CanView = res == "products";
                row.CanCreate = row.CanEdit = row.CanApprove = row.CanDelete = false;
            }
            await db.SaveChangesAsync();

            await using var host = await HostAsync();
            var c = host.ClientFor(restricted, CompanyB);
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/inventory/products/")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/inventory/units/")).StatusCode);   // units are part of the product master
            var create = await c.GetAsync("/inventory/products/create");
            Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
            // Refused to the module landing (rights_guard). The flash is not asserted: a [Right] refusal from an
            // authorization filter is answered before TempData is saved (Web.Shared Refusal.Right), so it never shows.
            Assert.Equal("/inventory/", create.Headers.Location!.OriginalString);
            Assert.Equal(HttpStatusCode.Redirect, (await c.GetAsync("/inventory/categories/")).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await c.GetAsync("/inventory/units/template")).StatusCode);
            var json = await c.PostFormAsync("/inventory/units/quick-create", UnitForm("x", "y"), "/inventory/products/");
            Assert.Equal(HttpStatusCode.Redirect, json.StatusCode);
            // The duplicate check is open to any member of the module.
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/inventory/products/check?field=sku&value=x")).StatusCode);
        }
        finally
        {
            membership.ModuleFlags = flags;
            foreach (var row in rows)
            {
                var old = saved.FirstOrDefault(s => s.Resource == row.Resource);
                if (old.Resource is null) db.Remove(row);
                else (row.CanView, row.CanCreate, row.CanEdit, row.CanApprove, row.CanDelete) =
                    (old.CanView, old.CanCreate, old.CanEdit, old.CanApprove, old.CanDelete);
            }
            await db.SaveChangesAsync();
        }
    }
}
