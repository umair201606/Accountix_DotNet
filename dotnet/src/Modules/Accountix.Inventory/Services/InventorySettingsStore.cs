namespace Accountix.Inventory.Services;

/// <summary>
/// InventorySettings.get() (shared/models/inventory_settings.py): the company's
/// one row, made on first use under serialize("first-use", "inventory_settings", cid)
/// so two first requests make one row.
/// </summary>
public sealed class InventorySettingsStore(AccountixDbContext db)
{
    public const string Fifo = "fifo";
    public const string WeightedAverage = "weighted_average";

    public async Task<InventorySetting> GetAsync(CancellationToken ct = default)
    {
        int? cid = db.Company.CompanyId;
        if (cid is null)
            return Defaults(new InventorySetting());
        var pending = db.ChangeTracker.Entries<InventorySetting>()
            .FirstOrDefault(e => e.State == EntityState.Added && e.Entity.CompanyId == cid)?.Entity;
        if (pending is not null)
            return pending;
        var s = await db.Set<InventorySetting>().Where(x => x.CompanyId == cid).OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (s is not null)
            return s;
        await db.SerializeAsync("first-use", "inventory_settings", cid.Value);
        s = await db.Set<InventorySetting>().Where(x => x.CompanyId == cid).OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (s is not null)
            return s;
        s = Defaults(new InventorySetting { CompanyId = cid });
        db.Add(s);
        await db.SaveChangesAsync(ct);
        return s;
    }

    /// <summary>The model's column defaults (Python sets them client-side).</summary>
    public static InventorySetting Defaults(InventorySetting s)
    {
        s.ValuationMethod ??= WeightedAverage;
        s.AllowNegativeStock ??= false;
        s.DecimalPlaces ??= 4;
        s.AutoGenerateVouchers ??= true;
        s.PurchaseFlow ??= "with_po";
        s.SalesFlow ??= "with_so";
        s.PerLineLabelingVoucher ??= false;
        s.PerLineLabelingInvoice ??= false;
        return s;
    }

    public static string Method(InventorySetting s) => s.ValuationMethod ?? WeightedAverage;
}
