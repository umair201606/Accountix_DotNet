using System.Globalization;

namespace Accountix.Platform.Services;

/// <summary>
/// shared/periods.py close_checklist: what is still open inside a period —
/// once closed, none of it can be posted there. Drafts dated in the period,
/// depreciation still due up to its end, payroll months never run.
/// The close itself locks the period row (FOR NO KEY UPDATE, as Python's
/// scoped_get(for_update=True)); postings hold FOR SHARE on it
/// (require_open_period), so a close waits for postings in flight and a
/// posting during a close waits, then sees it closed.
/// </summary>
public static class PeriodClose
{
    private sealed class Counts
    {
        public int Si { get; set; }
        public int Pi { get; set; }
        public int Sr { get; set; }
        public int Pr { get; set; }
        public int Av { get; set; }
        public int Stock { get; set; }
    }

    public static async Task<List<string>> ChecklistAsync(AccountixDbContext db, AccountingPeriod period, IServiceProvider services, CancellationToken ct = default)
    {
        int cid = period.CompanyId ?? db.Company.CompanyId!.Value;
        var start = period.StartDate.ToDateTime(TimeOnly.MinValue);
        var end = period.EndDate.ToDateTime(TimeOnly.MaxValue);
        // One round trip for the six draft counts.
        var c = (await db.Database.SqlQueryRaw<Counts>(
            """
            SELECT
              (SELECT COUNT(*) FROM inv_invoices WHERE company_id = @cid AND invoice_date >= @s AND invoice_date <= @e
                 AND voucher_status IS DISTINCT FROM 'approved')::int AS "Si",
              (SELECT COUNT(*) FROM inv_purchase_invoices WHERE company_id = @cid AND invoice_date >= @s AND invoice_date <= @e
                 AND status IN ('new', 'unapproved'))::int AS "Pi",
              (SELECT COUNT(*) FROM inv_sales_returns WHERE company_id = @cid AND return_date >= @s AND return_date <= @e
                 AND status IN ('new', 'unapproved'))::int AS "Sr",
              (SELECT COUNT(*) FROM inv_purchase_returns WHERE company_id = @cid AND return_date >= @s AND return_date <= @e
                 AND status IN ('new', 'unapproved'))::int AS "Pr",
              (SELECT COUNT(*) FROM accounting_vouchers WHERE company_id = @cid AND voucher_date >= @s AND voucher_date <= @e
                 AND status IN ('unapproved', 'draft'))::int AS "Av",
              ((SELECT COUNT(*) FROM consumption_vouchers WHERE company_id = @cid AND date >= @s AND date <= @e AND status = 'unapproved')
               + (SELECT COUNT(*) FROM scrap_vouchers WHERE company_id = @cid AND date >= @s AND date <= @e AND status = 'unapproved')
               + (SELECT COUNT(*) FROM stock_adjustment_vouchers WHERE company_id = @cid AND date >= @s AND date <= @e AND status = 'unapproved'))::int AS "Stock"
            """,
            new Npgsql.NpgsqlParameter("cid", cid),
            new Npgsql.NpgsqlParameter("s", NpgsqlTypes.NpgsqlDbType.Timestamp) { Value = start },
            new Npgsql.NpgsqlParameter("e", NpgsqlTypes.NpgsqlDbType.Timestamp) { Value = end }).ToListAsync(ct)).Single();
        var output = new List<string>();
        foreach (var (n, label) in new[]
                 {
                     (c.Si, "unapproved sales invoice(s)"), (c.Pi, "unapproved purchase invoice(s)"),
                     (c.Sr, "unapproved sales return(s)"), (c.Pr, "unapproved purchase return(s)"),
                     (c.Av, "unapproved accounting voucher(s)"), (c.Stock, "unapproved stock voucher(s)"),
                 })
            if (n > 0) output.Add($"{n} {label}");

        int due = await DepreciationDueCountAsync(db, services, period.EndDate, ct);
        if (due > 0)
            output.Add($"depreciation not yet posted to {period.EndDate.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)} on {due} asset(s)");

        if (await db.Set<PayrollProfile>().AnyAsync(ct))
        {
            var runs = (await db.Set<PayrollRun>().AsNoTracking().Select(r => new { r.Month, r.Year }).ToListAsync(ct))
                .Select(r => (r.Year, r.Month)).ToHashSet();
            var missing = new List<string>();
            int y = period.StartDate.Year, m = period.StartDate.Month;
            while (y < period.EndDate.Year || (y == period.EndDate.Year && m <= period.EndDate.Month))
            {
                if (!runs.Contains((y, m))) missing.Add($"{m}/{y}");
                if (++m == 13) { y++; m = 1; }
            }
            if (missing.Count > 0)
                output.Add("no payroll run for " + string.Join(", ", missing.Take(6)) + (missing.Count > 6 ? " …" : ""));
        }
        return output;
    }

    /// <summary>
    /// Assets with depreciation due up to <paramref name="asOf"/>: due_depreciation(asset, as_of) &gt; 0 for
    /// every active asset, through the Fixed Assets module's own engine (live charges in one query).
    /// </summary>
    private static async Task<int> DepreciationDueCountAsync(AccountixDbContext db, IServiceProvider services, DateOnly asOf, CancellationToken ct)
    {
        var assets = await db.Set<FixedAsset>().AsNoTracking().Where(a => a.Status == "active").ToListAsync(ct);
        if (assets.Count == 0) return 0;
        var books = Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<Accountix.FixedAssets.Services.AssetBooks>(services);
        var live = await books.LiveByAssetAsync(db.Set<FixedAsset>().Where(a => a.Status == "active").Select(a => a.Id), ct);
        return assets.Count(a => new Accountix.FixedAssets.Services.AssetCalc(a, live.GetValueOrDefault(a.Id, Accountix.FixedAssets.Services.LiveCharges.None))
            .DueDepreciation(asOf) > 0);
    }
}

/// <summary>shared/costing.py revalue_for_method_change: a method switch is a revaluation at book value, prospective only.</summary>
public static class Revaluation
{
    public static async Task ForMethodChangeAsync(AccountixDbContext db, string newMethod, CancellationToken ct = default)
    {
        var products = await db.Set<StockLayer>().Where(l => l.QtyRemaining > 0).Select(l => l.ProductId).Distinct()
            .OrderBy(id => id).ToListAsync(ct);
        if (products.Count == 0) return;
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlRawAsync(
                "SELECT 1 FROM inv_products WHERE id = ANY(@cid) ORDER BY id FOR NO KEY UPDATE", [products.ToArray()], ct);
        if (newMethod == "fifo") return;          // FIFO keeps its distinct layers: nothing to collapse
        var layers = await db.Set<StockLayer>().Where(l => products.Contains(l.ProductId) && l.QtyRemaining > 0)
            .OrderBy(l => l.Id).ToListAsync(ct);
        foreach (var group in layers.GroupBy(l => l.ProductId))
        {
            var open = group.ToList();
            if (open.Count <= 1) continue;
            decimal qty = open.Sum(l => l.QtyRemaining), value = open.Sum(l => l.ValueRemaining);
            var keep = open[0];
            keep.QtyRemaining = qty;
            keep.QtyOriginal = qty;
            keep.ValueRemaining = value;
            keep.UnitCost = qty > 0 ? Math.Round(value / qty, 4, MidpointRounding.ToEven) : 0;
            keep.Method = newMethod;
            keep.IsRevaluation = true;
            keep.Notes = $"Revaluation: collapsed {open.Count} layers on switch to {newMethod} at book value " +
                         Math.Round(value, 2, MidpointRounding.ToEven).ToString("F2", CultureInfo.InvariantCulture);
            foreach (var l in open.Skip(1))
            {
                l.QtyRemaining = 0;
                l.ValueRemaining = 0;
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
