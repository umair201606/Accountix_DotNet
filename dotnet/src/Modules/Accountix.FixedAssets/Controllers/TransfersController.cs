using Accountix.FixedAssets.Services;

namespace Accountix.FixedAssets.Controllers;

/// <summary>
/// Stock ↔ asset vouchers (fixed_assets_app/routes/transfers.py): capitalisation
/// drafts from stock and their approval, asset → stock at book value, unapprove
/// and delete. Rights follow rights_guard's _status_form: saving straight to
/// approved also needs "approve".
/// </summary>
[Area("FixedAssets")]
[Module(Modules.FixedAssets)]
[Route("fixed-assets/transfers")]
public sealed class TransfersController(AccountixDbContext db, ILedger ledger, IStockCosting costing, IVoucherNumbers numbers,
                                        Capitalisations caps, AssetBooks books, CompanyClock clock) : Controller
{
    private const string Section = "fixed_assets_assets";
    private int UserId => HttpContext.Access()!.User.Id;

    [HttpGet("")]
    [Right(Section, Rights.View)]
    public async Task<IActionResult> List(CancellationToken ct) =>
        View(await db.Set<AssetTransfer>().AsNoTracking().Include(t => t.Asset)
            .OrderByDescending(t => t.CreatedAt).ToListAsync(ct));

    // ── Capitalise stock as a fixed asset ───────────────────────────────

    [HttpGet("capitalise")]
    [Right(Section, Rights.Create)]
    public async Task<IActionResult> Capitalise(CancellationToken ct) => View(await CapitaliseModelAsync(ct));

    [HttpPost("capitalise")]
    [Right(Section, Rights.Create)]
    public async Task<IActionResult> CapitalisePost(CancellationToken ct)
    {
        var form = Request.Form;
        bool approve = form["status"].FirstOrDefault() == "approved";
        if (approve && this.Deny(Section, Rights.Approve) is { } refused) return refused;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        int? productId = AssetsController.IntOrNull(form["source_product_id"].FirstOrDefault());
        var product = productId is int pid && pid != 0 ? await db.Set<InvProduct>().FirstOrDefaultAsync(p => p.Id == pid, ct) : null;
        double qty = FloatOrNull(form["quantity"].FirstOrDefault()) is double q && q != 0 ? q : 1;
        string name = (form["name"].FirstOrDefault() ?? "").Trim();
        int? categoryId = AssetsController.IntOrNull(form["category_id"].FirstOrDefault());
        var category = categoryId is int cid && cid != 0 ? await db.Set<FaCategory>().FirstOrDefaultAsync(c => c.Id == cid, ct) : null;
        if (product is null || name.Length == 0 || category is null)
            return await Refuse("Pick a stock item, an asset name and a category.");
        if (qty <= 0)
            return await Refuse("Quantity must be positive.");
        double salvage = FormInput.Float(form, "salvage_value", 0);
        if (salvage < 0)
            return await Refuse("Salvage value cannot be negative.");
        var transfer = await caps.NewAsync(product, qty, name, category, TransferDateFromForm(null), UserId, "FA-CAP", new DraftAssetFields
        {
            UsefulLife = AssetsController.IntOrNull(form["useful_life"].FirstOrDefault()),
            DepreciationMethod = form["depreciation_method"].FirstOrDefault(),
            SalvageValue = salvage,
            Location = form["location"].FirstOrDefault() ?? "",
            Notes = form["description"].FirstOrDefault() ?? "",
        }, ct);
        if (form["description"].FirstOrDefault() is { Length: > 0 } desc)
            transfer.Description = desc;
        await db.SaveChangesAsync(ct);
        if (approve)
        {
            try
            {
                await caps.ApproveAsync(transfer, UserId, qty, ct);
            }
            catch (Exception e) when (e is InvalidOperationException or DbUpdateException)
            {
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                return await Refuse($"Capitalisation failed: {e.Message}");
            }
        }
        await tx.CommitAsync(ct);
        this.Flash($"{transfer.VoucherNumber} saved.", "success");
        return Redirect("/fixed-assets/transfers/");

        async Task<IActionResult> Refuse(string message)
        {
            this.Flash(message, "error");
            return View("Capitalise", await CapitaliseModelAsync(ct));
        }
    }

    [HttpPost("{id:int}/approve-capitalisation")]
    [Right(Section, Rights.Approve)]
    public async Task<IActionResult> ApproveCapitalisation(int id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var transfer = await db.LockAsync<AssetTransfer>(id, ct);
        if (transfer is null) return NotFound();
        if (transfer.Status == "approved" || transfer.Direction != "to_fixed_asset")
            return ToList("Not a draft capitalisation.", "error");
        try
        {
            await caps.ApproveAsync(transfer, UserId, null, ct);
        }
        catch (Exception e) when (e is InvalidOperationException or DbUpdateException)
        {
            await tx.RollbackAsync(ct);
            return ToList($"Capitalisation failed: {e.Message}", "error");
        }
        await tx.CommitAsync(ct);
        return ToList($"{transfer.VoucherNumber} approved; the asset is in service.", "success");
    }

    [HttpPost("{id:int}/unapprove-capitalisation")]
    [Right(Section, Rights.Approve)]
    public async Task<IActionResult> UnapproveCapitalisation(int id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var transfer = await db.LockAsync<AssetTransfer>(id, ct);
        if (transfer is null) return NotFound();
        if (transfer.Status != "approved" || transfer.Direction != "to_fixed_asset")
            return ToList("Not an approved capitalisation.", "error");
        var asset = await db.Set<FixedAsset>().FirstOrDefaultAsync(a => a.Id == transfer.AssetId, ct);
        if (asset is not null && await books.LiveRows(asset.Id).AnyAsync(ct))
            return ToList("Reverse this asset's depreciation charges first.", "error");
        if (asset is not null && asset.Status != "active")
            return ToList("The asset is no longer in service (disposed or transferred); reverse that first.", "error");
        try
        {
            await costing.ReverseVoucherStockAsync("FA-CAP", transfer.Id, UserId, ct);
            await ledger.ReverseAsync("FA-CAP", transfer.Id, UserId, ct);
        }
        catch (Exception e) when (e is InvalidOperationException or DbUpdateException)
        {
            await tx.RollbackAsync(ct);
            return ToList($"Reversal failed: {e.Message}", "error");
        }
        if (asset is not null)
        {
            asset.Status = "draft";
            asset.IsActive = false;
            asset.PurchaseCost = 0m;
            await books.RecalculateAsync(asset, ct);
        }
        transfer.Status = "unapproved";
        transfer.ApprovedBy = null;
        transfer.ApprovedAt = null;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToList($"{transfer.VoucherNumber} unapproved; stock returned.", "success");
    }

    // ── Asset → stock ───────────────────────────────────────────────────

    [HttpGet("create")]
    [Right(Section, Rights.Create)]
    public async Task<IActionResult> Create([FromQuery(Name = "asset_id")] string? assetIdRaw, CancellationToken ct)
    {
        var asset = await ActiveAssetAsync(assetIdRaw, ct);
        if (asset is null)
            return NoAsset();
        return View("Form", await FormModelAsync(asset, null, ct));
    }

    [HttpPost("create")]
    [Right(Section, Rights.Create)]
    public async Task<IActionResult> CreatePost([FromQuery(Name = "asset_id")] string? assetIdRaw, CancellationToken ct)
    {
        var form = Request.Form;
        bool approve = form["status"].FirstOrDefault() == "approved";
        if (approve && this.Deny(Section, Rights.Approve) is { } refused) return refused;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var asset = await ActiveAssetAsync(assetIdRaw, ct, track: true);
        if (asset is null)
            return NoAsset();
        int? productId = AssetsController.IntOrNull(form["product_id"].FirstOrDefault());
        string newName = (form["new_product_name"].FirstOrDefault() ?? "").Trim();
        int? stockAccountId;
        string stockDescription;
        try
        {
            (stockAccountId, stockDescription, productId) = await caps.StockTargetAsync(asset, productId, newName, ct);
        }
        catch (DbUpdateException e) when (productId is null or 0)
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            this.Flash($"Could not create product: {e.InnerException?.Message ?? e.Message}", "error");
            return View("Form", await FormModelAsync(asset, null, ct));
        }
        if (stockAccountId is null)
        {
            // A stock debit with no stock item would put value in the GL no layer carries.
            this.Flash("Pick the stock item the asset becomes, or name a new one.", "error");
            return View("Form", await FormModelAsync(asset, null, ct));
        }
        var calc = await books.CalcAsync(asset, ct);
        string number = await numbers.NextAsync("FA-TRF", ct);
        var transfer = new AssetTransfer
        {
            VoucherNumber = number,
            Direction = "to_inventory",
            AssetId = asset.Id,
            ProductId = productId,
            NewProductName = newName,
            TransferAmount = Py.Dec(calc.NetBookValue),
            Quantity = 1m,
            Description = form.ContainsKey("description") ? form["description"].FirstOrDefault() ?? ""
                : $"Transfer to inventory: {stockDescription}",
            TransferDate = TransferDateFromForm(null),
            Status = "unapproved",
            CreatedBy = UserId,
            CreatedAt = clock.UtcNow,
        };
        db.Add(transfer);
        await db.SaveChangesAsync(ct);
        if (approve)
        {
            try
            {
                await caps.ApproveToStockAsync(transfer, asset, stockAccountId.Value, stockDescription, UserId, ct);
            }
            catch (Exception e) when (e is InvalidOperationException or DbUpdateException)
            {
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                this.Flash($"Posting failed: {e.Message}", "error");
                return View("Form", await FormModelAsync(await ActiveAssetAsync(assetIdRaw, ct), null, ct));
            }
        }
        await tx.CommitAsync(ct);
        this.Flash($"Transfer {number} {(approve ? "approved and " : "")}saved.", "success");
        return Redirect("/fixed-assets/transfers/");
    }

    [HttpGet("{id:int}/edit")]
    [Right(Section, Rights.Edit)]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var transfer = await db.Set<AssetTransfer>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (transfer is null) return NotFound();
        if (EditRefusal(transfer) is { } refusal) return refusal;
        var asset = await db.Set<FixedAsset>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == transfer.AssetId, ct);
        return View("Form", await FormModelAsync(asset, transfer, ct));
    }

    [HttpPost("{id:int}/edit")]
    [Right(Section, Rights.Edit)]
    public async Task<IActionResult> EditPost(int id, CancellationToken ct)
    {
        var form = Request.Form;
        bool approve = form["status"].FirstOrDefault() == "approved";
        if (approve && this.Deny(Section, Rights.Approve) is { } refused) return refused;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var transfer = await db.LockAsync<AssetTransfer>(id, ct);
        if (transfer is null) return NotFound();
        if (EditRefusal(transfer) is { } refusal) return refusal;
        var asset = await db.Set<FixedAsset>().FirstOrDefaultAsync(a => a.Id == transfer.AssetId, ct);
        transfer.Description = form["description"].FirstOrDefault() ?? "";
        transfer.TransferDate = TransferDateFromForm(transfer.TransferDate);
        transfer.NewProductName = (form["new_product_name"].FirstOrDefault() ?? "").Trim();
        int? productId = AssetsController.IntOrNull(form["product_id"].FirstOrDefault());
        transfer.ProductId = productId;
        if (approve)
        {
            int? stockAccountId = null;
            string stockDescription = asset?.Name ?? "";
            if (asset is not null)
            {
                try
                {
                    (stockAccountId, stockDescription, int? pid) = await caps.StockTargetAsync(asset, productId, transfer.NewProductName, ct);
                    transfer.ProductId = pid;
                }
                catch (DbUpdateException e) when (productId is null or 0)
                {
                    await tx.RollbackAsync(ct);
                    db.ChangeTracker.Clear();
                    this.Flash($"Could not create product: {e.InnerException?.Message ?? e.Message}", "error");
                    return View("Form", await FormModelAsync(asset, null, ct));
                }
            }
            if (stockAccountId is null)
            {
                this.Flash("Pick the stock item the asset becomes, or name a new one.", "error");
                return View("Form", await FormModelAsync(asset, transfer, ct));
            }
            await db.SaveChangesAsync(ct);
            try
            {
                await caps.ApproveToStockAsync(transfer, asset, stockAccountId.Value, stockDescription, UserId, ct);
            }
            catch (Exception e) when (e is InvalidOperationException or DbUpdateException)
            {
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                this.Flash($"Posting failed: {e.Message}", "error");
                var fresh = await db.Set<FixedAsset>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == transfer.AssetId, ct);
                return View("Form", await FormModelAsync(fresh, null, ct));
            }
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToList($"Transfer {transfer.VoucherNumber} updated.", "success");
    }

    [HttpPost("{id:int}/unapprove")]
    [Right(Section, Rights.Approve)]
    public async Task<IActionResult> Unapprove(int id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var transfer = await db.LockAsync<AssetTransfer>(id, ct);
        if (transfer is null) return NotFound();
        if (transfer.Status != "approved")
            return ToList("Transfer is not approved.", "error");
        var asset = await db.Set<FixedAsset>().FirstOrDefaultAsync(a => a.Id == transfer.AssetId, ct);
        // Stock first: a transferred unit already sold or used is refused before anything else moves.
        try
        {
            await costing.ReverseVoucherStockAsync("FA-TRF", transfer.Id, UserId, ct);
            await ledger.ReverseAsync("FA-TRF", transfer.Id, UserId, ct);
        }
        catch (Exception e) when (e is InvalidOperationException or DbUpdateException)
        {
            await tx.RollbackAsync(ct);
            return ToList($"Reversal failed: {e.Message}", "error");
        }
        if (asset is not null)
        {
            asset.Status = "active";
            asset.IsActive = true;
            await books.RecalculateAsync(asset, ct);
        }
        transfer.Status = "unapproved";
        transfer.ApprovedBy = null;
        transfer.ApprovedAt = null;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToList($"{transfer.VoucherNumber} unapproved and reversed.", "success");
    }

    [HttpPost("{id:int}/delete")]
    [Right(Section, Rights.Delete)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var transfer = await db.LockAsync<AssetTransfer>(id, ct);
        if (transfer is null) return NotFound();
        if (transfer.Status == "approved")
            return ToList("Cannot delete an approved transfer. Unapprove it first.", "error");
        string number = transfer.VoucherNumber;
        if (!await caps.DiscardDraftAssetAsync(transfer, ct))
        {
            db.Remove(transfer);
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return ToList($"Transfer {number} deleted.", "success");
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private IActionResult ToList(string message, string category)
    {
        this.Flash(message, category);
        return Redirect("/fixed-assets/transfers/");
    }

    private IActionResult? EditRefusal(AssetTransfer t)
    {
        if (t.Status == "approved")
            return ToList("Cannot edit an approved transfer.", "error");
        // This form posts asset → stock; a draft capitalisation goes through Approve on the list.
        if (t.Direction != "to_inventory")
            return ToList("Approve a capitalisation from the transfer list.", "error");
        return null;
    }

    private IActionResult NoAsset()
    {
        this.Flash("Select an active asset to transfer.", "error");
        return Redirect("/fixed-assets/assets/");
    }

    private async Task<FixedAsset?> ActiveAssetAsync(string? raw, CancellationToken ct, bool track = false)
    {
        if (AssetsController.IntOrNull(raw) is not int aid || aid == 0)
            return null;
        var q = track ? db.Set<FixedAsset>() : db.Set<FixedAsset>().AsNoTracking();
        var asset = await q.FirstOrDefaultAsync(a => a.Id == aid, ct);
        return asset is { Status: "active" } ? asset : null;
    }

    /// <summary>_form_tdate: the form's transfer date; blank keeps the fallback, then today.</summary>
    private DateOnly TransferDateFromForm(DateOnly? fallback) =>
        DateOnly.FromDateTime(DocumentDates.Parse(Request.Form["transfer_date"].FirstOrDefault(),
                                                  fallback?.ToDateTime(TimeOnly.MinValue), clock));

    internal static double? FloatOrNull(string? raw) =>
        double.TryParse((raw ?? "").Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)
            ? v : null;

    private async Task<CapitaliseModel> CapitaliseModelAsync(CancellationToken ct) => new(
        await db.Set<InvProduct>().AsNoTracking().Where(p => p.IsActive == true).OrderBy(p => p.Name).ToListAsync(ct),
        await db.Set<FaCategory>().AsNoTracking().Where(c => c.IsActive == true).OrderBy(c => c.Name).ToListAsync(ct));

    private async Task<TransferFormModel> FormModelAsync(FixedAsset? asset, AssetTransfer? transfer, CancellationToken ct)
    {
        var calc = asset is null ? null : await books.CalcAsync(asset, ct);
        var products = await db.Set<InvProduct>().AsNoTracking().Where(p => p.IsActive == true).OrderBy(p => p.Name).ToListAsync(ct);
        return new TransferFormModel(calc, transfer, products);
    }
}

public sealed record CapitaliseModel(IReadOnlyList<InvProduct> Products, IReadOnlyList<FaCategory> Categories);

public sealed record TransferFormModel(AssetCalc? Calc, AssetTransfer? Transfer, IReadOnlyList<InvProduct> Products);
