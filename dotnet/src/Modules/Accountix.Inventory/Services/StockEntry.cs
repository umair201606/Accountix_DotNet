namespace Accountix.Inventory.Services;

/// <summary>
/// shared/stock_entry.py: stock that enters or leaves outside a purchase or a
/// sale (opening stock, the product page's quick adjustment), always through
/// the costing engine and always with its journal.
/// </summary>
public sealed class StockEntry(AccountixDbContext db, StockCosting costing, ILedger ledger, IVoucherNumbers numbers,
                               CompanyClock clock)
{
    /// <summary>
    /// post_opening_stock: a receipt at <paramref name="unitCost"/> dated <paramref name="when"/>
    /// (the go-live date), and Dr the product's stock account / Cr Opening Balance Equity.
    /// </summary>
    public async Task<StockLedger?> PostOpeningStockAsync(InvProduct product, decimal qty, decimal unitCost, DateTime? when,
                                                         int createdBy, CancellationToken ct = default)
    {
        if (qty <= 0)
            return null;
        // Undated: today in the company's clock, as a form dates it (shared/stock_entry._company_today).
        var at = when ?? clock.Today.ToDateTime(TimeOnly.MinValue);
        string number = Py.Cut($"OPEN-{(string.IsNullOrEmpty(product.Sku) ? product.Id.ToString() : product.Sku)}", 50);
        var row = await costing.ReceiveAsync(product.Id, "OPENING", product.Id, number, qty, unitCost,
                                             "Opening stock", createdBy, at, null, ct);
        decimal value = row.TotalCost;
        if (value != 0)
        {
            var stock = await ledger.EntityAccountAsync("product", product.Id, $"{product.Name} ({product.Sku})", ct);
            var equity = await ledger.PostingAccountAsync("suspense_equity", ct);
            await ledger.PostAsync("OPENING", product.Id, number, $"Opening stock — {product.Name}",
            [
                new(stock.Id, value, 0, $"Opening stock {Py.Normalize(qty)} x {product.Name}"),
                new(equity.Id, 0, value, $"Opening balance — {product.Name}"),
            ], at, createdBy, ct);
        }
        return row;
    }

    /// <summary>
    /// quick_adjustment: the same document the Stock Adjustment screen creates,
    /// approved at once — stock moves at its valuation cost and the difference
    /// goes to Inventory Adjustment.
    /// </summary>
    public async Task<StockAdjustmentVoucher?> QuickAdjustmentAsync(InvProduct product, decimal qty, string reason, DateTime? when,
                                                                   int createdBy, CancellationToken ct = default)
    {
        if (qty == 0)
            return null;
        var at = when ?? clock.Today.ToDateTime(TimeOnly.MinValue);
        decimal system = await costing.OnHandAsync(product.Id, null, ct);
        var v = new StockAdjustmentVoucher
        {
            VoucherNumber = await numbers.NextAsync("ADJ", ct), Date = at,
            Reason = string.IsNullOrEmpty(reason) ? "Quick adjustment from product page" : reason,
            Status = "approved", CreatedBy = createdBy, ApprovedBy = createdBy,
            ApprovedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow,
        };
        db.Add(v);
        await db.SaveChangesAsync(ct);
        decimal unit, total;
        if (qty > 0)
        {
            decimal current = await costing.CurrentUnitCostAsync(product.Id, ct);
            var row = await costing.ReceiveAsync(product.Id, "ADJ", v.Id, v.VoucherNumber, qty, current, v.Reason ?? "",
                                                 createdBy, at, null, ct);
            (unit, total) = (row.UnitCost, row.TotalCost);
        }
        else
            (unit, total) = await costing.IssueAsync(product.Id, "ADJ", v.Id, v.VoucherNumber, -qty, v.Reason ?? "",
                                                     createdBy, null, at, null, ct);
        db.Add(new StockAdjustmentItem
        {
            VoucherId = v.Id, ProductId = product.Id, ProductName = product.Name, SystemQty = system,
            PhysicalQty = system + qty, Difference = qty, UnitCost = unit, TotalCost = total,
        });
        if (total != 0)
        {
            var inv = await ledger.GetOrCreateAccountAsync("1200", "Inventory", "asset", null, ct);
            var adj = await ledger.GetOrCreateAccountAsync("5900", "Inventory Adjustment", "expense", null, ct);
            var (dr, cr) = qty > 0 ? (inv, adj) : (adj, inv);
            await ledger.PostAsync("ADJ", v.Id, v.VoucherNumber, $"Stock Adjustment {v.VoucherNumber}: {v.Reason}",
            [
                new(dr.Id, total, 0, v.Reason),
                new(cr.Id, 0, total, v.Reason),
            ], at, createdBy, ct);
        }
        await db.SaveChangesAsync(ct);
        return v;
    }

    /// <summary>unjournaled_openings: OPENING rows whose product has no posted OPENING journal.</summary>
    public async Task<List<StockLedger>> UnjournaledOpeningsAsync(CancellationToken ct = default)
    {
        var journaled = await db.Set<JournalEntry>()
            .Where(e => e.VoucherType == "OPENING" && e.IsPosted == true).Select(e => e.VoucherId).Distinct().ToListAsync(ct);
        var set = journaled.ToHashSet();
        var rows = await db.Set<StockLedger>().Where(r => r.VoucherType == "OPENING" && r.TransactionType == "IN").ToListAsync(ct);
        return rows.Where(r => !set.Contains(r.ProductId) && r.TotalCost != 0).ToList();
    }

    /// <summary>journal_opening_row: Dr the product's stock account / Cr Opening Balance Equity, on the row's date.</summary>
    public async Task<JournalEntry?> JournalOpeningRowAsync(StockLedger row, int createdBy, CancellationToken ct = default)
    {
        var product = await db.Set<InvProduct>().FirstOrDefaultAsync(p => p.Id == row.ProductId, ct);
        if (product is null)
            return null;
        decimal value = Math.Round(row.TotalCost, 2, MidpointRounding.ToEven);
        var stock = await ledger.EntityAccountAsync("product", product.Id, $"{product.Name} ({product.Sku})", ct);
        var equity = await ledger.PostingAccountAsync("suspense_equity", ct);
        DateTime when = row.TxnDate is DateOnly d ? d.ToDateTime(TimeOnly.MinValue) : row.CreatedAt ?? DateTime.UtcNow;
        return await ledger.PostAsync("OPENING", product.Id,
            Py.Cut(string.IsNullOrEmpty(row.VoucherNumber) ? $"OPEN-{product.Id:D5}" : row.VoucherNumber, 50),
            $"Opening stock — {product.Name}",
            [
                new(stock.Id, value, 0, $"Opening stock {product.Name}"),
                new(equity.Id, 0, value, $"Opening balance — {product.Name}"),
            ], when, createdBy, ct);
    }
}
