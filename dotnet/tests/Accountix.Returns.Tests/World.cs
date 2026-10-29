using System.Net;
using System.Text.Json;
using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.Core.Shared;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Accountix.Returns.Tests;

/// <summary>
/// Documents to return against, made through the .NET app on this module's copy of the
/// reference database (company B, owner_b): fresh products, a customer and a supplier
/// (inserted as the Python models' defaults write them), then purchase and sales invoices
/// approved through Invoicing's own routes, so their stock rows and journals are the real
/// engine's. Golden/capture/_world.py builds the same documents through the Python routes.
/// </summary>
public sealed class World
{
    public static int Owner => Reference.UserId(Reference.Email("owner_b"));
    public static int CompanyB => Reference.Company("b");
    public const string InvoiceDate = "2026-10-20";
    public const string ReturnDate = "2026-10-22";

    public required string Tag { get; init; }
    public required int[] Products { get; init; }
    public required int Customer { get; init; }
    public required int Supplier { get; init; }
    public required string CustomerName { get; init; }
    public required string SupplierName { get; init; }
    public required int[] PurchaseInvoices { get; init; }
    public required int[] SalesInvoices { get; init; }

    public static AccountixDbContext Db()
    {
        var company = new CompanyContext();
        company.Set(CompanyB);
        return Reference.NewDb(company);
    }

    /// <summary>One purchase invoice line: product index, qty, price, freight, line tax % and WHT %.</summary>
    public sealed record PiLine(int Product, double Qty, double Price, double Freight = 0, double TaxPct = 0, double WhtPct = 0);

    /// <summary>One sales invoice line: product index, qty, price, line tax %.</summary>
    public sealed record SiLine(int Product, double Qty, double Price, double TaxPct = 0, double DiscountPct = 0);

    /// <summary>The purchase invoice payload (line tax mode), as the Python test built it.</summary>
    public static Dictionary<string, object?> PiPayload(int supplier, int[] products, IReadOnlyList<PiLine> lines)
    {
        double total = lines.Sum(l => l.Qty * l.Price + l.Freight);
        return new()
        {
            ["supplier_id"] = supplier, ["invoice_date"] = InvoiceDate, ["discount_mode"] = "general", ["expenses_mode"] = "general",
            ["tax_mode"] = "line", ["global_discount_pct"] = 0, ["global_discount_value"] = 0, ["global_commission"] = 0, ["global_freight"] = 0,
            ["global_loading"] = 0, ["global_sales_tax_pct"] = 0, ["global_withholding_tax_pct"] = 0,
            ["subtotal"] = total, ["total_discount"] = 0, ["net_payable"] = total, ["total_amount"] = total,
            ["items"] = lines.Select(l => new Dictionary<string, object?>
            {
                ["product_id"] = products[l.Product], ["description"] = $"Part {l.Product}", ["unit"] = "pcs", ["quantity"] = l.Qty,
                ["unit_price"] = l.Price, ["discount_pct"] = 0, ["freight"] = l.Freight, ["sales_tax_pct"] = l.TaxPct,
                ["withholding_tax_pct"] = l.WhtPct, ["total_before_discount"] = l.Qty * l.Price, ["total_after_discount"] = l.Qty * l.Price,
            }).ToList(),
            ["action"] = "approve",
        };
    }

    /// <summary>The sales invoice payload (line discount and tax), as the Python test built it.</summary>
    public static Dictionary<string, object?> SiPayload(int customer, int[] products, IReadOnlyList<SiLine> lines)
    {
        double total = lines.Sum(l => l.Qty * l.Price);
        return new()
        {
            ["customer_id"] = customer, ["invoice_date"] = InvoiceDate, ["discount_mode"] = "line", ["charges_mode"] = "general",
            ["tax_mode"] = "line", ["global_discount_pct"] = 0, ["global_discount_value"] = 0, ["global_delivery"] = 0, ["global_installation"] = 0,
            ["global_sales_tax_pct"] = 0, ["further_tax_pct"] = 0, ["apply_further_tax"] = false, ["withholding_tax_pct"] = 0,
            ["subtotal"] = total, ["total_discount"] = 0, ["total_charges"] = 0, ["total_tax"] = 0, ["total_amount"] = total,
            ["items"] = lines.Select(l => new Dictionary<string, object?>
            {
                ["product_id"] = products[l.Product], ["description"] = $"Part {l.Product}", ["unit"] = "pcs", ["quantity"] = l.Qty,
                ["unit_price"] = l.Price, ["discount_pct"] = l.DiscountPct, ["delivery"] = 0, ["installation"] = 0, ["sales_tax_pct"] = l.TaxPct,
                ["total_before_discount"] = l.Qty * l.Price, ["total_after_discount"] = l.Qty * l.Price * (1 - l.DiscountPct / 100),
            }).ToList(),
            ["action"] = "approve",
        };
    }

    /// <summary>POST JSON through the host; the answer must be 200 with ok, and carries the new id.</summary>
    public static async Task<int> SaveAsync(PageClient c, string url, string tokenPage, object payload)
    {
        var r = await c.PostJsonAsync(url, payload, tokenPage);
        string body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url}: {(int)r.StatusCode} {body[..Math.Min(body.Length, 800)]}");
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), body);
        return doc.RootElement.GetProperty("id").GetInt32();
    }

    /// <summary>
    /// Makes <paramref name="products"/> products, then each purchase invoice (approved) and each
    /// sales invoice (approved), in that order, all on <see cref="InvoiceDate"/>, through the host's routes.
    /// </summary>
    public static async Task<World> CreateAsync(ModuleHost host, int products, IReadOnlyList<IReadOnlyList<PiLine>> purchases,
                                                IReadOnlyList<IReadOnlyList<SiLine>> sales)
    {
        string tag = Guid.NewGuid().ToString("N")[..8];
        var now = DateTime.UtcNow;
        int[] ids;
        InvCustomer cu;
        InvSupplier su;
        await using (var db = Db())
        {
            // InvProduct(name, sku, unit="pcs", cost_price=0, unit_price=150) with the model's other defaults.
            var prods = Enumerable.Range(0, products).Select(i => new InvProduct
            {
                Name = $"Ret {tag} {i}", Sku = $"RET-{tag}-{i}", Unit = "pcs", CostPrice = 0, UnitPrice = 150, ReorderLevel = 0,
                CurrentStock = 0, HsCode = "", Weight = 0, IsActive = true, CreatedAt = now, UpdatedAt = now,
            }).ToList();
            cu = new InvCustomer { Name = $"Ret Customer {tag}", CreditLimit = 0, IsActive = true, CreatedAt = now };
            su = new InvSupplier { Name = $"Ret Supplier {tag}", IsActive = true, CreatedAt = now };
            db.AddRange(prods);
            db.AddRange(cu, su);
            await db.SaveChangesAsync();
            ids = prods.Select(p => p.Id).ToArray();
        }
        var c = host.ClientFor(Owner, CompanyB);
        var pis = new List<int>();
        foreach (var lines in purchases)
            pis.Add(await SaveAsync(c, "/inventory/purchase-invoice/save", "/inventory/purchase-invoice/", PiPayload(su.Id, ids, lines)));
        var sis = new List<int>();
        foreach (var lines in sales)
            sis.Add(await SaveAsync(c, "/inventory/invoices/save", "/inventory/invoices/", SiPayload(cu.Id, ids, lines)));
        return new World
        {
            Tag = tag, Products = ids, Customer = cu.Id, Supplier = su.Id, CustomerName = cu.Name, SupplierName = su.Name,
            PurchaseInvoices = [.. pis], SalesInvoices = [.. sis],
        };
    }

    /// <summary>The costing engine's own check (assert_invariant): the open layers carry exactly the ledger's running cost.</summary>
    public static async Task AssertStockTiesAsync(ModuleHost host, IEnumerable<int> products)
    {
        foreach (int p in products)
        {
            await using var scope = host.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<CompanyContext>().Set(CompanyB);
            var costing = scope.ServiceProvider.GetRequiredService<IStockCosting>();
            var (ok, layers, running) = await costing.AssertInvariantAsync(p);
            Assert.True(ok, $"product {p}: layers {layers} vs ledger {running}");
        }
    }
}
