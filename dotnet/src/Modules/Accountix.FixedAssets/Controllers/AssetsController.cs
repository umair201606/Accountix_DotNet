using Accountix.FixedAssets.Services;

namespace Accountix.FixedAssets.Controllers;

/// <summary>Assets: list, create / edit rules, the asset page, manual charges, disposal (fixed_assets_app/routes/assets.py).</summary>
[Area("FixedAssets")]
[Module(Modules.FixedAssets)]
[Route("fixed-assets/assets")]
public sealed class AssetsController(AccountixDbContext db, ILedger ledger, AssetBooks books, CompanyClock clock) : Controller
{
    private const string Section = "fixed_assets_assets";
    private int UserId => HttpContext.Access()!.User.Id;

    // ── List ────────────────────────────────────────────────────────────

    [HttpGet("")]
    [Right(Section, Rights.View)]
    public async Task<IActionResult> List([FromQuery] string? q, [FromQuery] string? status, [FromQuery(Name = "category_id")] string? categoryId,
                                          CancellationToken ct)
    {
        q = (q ?? "").Trim();
        var query = db.Set<FixedAsset>().AsNoTracking().Where(a => a.IsActive == true);
        if (q.Length > 0)
            query = query.Where(a => EF.Functions.ILike(a.Name, $"%{q}%") || EF.Functions.ILike(a.AssetCode, $"%{q}%"));
        if (!string.IsNullOrEmpty(status))
            query = query.Where(a => a.Status == status);
        if (!string.IsNullOrEmpty(categoryId) && int.TryParse(categoryId, out int cid))
            query = query.Where(a => a.CategoryId == cid);
        var assets = await query.Include(a => a.Category).OrderByDescending(a => a.CreatedAt).ToListAsync(ct);
        var live = await books.LiveByAssetAsync(query.Select(a => a.Id), ct);
        return View(new AssetListModel
        {
            Assets = assets.Select(a => new AssetCalc(a, live.GetValueOrDefault(a.Id, LiveCharges.None))).ToList(),
            Categories = await ActiveCategoriesAsync(ct),
            Q = q, StatusFilter = status ?? "", CategoryFilter = categoryId ?? "",
        });
    }

    // ── Create ──────────────────────────────────────────────────────────

    [HttpGet("create")]
    [Right(Section, Rights.Create)]
    public async Task<IActionResult> Create(CancellationToken ct) => View("Form", await FormModelAsync(null, ct));

    [HttpPost("create")]
    [Right(Section, Rights.Create)]
    public async Task<IActionResult> CreatePost(CancellationToken ct)
    {
        var form = Request.Form;
        string name = (form["name"].FirstOrDefault() ?? "").Trim();
        if (name.Length == 0)
        {
            this.Flash("Asset name is required.", "error");
            return View("Form", await FormModelAsync(null, ct));
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        int categoryId = FormInput.Int(form, "category_id", 0);
        var category = await CategoryAsync(categoryId, ct);
        var purchaseDate = FormInput.Date(form, "purchase_date", null, "Purchase date");
        var (cost, life, salvage, method) = ReadTerms(form, category);
        var choices = await books.ChoicesAsync(ct);
        int faId = AssetBooks.Mapped(form, "fixed_asset_account_id", choices.Fa, label: "fixed-asset account")
                   ?? (await ledger.PostingAccountAsync("fixed_assets", ct)).Id;
        var now = clock.UtcNow;
        var asset = new FixedAsset
        {
            AssetCode = await books.NextCodeAsync(ct),
            Name = name,
            Description = form["description"].FirstOrDefault() ?? "",
            CategoryId = categoryId,
            PurchaseDate = purchaseDate,
            PurchaseCost = Py.Dec(cost),
            UsefulLife = life,
            DepreciationMethod = method,
            SalvageValue = Py.Dec(salvage),
            AccumulatedDepreciation = 0m,
            CurrentBookValue = Py.Dec(cost),
            Status = "active",
            Location = form["location"].FirstOrDefault() ?? "",
            AssignedTo = form["assigned_to"].FirstOrDefault() ?? "",
            Vendor = form["vendor"].FirstOrDefault() ?? "",
            SerialNumber = form["serial_number"].FirstOrDefault() ?? "",
            LabelId = IntOrNull(form["label_id"].FirstOrDefault()),
            FixedAssetAccountId = faId,
            AccumDepAccountId = AssetBooks.Mapped(form, "accum_dep_account_id", choices.Accum, label: "accumulated depreciation account"),
            DepExpenseAccountId = AssetBooks.Mapped(form, "dep_expense_account_id", choices.Expense, label: "expense account"),
            Notes = form["notes"].FirstOrDefault() ?? "",
            AcquisitionCreditAccountId = AssetBooks.Mapped(form, "acquisition_credit_account_id", choices.Credit, label: "credit account"),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Add(asset);
        await db.SaveChangesAsync(ct);
        try
        {
            await books.PostAcquisitionAsync(asset, UserId, ct: ct);
        }
        catch (Exception e) when (e is InvalidOperationException or DbUpdateException)
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            this.Flash($"Asset not created — acquisition entry failed: {e.Message}", "error");
            return View("Form", await FormModelAsync(null, ct));
        }
        await books.RecalculateAsync(asset, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"Asset '{name}' created and booked to the ledger.", "success");
        return Redirect("/fixed-assets/assets/");
    }

    // ── The asset page ──────────────────────────────────────────────────

    [HttpGet("{asset_id:int}")]
    [Right(Section, Rights.View)]
    public async Task<IActionResult> ViewAsset([FromRoute(Name = "asset_id")] int assetId, CancellationToken ct)
    {
        var asset = await db.Set<FixedAsset>().AsNoTracking().Include(a => a.Category).FirstOrDefaultAsync(a => a.Id == assetId, ct);
        if (asset is null) return NotFound();
        var entries = await db.Set<AssetDepreciation>().AsNoTracking().Where(d => d.AssetId == asset.Id)
            .OrderByDescending(d => d.EntryDate).ThenByDescending(d => d.Id).ToListAsync(ct);
        var jids = entries.Where(e => e.JournalEntryId is not null).Select(e => e.JournalEntryId!.Value).ToList();
        var posted = (await db.Set<JournalEntry>().Where(e => jids.Contains(e.Id) && e.IsPosted == true).Select(e => e.Id).ToListAsync(ct)).ToHashSet();
        var transfers = await db.Set<AssetTransfer>().AsNoTracking().Where(t => t.AssetId == asset.Id).OrderBy(t => t.Id)
            .Select(t => new { t.Id, t.Direction }).ToListAsync(ct);
        var calc = await books.CalcAsync(asset, ct);
        var also = entries.Select(e => $"FA-DEP:{e.Id}").Append($"FA-DISP:{asset.Id}")
            .Concat(transfers.Select(t => $"{(t.Direction != "to_inventory" ? "FA-CAP" : "FA-TRF")}:{t.Id}")).ToList();
        return View("View", new AssetViewModel
        {
            Calc = calc,
            Entries = entries.Select(e => (e, e.JournalEntryId is not int j || posted.Contains(j))).ToList(),
            Choices = await books.ChoicesAsync(ct),
            Schedule = calc.Schedule(),
            GlAlso = also,
        });
    }

    // ── Edit ────────────────────────────────────────────────────────────

    [HttpGet("{asset_id:int}/edit")]
    [Right(Section, Rights.Edit)]
    public async Task<IActionResult> Edit([FromRoute(Name = "asset_id")] int assetId, CancellationToken ct)
    {
        var asset = await db.Set<FixedAsset>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == assetId, ct);
        if (asset is null) return NotFound();
        return View("Form", await FormModelAsync(asset, ct));
    }

    [HttpPost("{asset_id:int}/edit")]
    [Right(Section, Rights.Edit)]
    public async Task<IActionResult> EditPost([FromRoute(Name = "asset_id")] int assetId, CancellationToken ct)
    {
        var form = Request.Form;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var asset = await db.LockAsync<FixedAsset>(assetId, ct);
        if (asset is null) return NotFound();
        // Descriptive fields are always editable.
        string name = (form["name"].FirstOrDefault() ?? "").Trim();
        if (name.Length == 0)
            throw new FormInputException("Asset name is required.");
        asset.Name = name;
        asset.Description = form["description"].FirstOrDefault() ?? "";
        asset.Location = form["location"].FirstOrDefault() ?? "";
        asset.AssignedTo = form["assigned_to"].FirstOrDefault() ?? "";
        asset.Vendor = form["vendor"].FirstOrDefault() ?? "";
        asset.SerialNumber = form["serial_number"].FirstOrDefault() ?? "";
        asset.Notes = form["notes"].FirstOrDefault() ?? "";
        if (asset.Status != "active")
        {
            // Out of the books: cost, life and accounts stay as the disposal left them.
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            this.Flash("Details updated. Cost, life and accounts are locked once an asset is out of service.", "success");
            return Redirect($"/fixed-assets/assets/{asset.Id}");
        }

        var category = await CategoryAsync(FormInput.Int(form, "category_id", 0), ct);
        var purchaseDate = FormInput.Date(form, "purchase_date", null, "Purchase date");
        var (cost, life, salvage, method) = ReadTerms(form, category);
        var live = await books.LiveAsync(asset.Id, ct);
        double posted = new AssetCalc(asset, live).PostedDepreciation;
        bool capitalised = await books.CapitalisedFromStockAsync(asset, ct);
        if (capitalised && (Py.Round(cost, 2) != Py.Round(Py.F(asset.PurchaseCost), 2) || purchaseDate != asset.PurchaseDate))
            throw new FormInputException("This asset was capitalised from stock: its cost and date come from that transfer. " +
                                         "Unapprove the transfer to change them.");
        if (live.First is DateOnly first && purchaseDate > first)
            throw new FormInputException($"Depreciation is already posted from {Py.DateLong(first)}; the purchase date cannot be later than that.");
        if (posted != 0 && cost - salvage < posted - 0.005)
            throw new FormInputException($"{Py.Comma(posted, 2)} of depreciation is already posted; cost less salvage cannot be below that.");
        var choices = await books.ChoicesAsync(ct);
        // Blank = automatic: keep the account the asset already uses.
        int? newAccum = AssetBooks.Mapped(form, "accum_dep_account_id", choices.Accum, asset.AccumDepAccountId, "accumulated depreciation account");
        if (posted != 0 && newAccum != asset.AccumDepAccountId)
            throw new FormInputException("Accumulated depreciation is already posted to this asset's account; it cannot be moved to another.");

        var prior = (asset.PurchaseCost, asset.PurchaseDate, asset.AcquisitionCreditAccountId, asset.FixedAssetAccountId, asset.LabelId);
        asset.CategoryId = category!.Id;
        asset.PurchaseDate = purchaseDate;
        asset.PurchaseCost = Py.Dec(cost);
        asset.UsefulLife = life;
        asset.DepreciationMethod = method;
        asset.SalvageValue = Py.Dec(salvage);
        asset.FixedAssetAccountId = AssetBooks.Mapped(form, "fixed_asset_account_id", choices.Fa, asset.FixedAssetAccountId, "fixed-asset account");
        asset.AccumDepAccountId = newAccum;
        asset.DepExpenseAccountId = AssetBooks.Mapped(form, "dep_expense_account_id", choices.Expense, label: "expense account");
        asset.LabelId = IntOrNull(form["label_id"].FirstOrDefault());
        asset.AcquisitionCreditAccountId = AssetBooks.Mapped(form, "acquisition_credit_account_id", choices.Credit,
                                                             asset.AcquisitionCreditAccountId, "credit account");
        // Life / salvage / method changes are prospective (the engine re-plans);
        // anything the acquisition journal was built from re-posts it.
        var after = (asset.PurchaseCost, asset.PurchaseDate, asset.AcquisitionCreditAccountId, asset.FixedAssetAccountId, asset.LabelId);
        if (!capitalised && after != prior)
        {
            await db.SaveChangesAsync(ct);
            try
            {
                await ledger.ReverseAsync("FA-ACQ", asset.Id, UserId, ct);
                await books.PostAcquisitionAsync(asset, UserId, ct: ct);
            }
            catch (Exception e) when (e is InvalidOperationException or DbUpdateException)
            {
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                this.Flash($"Could not restate the acquisition entry: {e.Message}", "error");
                return Redirect($"/fixed-assets/assets/{assetId}/edit");
            }
        }
        await books.RecalculateAsync(asset, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash("Asset updated successfully.", "success");
        return Redirect($"/fixed-assets/assets/{asset.Id}");
    }

    // ── Manual charge, disposal ─────────────────────────────────────────

    [HttpPost("{asset_id:int}/depreciate")]
    [Right(Section, Rights.Approve)]
    public async Task<IActionResult> RecordDepreciation([FromRoute(Name = "asset_id")] int assetId, CancellationToken ct)
    {
        var form = Request.Form;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var asset = await db.LockAsync<FixedAsset>(assetId, ct);
        if (asset is null) return NotFound();
        string back = $"/fixed-assets/assets/{asset.Id}";
        if (asset.Status != "active")
            return Back("Only an asset in service can be depreciated.");
        var entryDate = FormInput.Date(form, "entry_date");
        double amount = FormInput.Float(form, "amount", 0);
        var calc = await books.CalcAsync(asset, ct);
        if (entryDate < asset.PurchaseDate || (calc.Live.Last is DateOnly last && entryDate < last))
            return Back("A manual charge must be dated on or after the purchase date and the last posted charge.");
        if (amount <= 0)
            return Back("Depreciation amount must be positive.");
        if (amount > calc.RemainingDepreciable)
            return Back($"Only {Py.Comma(calc.RemainingDepreciable, 2)} remains depreciable before the salvage value of {Py.Comma(calc.Salvage, 2)}.");
        // IAS 36: an impairment is its own loss, through the same contra account.
        bool impairment = form["kind"].FirstOrDefault() == "impairment";
        int? expenseId = impairment
            ? (await ledger.GetOrCreateAccountAsync("5710", "Impairment Loss on Fixed Assets", "expense", null, ct)).Id
            : null;
        string note = (form["notes"].FirstOrDefault() ?? "").Trim();
        if (note.Length > 150) note = note[..150];
        string? notes = impairment ? "Impairment" + (note.Length > 0 ? $": {note}" : "") : note.Length > 0 ? note : null;
        double charged;
        try
        {
            charged = await books.PostDepreciationAsync(asset, entryDate, UserId, amount, expenseId, notes, ct);
        }
        catch (Exception e) when (e is InvalidOperationException or DbUpdateException)
        {
            await tx.RollbackAsync(ct);
            return Back($"Depreciation entry failed: {e.Message}");
        }
        if (charged == 0)
            return Back("Nothing left to depreciate on this asset.");
        await tx.CommitAsync(ct);
        this.Flash($"Depreciation of {Py.Comma(charged, 2)} recorded for '{asset.Name}'.", "success");
        return Redirect(back);

        IActionResult Back(string message)
        {
            this.Flash(message, "error");
            return Redirect(back);
        }
    }

    [HttpPost("{asset_id:int}/dispose")]
    [Right(Section, Rights.Approve)]
    public async Task<IActionResult> DisposeAsset([FromRoute(Name = "asset_id")] int assetId, CancellationToken ct)
    {
        var form = Request.Form;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var asset = await db.LockAsync<FixedAsset>(assetId, ct);
        if (asset is null) return NotFound();
        string back = $"/fixed-assets/assets/{asset.Id}";
        if (asset.FixedAssetAccountId is null)
        {
            var (fa, _) = await books.AssetAccountsAsync(asset, ct);
            asset.FixedAssetAccountId = fa.Id;
            await db.SaveChangesAsync(ct);
        }
        if (asset.AccumDepAccountId is null)
        {
            var (_, accum) = await books.AssetAccountsAsync(asset, ct);
            asset.AccumDepAccountId = accum.Id;
            await db.SaveChangesAsync(ct);
        }
        if (asset.Status == "disposed")
            return Back("This asset is already disposed.");
        var disposalDate = DateOnly.FromDateTime(DocumentDates.Parse(form["disposal_date"].FirstOrDefault(), null, clock));
        if (disposalDate < asset.PurchaseDate)
            return Back("Disposal date cannot be before the purchase date.");
        if (asset.Status != "active")
            return Back("Only an asset in service can be disposed.");
        if (await books.LiveRows(asset.Id).AnyAsync(d => d.EntryDate > disposalDate, ct))
            return Back("Depreciation is posted after that date. Reverse those charges, or dispose on or after the last one.");
        // Depreciate up to the disposal date first: that charge belongs in P&L as depreciation.
        double proceeds = FormInput.Float(form, "proceeds", 0);
        if (proceeds < 0)
            return Back("Proceeds cannot be negative.");
        await books.PostDepreciationAsync(asset, disposalDate, UserId, ct: ct);
        await db.SaveChangesAsync(ct);
        var calc = await books.CalcAsync(asset, ct);
        double cost = calc.Cost;
        double accumDep = calc.PostedDepreciation;
        double netBook = cost - accumDep;
        int proceedsId = AssetBooks.Mapped(form, "proceeds_account_id", (await books.ChoicesAsync(ct)).Proceeds, label: "account for the proceeds")
                         ?? (await ledger.PostingAccountAsync("cash", ct)).Id;
        var lines = new List<JournalLineInput>
        {
            new(asset.AccumDepAccountId!.Value, Py.Dec(accumDep), 0m, $"Write-off accumulated depreciation - {asset.Name}"),
            new(asset.FixedAssetAccountId!.Value, 0m, Py.Dec(cost), $"Asset disposal - {asset.Name}"),
        };
        if (proceeds != 0)
            lines.Add(new(proceedsId, Py.Dec(proceeds), 0m, $"Proceeds on disposal - {asset.Name}"));
        // Sale price against what the books still carry; scrapping is proceeds = 0.
        double result = Py.Round(proceeds - netBook, 2);
        if (result != 0)
        {
            var gainLoss = await ledger.PostingAccountAsync("disposal_gain_loss", ct);
            lines.Add(new(gainLoss.Id, result < 0 ? Py.Dec(Math.Abs(result)) : 0m, result > 0 ? Py.Dec(result) : 0m,
                $"{(result > 0 ? "Gain" : "Loss")} on disposal - {asset.Name}"));
        }
        asset.Status = "disposed";
        asset.IsActive = false;
        await db.SaveChangesAsync(ct);
        try
        {
            await ledger.PostAsync("FA-DISP", asset.Id, $"FA-DISP-{asset.AssetCode}", $"Disposal of {asset.Name}", lines,
                AssetBooks.Midnight(disposalDate), UserId, ct);
        }
        catch (Exception e) when (e is InvalidOperationException or DbUpdateException)
        {
            await tx.RollbackAsync(ct);
            return Back($"Disposal entry failed: {e.Message}");
        }
        await tx.CommitAsync(ct);
        string outcome = result == 0 ? "" : $" {(result > 0 ? "Gain" : "Loss")} of {Py.Comma(Math.Abs(result), 2)} booked.";
        this.Flash($"Asset '{asset.Name}' has been disposed.{outcome}", "success");
        return Redirect("/fixed-assets/assets/");

        IActionResult Back(string message)
        {
            this.Flash(message, "error");
            return Redirect(back);
        }
    }

    [HttpPost("{asset_id:int}/undo-disposal")]
    [Right(Section, Rights.Approve)]
    public async Task<IActionResult> UndoDisposal([FromRoute(Name = "asset_id")] int assetId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var asset = await db.LockAsync<FixedAsset>(assetId, ct);
        if (asset is null) return NotFound();
        string back = $"/fixed-assets/assets/{asset.Id}";
        if (asset.Status != "disposed")
        {
            this.Flash("This asset is not disposed.", "error");
            return Redirect(back);
        }
        try
        {
            await ledger.ReverseAsync("FA-DISP", asset.Id, UserId, ct);
        }
        catch (Exception e) when (e is InvalidOperationException or DbUpdateException)
        {
            await tx.RollbackAsync(ct);
            this.Flash($"Could not reverse the disposal: {e.Message}", "error");
            return Redirect(back);
        }
        asset.Status = "active";
        asset.IsActive = true;
        await books.RecalculateAsync(asset, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"Disposal of '{asset.Name}' reversed; book value is back to {Py.Comma(Py.F(asset.CurrentBookValue), 2)}.", "success");
        return Redirect(back);
    }

    // ── helpers ─────────────────────────────────────────────────────────

    /// <summary>_read_terms: cost, life, salvage and method, checked as an auditor would.</summary>
    internal static (double Cost, int Life, double Salvage, string Method) ReadTerms(IFormCollection form, FaCategory? category)
    {
        if (category is null)
            throw new FormInputException("Choose a category.");
        double cost = FormInput.Float(form, "purchase_cost", 0);
        int life = FormInput.Int(form, "useful_life", category.DefaultUsefulLife is int l && l != 0 ? l : 5);
        double salvage = FormInput.Float(form, "salvage_value", 0);
        string? method = form["depreciation_method"].FirstOrDefault();
        if (string.IsNullOrEmpty(method)) method = category.DefaultDepreciationMethod;
        if (cost <= 0)
            throw new FormInputException("Purchase cost must be more than zero.");
        if (life is < 1 or > 100)
            throw new FormInputException("Useful life must be between 1 and 100 years.");
        if (salvage < 0 || salvage >= cost)
            throw new FormInputException("Salvage value must be zero or more and less than the cost.");
        if (method is null || !AssetCalc.Methods.Contains(method))
            throw new FormInputException("Choose straight line or reducing balance.");
        return (Py.Round(cost, 2), life, Py.Round(salvage, 2), method);
    }

    internal static int? IntOrNull(string? raw) => int.TryParse(raw, out int v) ? v : null;

    private async Task<FaCategory?> CategoryAsync(int id, CancellationToken ct) =>
        id == 0 ? null : await db.Set<FaCategory>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);

    private Task<List<FaCategory>> ActiveCategoriesAsync(CancellationToken ct) =>
        db.Set<FaCategory>().AsNoTracking().Where(c => c.IsActive == true).OrderBy(c => c.Id).ToListAsync(ct);

    private async Task<AssetFormModel> FormModelAsync(FixedAsset? asset, CancellationToken ct) => new()
    {
        Asset = asset,
        Categories = await ActiveCategoriesAsync(ct),
        Choices = await books.ChoicesAsync(ct),
        Labels = await db.Set<ProjectLabel>().AsNoTracking().Where(l => l.IsActive == true).OrderBy(l => l.Name).ToListAsync(ct),
    };
}

public sealed class AssetListModel
{
    public IReadOnlyList<AssetCalc> Assets { get; init; } = [];
    public IReadOnlyList<FaCategory> Categories { get; init; } = [];
    public string Q { get; init; } = "";
    public string StatusFilter { get; init; } = "";
    public string CategoryFilter { get; init; } = "";
}

public sealed class AssetFormModel
{
    public FixedAsset? Asset { get; init; }
    public IReadOnlyList<FaCategory> Categories { get; init; } = [];
    public AccountChoices? Choices { get; init; }
    public IReadOnlyList<ProjectLabel> Labels { get; init; } = [];
}

public sealed class AssetViewModel
{
    public required AssetCalc Calc { get; init; }
    public FixedAsset Asset => Calc.Asset;
    public IReadOnlyList<(AssetDepreciation Entry, bool Live)> Entries { get; init; } = [];
    public required AccountChoices Choices { get; init; }
    public IReadOnlyList<ScheduleRow> Schedule { get; init; } = [];
    public IReadOnlyList<string> GlAlso { get; init; } = [];
}
