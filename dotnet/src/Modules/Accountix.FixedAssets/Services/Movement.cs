namespace Accountix.FixedAssets.Services;

/// <summary>One line of the movement schedule (a category, or the total).</summary>
public sealed class MovementRow
{
    public string Category { get; init; } = "";
    public double CostOpen, Additions, CostDisposals, CostClose, DepOpen, Charge, DepDisposals, DepClose, NbvOpen, NbvClose;

    public double[] Values => [CostOpen, Additions, CostDisposals, CostClose, DepOpen, Charge, DepDisposals, DepClose, NbvOpen, NbvClose];

    internal void Add(MovementRow r)
    {
        CostOpen += r.CostOpen; Additions += r.Additions; CostDisposals += r.CostDisposals; CostClose += r.CostClose;
        DepOpen += r.DepOpen; Charge += r.Charge; DepDisposals += r.DepDisposals; DepClose += r.DepClose;
        NbvOpen += r.NbvOpen; NbvClose += r.NbvClose;
    }

    internal static MovementRow From(string category, double[] v) => new()
    {
        Category = category, CostOpen = v[0], Additions = v[1], CostDisposals = v[2], CostClose = v[3], DepOpen = v[4],
        Charge = v[5], DepDisposals = v[6], DepClose = v[7], NbvOpen = v[8], NbvClose = v[9],
    };
}

public sealed record MovementData(IReadOnlyList<MovementRow> Rows, MovementRow Total);

/// <summary>
/// The property, plant &amp; equipment movement schedule (fixed_assets_app/movement.py,
/// the IAS 16 note): per category, cost and accumulated depreciation b/f + additions /
/// charge − disposals = c/f, from the asset subledger. Four queries whatever the size.
/// </summary>
public sealed class Movement(AccountixDbContext db, AssetBooks books)
{
    public async Task<MovementData> BuildAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var assets = await db.Set<FixedAsset>().AsNoTracking()
            .Where(a => a.Status != "draft" && a.Status != "cancelled" && a.PurchaseCost > 0)
            .Select(a => new { a.Id, a.AssetCode, a.CategoryId, a.PurchaseCost, a.PurchaseDate })
            .ToListAsync(ct);
        var ids = assets.Select(a => a.Id).ToList();

        // _derecognition_dates: disposal journals, then transfers back to stock.
        var gone = new Dictionary<int, DateOnly>();
        if (ids.Count > 0)
        {
            var disposals = await db.Set<JournalEntry>().AsNoTracking()
                .Where(e => e.VoucherType == "FA-DISP" && e.IsPosted == true && ids.Contains(e.VoucherId))
                .OrderBy(e => e.Id)
                .Select(e => new { e.VoucherId, e.EntryDate }).ToListAsync(ct);
            foreach (var d in disposals)
                gone[d.VoucherId] = DateOnly.FromDateTime(d.EntryDate);
            var transfers = await db.Set<AssetTransfer>().AsNoTracking()
                .Where(t => t.Direction == "to_inventory" && t.Status == "approved" && ids.Contains(t.AssetId))
                .OrderBy(t => t.Id)
                .Select(t => new { t.AssetId, t.TransferDate }).ToListAsync(ct);
            foreach (var t in transfers)
                if (t.TransferDate is DateOnly td)
                    gone[t.AssetId] = td;
        }

        // _live_charges: (date, amount) of depreciation still backed by a posted journal.
        var charges = ids.Count == 0
            ? new Dictionary<int, List<(DateOnly D, double V)>>()
            : (await books.AllLiveRows().AsNoTracking().Where(d => ids.Contains(d.AssetId)).OrderBy(d => d.Id)
                    .Select(d => new { d.AssetId, d.EntryDate, d.Amount }).ToListAsync(ct))
                .GroupBy(d => d.AssetId)
                .ToDictionary(g => g.Key, g => g.Select(x => (D: x.EntryDate, V: Py.F(x.Amount))).ToList());
        var cats = await db.Set<FaCategory>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        var byCat = new List<(string Name, MovementRow Agg)>();
        foreach (var a in assets.OrderBy(x => cats.GetValueOrDefault(x.CategoryId, ""), StringComparer.Ordinal)
                                .ThenBy(x => x.AssetCode, StringComparer.Ordinal))
        {
            double cost = Py.F(a.PurchaseCost);
            var acq = a.PurchaseDate;
            DateOnly? outD = gone.TryGetValue(a.Id, out var g) ? g : null;
            if (acq > to)
                continue;
            bool heldOpen = acq < from && !(outD is DateOnly o && o < from);
            if (!heldOpen && acq < from)
                continue;              // left the books before the period
            var dep = charges.GetValueOrDefault(a.Id) ?? [];
            double before = Py.Sum(dep.Where(x => x.D < from).Select(x => x.V));
            double during = Py.Sum(dep.Where(x => from <= x.D && x.D <= to).Select(x => x.V));
            bool leftInPeriod = outD is DateOnly od && from <= od && od <= to;
            var r = new MovementRow
            {
                CostOpen = heldOpen ? cost : 0.0,
                Additions = acq >= from ? cost : 0.0,
                CostDisposals = leftInPeriod ? cost : 0.0,
                DepOpen = heldOpen ? before : 0.0,
                Charge = during,
                DepDisposals = leftInPeriod ? before + during : 0.0,
            };
            r.CostClose = r.CostOpen + r.Additions - r.CostDisposals;
            r.DepClose = r.DepOpen + r.Charge - r.DepDisposals;
            r.NbvOpen = r.CostOpen - r.DepOpen;
            r.NbvClose = r.CostClose - r.DepClose;
            string name = cats.GetValueOrDefault(a.CategoryId) ?? "Uncategorised";
            int i = byCat.FindIndex(x => x.Name == name);
            if (i < 0)
            {
                byCat.Add((name, new MovementRow { Category = name }));
                i = byCat.Count - 1;
            }
            byCat[i].Agg.Add(r);
        }
        var rows = byCat.Select(x => x.Agg).ToList();
        var total = MovementRow.From("Total", Enumerable.Range(0, 10)
            .Select(k => Py.Round(Py.Sum(rows.Select(r => r.Values[k])), 2)).ToArray());
        var rounded = rows.Select(r => MovementRow.From(r.Category, r.Values.Select(v => Py.Round(v, 2)).ToArray())).ToList();
        return new MovementData(rounded, total);
    }
}
