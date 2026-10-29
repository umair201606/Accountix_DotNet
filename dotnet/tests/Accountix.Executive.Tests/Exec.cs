using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Accountix.Accounting.Services;
using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.Core.Shared;
using Accountix.Executive.Services;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Executive.Tests;

/// <summary>
/// Shared set-up for the executive tests (ACCOUNTIX_TEST_DB=executive). The golden tests read their own
/// company, "golden-exec" (made and provisioned by <see cref="World.CompanyAsync"/>, owner_b its admin),
/// seeded once through .NET's own ledger and stock services: customers C1..C7 and suppliers S1..S4
/// with invoices, receipts, an advance, a flip, a settled party, a future posting, a reversed
/// (unposted) posting, P&amp;L activity over 13 months, and opening stock of two products. Nothing else
/// writes there, so its figures are fixed and the Python app's answers for them are kept in Golden/.
/// Reference company B is for rights and for the races (they post vouchers).
/// </summary>
internal static class Exec
{
    public const string Slug = "golden-exec";

    /// <summary>The golden company (created on first use).</summary>
    public static int Company => CompanyId.Value;
    private static readonly Lazy<int> CompanyId = new(() => World.CompanyAsync(Slug, "Golden Executive").GetAwaiter().GetResult());

    /// <summary>Reference company B (fresh, fully provisioned): rights tests and races.</summary>
    public static int CompanyB => Reference.Company("b");
    public static int OwnerB => Reference.UserId(Reference.Email("owner_b"));
    public static int OwnerA => Reference.UserId(Reference.Email("owner_a"));
    public static int Restricted => Reference.UserId(Reference.Email("restricted"));

    /// <summary>
    /// The instant the golden figures without an as-of date were captured at: 29 Oct 2026 12:00 in Lahore.
    /// The test hosts run the company clock at it (<see cref="FixedClockForTests"/>) and the reports get it
    /// through <see cref="Reports"/>, as the capture stopped Python's clock there.
    /// </summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 29, 7, 0, 0, TimeSpan.Zero);

    public static AccountixDbContext Db(int companyId) => World.Db(companyId);

    public static ExecutiveReports Reports(AccountixDbContext db, TimeProvider? time = null) =>
        new(db, db.Company, new CompanyClock(db, db.Company, time ?? new FixedTime(Now)));

    /// <summary>The as-of day most tests use: every seeded layer has a known age at it.</summary>
    public static readonly DateOnly AsOf = new(2026, 6, 30);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _seeded;
    private static (int TradeDebtors, int TradeCreditors) _roots;

    /// <summary>Seed the golden company once, and put the selection back to Trade Debtors + Trade Creditors with sub-accounts.</summary>
    public static async Task<(int TradeDebtors, int TradeCreditors)> SeedAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (!_seeded)
            {
                await using var db = Db(Company);
                if (!await db.Set<JournalEntry>().AnyAsync(e => e.VoucherNumber.StartsWith("EXEC-SEED-")))
                    await PostSeedAsync(db);
                _roots = (await AccountIdAsync("1-01-02-01", Company), await AccountIdAsync("2-01-01-01", Company));
                _seeded = true;
            }
        }
        finally
        {
            Gate.Release();
        }
        await ResetSelectionAsync(_roots.TradeDebtors, _roots.TradeCreditors);
        return _roots;
    }

    private static async Task PostSeedAsync(AccountixDbContext db)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var periods = new Periods(db);
        var ledger = new Ledger(db, periods);
        var cash = await ledger.PostingAccountAsync("cash");
        var rev = await ledger.PostingAccountAsync("revenue");
        var cogs = await ledger.PostingAccountAsync("cogs");
        var ret = await ledger.PostingAccountAsync("sales_returns");
        var exp = await db.Set<ChartOfAccount>().SingleAsync(a => a.Code == "5-04-01-01-0001");
        var c = new Dictionary<int, ChartOfAccount>();
        for (int i = 1; i <= 7; i++) c[i] = await ledger.EntityAccountAsync("customer", 9000 + i, $"Exec Customer {i}");
        var s = new Dictionary<int, ChartOfAccount>();
        for (int i = 1; i <= 4; i++) s[i] = await ledger.EntityAccountAsync("supplier", 9000 + i, $"Exec Supplier {i}");
        int n = 0;
        async Task<int> Post(string day, params JournalLineInput[] lines)
        {
            n++;
            await ledger.PostAsync("EXEC", 900000 + n, $"EXEC-SEED-{n:000}", "Executive test posting", lines,
                                   DateTime.ParseExact(day, "yyyy-MM-dd", null).AddHours(10));
            return 900000 + n;
        }
        // Python's f"Invoice {amt}": 1000 printed as an int, 7300.5 as a float.
        static string T(decimal a) => a == decimal.Truncate(a) ? ((long)a).ToString() : Py.Repr(a);
        Task<int> Sale(int i, string d, decimal a) => Post(d, new(c[i].Id, a, 0, $"Invoice {T(a)}"), new(rev.Id, 0, a));
        Task<int> Receipt(int i, string d, decimal a) => Post(d, new(cash.Id, a, 0), new(c[i].Id, 0, a, $"Receipt {T(a)}"));
        Task<int> SReturn(int i, string d, decimal a) => Post(d, new(ret.Id, a, 0), new(c[i].Id, 0, a));
        Task<int> Purchase(int i, string d, decimal a) => Post(d, new(exp.Id, a, 0), new(s[i].Id, 0, a, $"Bill {T(a)}"));
        Task<int> Pay(int i, string d, decimal a) => Post(d, new(s[i].Id, a, 0), new(cash.Id, 0, a));
        Task<int> Cost(string d, decimal a, ChartOfAccount acct) => Post(d, new(acct.Id, a, 0), new(cash.Id, 0, a));

        await Sale(1, "2026-05-16", 1000); await Receipt(1, "2026-06-25", 400);
        await Sale(2, "2026-02-15", 5000); await Sale(2, "2026-04-20", 2500); await Sale(2, "2026-04-20", 500); await Receipt(2, "2026-05-10", 3000);
        await Receipt(3, "2026-06-01", 350); await Sale(3, "2026-06-10", 1000);   // 650 owed: never tied with C1's 600
        await Sale(4, "2026-06-20", 800); await Receipt(4, "2026-06-28", 1300);
        await Sale(5, "2026-03-03", 300); await Receipt(5, "2026-03-20", 300);
        await Sale(6, "2026-07-15", 1200);
        await Sale(7, "2025-10-05", 7300.50m); await Sale(7, "2025-11-11", 1999.99m); await SReturn(7, "2025-11-20", 199.99m); await Receipt(7, "2026-01-15", 2000);
        int undone = await Sale(7, "2026-06-01", 999);
        await ledger.ReverseAsync("EXEC", undone);
        await Purchase(1, "2026-06-25", 2000);
        await Purchase(2, "2026-03-01", 10000); await Pay(2, "2026-04-01", 4000); await Purchase(2, "2026-05-15", 3000);
        await Pay(3, "2026-06-01", 700);
        await Purchase(4, "2025-12-01", 4500.25m); await Pay(4, "2026-02-01", 4500.25m);
        await Cost("2025-10-31", 3000, cogs); await Cost("2026-02-28", 2500, cogs); await Cost("2026-05-31", 1500, cogs); await Cost("2026-06-30", 400, cogs);
        await Cost("2025-10-15", 1200, exp); await Cost("2026-01-15", 800, exp); await Cost("2026-06-15", 650, exp);

        // Opening stock of two products (stock ledger, layers, on-hand and the journal), so liquidity has
        // inventory and Books Integrity's stock checks have rows to check (and to break).
        var clock = new CompanyClock(db, db.Company, TimeProvider.System);
        var costing = new Accountix.Inventory.Services.StockCosting(db, ledger, periods, new Accountix.Inventory.Services.InventorySettingsStore(db), clock);
        var entry = new Accountix.Inventory.Services.StockEntry(db, costing, ledger, new VoucherNumbers(db), clock);
        foreach (var (sku, name, qty, cost) in new[] { ("EXP-1", "Exec Panel", 10m, 1500.50m), ("EXP-2", "Exec Inverter", 4m, 22000m) })
        {
            var now = DateTime.UtcNow;
            var p = new InvProduct
            {
                Sku = sku, Name = name, UnitPrice = cost * 1.25m, CostPrice = cost, ReorderLevel = 0, Weight = 0, Unit = "pcs",
                CurrentStock = 0, IsActive = true, CreatedAt = now, UpdatedAt = now,
            };
            db.Add(p);
            await db.SaveChangesAsync();
            await entry.PostOpeningStockAsync(p, qty, cost, new DateTime(2025, 9, 1), OwnerB);
            await db.SaveChangesAsync();
        }
        await tx.CommitAsync();
    }

    public static async Task ResetSelectionAsync(params int[] deepRoots) => await ResetSelectionInAsync(Company, deepRoots);

    public static async Task ResetSelectionInAsync(int companyId, params int[] deepRoots)
    {
        await using var db = Db(companyId);
        await Reports(db).SetSelectionAsync(deepRoots.Select(i => i.ToString()), deepRoots.Select(i => i.ToString()), null, DateTime.UtcNow);
    }

    public static async Task<int> AccountIdAsync(string code, int companyId)
    {
        await using var db = Db(companyId);
        return await db.Set<ChartOfAccount>().Where(a => a.Code == code).Select(a => a.Id).SingleAsync();
    }

    /// <summary>Account id → code for a company (golden answers name accounts by code).</summary>
    public static async Task<Dictionary<int, string>> CodesAsync(int companyId)
    {
        await using var db = Db(companyId);
        return await db.Set<ChartOfAccount>().AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Code);
    }

    /// <summary>Account ids in links (<c>/executive/party/123</c>, <c>group=45</c>) as codes, as the capture wrote them.</summary>
    public static string LinkCodes(string text, IReadOnlyDictionary<int, string> codes) =>
        Regex.Replace(text, @"(/executive/party/|group=)(\d+)",
            m => m.Groups[1].Value + "{" + (codes.TryGetValue(int.Parse(m.Groups[2].Value), out var code) ? code : "?" + m.Groups[2].Value) + "}");

    public static JsonNode? Num(decimal? v) => v is decimal d ? JsonValue.Create(d) : null;
}

/// <summary>A clock stopped at one instant (the company clock's tests).</summary>
public sealed class FixedTime(DateTimeOffset at) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => at;
}
