using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.Inventory.Services;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Inventory.Tests;

/// <summary>Shared set-up of the master-data page tests (products, units, categories): company B, unique names per run.</summary>
internal static class MasterTestKit
{
    public static int OwnerB => Reference.UserId(Reference.Email("owner_b"));
    public static int CompanyB => Reference.Company("b");

    /// <summary>The seeded super admin (admin@gmail.com), member of company 1 with the demo data.</summary>
    public static int AdminId()
    {
        using var db = Reference.NewDb();
        return db.Users.Single(u => u.Email == "admin@gmail.com").Id;
    }

    public static string Tag() => Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    public static AccountixDbContext Db(int? companyId = null)
    {
        var company = new CompanyContext();
        company.Set(companyId ?? CompanyB);
        return Reference.NewDb(company);
    }

    public static Task<ModuleHost> HostAsync() =>
        ModuleHost.StartAsync(typeof(InventoryModule).Assembly, typeof(Accountix.Accounting.AccountingModule).Assembly);

    /// <summary>A unit with this abbreviation in company B (made directly when missing).</summary>
    public static async Task EnsureUnitAsync(string name, string abbreviation)
    {
        await using var db = Db();
        if (await db.Set<InvUnit>().AnyAsync(u => u.Abbreviation == abbreviation || u.Name == name))
            return;
        db.Add(new InvUnit { Name = name, Abbreviation = abbreviation, IsActive = true, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    public static async Task<string> BodyAsync(HttpResponseMessage r) => await r.Content.ReadAsStringAsync();

    /// <summary>The flash messages a redirect leaves, read from the page it leads to.</summary>
    public static async Task<string> FollowAsync(PageClient c, HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        return await (await c.GetAsync(r.Headers.Location!.OriginalString)).Content.ReadAsStringAsync();
    }

    public static string Decode(string html) => WebUtility.HtmlDecode(html);

    public static List<KeyValuePair<string, string>> ProductForm(string sku, string name, string unit = "pcs",
                                                                string unitPrice = "100", string costPrice = "70",
                                                                string stock = "0", string reorder = "5",
                                                                string category = "", string openingDate = "",
                                                                string hs = "8541.4000", string weight = "1.5",
                                                                string description = "From the test") =>
    [
        new("sku", sku), new("name", name), new("description", description), new("category_id", category),
        new("unit_price", unitPrice), new("cost_price", costPrice), new("reorder_level", reorder),
        new("current_stock", stock), new("unit", unit), new("hs_code", hs), new("weight", weight),
        new("opening_date", openingDate),
    ];

    public static async Task<InvProduct?> ProductAsync(string sku)
    {
        await using var db = Db();
        return await db.Set<InvProduct>().AsNoTracking().FirstOrDefaultAsync(p => p.Sku == sku);
    }

    public static int? IdFromList(string html, string sku)
    {
        var m = Regex.Match(html, $"<strong>{Regex.Escape(sku)}</strong>[\\s\\S]*?/inventory/products/edit/(\\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    /// <summary>True while this copy still carries the stub costing engine (writes that move stock cannot run).</summary>
    public static bool EngineIsStub =>
        typeof(StockCosting).GetMethod("Todo", BindingFlags.NonPublic | BindingFlags.Static) is not null;
}

/// <summary>A page test that moves stock: needs the reference database and the real costing engine.</summary>
public sealed class EngineFactAttribute : FactAttribute
{
    public EngineFactAttribute()
    {
        if (!Reference.Available)
            Skip = "Reference database not available";
        else if (MasterTestKit.EngineIsStub)
            Skip = "The costing engine (StockCosting) is still a stub in this build";
    }
}
