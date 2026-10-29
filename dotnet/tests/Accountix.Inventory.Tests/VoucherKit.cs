using System.Globalization;
using System.Net;
using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.Core.Shared;
using Accountix.Inventory.Services;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Inventory.Tests;

/// <summary>Set-up for the stock voucher page tests: committed products with stock in company B.</summary>
internal static class VoucherKit
{
    public static int CompanyB => Reference.Company("b");
    public static int OwnerB => Reference.UserId(Reference.Email("owner_b"));
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Iso(DateTime d) => d.ToString("yyyy-MM-dd", Inv);
    public static DateTime Day(int daysAgo) => DateTime.Today.AddDays(-daysAgo);

    public static Task<ModuleHost> HostAsync() =>
        ModuleHost.StartAsync(typeof(InventoryModule).Assembly, typeof(Accountix.Accounting.AccountingModule).Assembly);

    public static AccountixDbContext Db() => CostingWorld.NewDb(CompanyB);

    /// <summary>A product with <paramref name="qty"/> received at <paramref name="cost"/> on <paramref name="when"/>, committed.</summary>
    public static async Task<int> StockedProductAsync(decimal qty, decimal cost, DateTime when, string name = "Voucher Part")
    {
        await using var db = Db();
        await using var tx = await db.Database.BeginTransactionAsync();
        string tag = Guid.NewGuid().ToString("N")[..8];
        var p = new InvProduct
        {
            Sku = "VT-" + tag, Name = $"{name} {tag}", CurrentStock = 0, CostPrice = 0, UnitPrice = 0, ReorderLevel = 0, Unit = "pcs",
            HsCode = "", Weight = 0, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.Add(p);
        await db.SaveChangesAsync();
        if (qty > 0)
        {
            var periods = new Accountix.Accounting.Services.Periods(db);
            var ledger = new Accountix.Accounting.Services.Ledger(db, periods);
            var clock = new CompanyClock(db, db.Company, TimeProvider.System);
            var costing = new StockCosting(db, ledger, periods, new InventorySettingsStore(db), clock);
            // Opening stock with its journal, so the inventory account keeps tying to the valuation.
            await new StockEntry(db, costing, ledger, new Accountix.Accounting.Services.VoucherNumbers(db), clock)
                .PostOpeningStockAsync(p, qty, cost, when, OwnerB);
        }
        await tx.CommitAsync();
        return p.Id;
    }

    public static async Task<decimal> OnHandAsync(int productId)
    {
        await using var db = Db();
        var last = await db.Set<StockLedger>().AsNoTracking().Where(r => r.ProductId == productId)
            .OrderByDescending(r => r.TxnDate).ThenByDescending(r => r.Id).FirstOrDefaultAsync();
        return last?.RunningQty ?? 0m;
    }

    public static async Task<bool> TiesAsync(int productId)
    {
        await using var db = Db();
        var periods = new Accountix.Accounting.Services.Periods(db);
        var costing = new StockCosting(db, new Accountix.Accounting.Services.Ledger(db, periods), periods, new InventorySettingsStore(db),
                                       new CompanyClock(db, db.Company, TimeProvider.System));
        return (await costing.AssertInvariantAsync(productId)).Ok;
    }

    public static List<KeyValuePair<string, string>> IssueForm(string status, int productId, string qty, DateTime when, string reason,
                                                               string department = "Workshop", string charge = "", string label = "") =>
    [
        new("department", department), new("reason", reason), new("date", Iso(when)), new("charge_account_id", charge),
        new("label_id", label), new("status", status), new("product_id[]", productId.ToString(Inv)), new("qty[]", qty),
    ];

    /// <summary>
    /// A golden voucher case (Golden/capture/vouchers.py posted the same through the Python form):
    /// products with opening stock by role (qty, unit cost, days ago), then the form, whose values may
    /// name a product role ("P"), a date ("D-2"), the charge account ("CHARGE") or the label ("LABEL").
    /// </summary>
    public sealed record GoldenVoucher(string Url, string VType, Dictionary<string, object[]> Stock, string[][] Form, string? ChargeCode = null)
    {
        public object Inputs => ChargeCode is null
            ? new { url = Url, stock = Stock, form = Form }
            : new { url = Url, stock = Stock, form = Form, charge_code = ChargeCode };
    }

    public sealed record PostedVoucher(Dictionary<string, int> Ids, HttpResponseMessage Response, List<string> Lines);

    /// <summary>Posts the case through the .NET form and returns what it wrote, normalised as the capture normalised Python's.</summary>
    public static async Task<PostedVoucher> PostGoldenAsync(string name, PageClient c, GoldenVoucher g)
    {
        Golden.SameInputs(name, g.Inputs);
        var ids = new Dictionary<string, int>();
        foreach (var (role, s) in g.Stock)
            ids[role] = await StockedProductAsync(decimal.Parse((string)s[0], Inv), decimal.Parse((string)s[1], Inv), Day((int)s[2]));
        int charge = 0, label = 0;
        await using (var db = Db())
        {
            if (g.ChargeCode is not null)
                charge = (await db.Set<ChartOfAccount>().SingleAsync(a => a.Code == g.ChargeCode)).Id;
            var l = await db.Set<ProjectLabel>().Where(x => x.IsActive == true).OrderBy(x => x.Id).FirstOrDefaultAsync();
            if (l is null)
            {
                l = new ProjectLabel { Name = "Voucher label", IsActive = true, CreatedAt = DateTime.UtcNow };
                db.Add(l);
                await db.SaveChangesAsync();
            }
            label = l.Id;
        }
        string Value(string v) =>
            ids.TryGetValue(v, out int id) ? id.ToString(Inv)
            : v.StartsWith("D-") ? Iso(Day(int.Parse(v[2..], Inv)))
            : v == "CHARGE" ? charge.ToString(Inv) : v == "LABEL" ? label.ToString(Inv) : v;
        var r = await c.PostFormAsync(g.Url, g.Form.Select(f => new KeyValuePair<string, string>(f[0], Value(f[1]))).ToList(), g.Url);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(Golden.Load(name).GetProperty("redirect").GetString(), r.Headers.Location!.OriginalString);

        var dump = new GoldenDump(CompanyB, OwnerB, DateOnly.FromDateTime(DateTime.Today));
        await using (var db = Db())
            foreach (var (role, id) in ids)
            {
                string sku = await db.Set<InvProduct>().Where(p => p.Id == id).Select(p => p.Sku).SingleAsync();
                dump.Product(id, role).Text(sku["VT-".Length..], "TAG" + role);
            }
        int p = ids["P"];
        List<string> lines;
        await using (var db = Db())
            lines = g.VType switch
            {
                "ST" => await dump.StockTakeAsync(await db.Set<StockTakeItem>().Where(i => i.ProductId == p).MaxAsync(i => i.StockTakeId)),
                "CONS" => await dump.VoucherAsync("CONS", await db.Set<ConsumptionItem>().Where(i => i.ProductId == p).MaxAsync(i => i.VoucherId)),
                "SCRAP" => await dump.VoucherAsync("SCRAP", await db.Set<ScrapItem>().Where(i => i.ProductId == p).MaxAsync(i => i.VoucherId)),
                "ADJ" => await dump.VoucherAsync("ADJ", await db.Set<StockAdjustmentItem>().Where(i => i.ProductId == p).MaxAsync(i => i.VoucherId)),
                _ => throw new ArgumentException(g.VType),
            };
        Golden.Match(name, lines);
        return new PostedVoucher(ids, r, lines);
    }

    public static async Task<string> BodyAsync(PageClient c, string url)
    {
        var r = await c.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await r.Content.ReadAsStringAsync();
    }

    /// <summary>The flash messages a redirect leaves, read from the page it leads to.</summary>
    public static async Task<string> FollowAsync(PageClient c, HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        return WebUtility.HtmlDecode(await (await c.GetAsync(r.Headers.Location!.OriginalString)).Content.ReadAsStringAsync());
    }
}
