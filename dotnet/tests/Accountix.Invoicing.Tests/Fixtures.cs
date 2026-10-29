using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.Core.Shared;
using Accountix.TestSupport;
using Accountix.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Accountix.Invoicing.Tests;

/// <summary>
/// Stock costing stand-in (only with INVOICING_FAKE_STOCK=1; the tests run on Inventory's real engine):
/// issues cost qty × the unit cost the test sets for the product (what Python's
/// costing gives for a product with one layer), receipts are recorded.
/// </summary>
public sealed class FakeStock : IStockCosting
{
    public static readonly ConcurrentDictionary<int, decimal> UnitCost = new();
    public static readonly ConcurrentQueue<(string Kind, int ProductId, string VoucherType, int VoucherId, decimal Qty, decimal? Unit, decimal? Total)> Calls = new();

    public Task LockProductsAsync(IEnumerable<int> productIds, CancellationToken ct = default) => Task.CompletedTask;

    public Task<decimal> RecordInAsync(int productId, string voucherType, int voucherId, string voucherNumber, decimal qty, decimal unitCost,
                                       string notes = "", int createdBy = 1, DateTime? txnDate = null, decimal? totalCost = null, CancellationToken ct = default)
    {
        Calls.Enqueue(("in", productId, voucherType, voucherId, qty, unitCost, totalCost));
        return Task.FromResult(totalCost ?? qty * unitCost);
    }

    public Task<decimal> RecordOutAsync(int productId, string voucherType, int voucherId, string voucherNumber, decimal qty, string notes = "",
                                        int createdBy = 1, decimal? unitCost = null, DateTime? txnDate = null, (string Type, int Id)? preferVoucher = null,
                                        CancellationToken ct = default) =>
        Task.FromResult(IssueAsync(productId, voucherType, voucherId, voucherNumber, qty, notes, createdBy, unitCost, txnDate, preferVoucher, ct).Result.TotalCost);

    public Task<(decimal UnitCost, decimal TotalCost)> IssueAsync(int productId, string voucherType, int voucherId, string voucherNumber, decimal qty,
                                                                 string notes = "", int createdBy = 1, decimal? unitCost = null, DateTime? txnDate = null,
                                                                 (string Type, int Id)? preferVoucher = null, CancellationToken ct = default)
    {
        decimal unit = unitCost ?? UnitCost.GetValueOrDefault(productId);
        Calls.Enqueue(("out", productId, voucherType, voucherId, qty, unit, qty * unit));
        return Task.FromResult((unit, Math.Round(qty * unit, 4, MidpointRounding.AwayFromZero)));
    }

    public Task ReverseVoucherStockAsync(string voucherType, int voucherId, int createdBy = 1, CancellationToken ct = default)
    {
        Calls.Enqueue(("reverse", 0, voucherType, voucherId, 0, null, null));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<int, decimal>> ReverseVoucherStockAsync(string voucherType, int voucherId, bool allowVariance, int createdBy = 1,
                                                                           CancellationToken ct = default)
    {
        Calls.Enqueue(("reverse", 0, voucherType, voucherId, 0, null, null));
        return Task.FromResult<IReadOnlyDictionary<int, decimal>>(new Dictionary<int, decimal>());
    }

    public Task<decimal> OnHandAsync(int productId, DateTime? at = null, CancellationToken ct = default) => Task.FromResult(0m);
    public Task<decimal> StockValueAsync(int productId, CancellationToken ct = default) => Task.FromResult(0m);
    public Task<decimal> CurrentUnitCostAsync(int productId, CancellationToken ct = default) => Task.FromResult(UnitCost.GetValueOrDefault(productId));
    public Task<decimal?> OriginalIssueCostAsync(string voucherType, int voucherId, int productId, CancellationToken ct = default) =>
        Task.FromResult<decimal?>(UnitCost.GetValueOrDefault(productId));
    public Task<StockLedger> ReceiveAsync(int productId, string voucherType, int voucherId, string voucherNumber, decimal qty, decimal unitCost,
                                          string notes = "", int createdBy = 1, DateTime? txnDate = null, decimal? totalCost = null, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<(decimal UnitCost, decimal TotalCost)> CostOfIssueAsync(int productId, decimal qty, CancellationToken ct = default) =>
        Task.FromResult((UnitCost.GetValueOrDefault(productId), qty * UnitCost.GetValueOrDefault(productId)));
    public Task<decimal> ValueAtAsync(int productId, DateTime at, CancellationToken ct = default) => Task.FromResult(0m);
    public Task<decimal?> OriginalReceiptCostAsync(string voucherType, int voucherId, int productId, CancellationToken ct = default) =>
        Task.FromResult<decimal?>(UnitCost.GetValueOrDefault(productId));
    public Task<IReadOnlyList<(string VoucherType, string VoucherNumber, decimal Qty)>> ConsumersOfVoucherAsync(string voucherType, int voucherId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(string, string, decimal)>>([]);
    public Task RevalueForMethodChangeAsync(string newMethod, int createdBy = 1, CancellationToken ct = default) => Task.CompletedTask;
    public Task<(bool Ok, decimal LayerValue, decimal RunningCost)> AssertInvariantAsync(int productId, CancellationToken ct = default) =>
        Task.FromResult((true, 0m, 0m));
}

/// <summary>Registers <see cref="FakeStock"/> (sorted after every Accountix.* module, so it wins).</summary>
public sealed class ZzFakeStockModule : IAccountixModule
{
    public string Key => "test-fake-stock";
    public void Register(IServiceCollection services, IConfiguration config) => services.AddScoped<IStockCosting, FakeStock>();
    public ModuleNav? Navigation => null;
}

/// <summary>Company B's people and fixtures, built through .NET only.</summary>
public static class Fx
{
    public static int OwnerB => Reference.UserId(Reference.Email("owner_b"));
    public static int CompanyB => Reference.Company("b");
    public static int Restricted => Reference.UserId(Reference.Email("restricted"));

    /// <summary>Invoicing + Accounting + Inventory's real costing engine (the fake only with INVOICING_FAKE_STOCK=1).</summary>
    public static Task<ModuleHost> HostAsync() =>
        Environment.GetEnvironmentVariable("INVOICING_FAKE_STOCK") == "1"
            ? ModuleHost.StartAsync(typeof(InvoicingModule).Assembly, typeof(Accountix.Accounting.AccountingModule).Assembly,
                                    typeof(ZzFakeStockModule).Assembly)
            : ModuleHost.StartAsync(typeof(InvoicingModule).Assembly, typeof(Accountix.Accounting.AccountingModule).Assembly,
                                    typeof(Accountix.Inventory.InventoryModule).Assembly);

    /// <summary>A plain purchase receipt payload: P1 and P2 at the given quantities and prices, on a date.</summary>
    public static Dictionary<string, object?> Receipt(Parties p, string date, double q1, double c1, double q2, double c2, string action = "approve") => new()
    {
        ["supplier_id"] = p.Supplier, ["invoice_date"] = date, ["action"] = action,
        ["discount_mode"] = "general", ["expenses_mode"] = "general", ["tax_mode"] = "general",
        ["global_discount_pct"] = 0, ["global_sales_tax_pct"] = 0, ["global_withholding_tax_pct"] = 0, ["apply_withholding_tax"] = false,
        ["subtotal"] = q1 * c1 + q2 * c2, ["total_discount"] = 0, ["notes"] = "Stock",
        ["items"] = new object[]
        {
            new Dictionary<string, object?> { ["product_id"] = p.P1, ["description"] = "Panel", ["quantity"] = q1, ["unit"] = "pcs", ["unit_price"] = c1, ["total_before_discount"] = q1 * c1, ["total_after_discount"] = q1 * c1 },
            new Dictionary<string, object?> { ["product_id"] = p.P2, ["description"] = "Inverter", ["quantity"] = q2, ["unit"] = "pcs", ["unit_price"] = c2, ["total_before_discount"] = q2 * c2, ["total_after_discount"] = q2 * c2 },
        },
        ["charges"] = Array.Empty<object>(),
    };

    /// <summary>Put stock on hand through a .NET purchase invoice (100 × 40 and 100 × 70 on 1 May 2026).</summary>
    public static async Task<int> StockUpAsync(PageClient c, Parties p, double q1 = 100, double q2 = 100)
    {
        var r = await c.PostJsonAsync("/inventory/purchase-invoice/save", Receipt(p, "2026-05-01", q1, 40, q2, 70), "/inventory/purchase-invoice/");
        string body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetInt32();
    }

    /// <summary>Stock rows of one product.</summary>
    public static async Task<int> StockRowsAsync(string voucherType, int voucherId)
    {
        await using var db = Db();
        return await db.Set<StockLedger>().CountAsync(s => s.VoucherType == voucherType && s.VoucherId == voucherId);
    }

    public static AccountixDbContext Db(int? company = null)
    {
        var c = new CompanyContext();
        c.Set(company ?? CompanyB);
        return Reference.NewDb(c);
    }

    public sealed record Parties(int Customer, int Supplier, int P1, int P2, int BillAccount, int ExpenseAccount, string Tag);

    /// <summary>
    /// Stand-in ids for the golden fixtures' "inputs": the payloads a test sends, built for these, must be
    /// the payloads the capture script sent to the Python app (built for the same stand-ins).
    /// </summary>
    public static readonly Parties Sentinel = new(-1, -2, -3, -4, -5, -6, "TAG");
    public const int SentinelLabel = -7;

    /// <summary>A customer, a supplier and two products of company B, named uniquely for this run.</summary>
    public static async Task<Parties> PartiesAsync()
    {
        string tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = Db();
        var now = DateTime.UtcNow;
        var cust = new InvCustomer { Name = $"Cust {tag}", Address = "1 Mall Road", City = "Lahore", IsActive = true, CreatedAt = now, TaxId = "NTN-1" };
        var sup = new InvSupplier { Name = $"Supp {tag}", Address = "Ind. Area", City = "Karachi", IsActive = true, CreatedAt = now };
        var p1 = new InvProduct { Sku = $"P1-{tag}", Name = $"Panel {tag}", UnitPrice = 2500, CostPrice = 40, CurrentStock = 0, Unit = "pcs", Weight = 12.5m, IsActive = true, CreatedAt = now, UpdatedAt = now, HsCode = "", ReorderLevel = 0 };
        var p2 = new InvProduct { Sku = $"P2-{tag}", Name = $"Inverter {tag}", UnitPrice = 9000, CostPrice = 70, CurrentStock = 0, Unit = "pcs", Weight = 4m, IsActive = true, CreatedAt = now, UpdatedAt = now, HsCode = "", ReorderLevel = 0 };
        db.AddRange(cust, sup, p1, p2);
        await db.SaveChangesAsync();
        int bill = db.Set<ChartOfAccount>().Single(a => a.Code == "4-01-01-01-0001").Id;
        int expense = db.Set<ChartOfAccount>().Single(a => a.Code == "5-04-01-01-0001").Id;
        FakeStock.UnitCost[p1.Id] = 40m;
        FakeStock.UnitCost[p2.Id] = 70m;
        return new Parties(cust.Id, sup.Id, p1.Id, p2.Id, bill, expense, tag);
    }

    /// <summary>
    /// The inventory settings the golden fixtures were captured under (company B's defaults), set again
    /// in case a test left them otherwise: weighted average, no negative stock, no per-line labels, no
    /// default accounts or label. Returns them for the fixture's "inputs".
    /// </summary>
    public static async Task<Dictionary<string, object?>> PinSettingsAsync()
    {
        await using var db = Db();
        var s = await new Accountix.Inventory.Services.InventorySettingsStore(db).GetAsync();
        s.ValuationMethod = "weighted_average";
        s.AllowNegativeStock = false;
        s.PerLineLabelingInvoice = false;
        s.DefaultCogsAccountId = null;
        s.DefaultInventoryAccountId = null;
        s.DefaultReturnAccountId = null;
        s.DefaultInvoiceLabelId = null;
        await db.SaveChangesAsync();
        return new()
        {
            ["valuation_method"] = s.ValuationMethod, ["allow_negative_stock"] = s.AllowNegativeStock,
            ["per_line_labeling_invoice"] = s.PerLineLabelingInvoice, ["default_cogs_account_id"] = null,
            ["default_inventory_account_id"] = null, ["default_return_account_id"] = null, ["default_invoice_label_id"] = null,
        };
    }

    /// <summary>Posted journal lines of a document, in id order.</summary>
    public static async Task<List<string>> JournalAsync(string type, int id, string? number = null)
    {
        await using var db = Db();
        var rows = await db.Set<JournalLine>()
            .Where(l => l.JournalEntry.VoucherType == type && l.JournalEntry.VoucherId == id && l.JournalEntry.IsPosted == true)
            .OrderBy(l => l.Id)
            .Select(l => new { l.AccountId, l.Debit, l.Credit, l.LabelId, l.Description })
            .ToListAsync();
        return rows.Select(r => $"{r.AccountId}|{r.Debit:0.0000}|{r.Credit:0.0000}|{r.LabelId}|{(number is null ? r.Description : r.Description?.Replace(number, "#"))}").ToList();
    }

    /// <summary>The raw page text from one marker to another (no decoding: script blocks must match byte for byte).</summary>
    public static string RawSegment(string html, string from, string to)
    {
        int a = html.IndexOf(from, StringComparison.Ordinal);
        Assert.True(a >= 0, $"marker not found: {from}");
        int b = html.IndexOf(to, a, StringComparison.Ordinal);
        Assert.True(b >= 0, $"marker not found: {to}");
        return html[a..(b + to.Length)];
    }

    /// <summary>The page's text from one marker to another, with whitespace and entities normalised.</summary>
    public static string Segment(string html, string from, string to)
    {
        int a = html.IndexOf(from, StringComparison.Ordinal);
        Assert.True(a >= 0, $"marker not found: {from}");
        int b = html.IndexOf(to, a, StringComparison.Ordinal);
        Assert.True(b >= 0, $"marker not found: {to}");
        return Normalise(html[a..(b + to.Length)]);
    }

    public static string Normalise(string s)
    {
        s = WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, @"\s+", " ");
        s = Regex.Replace(s, @">\s+<", "><");
        s = Regex.Replace(s, @"\s+>", ">");
        return s.Trim();
    }

}
