using Accountix.FixedAssets.Services;

namespace Accountix.FixedAssets.Controllers;

/// <summary>The depreciation run and the reversal of one charge (fixed_assets_app/routes/depreciation.py).</summary>
[Area("FixedAssets")]
[Module(Modules.FixedAssets)]
[Route("fixed-assets/depreciation")]
public sealed class DepreciationController(AccountixDbContext db, AssetBooks books, CompanyClock clock) : Controller
{
    private const string Section = "fixed_assets_assets";

    [HttpGet("")]
    [Right(Section, Rights.View)]
    public async Task<IActionResult> Index([FromQuery(Name = "as_of")] string? asOfRaw, CancellationToken ct)
    {
        var asOf = DateOnly.FromDateTime(DocumentDates.Parse(asOfRaw, null, clock));
        var inService = db.Set<FixedAsset>().Where(a => a.Status == "active" && a.IsActive == true);
        var assets = await inService.AsNoTracking().OrderBy(a => a.Id).ToListAsync(ct);
        var live = await books.LiveByAssetAsync(inService.Select(a => a.Id), ct);
        var due = new List<DueRow>();
        foreach (var a in assets)
        {
            var calc = new AssetCalc(a, live.GetValueOrDefault(a.Id, LiveCharges.None));
            double amount = calc.DueDepreciation(asOf);
            if (amount > 0)
                due.Add(new DueRow(calc, amount, calc.DueMonths(asOf), calc.LastDepreciationDate));
        }
        return View(new DepreciationRunModel(due, asOf, Py.Round(Py.Sum(due.Select(d => d.Amount)), 2)));
    }

    [HttpPost("post")]
    [Right(Section, Rights.Approve)]
    public async Task<IActionResult> Post(CancellationToken ct)
    {
        var form = Request.Form;
        var ids = form["asset_ids[]"].Select(s => (s ?? "").Trim())
            .Where(s => s.Length > 0 && s.All(char.IsAsciiDigit))
            .Select(int.Parse).Distinct().Order().ToList();
        var entryDate = DateOnly.FromDateTime(DocumentDates.Parse(form["entry_date"].FirstOrDefault(), null, clock));
        int userId = HttpContext.Access()!.User.Id;
        int posted = 0;
        var errors = new List<string>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (int aid in ids)
        {
            // Locked in id order, outside the savepoint, before its due months are
            // worked out: two runs at once charge each month once.
            var asset = await db.LockAsync<FixedAsset>(aid, ct);
            if (asset is null || asset.Status != "active")
                continue;
            // One savepoint per asset: a refused charge leaves nothing half-built behind.
            string sp = $"fa_dep_{aid}";
            var before = db.ChangeTracker.Entries().Select(e => e.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
            await tx.CreateSavepointAsync(sp, ct);
            try
            {
                double amt = await books.PostDepreciationAsync(asset, entryDate, userId, ct: ct);
                await tx.ReleaseSavepointAsync(sp, ct);
                if (amt != 0)
                    posted++;
            }
            catch (Exception e) when (e is InvalidOperationException or DbUpdateException)
            {
                await tx.RollbackToSavepointAsync(sp, ct);
                foreach (var entry in db.ChangeTracker.Entries().Where(x => !before.Contains(x.Entity)).ToList())
                    entry.State = EntityState.Detached;
                await db.Entry(asset).ReloadAsync(ct);
                errors.Add($"{asset.Name}: {e.Message}");
            }
        }
        await tx.CommitAsync(ct);
        if (posted > 0)
            this.Flash($"Depreciation posted for {posted} asset(s).", "success");
        else if (errors.Count == 0)
            this.Flash("Nothing posted. Select at least one active asset.", "warning");
        foreach (string e in errors)
            this.Flash(e, "error");
        return Redirect("/fixed-assets/depreciation/");
    }

    [HttpPost("entry/{dep_id:int}/reverse")]
    [Right(Section, Rights.Approve)]
    public async Task<IActionResult> Reverse([FromRoute(Name = "dep_id")] int depId, CancellationToken ct)
    {
        var first = await db.Set<AssetDepreciation>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == depId, ct);
        if (first is null) return NotFound();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Asset first, as the run locks it, then the charge itself, re-read.
        await db.LockAsync<FixedAsset>(first.AssetId, ct);
        var dep = await db.LockAsync<AssetDepreciation>(depId, ct);
        if (dep is null) return NotFound();
        string back = $"/fixed-assets/assets/{dep.AssetId}";
        if (!await books.IsLiveAsync(dep, ct))
        {
            this.Flash("That depreciation charge is already reversed.", "error");
            return Redirect(back);
        }
        FixedAsset? asset;
        try
        {
            asset = await books.ReverseDepreciationAsync(dep, HttpContext.Access()!.User.Id, ct);
        }
        catch (Exception e) when (e is InvalidOperationException or DbUpdateException)
        {
            await tx.RollbackAsync(ct);
            this.Flash($"Reversal failed: {e.Message}", "error");
            return Redirect(back);
        }
        await tx.CommitAsync(ct);
        this.Flash($"Depreciation of {Py.Comma(Py.F(dep.Amount), 2)} reversed; book value is now " +
                   $"{Py.Comma(Py.F(asset?.CurrentBookValue), 2)}.", "success");
        return Redirect(back);
    }
}

public sealed record DueRow(AssetCalc Calc, double Amount, int Months, DateOnly Last);

public sealed record DepreciationRunModel(IReadOnlyList<DueRow> Due, DateOnly AsOf, double Total);
