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

namespace Accountix.FixedAssets.Tests;

/// <summary>
/// Stand-ins registered after the real modules (type names sort after
/// Accountix.FixedAssets.FixedAssetsModule): stock costing until the Inventory
/// module's is done, and an export that keeps the table it was given.
/// </summary>
public sealed class TestFakesModule : IAccountixModule
{
    public string Key => "fixed_assets_test_fakes";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<IStockCosting>(FakeStock.Instance);
        services.AddSingleton<IReportExport>(FakeExport.Instance);
    }

    public ModuleNav? Navigation => null;
}

/// <summary>IStockCosting stand-in: an issue costs UnitCost a unit; every call is recorded.</summary>
public sealed class FakeStock : IStockCosting
{
    public static readonly FakeStock Instance = new();
    public decimal UnitCost { get; set; } = 250_000m;
    public ConcurrentQueue<string> Calls { get; } = new();

    public Task LockProductsAsync(IEnumerable<int> productIds, CancellationToken ct = default) => Task.CompletedTask;

    public Task<decimal> RecordInAsync(int productId, string voucherType, int voucherId, string voucherNumber, decimal qty, decimal unitCost,
                                       string notes = "", int createdBy = 1, DateTime? txnDate = null, decimal? totalCost = null,
                                       CancellationToken ct = default)
    {
        Calls.Enqueue($"in {voucherType}:{voucherId} p{productId} {qty}@{unitCost} {txnDate:yyyy-MM-dd} {notes}");
        return Task.FromResult(totalCost ?? Math.Round(qty * unitCost, 2));
    }

    public Task<decimal> RecordOutAsync(int productId, string voucherType, int voucherId, string voucherNumber, decimal qty,
                                        string notes = "", int createdBy = 1, decimal? unitCost = null, DateTime? txnDate = null,
                                        (string Type, int Id)? preferVoucher = null, CancellationToken ct = default)
    {
        Calls.Enqueue($"out {voucherType}:{voucherId} p{productId} {qty} {txnDate:yyyy-MM-dd} {notes}");
        return Task.FromResult(Math.Round(qty * (unitCost ?? UnitCost), 2));
    }

    public Task ReverseVoucherStockAsync(string voucherType, int voucherId, int createdBy = 1, CancellationToken ct = default)
    {
        Calls.Enqueue($"reverse {voucherType}:{voucherId}");
        return Task.CompletedTask;
    }

    public Task<decimal> OnHandAsync(int productId, DateTime? at = null, CancellationToken ct = default) => Task.FromResult(0m);
    public Task<decimal> StockValueAsync(int productId, CancellationToken ct = default) => Task.FromResult(0m);
    public Task<decimal> CurrentUnitCostAsync(int productId, CancellationToken ct = default) => Task.FromResult(UnitCost);

    public Task<decimal?> OriginalIssueCostAsync(string voucherType, int voucherId, int productId, CancellationToken ct = default) =>
        Task.FromResult<decimal?>(null);

    public async Task<StockLedger> ReceiveAsync(int productId, string voucherType, int voucherId, string voucherNumber, decimal qty,
                                                decimal unitCost, string notes = "", int createdBy = 1, DateTime? txnDate = null,
                                                decimal? totalCost = null, CancellationToken ct = default)
    {
        decimal total = await RecordInAsync(productId, voucherType, voucherId, voucherNumber, qty, unitCost, notes, createdBy, txnDate, totalCost, ct);
        return new StockLedger { ProductId = productId, VoucherType = voucherType, VoucherId = voucherId, UnitCost = unitCost, TotalCost = total };
    }

    public async Task<(decimal UnitCost, decimal TotalCost)> IssueAsync(int productId, string voucherType, int voucherId, string voucherNumber,
        decimal qty, string notes = "", int createdBy = 1, decimal? unitCost = null, DateTime? txnDate = null,
        (string Type, int Id)? preferVoucher = null, CancellationToken ct = default)
    {
        decimal total = await RecordOutAsync(productId, voucherType, voucherId, voucherNumber, qty, notes, createdBy, unitCost, txnDate, preferVoucher, ct);
        return (unitCost ?? UnitCost, total);
    }

    public Task<(decimal UnitCost, decimal TotalCost)> CostOfIssueAsync(int productId, decimal qty, CancellationToken ct = default) =>
        Task.FromResult((UnitCost, Math.Round(qty * UnitCost, 2)));

    public Task<decimal> ValueAtAsync(int productId, DateTime at, CancellationToken ct = default) => Task.FromResult(0m);

    public Task<decimal?> OriginalReceiptCostAsync(string voucherType, int voucherId, int productId, CancellationToken ct = default) =>
        Task.FromResult<decimal?>(null);

    public Task<IReadOnlyList<(string VoucherType, string VoucherNumber, decimal Qty)>> ConsumersOfVoucherAsync(string voucherType, int voucherId,
        CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(string, string, decimal)>>([]);

    public async Task<IReadOnlyDictionary<int, decimal>> ReverseVoucherStockAsync(string voucherType, int voucherId, bool allowVariance,
        int createdBy = 1, CancellationToken ct = default)
    {
        await ReverseVoucherStockAsync(voucherType, voucherId, createdBy, ct);
        return new Dictionary<int, decimal>();
    }

    public Task RevalueForMethodChangeAsync(string newMethod, int createdBy = 1, CancellationToken ct = default) => Task.CompletedTask;

    public Task<(bool Ok, decimal LayerValue, decimal RunningCost)> AssertInvariantAsync(int productId, CancellationToken ct = default) =>
        Task.FromResult((true, 0m, 0m));
}

/// <summary>IReportExport stand-in: answers a tiny file and keeps the table (the real pipeline is Finance's).</summary>
public sealed class FakeExport : IReportExport
{
    public static readonly FakeExport Instance = new();
    public (string Format, ReportTable Table)? Last { get; private set; }

    public Task<ExportFile> ExportTableAsync(string format, ReportTable table, CancellationToken ct = default)
    {
        if (format is not ("excel" or "xlsx" or "pdf" or "csv"))
            throw new UnknownExportFormatException(format);
        Last = (format, table);
        return Task.FromResult(new ExportFile($"{table.Title}.{format}", "application/octet-stream", "PK"u8.ToArray()));
    }
}

/// <summary>Shared helpers for the fixed-asset tests (company B is fresh and fully provisioned; A takes the stock tests).</summary>
public static class Fa
{
    public static int OwnerB => Reference.UserId(Reference.Email("owner_b"));
    public static int CompanyB => Reference.Company("b");
    public static int OwnerA => Reference.UserId(Reference.Email("owner_a"));
    public static int CompanyA => Reference.Company("a");

    public static AccountixDbContext Db(int companyId)
    {
        var company = new CompanyContext();
        company.Set(companyId);
        return Reference.NewDb(company);
    }

    public static Task<ModuleHost> HostAsync() =>
        ModuleHost.StartAsync(typeof(FixedAssetsModule).Assembly, typeof(Accounting.AccountingModule).Assembly,
                              typeof(TestFakesModule).Assembly);

    public static string Tag() => Guid.NewGuid().ToString("N")[..8];

    public static async Task<int> CategoryAsync(int companyId, string name = "Vehicles")
    {
        await using var db = Db(companyId);
        return await db.Set<FaCategory>().Where(c => c.Name == name).Select(c => c.Id).FirstAsync();
    }

    public static async Task<FixedAsset> AssetAsync(int companyId, string name)
    {
        await using var db = Db(companyId);
        return await db.Set<FixedAsset>().AsNoTracking().SingleAsync(a => a.Name == name);
    }

    public static async Task<FixedAsset?> FindAsync(int companyId, string name)
    {
        await using var db = Db(companyId);
        return await db.Set<FixedAsset>().AsNoTracking().SingleOrDefaultAsync(a => a.Name == name);
    }

    /// <summary>Live depreciation of an asset, as Python's posted_depreciation (before the floor).</summary>
    public static async Task<decimal> PostedAsync(int companyId, int assetId)
    {
        await using var db = Db(companyId);
        return await db.Set<AssetDepreciation>().Where(d => d.AssetId == assetId
                && (d.JournalEntryId == null || db.Set<JournalEntry>().Any(e => e.Id == d.JournalEntryId && e.IsPosted == true)))
            .SumAsync(d => d.Amount);
    }

    /// <summary>Posted balance (Dr − Cr) of an account.</summary>
    public static async Task<decimal> BalanceAsync(int companyId, int accountId)
    {
        await using var db = Db(companyId);
        return await db.Set<JournalLine>().Where(l => l.AccountId == accountId && l.JournalEntry.IsPosted == true)
            .SumAsync(l => (l.Debit ?? 0) - (l.Credit ?? 0));
    }

    /// <summary>
    /// What this asset's own journals (FA-ACQ / FA-DEP / FA-DISP / FA-CAP / FA-TRF) leave on an account — the
    /// general "Fixed Assets" account is shared by every asset booked to it.
    /// </summary>
    public static async Task<decimal> AssetBalanceAsync(int companyId, int accountId, int assetId)
    {
        await using var db = Db(companyId);
        var deps = db.Set<AssetDepreciation>().Where(d => d.AssetId == assetId).Select(d => d.Id);
        var transfers = db.Set<AssetTransfer>().Where(t => t.AssetId == assetId).Select(t => t.Id);
        return await db.Set<JournalLine>()
            .Where(l => l.AccountId == accountId && l.JournalEntry.IsPosted == true
                        && (((l.JournalEntry.VoucherType == "FA-ACQ" || l.JournalEntry.VoucherType == "FA-DISP") && l.JournalEntry.VoucherId == assetId)
                            || (l.JournalEntry.VoucherType == "FA-DEP" && deps.Contains(l.JournalEntry.VoucherId))
                            || ((l.JournalEntry.VoucherType == "FA-CAP" || l.JournalEntry.VoucherType == "FA-TRF") && transfers.Contains(l.JournalEntry.VoucherId))))
            .SumAsync(l => (l.Debit ?? 0) - (l.Credit ?? 0));
    }

    public static async Task<int> PostedEntriesAsync(int companyId, string type, int voucherId)
    {
        await using var db = Db(companyId);
        return await db.Set<JournalEntry>().CountAsync(e => e.VoucherType == type && e.VoucherId == voucherId && e.IsPosted == true);
    }

    /// <summary>
    /// A posted journal as comparable text: entry (number, description, date) and lines (account code, Dr, Cr,
    /// description, label). The asset's own accounts and the ids in numbers are written as {A} / {V}, so the
    /// same asset booked by each app compares equal.
    /// </summary>
    public static async Task<List<string>> JournalAsync(int companyId, string type, int voucherId, int assetId, string? assetCode = null)
    {
        await using var db = Db(companyId);
        var entries = await db.Set<JournalEntry>().AsNoTracking()
            .Where(e => e.VoucherType == type && e.VoucherId == voucherId && e.IsPosted == true)
            .Include(e => e.JournalLines).ThenInclude(l => l.Account).OrderBy(e => e.Id).ToListAsync();
        string entity = $"-{assetId + 100:D4}";
        string Norm(string s)
        {
            if (assetCode is not null) s = s.Replace(assetCode, "{CODE}");
            s = Regex.Replace(s, $@"-{voucherId}\b", "-{V}");
            return Regex.Replace(s, @"-\d{5}\b", "-{N}");      // a voucher series number (FA-CAP-00003)
        }
        var output = new List<string>();
        foreach (var e in entries)
        {
            output.Add($"E|{Norm(e.VoucherNumber)}|{(assetCode is null ? e.Description : e.Description?.Replace(assetCode, "{CODE}"))}|{e.EntryDate:yyyy-MM-dd HH:mm:ss}");
            foreach (var l in e.JournalLines.OrderBy(l => l.Id))
            {
                string code = l.Account.Code.EndsWith(entity, StringComparison.Ordinal) ? l.Account.Code[..^entity.Length] + "-{A}"
                    // get_or_create_account("5710", …) makes a new account on every impairment, in both apps.
                    : l.Account.Name == "Impairment Loss on Fixed Assets" ? "{IMPAIRMENT}" : l.Account.Code;
                output.Add($"L|{code}|{l.Debit:0.0000}|{l.Credit:0.0000}|{l.Description}|{l.LabelId}");
            }
        }
        return output;
    }

    public static string Location(HttpResponseMessage r) => r.Headers.Location?.OriginalString ?? "";

    /// <summary>POST a form as the page <paramref name="from"/> would, with its Referer (refusals return there).</summary>
    public static async Task<HttpResponseMessage> PostAsync(PageClient c, string url, IEnumerable<KeyValuePair<string, string>> fields, string from)
    {
        string token = await c.TokenFromAsync(from);
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields.Append(new("__RequestVerificationToken", token))),
        };
        req.Headers.Referrer = new Uri(new Uri("http://localhost/"), from);
        return await c.SendAsync(req);
    }

    /// <summary>POST, then read the page the redirect lands on (where the flash shows).</summary>
    public static async Task<string> PostAndFollowAsync(PageClient c, string url, IEnumerable<KeyValuePair<string, string>> fields, string from) =>
        await FollowAsync(c, await PostAsync(c, url, fields, from));

    /// <summary>The flashes a redirect carried, read off the next page.</summary>
    public static async Task<string> FollowAsync(PageClient c, HttpResponseMessage r)
    {
        if (r.StatusCode != HttpStatusCode.Redirect)
            return await r.Content.ReadAsStringAsync();
        return await (await c.GetAsync(Location(r))).Content.ReadAsStringAsync();
    }

    /// <summary>Books Integrity for the company (Executive's port of shared/integrity.py): the failures as text, "[]" when the books tie.</summary>
    public static async Task<string> IntegrityAsync(int companyId)
    {
        await using var db = Db(companyId);
        var bad = await new Accountix.Executive.Services.BooksIntegrity(db).FailuresAsync();
        return JsonSerializer.Serialize(bad.Select(b => $"{b.Label}: expected {b.Expected} got {b.Actual} ({b.Detail})"));
    }

    public static List<KeyValuePair<string, string>> Form(params (string Key, string Value)[] fields) =>
        fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)).ToList();

    public static int IdFrom(string location) => int.Parse(Regex.Match(location, @"/(\d+)(/|$)").Groups[1].Value);

    public static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;
}
