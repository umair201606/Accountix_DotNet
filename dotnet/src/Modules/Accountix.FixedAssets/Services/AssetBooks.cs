using System.Text.RegularExpressions;

namespace Accountix.FixedAssets.Services;

/// <summary>The accounts each mapping of the asset form may point at (assets.account_choices).</summary>
public sealed record AccountChoices(
    IReadOnlyList<ChartOfAccount> Fa, IReadOnlyList<ChartOfAccount> Accum, IReadOnlyList<ChartOfAccount> Expense,
    IReadOnlyList<ChartOfAccount> Credit, IReadOnlyList<ChartOfAccount> Proceeds);

/// <summary>A refusal of the fixed-asset rules (Python's ValueError in the transfer helpers).</summary>
public sealed class AssetRuleException(string message) : BusinessRuleException(message);

/// <summary>
/// Fixed assets in the ledger (fixed_assets_app/routes/assets.py and
/// depreciation.py): live depreciation, acquisition and depreciation journals,
/// the asset's own accounts, codes. Every write is in the caller's transaction.
/// </summary>
public sealed partial class AssetBooks(AccountixDbContext db, ILedger ledger, CompanyClock clock)
{
    public const int PostingLevel = 5;

    // ── Live depreciation (rows whose journal is still posted, or that have none) ──

    /// <summary>live_depreciation_query: an asset's charges still backed by a posted journal.</summary>
    public IQueryable<AssetDepreciation> LiveRows(int assetId) =>
        AllLiveRows().Where(d => d.AssetId == assetId);

    public IQueryable<AssetDepreciation> AllLiveRows() =>
        db.Set<AssetDepreciation>().Where(d => d.JournalEntryId == null
            || db.Set<JournalEntry>().Any(e => e.Id == d.JournalEntryId && e.IsPosted == true));

    /// <summary>Live charges per asset, aggregated in SQL (one query for any number of assets).</summary>
    public async Task<Dictionary<int, LiveCharges>> LiveByAssetAsync(IQueryable<int>? assetIds = null, CancellationToken ct = default)
    {
        var rows = AllLiveRows();
        if (assetIds is not null)
            rows = rows.Where(d => assetIds.Contains(d.AssetId));
        return await rows.GroupBy(d => d.AssetId)
            .Select(g => new { g.Key, Sum = g.Sum(x => x.Amount), Last = g.Max(x => x.EntryDate), First = g.Min(x => x.EntryDate), N = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => new LiveCharges(x.Sum, x.Last, x.First, x.N), ct);
    }

    public async Task<LiveCharges> LiveAsync(int assetId, CancellationToken ct = default)
    {
        var x = await LiveRows(assetId).GroupBy(d => d.AssetId)
            .Select(g => new { Sum = g.Sum(r => r.Amount), Last = g.Max(r => r.EntryDate), First = g.Min(r => r.EntryDate), N = g.Count() })
            .FirstOrDefaultAsync(ct);
        return x is null ? LiveCharges.None : new LiveCharges(x.Sum, x.Last, x.First, x.N);
    }

    public async Task<AssetCalc> CalcAsync(FixedAsset a, CancellationToken ct = default) => new(a, await LiveAsync(a.Id, ct));

    /// <summary>recalculate: refresh the cached columns from the live rows.</summary>
    public async Task<AssetCalc> RecalculateAsync(FixedAsset a, CancellationToken ct = default)
    {
        var calc = await CalcAsync(a, ct);
        double posted = calc.PostedDepreciation;
        a.AccumulatedDepreciation = Py.Dec(posted);
        a.CurrentBookValue = Py.Dec(calc.Cost - posted);
        return calc;
    }

    /// <summary>AssetDepreciation.is_live.</summary>
    public async Task<bool> IsLiveAsync(AssetDepreciation d, CancellationToken ct = default) =>
        d.JournalEntryId is not int jid || await db.Set<JournalEntry>().AnyAsync(e => e.Id == jid && e.IsPosted == true, ct);

    // ── Accounts ──────────────────────────────────────────────────────────

    /// <summary>create_fixed_asset_accounts: the asset's own level-5 accounts (asset, accumulated depreciation).</summary>
    public async Task<(ChartOfAccount Fa, ChartOfAccount Accum)> AssetAccountsAsync(FixedAsset a, CancellationToken ct = default)
    {
        var fa = await ledger.EntityAccountAsync("fixed_asset", a.Id, a.Name, ct);
        var accum = await ledger.EntityAccountAsync("accum_dep", a.Id, $"Accum. Dep. — {a.Name}", ct);
        return (fa, accum);
    }

    /// <summary>account_choices: one read of the posting accounts, split as Python's seven queries split them.</summary>
    public async Task<AccountChoices> ChoicesAsync(CancellationToken ct = default)
    {
        var all = await db.Set<ChartOfAccount>().AsNoTracking()
            .Where(a => a.IsActive == true && a.Level >= PostingLevel)
            .OrderBy(a => a.Code)
            .ToListAsync(ct);
        List<ChartOfAccount> Under(string prefix) => all.Where(a => a.Code.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        var cash = all.Where(a => a.Type == "asset" && (a.Code.StartsWith("1-01-01-", StringComparison.Ordinal)
                                                        || a.Name.Contains("cash", StringComparison.OrdinalIgnoreCase)
                                                        || a.Name.Contains("bank", StringComparison.OrdinalIgnoreCase))).ToList();
        return new AccountChoices(
            Fa: [.. Under("1-02-01"), .. Under("1-02-03")],
            Accum: Under("1-02-02"),
            Expense: all.Where(a => a.Type == "expense").ToList(),
            Credit: [.. Under("2-"), .. cash, .. Under("1-01-03-02")],
            Proceeds: [.. cash, .. Under("1-01-02"), .. Under("1-01-03")]);
    }

    /// <summary>_mapped: the account id posted in a field if it is allowed; blank keeps <paramref name="current"/>.</summary>
    public static int? Mapped(IFormCollection form, string field, IReadOnlyList<ChartOfAccount> allowed, int? current = null,
                              string label = "account")
    {
        if (!int.TryParse(form[field].FirstOrDefault(), out int aid) || aid == 0)
            return current;
        if (!allowed.Any(a => a.Id == aid))
            throw new FormInputException($"That {label} cannot be used here.");
        return aid;
    }

    // ── Codes ─────────────────────────────────────────────────────────────

    /// <summary>next_code: FA-0001, FA-0002 … per company, queued per company so two creations never share one.</summary>
    public async Task<string> NextCodeAsync(CancellationToken ct = default)
    {
        await db.SerializeAsync("asset-code", db.Company.CompanyId);
        var codes = await db.Set<FixedAsset>().Where(a => a.AssetCode.StartsWith("FA-")).Select(a => a.AssetCode).ToListAsync(ct);
        int top = 0;
        foreach (string code in codes)
        {
            var m = CodeRe().Match(code);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int n))
                top = Math.Max(top, n);
        }
        return $"FA-{top + 1:D4}";
    }

    [GeneratedRegex(@"^FA-(\d+)$")]
    private static partial Regex CodeRe();

    // ── Journals ──────────────────────────────────────────────────────────

    /// <summary>post_acquisition: Dr the asset, Cr whatever paid for it. Skipped when a posted FA-ACQ exists.</summary>
    public async Task<JournalEntry?> PostAcquisitionAsync(FixedAsset a, int userId, int? creditAccountId = null, CancellationToken ct = default)
    {
        if (a.PurchaseCost == 0)
            return null;
        var existing = await db.Set<JournalEntry>()
            .FirstOrDefaultAsync(e => e.VoucherType == "FA-ACQ" && e.VoucherId == a.Id && e.IsPosted == true, ct);
        if (existing is not null)
            return existing;
        if (a.FixedAssetAccountId is null)
        {
            var (fa, _) = await AssetAccountsAsync(a, ct);
            a.FixedAssetAccountId = fa.Id;
            await db.SaveChangesAsync(ct);
        }
        int credit = creditAccountId ?? a.AcquisitionCreditAccountId ?? (await ledger.PostingAccountAsync("ap", ct)).Id;
        a.AcquisitionCreditAccountId = credit;
        decimal cost = Py.Dec(Py.F(a.PurchaseCost));
        return await ledger.PostAsync("FA-ACQ", a.Id, $"FA-ACQ-{a.AssetCode}", $"Acquisition of {a.Name}",
        [
            new JournalLineInput(a.FixedAssetAccountId.Value, cost, 0m, $"Fixed asset - {a.Name}", a.LabelId),
            new JournalLineInput(credit, 0m, cost, $"Acquisition of {a.Name}"),
        ], Midnight(a.PurchaseDate), userId, ct);
    }

    /// <summary>
    /// post_asset_depreciation: one charge (computed, or a manual amount capped at
    /// the salvage floor), keyed voucher_id = the AssetDepreciation row. Returns the amount.
    /// </summary>
    public async Task<double> PostDepreciationAsync(FixedAsset a, DateOnly entryDate, int userId, double? amount = null,
                                                    int? expenseAccountId = null, string? notes = null, CancellationToken ct = default)
    {
        if (a.Status != "active" || a.IsActive != true)
            return 0;      // disposed / transferred / draft assets take no charge
        var calc = await CalcAsync(a, ct);
        double computed = calc.DueDepreciation(entryDate);
        double charge = amount is double manual ? Py.Round(Py.Min(manual, calc.RemainingDepreciable), 2) : computed;
        if (charge <= 0)
            return 0;
        if (a.AccumDepAccountId is null)
        {
            var (_, accum) = await AssetAccountsAsync(a, ct);
            a.AccumDepAccountId = accum.Id;
        }
        int accumId = a.AccumDepAccountId.Value;
        int expenseId = expenseAccountId ?? a.DepExpenseAccountId ?? (await ledger.PostingAccountAsync("depreciation_expense", ct)).Id;
        bool hasNotes = !string.IsNullOrEmpty(notes);
        var dep = new AssetDepreciation
        {
            AssetId = a.Id,
            EntryDate = entryDate,
            Amount = Py.Dec(charge),
            AccumulatedAfter = 0m,
            NetBookValueAfter = 0m,
            Notes = hasNotes ? notes : $"Depreciation posted {Py.Date(entryDate)}",
            CreatedAt = clock.UtcNow,
        };
        db.Add(dep);
        await db.SaveChangesAsync(ct);
        decimal amt = Py.Dec(charge);
        var je = await ledger.PostAsync("FA-DEP", dep.Id, $"FA-DEP-{a.AssetCode}-{dep.Id}",
            hasNotes ? $"{notes} - {a.Name}" : $"Depreciation for {a.Name} - {Py.Date(entryDate)}",
        [
            new JournalLineInput(expenseId, amt, 0m, $"Depreciation expense - {a.Name}", a.LabelId),
            new JournalLineInput(accumId, 0m, amt, $"Accumulated depreciation - {a.Name}"),
        ], Midnight(entryDate), userId, ct);
        dep.JournalEntryId = je.Id;
        await db.SaveChangesAsync(ct);
        await RecalculateAsync(a, ct);
        dep.AccumulatedAfter = a.AccumulatedDepreciation ?? 0m;
        dep.NetBookValueAfter = a.CurrentBookValue;
        await db.SaveChangesAsync(ct);
        return charge;
    }

    /// <summary>reverse_asset_depreciation: un-post one charge (rows kept) and re-derive the asset.</summary>
    public async Task<FixedAsset?> ReverseDepreciationAsync(AssetDepreciation dep, int userId, CancellationToken ct = default)
    {
        var asset = await db.Set<FixedAsset>().FirstOrDefaultAsync(x => x.Id == dep.AssetId, ct);
        await ledger.ReverseAsync("FA-DEP", dep.Id, userId, ct);
        if (asset is not null)
            await RecalculateAsync(asset, ct);
        await db.SaveChangesAsync(ct);
        return asset;
    }

    /// <summary>_capitalised_from_stock: the asset entered the books through an approved capitalisation.</summary>
    public Task<bool> CapitalisedFromStockAsync(FixedAsset a, CancellationToken ct = default) =>
        db.Set<AssetTransfer>().AnyAsync(t => t.AssetId == a.Id && t.Direction == "to_fixed_asset" && t.Status == "approved", ct);

    public static DateTime Midnight(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);
}
