namespace Accountix.FixedAssets.Services;

/// <summary>The optional asset fields a capitalisation form fills (new_capitalisation's **asset_fields).</summary>
public sealed record DraftAssetFields
{
    public int? UsefulLife { get; init; }
    public string? DepreciationMethod { get; init; }
    public double SalvageValue { get; init; }
    public string Description { get; init; } = "";
    public string Location { get; init; } = "";
    public string SerialNumber { get; init; } = "";
    public string Notes { get; init; } = "";
}

/// <summary>
/// Stock ↔ fixed asset (fixed_assets_app/routes/transfers.py): capitalisation
/// drafts and their approval through the costing engine, and the asset → stock
/// transfer at book value. Journals through ILedger, stock through IStockCosting,
/// both keyed by the transfer so unapproving reverses them together.
/// </summary>
public sealed class Capitalisations(AccountixDbContext db, ILedger ledger, IVoucherNumbers numbers, IStockCosting costing,
                                    AssetBooks books, CompanyClock clock)
{
    public static readonly string[] InventoryDirections = ["to_fixed_asset", "from_inventory"];

    /// <summary>_tdate: the transfer's own date (its journal, stock and catch-up depreciation post there).</summary>
    public DateOnly TransferDate(AssetTransfer t) => t.TransferDate ?? clock.Today;

    /// <summary>new_capitalisation: a capitalisation voucher and its asset, both drafts (never in service, never charged).</summary>
    public async Task<AssetTransfer> NewAsync(InvProduct product, double qty, string name, FaCategory category, DateOnly tdate,
                                              int userId, string voucherPrefix, DraftAssetFields f, CancellationToken ct = default)
    {
        int life = f.UsefulLife is int l && l != 0 ? l : category.DefaultUsefulLife is int cl && cl != 0 ? cl : 5;
        string method = !string.IsNullOrEmpty(f.DepreciationMethod) ? f.DepreciationMethod
            : !string.IsNullOrEmpty(category.DefaultDepreciationMethod) ? category.DefaultDepreciationMethod : AssetCalc.StraightLine;
        if (!AssetCalc.Methods.Contains(method))
            method = AssetCalc.StraightLine;
        var now = clock.UtcNow;
        var asset = new FixedAsset
        {
            AssetCode = await books.NextCodeAsync(ct),
            Name = name,
            Description = f.Description,
            CategoryId = category.Id,
            PurchaseDate = tdate,
            PurchaseCost = 0m,
            UsefulLife = life,
            DepreciationMethod = method,
            SalvageValue = Py.Dec(f.SalvageValue),
            AccumulatedDepreciation = 0m,
            CurrentBookValue = 0m,
            Status = "draft",
            Location = f.Location,
            AssignedTo = "",
            Vendor = "",
            SerialNumber = f.SerialNumber,
            Notes = f.Notes,
            IsActive = false,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Add(asset);
        await db.SaveChangesAsync(ct);
        var transfer = new AssetTransfer
        {
            VoucherNumber = await numbers.NextAsync(voucherPrefix, ct),
            Direction = "to_fixed_asset",
            AssetId = asset.Id,
            SourceProductId = product.Id,
            ProductId = product.Id,
            NewProductName = "",
            Quantity = Py.Dec(qty),
            TransferDate = tdate,
            TransferAmount = 0m,
            Description = $"Capitalise {product.Name} as {name}",
            Status = "unapproved",
            CreatedBy = userId,
            CreatedAt = now,
        };
        db.Add(transfer);
        await db.SaveChangesAsync(ct);
        return transfer;
    }

    /// <summary>approve_capitalisation: approve a draft (raised in either module) at what the stock cost.</summary>
    public async Task<FixedAsset> ApproveAsync(AssetTransfer t, int userId, double? qty = null, CancellationToken ct = default)
    {
        int pid = t.SourceProductId is int sp && sp != 0 ? sp : t.ProductId ?? 0;
        var product = await db.Set<InvProduct>().FirstOrDefaultAsync(p => p.Id == pid, ct);
        var asset = await db.Set<FixedAsset>().FirstOrDefaultAsync(a => a.Id == t.AssetId, ct);
        if (product is null || asset is null)
            throw new AssetRuleException("The stock item or the asset of this voucher no longer exists.");
        if (asset.Status is not ("draft" or "inactive"))
            throw new AssetRuleException("This asset is already in service.");
        double q = qty is double given && given != 0 ? given : t.Quantity is decimal tq && tq != 0 ? Py.F(tq) : 1;
        if (q <= 0)
            throw new AssetRuleException("Quantity must be positive.");
        t.Quantity = Py.Dec(q);
        t.TransferDate ??= asset.PurchaseDate;
        await CapitaliseFromStockAsync(t, product, asset, q, userId, ct);
        if (Py.F(asset.SalvageValue) >= Py.F(asset.PurchaseCost))
            throw new AssetRuleException($"Salvage value must be less than what the stock cost ({Py.Comma(Py.F(asset.PurchaseCost), 2)}).");
        return asset;
    }

    /// <summary>_capitalise_from_stock: issue the stock, book the asset at exactly what the issue cost (this is its acquisition).</summary>
    private async Task CapitaliseFromStockAsync(AssetTransfer t, InvProduct product, FixedAsset asset, double qty, int userId,
                                                CancellationToken ct)
    {
        var tdate = TransferDate(t);
        decimal issued = await costing.RecordOutAsync(product.Id, "FA-CAP", t.Id, t.VoucherNumber, Py.Dec(qty),
            $"Capitalised as fixed asset {asset.AssetCode}", userId, txnDate: AssetBooks.Midnight(tdate), ct: ct);
        double total = Py.F(issued);
        if (asset.FixedAssetAccountId is null)
        {
            var (fa, _) = await books.AssetAccountsAsync(asset, ct);
            asset.FixedAssetAccountId = fa.Id;
            await db.SaveChangesAsync(ct);
        }
        var stock = await ledger.EntityAccountAsync("product", product.Id, product.Name, ct);
        decimal amount = Py.Dec(total);
        await ledger.PostAsync("FA-CAP", t.Id, t.VoucherNumber, $"Capitalise {product.Name} as fixed asset {asset.AssetCode}",
        [
            new JournalLineInput(asset.FixedAssetAccountId.Value, amount, 0m, $"Fixed asset - {asset.Name}"),
            new JournalLineInput(stock.Id, 0m, amount, $"Transfer from stock - {product.Name}"),
        ], AssetBooks.Midnight(tdate), userId, ct);
        asset.PurchaseCost = amount;
        asset.PurchaseDate = tdate;
        asset.Status = "active";
        asset.IsActive = true;
        await books.RecalculateAsync(asset, ct);
        t.TransferAmount = amount;
        t.ApprovedBy = userId;
        t.ApprovedAt = clock.UtcNow;
        t.Status = "approved";
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// discard_draft_asset: deleting a draft capitalisation takes its never-booked
    /// asset with it; one that carried charges is kept, cancelled. True when the
    /// transfer itself was deleted here.
    /// </summary>
    public async Task<bool> DiscardDraftAssetAsync(AssetTransfer t, CancellationToken ct = default)
    {
        if (!InventoryDirections.Contains(t.Direction))
            return false;
        var asset = await db.Set<FixedAsset>().FirstOrDefaultAsync(a => a.Id == t.AssetId, ct);
        if (asset is null || asset.Status is not ("draft" or "inactive"))
            return false;
        if (await db.Set<AssetDepreciation>().AnyAsync(d => d.AssetId == asset.Id, ct))
        {
            asset.Status = "cancelled";
            asset.IsActive = false;
            await db.SaveChangesAsync(ct);
            return false;
        }
        db.Remove(t);
        await db.SaveChangesAsync(ct);
        db.Remove(asset);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// _approve_transfer: asset → stock at net book value: catch-up depreciation,
    /// the derecognition journal and one unit of stock, all keyed FA-TRF/transfer.
    /// </summary>
    public async Task ApproveToStockAsync(AssetTransfer t, FixedAsset? asset, int stockAccountId, string stockDescription, int userId,
                                          CancellationToken ct = default)
    {
        if (asset is null || asset.Status != "active")
            throw new AssetRuleException("Only an asset in service can be transferred to stock.");
        var tdate = TransferDate(t);
        if (await books.LiveRows(asset.Id).AnyAsync(d => d.EntryDate > tdate, ct))
            throw new AssetRuleException("Depreciation is posted after the transfer date; reverse it or date the transfer on or after the last charge.");
        await books.PostDepreciationAsync(asset, tdate, userId, ct: ct);
        await db.SaveChangesAsync(ct);
        var calc = await books.CalcAsync(asset, ct);
        double cost = calc.Cost, accum = calc.PostedDepreciation, netBook = cost - accum;

        if (asset.FixedAssetAccountId is null || asset.AccumDepAccountId is null)
        {
            var (fa, acc) = await books.AssetAccountsAsync(asset, ct);
            asset.FixedAssetAccountId ??= fa.Id;
            asset.AccumDepAccountId ??= acc.Id;
            await db.SaveChangesAsync(ct);
        }
        await ledger.PostAsync("FA-TRF", t.Id, t.VoucherNumber, $"Transfer to inventory: {asset.Name} at BV {Py.Comma(netBook)}",
        [
            new JournalLineInput(asset.AccumDepAccountId!.Value, Py.Dec(accum), 0m, $"Derecognise accum dep - {asset.Name}"),
            new JournalLineInput(stockAccountId, Py.Dec(netBook), 0m, $"Transfer to stock - {stockDescription}"),
            new JournalLineInput(asset.FixedAssetAccountId!.Value, 0m, Py.Dec(cost), $"Derecognise asset - {asset.Name}"),
        ], AssetBooks.Midnight(tdate), userId, ct);
        // One unit of stock carried at the book value, so the layer equals the debit.
        if (t.ProductId is int pid && pid != 0)
            await costing.RecordInAsync(pid, "FA-TRF", t.Id, t.VoucherNumber, 1m, Py.Dec(netBook),
                $"Transferred from fixed asset {asset.AssetCode}", userId, AssetBooks.Midnight(tdate), ct: ct);
        t.TransferAmount = Py.Dec(netBook);
        asset.Status = "transferred";
        asset.IsActive = false;
        await books.RecalculateAsync(asset, ct);
        t.ApprovedBy = userId;
        t.ApprovedAt = clock.UtcNow;
        t.Status = "approved";
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The stock account for an asset → stock transfer: an existing product's, or a new product named on the form.</summary>
    public async Task<(int? AccountId, string Description, int? ProductId)> StockTargetAsync(
        FixedAsset asset, int? productId, string newProductName, CancellationToken ct = default)
    {
        if (productId is int pid && pid != 0)
        {
            var prod = await db.Set<InvProduct>().FirstOrDefaultAsync(p => p.Id == pid, ct);
            if (prod is null)
                return (null, asset.Name, productId);
            var acct = await ledger.EntityAccountAsync("product", prod.Id, prod.Name, ct);
            return (acct.Id, prod.Name, productId);
        }
        if (newProductName.Length > 0)
        {
            var now = clock.UtcNow;
            var prod = new InvProduct
            {
                Name = newProductName, Sku = $"FA-{asset.AssetCode}", UnitPrice = 0m, CostPrice = 0m, CurrentStock = 0m,
                ReorderLevel = 0, Unit = "pcs", HsCode = "", Weight = 0m, IsActive = true, CreatedAt = now, UpdatedAt = now,
            };
            db.Add(prod);
            await db.SaveChangesAsync(ct);
            var acct = await ledger.EntityAccountAsync("product", prod.Id, prod.Name, ct);
            return (acct.Id, prod.Name, prod.Id);
        }
        return (null, asset.Name, null);
    }
}
