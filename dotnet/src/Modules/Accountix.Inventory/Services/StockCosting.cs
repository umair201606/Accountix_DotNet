using System.Globalization;
using Npgsql;

namespace Accountix.Inventory.Services;

/// <summary>
/// The costing engine: a line-for-line port of shared/costing.py with
/// shared/cost_adjustment.py (re-costing journals).
///
/// Cost lives in stock_layers (FIFO: a layer per receipt; weighted average:
/// one pool). Issues consume layers oldest first at each layer's carried
/// value, so sum(value_remaining) == the ledger's running cost. Rows carry the
/// DOCUMENT date; a back-dated document (or the reversal of an earlier one)
/// replays the product's history in (txn_date, id) order: rows before it are
/// frozen, later issues are re-costed, and every cost that moves is posted as a
/// CADJ-&lt;type&gt; journal charging the account the issue originally hit.
///
/// The arithmetic is Python's: decimals keep their scale as Python's Decimal
/// does, quantize is banker's rounding, and the database rounds to its column
/// scale on storage. A product's layers are worked on in memory and written in
/// one save per operation (a replay of a year of history is a handful of
/// statements, not one per layer); re-costing journals are posted in one save.
///
/// Every operation runs in the caller's transaction (the caller commits).
/// </summary>
public sealed class StockCosting(AccountixDbContext db, ILedger ledger, IPeriods periods, InventorySettingsStore settingsStore,
                                 CompanyClock clock) : IStockCosting
{
    public const string PendingShort = "[pending correction]";

    private ILedger Ledger => ledger;


    /// <summary>OUT rows whose cost is a fixed basis rather than drawn from the layers: a purchase return.</summary>
    private static readonly HashSet<string> ExplicitBasisTypes = ["PRV"];

    /// <summary>Receipts whose cost mirrors an issue and follows it when it is re-costed: a sales return.</summary>
    private static readonly HashSet<string> InRecostTypes = ["SRV"];

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ─────────────────────────────────────────────
    // Decimal arithmetic as Python's Decimal does it
    // ─────────────────────────────────────────────

    /// <summary>_q: quantize to <paramref name="places"/> (ROUND_HALF_EVEN), exactly that many places.</summary>
    public static decimal Q(decimal value, int places = 4)
    {
        decimal r = Math.Round(value, places, MidpointRounding.ToEven);
        return Rescale(r, places);
    }

    private static decimal Rescale(decimal value, int places)
    {
        int scale = (decimal.GetBits(value)[3] >> 16) & 0xFF;
        if (scale >= places) return value;
        return value + new decimal(0, 0, 0, false, (byte)places);
    }

    private static decimal Sum(IEnumerable<decimal> values)
    {
        decimal total = 0m;
        foreach (decimal v in values) total += v;
        return total;
    }

    /// <summary>Python's min(a, b): b only when strictly smaller.</summary>
    private static decimal Min(decimal a, decimal b) => b < a ? b : a;

    // ─────────────────────────────────────────────
    // Locks
    // ─────────────────────────────────────────────

    private object? _lockTransaction;
    private readonly HashSet<int> _held = [];

    /// <summary>
    /// lock_products: FOR NO KEY UPDATE on the product rows, ascending ids, all at
    /// once, held to the end of the transaction. Stock rows this context read
    /// before the lock are forgotten so they are read again.
    /// </summary>
    public async Task LockProductsAsync(IEnumerable<int> productIds, CancellationToken ct = default)
    {
        var tx = db.Database.CurrentTransaction;
        if (tx is null || !ReferenceEquals(tx, _lockTransaction))
        {
            _held.Clear();
            _lockTransaction = tx;
        }
        var ids = productIds.Where(i => i > 0).Distinct().Where(i => !_held.Contains(i)).Order().ToArray();
        if (ids.Length == 0)
            return;
        await db.SaveChangesAsync(ct);
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlRawAsync(
                "SELECT id FROM inv_products WHERE id = ANY({0}) ORDER BY id FOR NO KEY UPDATE", [ids], ct);
        if (tx is not null)
            _held.UnionWith(ids);
        var wanted = ids.ToHashSet();
        foreach (var e in db.ChangeTracker.Entries().ToList())
        {
            switch (e.Entity)
            {
                case StockLedger r when wanted.Contains(r.ProductId):
                case StockLayer l when wanted.Contains(l.ProductId):
                case LayerConsumption c when wanted.Contains(c.ProductId):
                    if (e.State == EntityState.Unchanged) e.State = EntityState.Detached;
                    break;
                case InvProduct p when wanted.Contains(p.Id) && e.State == EntityState.Unchanged:
                    await e.ReloadAsync(ct);
                    break;
            }
        }
    }

    /// <summary>
    /// Forget the stock rows of these products once an operation has saved them, as SQLAlchemy's weak
    /// identity map does: the next read sees them as stored (Numeric(16,4): "5.0000"), which is how
    /// the Python app's refusal texts and short-layer notes print their quantities.
    /// </summary>
    private void Forget(IEnumerable<int> productIds)
    {
        var ids = productIds.ToHashSet();
        foreach (var e in db.ChangeTracker.Entries().ToList())
            if (e.State == EntityState.Unchanged && e.Entity switch
                {
                    StockLedger r => ids.Contains(r.ProductId),
                    StockLayer l => ids.Contains(l.ProductId),
                    LayerConsumption c => ids.Contains(c.ProductId),
                    _ => false,
                })
                e.State = EntityState.Detached;
    }

    // ─────────────────────────────────────────────
    // Reads
    // ─────────────────────────────────────────────

    private async Task<InventorySetting> SettingsAsync(CancellationToken ct) => await settingsStore.GetAsync(ct);

    private static bool IsFifo(InventorySetting s) => s.ValuationMethod == InventorySettingsStore.Fifo;

    /// <summary>as_txn_date: a document date (None = today).</summary>
    private DateOnly TxnDate(DateTime? when) => when is DateTime d ? DateOnly.FromDateTime(d) : clock.Today;

    private static (DateOnly, int) Key(StockLedger r) => (r.TxnDate ?? DateOnly.MinValue, r.Id);

    private static int Compare((DateOnly, int) a, (DateOnly, int) b) =>
        a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : a.Item2.CompareTo(b.Item2);

    /// <summary>StockLedger.get_running_balance: the latest row by document date (txn_date DESC, id DESC).</summary>
    public async Task<(decimal Qty, decimal Cost, decimal Avg)> RunningBalanceAsync(int productId, CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct);
        var last = await db.Set<StockLedger>().Where(r => r.ProductId == productId)
            .OrderByDescending(r => r.TxnDate).ThenByDescending(r => r.Id).FirstOrDefaultAsync(ct);
        return last is null ? (0.0000m, 0.0000m, 0.0000m) : (last.RunningQty, last.RunningCost, last.RunningAvg);
    }

    private async Task<bool> IsBackdatedAsync(int productId, DateOnly txnDate, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        return await db.Set<StockLedger>().AnyAsync(r => r.ProductId == productId && r.TxnDate > txnDate, ct);
    }

    private async Task<List<StockLedger>> OrderedRowsAsync(int productId, CancellationToken ct) =>
        await db.Set<StockLedger>().Where(r => r.ProductId == productId)
            .OrderBy(r => r.TxnDate).ThenBy(r => r.Id).ToListAsync(ct);

    public async Task<decimal> OnHandAsync(int productId, DateTime? at = null, CancellationToken ct = default)
    {
        if (at is null)
            return (await RunningBalanceAsync(productId, ct)).Qty;
        await db.SaveChangesAsync(ct);
        var d = DateOnly.FromDateTime(at.Value);
        return await db.Set<StockLedger>().Where(r => r.ProductId == productId && r.TxnDate <= d)
            .SumAsync(r => r.TransactionType == "IN" ? r.Quantity : -r.Quantity, ct);
    }

    public async Task<decimal> ValueAtAsync(int productId, DateTime at, CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct);
        var d = DateOnly.FromDateTime(at);
        return await db.Set<StockLedger>().Where(r => r.ProductId == productId && r.TxnDate <= d)
            .SumAsync(r => r.TransactionType == "IN" ? r.TotalCost : -r.TotalCost, ct);
    }

    /// <summary>stock_value: the carried value of every layer still holding a balance (shorts included).</summary>
    public async Task<decimal> StockValueAsync(int productId, CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct);
        return await db.Set<StockLayer>().Where(l => l.ProductId == productId && l.QtyRemaining != 0)
            .SumAsync(l => l.ValueRemaining, ct);
    }

    /// <summary>layers_remaining: (unit_cost, qty_remaining) of the open layers, oldest first.</summary>
    public async Task<List<(decimal UnitCost, decimal Qty)>> LayersRemainingAsync(int productId, CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct);
        var rows = await db.Set<StockLayer>().AsNoTracking().Where(l => l.ProductId == productId && l.QtyRemaining > 0)
            .OrderBy(l => l.Id).Select(l => new { l.UnitCost, l.QtyRemaining }).ToListAsync(ct);
        return rows.Select(r => (r.UnitCost, r.QtyRemaining)).ToList();
    }

    public async Task<decimal> CurrentUnitCostAsync(int productId, CancellationToken ct = default) =>
        CurrentUnitCost(await LoadBookAsync(productId, ct));

    public async Task<(decimal UnitCost, decimal TotalCost)> CostOfIssueAsync(int productId, decimal qty, CancellationToken ct = default)
    {
        if (qty <= 0)
            return (0m, 0m);
        return CostOfIssue(await LoadBookAsync(productId, ct), qty);
    }

    public async Task<decimal?> OriginalIssueCostAsync(string voucherType, int voucherId, int productId, CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct);
        var row = await db.Set<StockLedger>()
            .Where(r => r.VoucherType == voucherType && r.VoucherId == voucherId && r.ProductId == productId && r.TransactionType == "OUT")
            .OrderBy(r => r.Id).Select(r => new { r.UnitCost }).FirstOrDefaultAsync(ct);
        return row?.UnitCost;
    }

    public async Task<decimal?> OriginalReceiptCostAsync(string voucherType, int voucherId, int productId, CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct);
        var rows = await db.Set<StockLedger>()
            .Where(r => r.VoucherType == voucherType && r.VoucherId == voucherId && r.ProductId == productId && r.TransactionType == "IN")
            .Select(r => new { r.Quantity, r.TotalCost }).ToListAsync(ct);
        decimal qty = Sum(rows.Select(r => r.Quantity));
        if (qty <= 0)
            return null;
        return Q(Sum(rows.Select(r => r.TotalCost)) / qty);
    }

    public async Task<IReadOnlyList<(string VoucherType, string VoucherNumber, decimal Qty)>> ConsumersOfVoucherAsync(
        string voucherType, int voucherId, CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct);
        var receipts = db.Set<StockLedger>()
            .Where(r => r.VoucherType == voucherType && r.VoucherId == voucherId && r.TransactionType == "IN").Select(r => (int?)r.Id);
        var layers = db.Set<StockLayer>().Where(l => receipts.Contains(l.SourceLedgerId)).Select(l => l.Id);
        var rows = await (from c in db.Set<LayerConsumption>()
                          where layers.Contains(c.LayerId)
                          join r in db.Set<StockLedger>() on c.OutLedgerId equals r.Id
                          orderby c.Id
                          select new { r.VoucherType, r.VoucherNumber, c.Qty }).ToListAsync(ct);
        return rows.Select(r => (r.VoucherType, r.VoucherNumber, r.Qty)).ToList();
    }

    public async Task<(bool Ok, decimal LayerValue, decimal RunningCost)> AssertInvariantAsync(int productId, CancellationToken ct = default)
    {
        var (_, running, _) = await RunningBalanceAsync(productId, ct);
        decimal layers = await StockValueAsync(productId, ct);
        return (Math.Abs(layers - running) <= 0.01m, layers, running);
    }

    // ─────────────────────────────────────────────
    // A product's layers, worked on in memory
    // ─────────────────────────────────────────────

    /// <summary>The layers that matter to the next movement: every one still holding a balance, in id order, and the newest.</summary>
    private sealed class Book(int productId)
    {
        public int ProductId { get; } = productId;
        public List<StockLayer> Layers { get; } = [];
        public StockLayer? Newest { get; set; }

        /// <summary>_open_layers: stock left, oldest first (the consumption order).</summary>
        public List<StockLayer> Open() => Layers.Where(l => l.QtyRemaining > 0).ToList();

        public List<StockLayer> Shorts() => Layers.Where(l => l.QtyRemaining < 0).ToList();

        public void Add(StockLayer l)
        {
            Layers.Add(l);
            Newest = l;
        }
    }

    private async Task<Book> LoadBookAsync(int productId, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        var layers = await db.Set<StockLayer>()
            .Where(l => l.ProductId == productId && (l.QtyRemaining != 0
                        || l.Id == db.Set<StockLayer>().Where(x => x.ProductId == productId).Max(x => (int?)x.Id)))
            .OrderBy(l => l.Id).ToListAsync(ct);
        var book = new Book(productId);
        foreach (var l in layers)
            if (l.QtyRemaining != 0) book.Layers.Add(l);
        book.Newest = layers.Count > 0 ? layers[^1] : null;
        return book;
    }

    /// <summary>unit_cost_effective: carried value over remaining quantity, unrounded.</summary>
    private static decimal Effective(StockLayer l) => l.QtyRemaining <= 0 ? l.UnitCost : l.ValueRemaining / l.QtyRemaining;

    /// <summary>current_unit_cost: total layer value / total layer qty; with nothing on hand, the newest layer's cost.</summary>
    private static decimal CurrentUnitCost(Book book)
    {
        var open = book.Open();
        decimal qty = Sum(open.Select(l => l.QtyRemaining));
        if (qty <= 0)
            return book.Newest?.UnitCost ?? 0m;
        return Q(Sum(open.Select(l => l.ValueRemaining)) / qty);
    }

    private sealed record Take(StockLayer Layer, decimal Qty, decimal Cost);

    /// <summary>_plan_consumption: (plan, uncovered), oldest layers first, the preferred receipt's first. Pure.</summary>
    private static (List<Take> Plan, decimal Uncovered) PlanConsumption(Book book, decimal qty, Func<StockLayer, bool>? prefer = null)
    {
        decimal remaining = qty;
        var plan = new List<Take>();
        var layers = book.Open();
        if (prefer is not null)
            layers = layers.Where(prefer).Concat(layers.Where(l => !prefer(l))).ToList();
        foreach (var layer in layers)
        {
            if (remaining <= 0)
                break;
            decimal take = Min(layer.QtyRemaining, remaining);
            if (take <= 0)
                continue;
            plan.Add(new Take(layer, take, Effective(layer)));
            remaining -= take;
        }
        return (plan, remaining);
    }

    /// <summary>cost_of_issue: (unit, total) issuing qty would carry now (no preference).</summary>
    private static (decimal Unit, decimal Total) CostOfIssue(Book book, decimal qty)
    {
        if (qty <= 0)
            return (0m, 0m);
        var (plan, uncovered) = PlanConsumption(book, qty);
        decimal total = 0m;
        foreach (var t in plan) total += t.Qty * t.Cost;
        if (uncovered > 0)
            total += uncovered * CurrentUnitCost(book);
        return (Q(total / qty), Q(total, 2));
    }

    /// <summary>_withdraw_value: take exactly the posted total off the planned layers; returns what each gave up.</summary>
    private static List<decimal> WithdrawValue(List<Take> plan, decimal postedTotal)
    {
        var values = new List<decimal>(plan.Count);
        foreach (var t in plan)
            values.Add(t.Qty >= t.Layer.QtyRemaining ? t.Layer.ValueRemaining : Q(t.Qty * Effective(t.Layer)));
        decimal residual = postedTotal - Sum(values);
        if (residual != 0)
            for (int i = plan.Count - 1; i >= 0; i--)
                if (plan[i].Layer.QtyRemaining - plan[i].Qty > 0)
                {
                    values[i] += residual;
                    break;
                }
        for (int i = 0; i < plan.Count; i++)
        {
            plan[i].Layer.QtyRemaining -= plan[i].Qty;
            plan[i].Layer.ValueRemaining -= values[i];
        }
        return values;
    }

    private LayerConsumption Consumption(StockLayer layer, StockLedger outRow, decimal qty, decimal unitCost, decimal totalCost)
    {
        var c = new LayerConsumption
        {
            OutLedgerId = outRow.Id, ProductId = outRow.ProductId, Qty = qty, UnitCost = unitCost, TotalCost = totalCost,
            CreatedAt = clock.UtcNow,
        };
        if (_bulk is not null)
        {
            _bulk.Consumptions.Add((c, layer));
            return c;
        }
        if (layer.Id > 0) c.LayerId = layer.Id;
        else c.Layer = layer;
        db.Add(c);
        return c;
    }

    private StockLayer NewLayer(int productId, int? sourceLedgerId, decimal unitCost, decimal qtyOriginal, decimal qtyRemaining,
                                decimal valueRemaining, string method, string? notes)
    {
        var l = new StockLayer
        {
            // As stored (and as Python reads it back once the flushed object is collected).
            ProductId = productId, SourceLedgerId = sourceLedgerId, UnitCost = Rescale(unitCost, 4), QtyOriginal = Rescale(qtyOriginal, 4),
            QtyRemaining = Rescale(qtyRemaining, 4), ValueRemaining = Rescale(valueRemaining, 4), Method = method, IsRevaluation = false,
            CreatedAt = clock.UtcNow, Notes = notes,
        };
        if (_bulk is not null)
            _bulk.Layers.Add(l);
        else
            db.Add(l);
        return l;
    }

    /// <summary>_cover_shorts: net a receipt against short layers, oldest first; (qty, value) left for the shelf.</summary>
    private (decimal Qty, decimal Value) CoverShorts(Book book, StockLedger receipt, decimal qty, decimal value)
    {
        foreach (var s in book.Shorts())
        {
            if (qty <= 0)
                break;
            decimal owed = -s.QtyRemaining;
            decimal take = Min(owed, qty);
            decimal moved = take >= owed ? -s.ValueRemaining : Q(take * s.ValueRemaining / s.QtyRemaining);
            s.QtyRemaining += take;
            s.ValueRemaining += moved;
            Consumption(s, receipt, -take, s.UnitCost, -moved);
            qty -= take;
            value -= moved;
        }
        return (qty, value);
    }

    /// <summary>_apply_receipt: FIFO opens a layer; weighted average re-averages the one open pool.</summary>
    private void ApplyReceipt(Book book, StockLedger row, decimal qty, decimal unitCost, decimal totalCost, string method, string? notes)
    {
        var (layerQty, layerValue) = CoverShorts(book, row, qty, totalCost);
        bool fifo = method == InventorySettingsStore.Fifo;
        var open = book.Open();
        if (layerQty <= 0)
            return;     // absorbed by the short entirely
        if (fifo || open.Count == 0)
        {
            book.Add(NewLayer(row.ProductId, row.Id, unitCost, layerQty, layerQty, layerValue, method, notes));
            return;
        }
        decimal totalQty = Sum(open.Select(l => l.QtyRemaining)) + layerQty;
        decimal totalValue = Sum(open.Select(l => l.ValueRemaining)) + layerValue;
        var keep = open[0];
        keep.QtyRemaining = totalQty;
        keep.ValueRemaining = totalValue;
        keep.UnitCost = totalQty > 0 ? Q(totalValue / totalQty) : 0m;
        keep.QtyOriginal += layerQty;
        foreach (var l in open.Skip(1))
        {
            l.QtyRemaining = 0m;
            l.ValueRemaining = 0m;
        }
    }

    /// <summary>_apply_issue: take the issue's quantity and posted value off the layers; a short layer for what no layer covered.</summary>
    private void ApplyIssue(Book book, StockLedger row, List<Take> plan, decimal uncovered, decimal unit, decimal total,
                            decimal? explicitUnit, string method)
    {
        var withdrawn = WithdrawValue(plan, total);
        for (int i = 0; i < plan.Count; i++)
        {
            decimal charged = explicitUnit ?? plan[i].Cost;
            Consumption(plan[i].Layer, row, plan[i].Qty, Q(charged), withdrawn[i]);
        }
        if (uncovered > 0)
        {
            decimal shortValue = total - Sum(withdrawn);
            var shortLayer = NewLayer(row.ProductId, row.Id, unit, -uncovered, -uncovered, -shortValue, method,
                $"Short {Py.Dec(uncovered)} unit(s) issued beyond stock on {row.VoucherNumber}; covered by a future receipt");
            book.Add(shortLayer);
            Consumption(shortLayer, row, uncovered, unit, shortValue);
        }
    }

    // ─────────────────────────────────────────────
    // Movements
    // ─────────────────────────────────────────────

    private async Task<StockLedger> WriteRowAsync(int productId, string voucherType, int voucherId, string voucherNumber,
                                                  string transactionType, decimal qty, decimal unitCost, decimal totalCost,
                                                  string notes, int createdBy, DateOnly txnDate, InventorySetting settings,
                                                  CancellationToken ct)
    {
        var (prevQty, prevCost, _) = await RunningBalanceAsync(productId, ct);
        decimal newQty, newCost;
        if (transactionType == "IN")
        {
            newQty = prevQty + qty;
            newCost = prevCost + totalCost;
        }
        else
        {
            newQty = prevQty - qty;
            newCost = prevCost - totalCost;
        }
        if (newQty == 0)
            newCost = 0m;
        var row = new StockLedger
        {
            ProductId = productId, VoucherType = voucherType, VoucherId = voucherId, VoucherNumber = voucherNumber,
            TransactionType = transactionType, Quantity = qty, UnitCost = Q(unitCost), TotalCost = Q(totalCost, 2),
            RunningQty = newQty, RunningCost = newCost, RunningAvg = newQty > 0 ? Q(newCost / newQty) : 0m,
            ValuationMethod = settings.ValuationMethod, TxnDate = txnDate, Notes = notes, CreatedBy = createdBy,
            CreatedAt = clock.UtcNow,
        };
        db.Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<decimal> RecordInAsync(int productId, string voucherType, int voucherId, string voucherNumber, decimal qty,
                                             decimal unitCost, string notes = "", int createdBy = 1, DateTime? txnDate = null,
                                             decimal? totalCost = null, CancellationToken ct = default) =>
        (await ReceiveAsync(productId, voucherType, voucherId, voucherNumber, qty, unitCost, notes, createdBy, txnDate, totalCost, ct)).TotalCost;

    /// <summary>
    /// record_in: stock received at an actual cost. <paramref name="totalCost"/> is the exact value the
    /// document posts (a purchase line's landed total); otherwise qty × the 4dp unit cost.
    /// </summary>
    public async Task<StockLedger> ReceiveAsync(int productId, string voucherType, int voucherId, string voucherNumber, decimal qty,
                                                decimal unitCost, string notes = "", int createdBy = 1, DateTime? txnDate = null,
                                                decimal? totalCost = null, CancellationToken ct = default)
    {
        if (qty <= 0)
            throw new StockQuantityException(
                $"Cannot receive {Py.Dec(qty)} of product {productId}: receipt quantity must be positive.");
        await LockProductsAsync([productId], ct);
        var settings = await SettingsAsync(ct);
        decimal unit = Q(unitCost);
        decimal total = totalCost is decimal t ? Q(t, 2) : Q(qty * unit, 2);
        var date = TxnDate(txnDate);
        bool backdated = await IsBackdatedAsync(productId, date, ct);
        var row = await WriteRowAsync(productId, voucherType, voucherId, voucherNumber, "IN", qty, unit, total, notes, createdBy,
                                      date, settings, ct);
        if (backdated)
        {
            await ReplayAsync(productId, Key(row), new HashSet<int> { row.Id }, new HashSet<int>(), null, null, voucherNumber,
                              createdBy, ct);
            Forget([productId]);
            return row;
        }
        var book = await LoadBookAsync(productId, ct);
        ApplyReceipt(book, row, qty, unit, total, InventorySettingsStore.Method(settings), notes);
        await db.SaveChangesAsync(ct);
        await SyncProductStockAsync(productId, null, book, ct);
        Forget([productId]);
        return row;
    }

    public async Task<decimal> RecordOutAsync(int productId, string voucherType, int voucherId, string voucherNumber, decimal qty,
                                              string notes = "", int createdBy = 1, decimal? unitCost = null, DateTime? txnDate = null,
                                              (string Type, int Id)? preferVoucher = null, CancellationToken ct = default) =>
        (await IssueAsync(productId, voucherType, voucherId, voucherNumber, qty, notes, createdBy, unitCost, txnDate, preferVoucher, ct)).TotalCost;

    /// <summary>
    /// record_out: stock issued at the cost the layers carry (or an explicit basis: purchase returns).
    /// Refused (NegativeStockException) when not on hand and negative stock is off.
    /// </summary>
    public async Task<(decimal UnitCost, decimal TotalCost)> IssueAsync(int productId, string voucherType, int voucherId,
        string voucherNumber, decimal qty, string notes = "", int createdBy = 1, decimal? unitCost = null, DateTime? txnDate = null,
        (string Type, int Id)? preferVoucher = null, CancellationToken ct = default)
    {
        if (qty <= 0)
            return (0m, 0m);
        await LockProductsAsync([productId], ct);
        var settings = await SettingsAsync(ct);
        var date = TxnDate(txnDate);
        if (await IsBackdatedAsync(productId, date, ct))
        {
            decimal u = 0m, tot = 0m;
            if (unitCost is decimal given)
            {
                u = Q(given);
                tot = Q(qty * u, 2);
            }
            var r = await WriteRowAsync(productId, voucherType, voucherId, voucherNumber, "OUT", qty, u, tot, notes, createdBy,
                                        date, settings, ct);
            await ReplayAsync(productId, Key(r), new HashSet<int> { r.Id },
                              unitCost is null ? new HashSet<int>() : new HashSet<int> { r.Id }, null, null, voucherNumber, createdBy, ct);
            Forget([productId]);
            return (Q(r.UnitCost), Q(r.TotalCost, 2));
        }

        var book = await LoadBookAsync(productId, ct);
        var prefer = await PreferredLayerIdsAsync(productId, preferVoucher, ct);
        var (plan, uncovered) = PlanConsumption(book, qty, prefer);
        if (uncovered > 0 && settings.AllowNegativeStock != true)
        {
            decimal onHand = (await RunningBalanceAsync(productId, ct)).Qty;
            throw new NegativeStockException(
                $"Cannot issue {Py.Dec(qty)} of product {productId}: only {Py.Dec(onHand)} on hand. The uncovered " +
                $"{Py.Dec(uncovered)} unit(s) have no acquisition cost, so any value posted for them would be invented. " +
                "Receive the stock first, or enable 'allow negative stock' in Inventory Settings.");
        }
        decimal unit, total;
        if (unitCost is decimal basis)
        {
            unit = Q(basis);
            total = Q(qty * unit, 2);
        }
        else
            (unit, total) = CostOfIssue(book, qty);
        var row = await WriteRowAsync(productId, voucherType, voucherId, voucherNumber, "OUT", qty, unit, total, notes, createdBy,
                                      date, settings, ct);
        ApplyIssue(book, row, plan, uncovered, unit, total, unitCost is null ? null : unit, InventorySettingsStore.Method(settings));
        await db.SaveChangesAsync(ct);
        await SyncProductStockAsync(productId, null, book, ct);
        Forget([productId]);
        return (unit, total);
    }

    /// <summary>_preferred_layer_ids: layers opened by the receipt (type, id) a purchase return sends back.</summary>
    private async Task<Func<StockLayer, bool>?> PreferredLayerIdsAsync(int productId, (string Type, int Id)? prefer, CancellationToken ct)
    {
        if (prefer is not { } p)
            return null;
        var receipts = db.Set<StockLedger>()
            .Where(r => r.VoucherType == p.Type && r.VoucherId == p.Id && r.ProductId == productId && r.TransactionType == "IN")
            .Select(r => (int?)r.Id);
        var ids = await db.Set<StockLayer>().Where(l => receipts.Contains(l.SourceLedgerId)).Select(l => l.Id).ToListAsync(ct);
        if (ids.Count == 0)
            return null;
        var set = ids.ToHashSet();
        return l => set.Contains(l.Id);
    }

    /// <summary>_sync_product_stock: the product's display stock and cost follow the engine.</summary>
    private async Task SyncProductStockAsync(int productId, List<StockLedger>? rows, Book book, CancellationToken ct)
    {
        var p = await db.Set<InvProduct>().FirstOrDefaultAsync(x => x.Id == productId, ct);
        if (p is null)
            return;
        decimal onHand;
        if (rows is not null)
            onHand = rows.Count == 0 ? 0.0000m : Latest(rows).RunningQty;
        else
            onHand = (await RunningBalanceAsync(productId, ct)).Qty;
        bool changed = p.CurrentStock != onHand;
        p.CurrentStock = onHand;
        decimal unit = CurrentUnitCost(book);
        if (unit > 0)
        {
            changed |= p.CostPrice != unit;
            p.CostPrice = unit;
        }
        if (changed)
            p.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>An untracked copy of a row (the replay works on copies and writes them with set-based SQL).</summary>
    private static StockLedger Copy(StockLedger r) => new()
    {
        Id = r.Id, CompanyId = r.CompanyId, ProductId = r.ProductId, VoucherType = r.VoucherType, VoucherId = r.VoucherId,
        VoucherNumber = r.VoucherNumber, TransactionType = r.TransactionType, Quantity = r.Quantity, UnitCost = r.UnitCost,
        TotalCost = r.TotalCost, RunningQty = r.RunningQty, RunningCost = r.RunningCost, RunningAvg = r.RunningAvg,
        ValuationMethod = r.ValuationMethod, TxnDate = r.TxnDate, CreatedAt = r.CreatedAt, CreatedBy = r.CreatedBy, Notes = r.Notes,
    };

    /// <summary>The row get_running_balance picks: txn_date DESC (NULLs first, as Postgres sorts), then id DESC.</summary>
    private static StockLedger Latest(List<StockLedger> rows)
    {
        var undated = rows.Where(r => r.TxnDate is null).ToList();
        if (undated.Count > 0)
            return undated.MaxBy(r => r.Id)!;
        return rows.MaxBy(r => (r.TxnDate!.Value, r.Id))!;
    }

    // ─────────────────────────────────────────────
    // Reversal
    // ─────────────────────────────────────────────

    public async Task ReverseVoucherStockAsync(string voucherType, int voucherId, int createdBy = 1, CancellationToken ct = default) =>
        await ReverseVoucherStockAsync(voucherType, voucherId, false, createdBy, ct);

    /// <summary>
    /// reverse_voucher_stock: remove a voucher's stock rows and give back what they consumed; later issues
    /// are re-costed (and journalled). A receipt whose stock was issued since is refused unless
    /// <paramref name="allowVariance"/> (correcting an earlier purchase).
    /// </summary>
    public async Task<IReadOnlyDictionary<int, decimal>> ReverseVoucherStockAsync(string voucherType, int voucherId, bool allowVariance,
        int createdBy = 1, CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct);
        var rows = await db.Set<StockLedger>().Where(r => r.VoucherType == voucherType && r.VoucherId == voucherId)
            .OrderBy(r => r.Id).ToListAsync(ct);
        if (rows.Count == 0)
            return new Dictionary<int, decimal>();
        var productIds = rows.Select(r => r.ProductId).Distinct().ToList();
        await LockProductsAsync(productIds, ct);
        rows = await db.Set<StockLedger>().Where(r => r.VoucherType == voucherType && r.VoucherId == voucherId)
            .OrderBy(r => r.Id).ToListAsync(ct);
        string voucherNumber = rows[0].VoucherNumber;
        var settings = await SettingsAsync(ct);

        var dependents = await ConsumersOfVoucherAsync(voucherType, voucherId, ct);
        if (dependents.Count > 0 && !allowVariance)
        {
            string listed = string.Join(", ", dependents.Take(5).Select(d => $"{d.VoucherType} {d.VoucherNumber} ({Py.Dec(d.Qty)})"));
            string more = dependents.Count > 5 ? $" and {dependents.Count - 5} more" : "";
            throw new ConsumedLayerException(
                $"Cannot reverse {voucherType} #{voucherId}: its stock has already been issued by {listed}{more}. Those issues " +
                "posted a cost drawn from this receipt, and that posted cost cannot change. Reverse them first, then reverse this receipt.");
        }

        if (!allowVariance)
            foreach (var r in rows.Where(r => r.TransactionType == "IN"))
            {
                decimal available = (await RunningBalanceAsync(r.ProductId, ct)).Qty;
                if (r.Quantity > available)
                    throw new ConsumedLayerException(
                        $"Cannot reverse {voucherType} #{voucherId}: it received {Py.Dec(r.Quantity)} unit(s) of product {r.ProductId} " +
                        $"but only {Py.Dec(available)} remain on hand, so part of it has already been issued at a cost that is now posted " +
                        "and cannot change. Reverse the issues that consumed it first.");
                if (!IsFifo(settings))
                {
                    var sums = await db.Set<StockLedger>().Where(x => x.ProductId == r.ProductId)
                        .GroupBy(x => x.TransactionType).Select(g => new { g.Key, Total = g.Sum(x => x.TotalCost) }).ToListAsync(ct);
                    decimal received = sums.Where(s => s.Key == "IN").Sum(s => s.Total);
                    decimal issued = sums.Where(s => s.Key == "OUT").Sum(s => s.Total);
                    if (issued > received - r.TotalCost)
                        throw new ConsumedLayerException(
                            $"Cannot reverse {voucherType} #{voucherId}: {Py.Dec(issued)} of product {r.ProductId} has already been " +
                            $"issued at a posted cost, but only {Py.Dec(received - r.TotalCost)} of received value would survive this " +
                            "reversal. Reverse the issues first, or reverse with a cost variance.");
                }
            }

        // This voucher's own past cost adjustments belong to it and go with it.
        await ledger.ReverseAsync($"CADJ-{voucherType}", voucherId, createdBy, ct);

        var thresholds = new Dictionary<int, (DateOnly, int)>();
        foreach (var r in rows)
            if (!thresholds.TryGetValue(r.ProductId, out var k) || Compare(Key(r), k) < 0)
                thresholds[r.ProductId] = Key(r);
        var previouslyShort = new Dictionary<int, HashSet<int>>();
        foreach (int pid in productIds)
            previouslyShort[pid] = await ShortSourcesAsync(pid, ct);
        foreach (int pid in productIds)
            await WipeLayersAsync(pid, ct);
        var ids = rows.Select(r => r.Id).ToList();
        foreach (var r in rows)
            db.Entry(r).State = EntityState.Detached;
        await db.Set<StockLedger>().Where(r => ids.Contains(r.Id)).ExecuteDeleteAsync(ct);

        bool allowShort = allowVariance || settings.AllowNegativeStock == true;
        var moved = new Dictionary<int, decimal>();
        foreach (int pid in productIds)
        {
            var adjustments = await ReplayAsync(pid, thresholds[pid], new HashSet<int>(), new HashSet<int>(), allowShort,
                                                previouslyShort[pid], $"reversal of {voucherNumber}", createdBy, ct);
            decimal total = Sum(adjustments.Select(a => a.New - a.Old));
            if (total != 0)
                moved[pid] = total;
        }
        Forget(productIds);
        return moved;
    }

    // ─────────────────────────────────────────────
    // Date-ordered replay
    // ─────────────────────────────────────────────

    /// <summary>What a replay writes: built in memory, written with a few set-based statements.</summary>
    private sealed class Bulk
    {
        public List<StockLayer> Layers { get; } = [];
        public List<(LayerConsumption Row, StockLayer Layer)> Consumptions { get; } = [];
    }

    private Bulk? _bulk;

    private async Task<int[]> NextIdsAsync(string table, int n, CancellationToken ct)
    {
        var ids = await db.Database.SqlQueryRaw<long>(
                "SELECT nextval(pg_get_serial_sequence({0}, 'id')) AS \"Value\" FROM generate_series(1, {1})", table, n)
            .ToListAsync(ct);
        return ids.Order().Select(i => (int)i).ToArray();
    }

    /// <summary>
    /// The replay's layers and consumptions inserted, and its re-costed rows updated, in one statement
    /// each. Ids are taken in creation order, so the layers' id order is the consumption order, as the
    /// Python app's flush-per-layer gives it.
    /// </summary>
    private async Task WriteReplayAsync(int productId, List<StockLedger> rows, Dictionary<int, string> before, Bulk bulk,
                                        CancellationToken ct)
    {
        int cid = db.Company.CompanyId ?? throw new NoActiveCompanyException("stock replay with no active company");
        // Naive UTC, as every created_at holds it (passed as text: a DateTime parameter would be timestamptz).
        string now = clock.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.ffffff", Inv);
        if (bulk.Layers.Count > 0)
        {
            var ids = await NextIdsAsync("stock_layers", bulk.Layers.Count, ct);
            for (int i = 0; i < ids.Length; i++)
                bulk.Layers[i].Id = ids[i];
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO stock_layers (id, company_id, product_id, source_ledger_id, unit_cost, qty_original, qty_remaining,
                                          value_remaining, method, is_revaluation, created_at, notes)
                SELECT v.id, {0}, {1}, v.src, v.uc, v.qo, v.qr, v.vr, v.m, false, {2}::timestamp, v.n
                FROM unnest({3}::int[], {4}::int[], {5}::numeric[], {6}::numeric[], {7}::numeric[], {8}::numeric[], {9}::text[], {10}::text[])
                     AS v(id, src, uc, qo, qr, vr, m, n)
                ORDER BY v.id
                """,
                [cid, productId, now, ids, bulk.Layers.Select(l => l.SourceLedgerId).ToArray(),
                 bulk.Layers.Select(l => l.UnitCost).ToArray(), bulk.Layers.Select(l => l.QtyOriginal).ToArray(),
                 bulk.Layers.Select(l => l.QtyRemaining).ToArray(), bulk.Layers.Select(l => l.ValueRemaining).ToArray(),
                 bulk.Layers.Select(l => l.Method).ToArray(), bulk.Layers.Select(l => l.Notes).ToArray()], ct);
        }
        if (bulk.Consumptions.Count > 0)
        {
            var ids = await NextIdsAsync("layer_consumptions", bulk.Consumptions.Count, ct);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO layer_consumptions (id, company_id, layer_id, out_ledger_id, product_id, qty, unit_cost, total_cost, created_at)
                SELECT v.id, {0}, v.layer, v.out, {1}, v.q, v.uc, v.tc, {2}::timestamp
                FROM unnest({3}::int[], {4}::int[], {5}::int[], {6}::numeric[], {7}::numeric[], {8}::numeric[]) AS v(id, layer, out, q, uc, tc)
                ORDER BY v.id
                """,
                [cid, productId, now, ids, bulk.Consumptions.Select(c => c.Layer.Id).ToArray(),
                 bulk.Consumptions.Select(c => c.Row.OutLedgerId).ToArray(), bulk.Consumptions.Select(c => c.Row.Qty).ToArray(),
                 bulk.Consumptions.Select(c => c.Row.UnitCost).ToArray(), bulk.Consumptions.Select(c => c.Row.TotalCost).ToArray()], ct);
        }
        var changed = rows.Where(r => before[r.Id] != RowCosts(r)).ToList();
        if (changed.Count > 0)
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE stock_ledger AS s
                SET unit_cost = v.uc, total_cost = v.tc, running_qty = v.rq, running_cost = v.rc, running_avg = v.ra
                FROM unnest({0}::int[], {1}::numeric[], {2}::numeric[], {3}::numeric[], {4}::numeric[], {5}::numeric[])
                     AS v(id, uc, tc, rq, rc, ra)
                WHERE s.id = v.id
                """,
                [changed.Select(r => r.Id).ToArray(), changed.Select(r => r.UnitCost).ToArray(), changed.Select(r => r.TotalCost).ToArray(),
                 changed.Select(r => r.RunningQty).ToArray(), changed.Select(r => r.RunningCost).ToArray(),
                 changed.Select(r => r.RunningAvg).ToArray()], ct);
        // A row this context tracks (the one being posted now) shows what the replay made of it.
        var byId = rows.ToDictionary(r => r.Id);
        foreach (var e in db.ChangeTracker.Entries<StockLedger>().ToList())
            if (byId.TryGetValue(e.Entity.Id, out var r))
            {
                e.Entity.UnitCost = r.UnitCost;
                e.Entity.TotalCost = r.TotalCost;
                e.Entity.RunningQty = r.RunningQty;
                e.Entity.RunningCost = r.RunningCost;
                e.Entity.RunningAvg = r.RunningAvg;
                e.State = EntityState.Unchanged;
            }
    }

    private static string RowCosts(StockLedger r) =>
        string.Join("|", Q(r.UnitCost), Q(r.TotalCost), Q(r.RunningQty), Q(r.RunningCost), Q(r.RunningAvg));

    /// <summary>_short_sources: rows whose short the company accepted (not ones pending a corrected receipt).</summary>
    private async Task<HashSet<int>> ShortSourcesAsync(int productId, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        var shorts = await db.Set<StockLayer>().AsNoTracking()
            .Where(l => l.ProductId == productId && l.QtyOriginal < 0)
            .Select(l => new { l.SourceLedgerId, l.Notes }).ToListAsync(ct);
        return shorts.Where(s => s.SourceLedgerId is > 0 && !(s.Notes ?? "").Contains(PendingShort))
            .Select(s => s.SourceLedgerId!.Value).ToHashSet();
    }

    private async Task WipeLayersAsync(int productId, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        foreach (var e in db.ChangeTracker.Entries().ToList())
            if (e.Entity is StockLayer l && l.ProductId == productId || e.Entity is LayerConsumption c && c.ProductId == productId)
                e.State = EntityState.Detached;
        await db.Set<LayerConsumption>().Where(c => c.ProductId == productId).ExecuteDeleteAsync(ct);
        await db.Set<StockLayer>().Where(l => l.ProductId == productId).ExecuteDeleteAsync(ct);
    }

    /// <summary>_collapse_pool: weighted average holds one pool; fold any extra positive layers in.</summary>
    private static void CollapsePool(Book book)
    {
        var open = book.Open();
        if (open.Count <= 1)
            return;
        decimal qty = Sum(open.Select(l => l.QtyRemaining));
        decimal value = Sum(open.Select(l => l.ValueRemaining));
        var keep = open[0];
        keep.QtyRemaining = qty;
        keep.ValueRemaining = value;
        keep.UnitCost = qty > 0 ? Q(value / qty) : 0m;
        foreach (var l in open.Skip(1))
        {
            l.QtyRemaining = 0m;
            l.ValueRemaining = 0m;
        }
    }

    /// <summary>_shift_value: a value-only movement (legacy VAR rows) on the newest open layer.</summary>
    private static void ShiftValue(Book book, decimal amount)
    {
        var open = book.Open();
        if (open.Count > 0 && amount != 0)
            open[^1].ValueRemaining += amount;
    }

    private sealed record ReturnDocs(Dictionary<int, int> SalesReturnInvoice, Dictionary<int, int> PurchaseReturnInvoice);

    private async Task<ReturnDocs> ReturnDocsAsync(List<StockLedger> rows, CancellationToken ct)
    {
        var srv = rows.Where(r => r.VoucherType == "SRV").Select(r => r.VoucherId).Distinct().ToList();
        var prv = rows.Where(r => r.VoucherType == "PRV").Select(r => r.VoucherId).Distinct().ToList();
        var sales = srv.Count == 0 ? new Dictionary<int, int>()
            : await db.Set<InvSalesReturn>().Where(x => srv.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.OriginalInvoiceId, ct);
        var purchases = prv.Count == 0 ? new Dictionary<int, int>()
            : await db.Set<InvPurchaseReturn>().Where(x => prv.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.OriginalInvoiceId, ct);
        return new ReturnDocs(sales, purchases);
    }

    /// <summary>
    /// _replay: rebuild a product's layers from its ledger in (txn_date, id) order. Rows before
    /// <paramref name="threshold"/> are frozen; later ones are re-costed. <paramref name="newIds"/> are rows being
    /// posted now (no journal yet). Returns [(row, old, new)] for every posted row whose cost moved, journalled.
    /// </summary>
    private async Task<List<(StockLedger Row, decimal Old, decimal New)>> ReplayAsync(int productId, (DateOnly, int) threshold,
        HashSet<int> newIds, HashSet<int> explicitIds, bool? allowShort, HashSet<int>? previouslyShort, string trigger,
        int createdBy, CancellationToken ct)
    {
        var settings = await SettingsAsync(ct);
        bool shortOk = allowShort ?? settings.AllowNegativeStock == true;
        previouslyShort ??= await ShortSourcesAsync(productId, ct);
        await WipeLayersAsync(productId, ct);
        // Read as stored (the row being posted now included: its tracked copy is brought up to date after).
        var rows = await db.Set<StockLedger>().AsNoTracking().Where(r => r.ProductId == productId)
            .OrderBy(r => r.TxnDate).ThenBy(r => r.Id).ToListAsync(ct);
        foreach (var tracked in db.ChangeTracker.Entries<StockLedger>().Where(e => e.Entity.ProductId == productId))
            if (rows.FindIndex(r => r.Id == tracked.Entity.Id) is int at and >= 0)
                rows[at] = Copy(tracked.Entity);
        var before = rows.ToDictionary(r => r.Id, RowCosts);
        var docs = await ReturnDocsAsync(rows, ct);
        var book = new Book(productId);
        var adjustments = new List<(StockLedger Row, decimal Old, decimal New)>();
        string settingsMethod = InventorySettingsStore.Method(settings);
        var bulk = _bulk = new Bulk();
        try
        {

        foreach (var r in rows)
        {
            bool frozen = Compare(Key(r), threshold) < 0 && !newIds.Contains(r.Id);
            string method = string.IsNullOrEmpty(r.ValuationMethod) ? settingsMethod : r.ValuationMethod;
            if (method != InventorySettingsStore.Fifo)
                CollapsePool(book);
            decimal qty = r.Quantity;

            if (r.TransactionType == "IN")
            {
                if (qty <= 0)
                {
                    ShiftValue(book, r.TotalCost);
                    continue;
                }
                decimal unit = r.UnitCost, total = r.TotalCost;
                if (!frozen && !newIds.Contains(r.Id) && InRecostTypes.Contains(r.VoucherType))
                {
                    decimal? basis = ResolveInCost(r, rows, docs);
                    if (basis is decimal b)
                    {
                        decimal newTotal = Q(qty * b, 2);
                        if (newTotal != Q(total, 2))
                            adjustments.Add((r, Q(total, 2), newTotal));
                        unit = Q(b);
                        total = newTotal;
                        r.UnitCost = unit;
                        r.TotalCost = total;
                    }
                }
                ApplyReceipt(book, r, qty, unit, total, method, r.Notes);
                continue;
            }

            if (qty <= 0)
            {
                ShiftValue(book, -r.TotalCost);
                continue;
            }
            var prefer = PreferredInReplay(r, rows, book, docs);
            var (plan, uncovered) = PlanConsumption(book, qty, prefer);
            if (uncovered > 0 && !shortOk && !frozen && (newIds.Contains(r.Id) || !previouslyShort.Contains(r.Id)))
            {
                string when = r.TxnDate is DateOnly d ? d.ToString("dd MMM yyyy", Inv) : "its date";
                string msg = newIds.Contains(r.Id)
                    ? $"Cannot issue {Py.Normalize(qty)} of product {productId} on {when}: only {Py.Normalize(qty - uncovered)} were on hand at that date."
                    : $"Cannot post this change: {r.VoucherType} {r.VoucherNumber} dated {when} would be left {Py.Normalize(uncovered)} " +
                      "unit(s) short -- the stock it issued would no longer have been on hand.";
                throw new NegativeStockException(msg + " Receive the stock first, or enable 'allow negative stock' in Inventory Settings.");
            }
            bool isExplicit = frozen || ExplicitBasisTypes.Contains(r.VoucherType) || explicitIds.Contains(r.Id);
            decimal u2, t2;
            if (isExplicit)
            {
                u2 = r.UnitCost;
                t2 = r.TotalCost;
            }
            else
            {
                decimal cost = 0m;
                foreach (var t in plan) cost += t.Qty * t.Cost;
                if (uncovered > 0)
                {
                    decimal fallback = !newIds.Contains(r.Id) && r.UnitCost > 0 ? r.UnitCost : FallbackCost(book, rows);
                    cost += uncovered * fallback;
                }
                u2 = Q(cost / qty);
                t2 = Q(cost, 2);
                if (!newIds.Contains(r.Id) && t2 != Q(r.TotalCost, 2))
                    adjustments.Add((r, Q(r.TotalCost, 2), t2));
                r.UnitCost = u2;
                r.TotalCost = t2;
            }
            ApplyIssue(book, r, plan, uncovered, u2, t2, isExplicit ? u2 : null, method);
        }

        if (shortOk && settings.AllowNegativeStock != true)
            foreach (var l in book.Layers.Where(l => l.QtyRemaining < 0))
                if (l.SourceLedgerId is not int src || !previouslyShort.Contains(src))
                    l.Notes = $"{l.Notes ?? ""} {PendingShort}".Trim();
        RebuildRunning(rows);
        }
        finally
        {
            _bulk = null;
        }
        await WriteReplayAsync(productId, rows, before, bulk, ct);
        await SyncProductStockAsync(productId, rows, book, ct);
        if (adjustments.Count > 0)
            await PostAdjustmentsAsync(adjustments, trigger, createdBy, ct);
        return adjustments;
    }

    /// <summary>_preferred_layer_ids inside a replay: the layers the replay opened from that invoice's receipts.</summary>
    private static Func<StockLayer, bool>? PreferredInReplay(StockLedger r, List<StockLedger> rows, Book book, ReturnDocs docs)
    {
        if (r.VoucherType != "PRV" || !docs.PurchaseReturnInvoice.TryGetValue(r.VoucherId, out int invoice) || invoice == 0)
            return null;
        var receipts = rows.Where(x => x.VoucherType == "PI" && x.VoucherId == invoice && x.TransactionType == "IN")
            .Select(x => x.Id).ToHashSet();
        if (receipts.Count == 0)
            return null;
        var layers = book.Layers.Where(l => l.SourceLedgerId is int s && receipts.Contains(s)).ToList();
        if (layers.Count == 0)
            return null;
        var set = layers.ToHashSet(ReferenceEqualityComparer.Instance);
        return l => set.Contains(l);
    }

    /// <summary>_resolve_in_cost: a sales return comes back at what its sale (as re-costed) left at.</summary>
    private static decimal? ResolveInCost(StockLedger r, List<StockLedger> rows, ReturnDocs docs)
    {
        if (r.VoucherType != "SRV" || !docs.SalesReturnInvoice.TryGetValue(r.VoucherId, out int invoice) || invoice == 0)
            return null;
        return rows.Where(x => x.VoucherType == "SI" && x.VoucherId == invoice && x.TransactionType == "OUT")
            .OrderBy(x => x.Id).Select(x => (decimal?)x.UnitCost).FirstOrDefault();
    }

    /// <summary>_fallback_cost: the last known layer cost, else the first receipt's.</summary>
    private static decimal FallbackCost(Book book, List<StockLedger> rows)
    {
        decimal unit = CurrentUnitCost(book);
        if (unit > 0)
            return unit;
        var first = rows.FirstOrDefault(x => x.TransactionType == "IN" && x.Quantity > 0 && x.UnitCost > 0);
        return first?.UnitCost ?? 0m;
    }

    /// <summary>rebuild_running: running qty / cost / avg from the rows in document-date order.</summary>
    private static void RebuildRunning(List<StockLedger> rows)
    {
        decimal qty = 0m, cost = 0m;
        foreach (var r in rows)
        {
            if (r.TransactionType == "IN")
            {
                qty += r.Quantity;
                cost += r.TotalCost;
            }
            else
            {
                qty -= r.Quantity;
                cost -= r.TotalCost;
            }
            if (qty == 0)
                cost = 0m;
            r.RunningQty = qty;
            r.RunningCost = cost;
            r.RunningAvg = qty > 0 ? Q(cost / qty) : 0m;
        }
    }

    /// <summary>
    /// Re-cost a product's whole history as if every row were being posted now: costs are set, no
    /// adjustment is journalled. For stock history loaded in bulk (and the speed tests' set-up).
    /// </summary>
    internal async Task RecostFromStartAsync(int productId, int createdBy = 1, CancellationToken ct = default)
    {
        await LockProductsAsync([productId], ct);
        var ids = await db.Set<StockLedger>().Where(r => r.ProductId == productId).Select(r => r.Id).ToListAsync(ct);
        await ReplayAsync(productId, (DateOnly.MinValue, 0), ids.ToHashSet(), new HashSet<int>(), null, null, "rebuild", createdBy, ct);
        Forget([productId]);
    }

    /// <summary>rebuild_running for one product (the running columns only; posted costs never change).</summary>
    public async Task RebuildRunningAsync(int productId, CancellationToken ct = default)
    {
        await LockProductsAsync([productId], ct);
        var rows = await OrderedRowsAsync(productId, ct);
        RebuildRunning(rows);
        await db.SaveChangesAsync(ct);
        await SyncProductStockAsync(productId, rows, await LoadBookAsync(productId, ct), ct);
        Forget([productId]);
    }

    // ─────────────────────────────────────────────
    // Valuation method switch
    // ─────────────────────────────────────────────

    /// <summary>revalue_for_method_change: FIFO → WA collapses each product's open layers at book value; WA → FIFO carries the pool.</summary>
    public async Task RevalueForMethodChangeAsync(string newMethod, int createdBy = 1, CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct);
        var products = await db.Set<StockLayer>().Where(l => l.QtyRemaining > 0).Select(l => l.ProductId).Distinct().ToListAsync(ct);
        await LockProductsAsync(products, ct);
        if (newMethod == InventorySettingsStore.Fifo || products.Count == 0)
            return;
        var open = await db.Set<StockLayer>().Where(l => l.QtyRemaining > 0).OrderBy(l => l.Id).ToListAsync(ct);
        foreach (var group in open.GroupBy(l => l.ProductId))
        {
            var layers = group.ToList();
            if (layers.Count <= 1)
                continue;
            decimal qty = Sum(layers.Select(l => l.QtyRemaining));
            decimal value = Sum(layers.Select(l => l.ValueRemaining));
            var keep = layers[0];
            keep.QtyRemaining = qty;
            keep.QtyOriginal = qty;
            keep.ValueRemaining = value;
            keep.UnitCost = qty > 0 ? Q(value / qty) : 0m;
            keep.Method = newMethod;
            keep.IsRevaluation = true;
            keep.Notes = $"Revaluation: collapsed {layers.Count} layers on switch to {newMethod} at book value {Py.Dec(Q(value, 2))}";
            foreach (var l in layers.Skip(1))
            {
                l.QtyRemaining = 0m;
                l.ValueRemaining = 0m;
            }
        }
        await db.SaveChangesAsync(ct);
        Forget(products);
    }

    // ─────────────────────────────────────────────
    // Re-costing journals (shared/cost_adjustment.py)
    // ─────────────────────────────────────────────

    private sealed class Accounts(StockCosting owner)
    {
        private readonly Dictionary<string, int> _cache = [];

        public async Task<int> RoleAsync(string role, CancellationToken ct)
        {
            if (!_cache.TryGetValue("role:" + role, out int id))
                _cache["role:" + role] = id = (await owner.Ledger.PostingAccountAsync(role, ct)).Id;
            return id;
        }

        public async Task<int> CodeAsync(string code, string name, string type, CancellationToken ct)
        {
            if (!_cache.TryGetValue("code:" + code, out int id))
                _cache["code:" + code] = id = (await owner.Ledger.GetOrCreateAccountAsync(code, name, type, null, ct)).Id;
            return id;
        }
    }

    /// <summary>adjustment_date: the issue's own date while its period is open, else today.</summary>
    private DateOnly AdjustmentDate(DateOnly? issueDate, IReadOnlyList<AccountingPeriod> periodsList)
    {
        var d = issueDate ?? clock.Today;
        var p = periodsList.Where(x => x.StartDate <= d && x.EndDate >= d).OrderByDescending(x => x.StartDate).FirstOrDefault();
        return p is not null && p.IsClosed == true ? clock.Today : d;
    }

    /// <summary>
    /// post_adjustments: one journal per (entry date, voucher), keyed CADJ-&lt;type&gt; / &lt;voucher id&gt; so reversing
    /// that voucher later un-posts its adjustments with it. Built in memory and saved together.
    /// </summary>
    private async Task PostAdjustmentsAsync(List<(StockLedger Row, decimal Old, decimal New)> adjustments, string trigger, int createdBy,
                                            CancellationToken ct)
    {
        var periodsList = await db.Set<AccountingPeriod>().AsNoTracking().ToListAsync(ct);
        var groups = new Dictionary<(DateOnly, string, int), List<(StockLedger Row, decimal Old, decimal New, decimal Delta)>>();
        foreach (var (row, old, @new) in adjustments)
        {
            decimal delta = Q(@new, 2) - Q(old, 2);
            if (delta == 0)
                continue;
            var key = (AdjustmentDate(row.TxnDate, periodsList), row.VoucherType, row.VoucherId);
            if (!groups.TryGetValue(key, out var list))
            {
                groups[key] = list = [];
            }
            list.Add((row, Q(old, 2), Q(@new, 2), delta));
        }
        if (groups.Count == 0)
            return;
        var keys = groups.Keys.OrderBy(k => k.Item1).ThenBy(k => k.Item2, StringComparer.Ordinal).ThenBy(k => k.Item3).ToList();

        var counters = await CounterAccountsAsync(groups.Values.SelectMany(g => g.Select(x => x.Row)).ToList(), ct);
        var accounts = new Accounts(this);
        var follow = new List<(StockLedger Row, decimal New)>();
        var built = new List<(DateOnly When, string Type, int Id, string Number, List<JournalLineInput> Lines, List<StockCostAdjustment> Records)>();
        var entries = new List<(JournalEntry Entry, List<StockCostAdjustment> Records)>();
        foreach (var key in keys)
        {
            var (when, vtype, vid) = key;
            var lines = new List<JournalLineInput>();
            var records = new List<StockCostAdjustment>();
            foreach (var (row, old, @new, delta) in groups[key])
            {
                var (counter, label) = await counters(row, accounts);
                int stock = await InventoryAccountAsync(row, accounts, ct);
                decimal amount = Math.Abs(delta);
                // More cost on an issue is a further charge to its account; on a receipt that mirrors
                // an issue (a sales return) it is more stock.
                bool moreCharge = (delta > 0) == (row.TransactionType == "OUT");
                var (dr, cr) = moreCharge ? (counter, stock) : (stock, counter);
                string text = $"Cost adjustment {row.VoucherNumber}: {Py.Dec(old)} -> {Py.Dec(@new)} ({trigger})";
                lines.Add(new JournalLineInput(dr, amount, 0, text, label));
                lines.Add(new JournalLineInput(cr, 0, amount, text, label));
                records.Add(new StockCostAdjustment
                {
                    ProductId = row.ProductId, LedgerId = row.Id, VoucherType = row.VoucherType, VoucherId = row.VoucherId,
                    VoucherNumber = row.VoucherNumber, OldCost = old, NewCost = @new, Delta = delta,
                    Trigger = Py.Cut(trigger ?? "", 120), EntryDate = when, CreatedAt = clock.UtcNow,
                });
                follow.Add((row, @new));
            }
            if (lines.Count > 0)
                built.Add((when, vtype, vid, groups[key][0].Row.VoucherNumber, lines, records));
        }

        // post_journal_entry's checks, in its order: postable accounts, then the period (FOR SHARE), once per date.
        var accountIds = built.SelectMany(b => b.Lines.Select(l => l.AccountId)).Distinct().ToList();
        var known = await db.Set<ChartOfAccount>().AsNoTracking().Where(a => accountIds.Contains(a.Id))
            .Select(a => new { a.Id, a.Code, a.Name, a.Level }).ToDictionaryAsync(a => a.Id, ct);
        // require_open_period takes FOR SHARE on each entry's period: here every period the entries fall in,
        // at once and in id order, then each entry is checked against its period as Python checks it.
        AccountingPeriod? PeriodOf(DateOnly d) =>
            periodsList.Where(x => x.StartDate <= d && x.EndDate >= d).OrderByDescending(x => x.StartDate).FirstOrDefault();
        var periodIds = built.Select(b => PeriodOf(b.When)?.Id).OfType<int>().Distinct().Order().ToArray();
        var closed = new HashSet<int>();
        if (periodIds.Length > 0)
        {
            if (db.Database.IsNpgsql())
                await db.Database.ExecuteSqlRawAsync(
                    "SELECT 1 FROM accounting_periods WHERE id = ANY({0}) ORDER BY id FOR SHARE", [periodIds], ct);
            closed = (await db.Set<AccountingPeriod>().AsNoTracking().Where(x => periodIds.Contains(x.Id) && x.IsClosed == true)
                .Select(x => x.Id).ToListAsync(ct)).ToHashSet();
        }
        foreach (var b in built)
        {
            string voucherNumber = Py.Cut($"CADJ-{b.Number}", 50);
            foreach (var l in b.Lines)
            {
                if (!known.TryGetValue(l.AccountId, out var a))
                    throw new BusinessRuleException($"Journal for {voucherNumber} references unknown account id {l.AccountId}");
                if (a.Level < 5)
                    throw new BusinessRuleException(
                        $"Cannot post to aggregating account {a.Code} {a.Name} (level {a.Level}); post to a level-5 operational account.");
            }
            var entryDate = b.When.ToDateTime(TimeOnly.MinValue);
            if (PeriodOf(b.When) is { } period && closed.Contains(period.Id))
                await periods.RequireOpenAsync(entryDate, $"post {voucherNumber} into", ct);     // refuses in Python's words
            var je = new JournalEntry
            {
                VoucherType = $"CADJ-{b.Type}", VoucherId = b.Id, VoucherNumber = voucherNumber,
                Description = $"Inventory cost adjustment of {b.Number} — {trigger}", EntryDate = entryDate,
                CreatedBy = createdBy, CreatedAt = clock.UtcNow, IsPosted = true,
            };
            foreach (var l in b.Lines)
                je.JournalLines.Add(new JournalLine
                {
                    AccountId = l.AccountId, Debit = l.Debit, Credit = l.Credit, Description = l.Description ?? "", LabelId = l.LabelId,
                });
            db.Add(je);
            entries.Add((je, b.Records));
        }
        await FollowDocumentsAsync(follow, ct);
        // The journals through EF (gl_daily_balances and the audit trail follow every journal saved);
        // their adjustment records, which nothing else watches, in one statement.
        await db.SaveChangesAsync(ct);
        var saved = entries.SelectMany(e => e.Records.Select(r => (r, e.Entry.Id))).ToList();
        if (saved.Count > 0)
        {
            int cid = db.Company.CompanyId ?? throw new NoActiveCompanyException("cost adjustments with no active company");
            string now = clock.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.ffffff", Inv);
            var ids = await NextIdsAsync("stock_cost_adjustments", saved.Count, ct);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO stock_cost_adjustments (id, company_id, product_id, ledger_id, voucher_type, voucher_id, voucher_number,
                                                    old_cost, new_cost, delta, trigger, journal_entry_id, entry_date, created_at)
                SELECT v.id, {0}, v.p, v.l, v.vt, v.vid, v.vn, v.o, v.n, v.d, v.t, v.je, v.ed, {1}::timestamp
                FROM unnest({2}::int[], {3}::int[], {4}::int[], {5}::text[], {6}::int[], {7}::text[], {8}::numeric[], {9}::numeric[],
                            {10}::numeric[], {11}::text[], {12}::int[], {13}::date[]) AS v(id, p, l, vt, vid, vn, o, n, d, t, je, ed)
                ORDER BY v.id
                """,
                [cid, now, ids, saved.Select(x => x.r.ProductId).ToArray(), saved.Select(x => x.r.LedgerId).ToArray(),
                 saved.Select(x => x.r.VoucherType).ToArray(), saved.Select(x => x.r.VoucherId).ToArray(),
                 saved.Select(x => x.r.VoucherNumber).ToArray(), saved.Select(x => x.r.OldCost).ToArray(),
                 saved.Select(x => x.r.NewCost).ToArray(), saved.Select(x => x.r.Delta).ToArray(),
                 saved.Select(x => x.r.Trigger).ToArray(), saved.Select(x => x.Item2).ToArray(),
                 saved.Select(x => x.r.EntryDate).ToArray()], ct);
        }
    }

    /// <summary>_counter_account, batched: (account, label) each re-costed row's voucher charged its cost to.</summary>
    private async Task<Func<StockLedger, Accounts, Task<(int Account, int? Label)>>> CounterAccountsAsync(List<StockLedger> rows,
                                                                                                          CancellationToken ct)
    {
        List<int> Ids(string t) => rows.Where(r => r.VoucherType == t).Select(r => r.VoucherId).Distinct().ToList();
        var si = Ids("SI");
        var siItems = si.Count == 0 ? []
            : await db.Set<InvInvoiceItem>().AsNoTracking().Where(i => si.Contains(i.InvoiceId))
                .OrderBy(i => i.Id).Select(i => new { i.InvoiceId, i.ProductId, i.LabelId }).ToListAsync(ct);
        var cons = Ids("CONS");
        var consV = cons.Count == 0 ? new Dictionary<int, (int?, int?)>()
            : await db.Set<ConsumptionVoucher>().AsNoTracking().Where(v => cons.Contains(v.Id))
                .ToDictionaryAsync(v => v.Id, v => (v.ChargeAccountId, v.LabelId), ct);
        var scrap = Ids("SCRAP");
        var scrapV = scrap.Count == 0 ? new Dictionary<int, (int?, int?)>()
            : await db.Set<ScrapVoucher>().AsNoTracking().Where(v => scrap.Contains(v.Id))
                .ToDictionaryAsync(v => v.Id, v => (v.ChargeAccountId, v.LabelId), ct);
        var adj = Ids("ADJ");
        var adjV = adj.Count == 0 ? new Dictionary<int, int?>()
            : await db.Set<StockAdjustmentVoucher>().AsNoTracking().Where(v => adj.Contains(v.Id))
                .ToDictionaryAsync(v => v.Id, v => v.LabelId, ct);
        var cap = Ids("FA-CAP");
        var capAssets = cap.Count == 0 ? new Dictionary<int, (int?, int?)>()
            : await (from t in db.Set<AssetTransfer>().AsNoTracking()
                     where cap.Contains(t.Id)
                     join a in db.Set<FixedAsset>().AsNoTracking() on t.AssetId equals a.Id
                     select new { t.Id, a.FixedAssetAccountId, a.LabelId })
                .ToDictionaryAsync(x => x.Id, x => (x.FixedAssetAccountId, x.LabelId), ct);

        return async (row, accounts) =>
        {
            int vid = row.VoucherId;
            switch (row.VoucherType)
            {
                case "SI" or "SRV":
                    int? label = row.VoucherType == "SI"
                        ? siItems.FirstOrDefault(i => i.InvoiceId == vid && i.ProductId == row.ProductId)?.LabelId
                        : null;
                    return (await accounts.RoleAsync("cogs", ct), label);
                case "CONS" or "SCRAP":
                    var v = (row.VoucherType == "CONS" ? consV : scrapV).GetValueOrDefault(vid);
                    if (v.Item1 is int charge and not 0)
                        return (charge, v.Item2);
                    return row.VoucherType == "CONS"
                        ? (await accounts.CodeAsync("5700", "Consumption Expense", "expense", ct), null)
                        : (await accounts.CodeAsync("5800", "Scrap/Write-off", "expense", ct), null);
                case "ADJ":
                    return (await accounts.CodeAsync("5900", "Inventory Adjustment", "expense", ct), adjV.GetValueOrDefault(vid));
                case "FA-CAP" when capAssets.TryGetValue(vid, out var asset) && asset.Item1 is int fa and not 0:
                    return (fa, asset.Item2);
                default:
                    return (await accounts.RoleAsync("inventory_variance", ct), null);
            }
        };
    }

    /// <summary>_inventory_account: the stock account the voucher behind the row moved.</summary>
    private async Task<int> InventoryAccountAsync(StockLedger row, Accounts accounts, CancellationToken ct)
    {
        if (row.VoucherType == "FA-CAP")
        {
            var p = await db.Set<InvProduct>().AsNoTracking().Where(x => x.Id == row.ProductId)
                .Select(x => new { x.Id, x.Name }).FirstOrDefaultAsync(ct);
            if (p is not null)
                return (await ledger.EntityAccountAsync("product", p.Id, p.Name, ct)).Id;
        }
        return await accounts.RoleAsync("inventory", ct);
    }

    /// <summary>_follow_documents: the source document's own cost figures follow the ledger.</summary>
    private async Task FollowDocumentsAsync(List<(StockLedger Row, decimal New)> follow, CancellationToken ct)
    {
        foreach (var group in follow.GroupBy(f => f.Row.VoucherType))
        {
            var vids = group.Select(f => f.Row.VoucherId).Distinct().ToList();
            switch (group.Key)
            {
                case "FA-CAP":
                {
                    var transfers = await db.Set<AssetTransfer>().Where(t => vids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
                    var assetIds = transfers.Values.Select(t => t.AssetId).Distinct().ToList();
                    var assets = await db.Set<FixedAsset>().Where(a => assetIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
                    foreach (var (row, @new) in group)
                    {
                        if (!transfers.TryGetValue(row.VoucherId, out var t) || !assets.TryGetValue(t.AssetId, out var asset))
                            continue;
                        asset.PurchaseCost = @new;
                        t.TransferAmount = @new;
                        await RecalculateAsync(asset, ct);
                    }
                    break;
                }
                case "CONS":
                    await FollowItemsAsync(group, db.Set<ConsumptionItem>().Where(i => vids.Contains(i.VoucherId)),
                        i => (i.VoucherId, i.ProductId), (i, total, unit) => { i.TotalCost = total; if (unit is decimal u) i.UnitCost = u; }, ct);
                    break;
                case "SCRAP":
                    await FollowItemsAsync(group, db.Set<ScrapItem>().Where(i => vids.Contains(i.VoucherId)),
                        i => (i.VoucherId, i.ProductId), (i, total, unit) => { i.TotalCost = total; if (unit is decimal u) i.UnitCost = u; }, ct);
                    break;
                case "ADJ":
                    await FollowItemsAsync(group, db.Set<StockAdjustmentItem>().Where(i => vids.Contains(i.VoucherId)),
                        i => (i.VoucherId, i.ProductId), (i, total, unit) => { i.TotalCost = total; if (unit is decimal u) i.UnitCost = u; }, ct);
                    break;
            }
        }
    }

    private static async Task FollowItemsAsync<T>(IEnumerable<(StockLedger Row, decimal New)> group, IQueryable<T> items,
                                                  Func<T, (int, int)> key, Action<T, decimal, decimal?> set, CancellationToken ct)
        where T : class
    {
        var list = await items.OrderBy(i => EF.Property<int>(i, "Id")).ToListAsync(ct);
        foreach (var (row, @new) in group)
        {
            var item = list.FirstOrDefault(i => key(i) == (row.VoucherId, row.ProductId));
            if (item is null)
                continue;
            set(item, @new, row.Quantity != 0 ? Q(@new / row.Quantity) : null);
        }
    }

    /// <summary>FixedAsset.recalculate: cached accumulated depreciation and book value from the live (posted) rows.</summary>
    private async Task RecalculateAsync(FixedAsset asset, CancellationToken ct)
    {
        decimal total = await (from d in db.Set<AssetDepreciation>()
                               where d.AssetId == asset.Id
                               join e in db.Set<JournalEntry>() on d.JournalEntryId equals e.Id into je
                               from e in je.DefaultIfEmpty()
                               where d.JournalEntryId == null || e.IsPosted == true
                               select d.Amount).SumAsync(ct);
        double depreciable = (double)asset.PurchaseCost - (double)(asset.SalvageValue ?? 0);
        double accumulated = Math.Min((double)total, depreciable);
        asset.AccumulatedDepreciation = (decimal)accumulated;
        asset.CurrentBookValue = (decimal)((double)asset.PurchaseCost - accumulated);
    }
}

/// <summary>A receipt of zero or negative quantity (Python's ValueError in record_in).</summary>
public sealed class StockQuantityException(string message) : BusinessRuleException(message);
