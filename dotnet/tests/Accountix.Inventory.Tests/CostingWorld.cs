using Accountix.Accounting.Services;
using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.Core.Shared;
using Accountix.Inventory.Services;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Accountix.Inventory.Tests;

/// <summary>
/// One product in company B, inside a transaction that is rolled back at the
/// end: the costing engine's tests can run again and again on the same copy.
/// Voucher ids are offset by a random base so a test never meets real rows of
/// the same (voucher type, id); numbers keep Python's "PI-00001" form.
/// </summary>
public sealed class CostingWorld : IAsyncDisposable
{
    public required AccountixDbContext Db { get; init; }
    public required IDbContextTransaction Tx { get; init; }
    public required StockCosting Costing { get; init; }
    public required StockEntry Entry { get; init; }
    public required Ledger Ledger { get; init; }
    public required int Product { get; set; }
    public required int Base { get; init; }
    public required int User { get; init; }
    public required int CompanyId { get; init; }

    public static int CompanyB => Reference.Company("b");
    public static int OwnerB => Reference.UserId(Reference.Email("owner_b"));

    public static AccountixDbContext NewDb(int companyId, params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] extra)
    {
        var company = new CompanyContext();
        company.Set(companyId);
        var options = new DbContextOptionsBuilder<AccountixDbContext>()
            .UseNpgsql(Reference.ConnectionString)
            .AddInterceptors([new GlSummaryInterceptor(), .. extra])
            .Options;
        return new AccountixDbContext(options, company);
    }

    public static async Task<CostingWorld> StartAsync(string method = "weighted_average", bool allowNegative = false, int? companyId = null,
                                                      QueryCounter? queries = null)
    {
        int cid = companyId ?? CompanyB;
        var db = queries is null ? NewDb(cid) : NewDb(cid, queries);
        var tx = await db.Database.BeginTransactionAsync();
        var periods = new Periods(db);
        var ledger = new Ledger(db, periods);
        var clock = new CompanyClock(db, db.Company, TimeProvider.System);
        var store = new InventorySettingsStore(db);
        var costing = new StockCosting(db, ledger, periods, store, clock);
        var w = new CostingWorld
        {
            Db = db, Tx = tx, Costing = costing, Ledger = ledger,
            Entry = new StockEntry(db, costing, ledger, new VoucherNumbers(db), clock),
            Product = 0, Base = Random.Shared.Next(1_000_000, 1_900_000) * 100, User = OwnerB, CompanyId = cid,
        };
        var s = await store.GetAsync();
        s.ValuationMethod = method;
        s.AllowNegativeStock = allowNegative;
        await db.SaveChangesAsync();
        w.Product = await w.NewProductAsync();
        return w;
    }

    public async Task<int> NewProductAsync(string name = "Widget")
    {
        string tag = Guid.NewGuid().ToString("N")[..8];
        var p = new InvProduct
        {
            Sku = "CT-" + tag, Name = $"{name} {tag}", CurrentStock = 0, CostPrice = 0, UnitPrice = 0, ReorderLevel = 0,
            Unit = "pcs", HsCode = "", Weight = 0, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        Db.Add(p);
        await Db.SaveChangesAsync();
        return p.Id;
    }

    public async Task SetAsync(string? method = null, bool? allowNegative = null)
    {
        var s = await new InventorySettingsStore(Db).GetAsync();
        if (method is not null) s.ValuationMethod = method;
        if (allowNegative is not null) s.AllowNegativeStock = allowNegative;
        await Db.SaveChangesAsync();
    }

    public int V(int n) => Base + n;

    public static DateTime Day(int daysAgo) => DateTime.Today.AddDays(-daysAgo);

    public Task<StockLedger> BuyAsync(decimal qty, decimal cost, int n = 1, DateTime? when = null, int? product = null) =>
        Costing.ReceiveAsync(product ?? Product, "PI", V(n), $"PI-{n:D5}", qty, cost, "", User, when);

    public Task<(decimal UnitCost, decimal TotalCost)> IssueAsync(decimal qty, int n = 1, string vtype = "SI", DateTime? when = null,
                                                                 decimal? unitCost = null, int? product = null) =>
        Costing.IssueAsync(product ?? Product, vtype, V(n), $"{vtype}-{n:D5}", qty, "", User, unitCost, when);

    public Task<IReadOnlyDictionary<int, decimal>> ReverseAsync(string vtype, int n, bool allowVariance = false) =>
        Costing.ReverseVoucherStockAsync(vtype, V(n), allowVariance, User);

    public async Task<decimal> BookValueAsync() => (await Costing.RunningBalanceAsync(Product)).Cost;

    public async Task TiesAsync(string msg)
    {
        var (ok, layers, running) = await Costing.AssertInvariantAsync(Product);
        Assert.True(ok, $"{msg}: layers value {layers} != ledger running_cost {running} — inventory no longer ties to COGS");
    }

    public async Task<List<(decimal UnitCost, decimal Qty)>> LayersAsync() => await Costing.LayersRemainingAsync(Product);

    public async Task<decimal> CostOfAsync(string vtype, int n) =>
        (await Db.Set<StockLedger>().AsNoTracking().SingleAsync(r => r.VoucherType == vtype && r.VoucherId == V(n))).TotalCost;

    public async Task<List<StockCostAdjustment>> AdjustmentsAsync() =>
        await Db.Set<StockCostAdjustment>().AsNoTracking().Where(a => a.ProductId == Product).OrderBy(a => a.Id).ToListAsync();

    /// <summary>Net debit the cost-adjustment journals put on the counter accounts; each journal must balance and be posted.</summary>
    public async Task<decimal> AdjustmentJournalTotalAsync()
    {
        decimal total = 0;
        foreach (var adj in await AdjustmentsAsync())
        {
            total += adj.Delta;
            var je = await Db.Set<JournalEntry>().AsNoTracking().Include(e => e.JournalLines).SingleAsync(e => e.Id == adj.JournalEntryId);
            Assert.True(je.IsPosted);
            Assert.Equal(je.JournalLines.Sum(l => l.Debit ?? 0), je.JournalLines.Sum(l => l.Credit ?? 0));
        }
        return total;
    }

    /// <summary>A refusal inside the test's transaction: back to before it, as the request's rollback would.</summary>
    public async Task<T> RefusedAsync<T>(Func<Task> action) where T : Exception
    {
        await Tx.CreateSavepointAsync("refused");
        var e = await Assert.ThrowsAsync<T>(action);
        await Tx.RollbackToSavepointAsync("refused");
        Db.ChangeTracker.Clear();
        return e;
    }

    public async ValueTask DisposeAsync()
    {
        await Tx.RollbackAsync();
        await Db.DisposeAsync();
    }
}
