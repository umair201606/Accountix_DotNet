using Accountix.FixedAssets.Services;

namespace Accountix.FixedAssets.Controllers;

/// <summary>
/// Inventory → Fixed Assets (inventory_app/routes/transfers.py), under the
/// Inventory module: the same capitalisation vouchers Fixed Assets raises,
/// numbered INV-FA, at the cost the costing engine gives on approval.
/// </summary>
[Area("FixedAssets")]
[Module(Modules.Inventory)]
[Route("inventory/transfer-to-fa")]
public sealed class InventoryTransfersController(AccountixDbContext db, ILedger ledger, IStockCosting costing, Capitalisations caps,
                                                 AssetBooks books) : Controller
{
    private const string Section = "stock";
    private int UserId => HttpContext.Access()!.User.Id;

    [HttpGet("")]
    [Right(Section, Rights.View)]
    public async Task<IActionResult> List(CancellationToken ct) =>
        View(await db.Set<AssetTransfer>().AsNoTracking().Include(t => t.Asset)
            .Where(t => Capitalisations.InventoryDirections.Contains(t.Direction))
            .OrderByDescending(t => t.CreatedAt).ToListAsync(ct));

    [HttpGet("create")]
    [Right(Section, Rights.Create)]
    public async Task<IActionResult> Create(CancellationToken ct) => View("Form", await FormModelAsync(null, null, ct));

    [HttpPost("create")]
    [Right(Section, Rights.Create)]
    public async Task<IActionResult> CreatePost(CancellationToken ct)
    {
        var form = Request.Form;
        bool approve = form["status"].FirstOrDefault() == "approved";
        if (approve && this.Deny(Section, Rights.Approve) is { } refused) return refused;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        int pid = AssetsController.IntOrNull(form["product_id"].FirstOrDefault()) ?? 0;
        var product = pid == 0 ? null : await db.Set<InvProduct>().FirstOrDefaultAsync(p => p.Id == pid, ct);
        int cid = FormInput.Int(form, "category_id", 0);
        var category = cid == 0 ? null : await db.Set<FaCategory>().FirstOrDefaultAsync(c => c.Id == cid, ct);
        if (product is null || category is null)
            return await Refuse("Select a product and an asset category.");
        double qty = FormInput.Float(form, "quantity", 1);
        double salvage = FormInput.Float(form, "salvage_value", 0);
        if (qty <= 0 || salvage < 0)
            return await Refuse("Quantity must be positive and salvage value cannot be negative.");
        string name = (form["name"].FirstOrDefault() ?? "").Trim();
        if (name.Length == 0) name = product.Name;
        // The asset's cost is what the costing engine says the stock cost, set on approval.
        var transfer = await caps.NewAsync(product, qty, name, category, FormInput.Date(form, "purchase_date"), UserId, "INV-FA",
            new DraftAssetFields
            {
                UsefulLife = FormInput.Int(form, "useful_life", category.DefaultUsefulLife is int l && l != 0 ? l : 5),
                DepreciationMethod = form["depreciation_method"].FirstOrDefault(),
                SalvageValue = salvage,
                Description = form["description"].FirstOrDefault() ?? "",
                Location = form["location"].FirstOrDefault() ?? "",
                SerialNumber = form["serial_number"].FirstOrDefault() ?? "",
                Notes = form["notes"].FirstOrDefault() ?? "",
            }, ct);
        transfer.Description = $"Transfer from inventory: {name}";
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
                return await Refuse($"Posting failed: {e.Message}");
            }
        }
        await tx.CommitAsync(ct);
        this.Flash($"Transfer {transfer.VoucherNumber} {(approve ? "approved and " : "")}saved.", "success");
        return Redirect("/inventory/transfer-to-fa/");

        async Task<IActionResult> Refuse(string message)
        {
            this.Flash(message, "error");
            return View("Form", await FormModelAsync(null, null, ct));
        }
    }

    [HttpGet("{id:int}/edit")]
    [Right(Section, Rights.Edit)]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var transfer = await db.Set<AssetTransfer>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (transfer is null) return NotFound();
        if (EditRefusal(transfer) is { } refusal) return refusal;
        var asset = await db.Set<FixedAsset>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == transfer.AssetId, ct);
        return View("Form", await FormModelAsync(transfer, asset, ct));
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
        string back = $"/inventory/transfer-to-fa/{transfer.Id}/edit";
        if (form["description"].FirstOrDefault() is { Length: > 0 } desc)
            transfer.Description = desc;
        if (approve)
        {
            int productId = AssetsController.IntOrNull(form["product_id"].FirstOrDefault()) is int p && p != 0
                ? p : transfer.SourceProductId ?? 0;
            var product = productId == 0 ? null : await db.Set<InvProduct>().FirstOrDefaultAsync(x => x.Id == productId, ct);
            if (product is null || asset is null)
            {
                this.Flash("Select the stock item to capitalise.", "error");
                return Redirect(back);
            }
            transfer.ProductId = transfer.SourceProductId = product.Id;
            transfer.Direction = "to_fixed_asset";
            await db.SaveChangesAsync(ct);
            double qty = FormInput.Float(form, "quantity", 0);
            try
            {
                await caps.ApproveAsync(transfer, UserId, qty != 0 ? qty : null, ct);
            }
            catch (Exception e) when (e is InvalidOperationException or DbUpdateException)
            {
                await tx.RollbackAsync(ct);
                this.Flash($"Posting failed: {e.Message}", "error");
                return Redirect(back);
            }
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"Transfer {transfer.VoucherNumber} updated.", "success");
        return Redirect("/inventory/transfer-to-fa/");
    }

    [HttpPost("{id:int}/unapprove")]
    [Right(Section, Rights.Approve)]
    public async Task<IActionResult> Unapprove(int id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var transfer = await db.LockAsync<AssetTransfer>(id, ct);
        if (transfer is null) return NotFound();
        if (transfer.Status != "approved" || !Capitalisations.InventoryDirections.Contains(transfer.Direction))
            return ToList("Transfer is not an approved capitalisation.", "error");
        var asset = await db.Set<FixedAsset>().FirstOrDefaultAsync(a => a.Id == transfer.AssetId, ct);
        if (asset is not null && await books.LiveRows(asset.Id).AnyAsync(ct))
            return ToList("Reverse this asset's depreciation charges first.", "error");
        if (asset is not null && asset.Status != "active")
            return ToList("The asset is no longer in service (disposed or transferred); reverse that first.", "error");
        try
        {
            // Journal first (refuses a closed period), then the stock comes back;
            // "INV-FA" covers transfers approved before they moved stock.
            await ledger.ReverseAsync("FA-CAP", transfer.Id, UserId, ct);
            await ledger.ReverseAsync("INV-FA", transfer.Id, UserId, ct);
            await costing.ReverseVoucherStockAsync("FA-CAP", transfer.Id, UserId, ct);
        }
        catch (Exception e) when (e is InvalidOperationException or DbUpdateException)
        {
            await tx.RollbackAsync(ct);
            return ToList($"Reversal failed: {e.Message}", "error");
        }
        if (asset is not null)
        {
            // Kept as a draft: the voucher can be approved again.
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
        return ToList($"{transfer.VoucherNumber} unapproved and reversed.", "success");
    }

    [HttpPost("{id:int}/delete")]
    [Right(Section, Rights.Delete)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var transfer = await db.LockAsync<AssetTransfer>(id, ct);
        if (transfer is null) return NotFound();
        if (!Capitalisations.InventoryDirections.Contains(transfer.Direction))
            return ToList("That transfer belongs to Fixed Assets.", "error");
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

    private IActionResult ToList(string message, string category)
    {
        this.Flash(message, category);
        return Redirect("/inventory/transfer-to-fa/");
    }

    private IActionResult? EditRefusal(AssetTransfer t)
    {
        if (!Capitalisations.InventoryDirections.Contains(t.Direction))
            return ToList("That transfer belongs to Fixed Assets.", "error");
        if (t.Status == "approved")
            return ToList("Cannot edit an approved transfer.", "error");
        return null;
    }

    private async Task<InventoryTransferFormModel> FormModelAsync(AssetTransfer? transfer, FixedAsset? asset, CancellationToken ct) => new(
        transfer, asset,
        await db.Set<InvProduct>().AsNoTracking().Where(p => p.IsActive == true).OrderBy(p => p.Name).ToListAsync(ct),
        await db.Set<FaCategory>().AsNoTracking().Where(c => c.IsActive == true).OrderBy(c => c.Id).ToListAsync(ct));
}

public sealed record InventoryTransferFormModel(AssetTransfer? Transfer, FixedAsset? Asset, IReadOnlyList<InvProduct> Products,
                                                IReadOnlyList<FaCategory> Categories);
