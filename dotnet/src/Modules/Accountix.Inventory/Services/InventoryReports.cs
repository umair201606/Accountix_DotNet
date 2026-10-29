using System.Data;
using System.Globalization;

namespace Accountix.Inventory.Services;

/// <summary>
/// The read side of inventory: dashboard figures, stock lists, the stock and
/// product ledgers, stock valuation with its GL reconciliation, low stock and
/// the product sub-ledgers (inventory_app/routes/auth.py dashboard, stock.py,
/// reports.py, vouchers.py product_ledger*). Everything is set-based: one
/// query per figure, never one per product.
/// </summary>
public sealed class InventoryReports(AccountixDbContext db)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ── Running balances ───────────────────────────────────────────────────

    /// <summary>StockLedger.get_running_balance for one product's latest row.</summary>
    public sealed record Balance(int ProductId, decimal Qty, decimal Cost, decimal Avg);

    private sealed record BalanceRow(int ProductId, decimal RunningQty, decimal RunningCost, decimal RunningAvg);

    /// <summary>
    /// get_running_balance of every product that has ledger rows, in ONE query:
    /// the latest row by document date (txn_date DESC — NULLs first, as
    /// Postgres orders a DESC column — then id DESC), via DISTINCT ON.
    /// </summary>
    public async Task<Dictionary<int, Balance>> LatestBalancesAsync(CancellationToken ct = default)
    {
        int cid = db.Company.CompanyId ?? throw new NoActiveCompanyException("stock balances with no active company");
        var rows = await db.Database.SqlQuery<BalanceRow>($"""
            SELECT DISTINCT ON (product_id) product_id AS "ProductId", running_qty AS "RunningQty",
                   running_cost AS "RunningCost", running_avg AS "RunningAvg"
            FROM stock_ledger WHERE company_id = {cid}
            ORDER BY product_id, txn_date DESC, id DESC
            """).ToListAsync(ct);
        return rows.ToDictionary(r => r.ProductId, r => new Balance(r.ProductId, r.RunningQty, r.RunningCost, r.RunningAvg));
    }

    // ── Dashboard ──────────────────────────────────────────────────────────

    public sealed record RecentProduct(string Sku, string Name, decimal? CurrentStock, int? ReorderLevel, decimal? UnitPrice);

    public sealed class DashboardModel
    {
        public int TotalProducts { get; init; }
        public int TotalCategories { get; init; }
        public int LowStock { get; init; }
        public double TotalStockValue { get; init; }
        public int ConsumptionCount { get; init; }
        public int ScrapCount { get; init; }
        public int AdjustmentCount { get; init; }
        public int StockTakeCount { get; init; }
        public int PendingVouchers { get; init; }
        public IReadOnlyList<RecentProduct> RecentProducts { get; init; } = [];
        /// <summary>Products whose display stock was brought back to the ledger (the dashboard's self-heal).</summary>
        public int Resynced { get; init; }
    }

    /// <summary>
    /// The dashboard (inv_auth.dashboard). As Python, it also brings every
    /// active product's denormalised current_stock back to the ledger's running
    /// quantity when the two drifted — but with one balance query and an
    /// update of only the drifted products, not one query per product.
    /// </summary>
    public async Task<DashboardModel> DashboardAsync(CancellationToken ct = default)
    {
        var products = db.Set<InvProduct>().AsNoTracking();
        int totalProducts = await products.CountAsync(ct);
        int totalCategories = await products.Where(p => p.CategoryId != null).Select(p => p.CategoryId).Distinct().CountAsync(ct);
        int lowStock = await products.CountAsync(p => p.CurrentStock <= (decimal?)p.ReorderLevel && p.ReorderLevel > 0, ct);

        var balances = await LatestBalancesAsync(ct);
        var active = await products.Where(p => p.IsActive == true)
            .Select(p => new { p.Id, p.CurrentStock }).ToListAsync(ct);
        double totalValue = 0;
        var drifted = new Dictionary<int, decimal>();
        foreach (var p in active)
        {
            var bal = balances.GetValueOrDefault(p.Id);
            double qty = (double)(bal?.Qty ?? 0m);
            // The ledger's carried value — the same figure the inventory account holds.
            totalValue += (double)(bal?.Cost ?? 0m);
            if (Math.Abs((double)(p.CurrentStock ?? 0m) - qty) > 0.00005)
                drifted[p.Id] = Math.Round(bal?.Qty ?? 0m, 4, MidpointRounding.ToEven);
        }
        if (drifted.Count > 0)
        {
            // Tracked, so the audit trail sees each product's change as Python's session hook does.
            var ids = drifted.Keys.ToList();
            foreach (var p in await db.Set<InvProduct>().Where(p => ids.Contains(p.Id)).ToListAsync(ct))
                p.CurrentStock = drifted[p.Id];
            await db.SaveChangesAsync(ct);
        }

        int cons = await db.Set<ConsumptionVoucher>().CountAsync(v => v.Status == "approved", ct);
        int scrap = await db.Set<ScrapVoucher>().CountAsync(v => v.Status == "approved", ct);
        int adj = await db.Set<StockAdjustmentVoucher>().CountAsync(v => v.Status == "approved", ct);
        int takes = await db.Set<StockTake>().CountAsync(v => v.Status == "approved", ct);
        int pending = await db.Set<ConsumptionVoucher>().CountAsync(v => v.Status == "unapproved", ct)
                      + await db.Set<ScrapVoucher>().CountAsync(v => v.Status == "unapproved", ct)
                      + await db.Set<StockAdjustmentVoucher>().CountAsync(v => v.Status == "unapproved", ct);

        var recent = await products.OrderByDescending(p => p.Id).Take(5)
            .Select(p => new RecentProduct(p.Sku, p.Name, p.CurrentStock, p.ReorderLevel, p.UnitPrice)).ToListAsync(ct);

        return new DashboardModel
        {
            TotalProducts = totalProducts, TotalCategories = totalCategories, LowStock = lowStock,
            TotalStockValue = Math.Round(totalValue, 2), ConsumptionCount = cons, ScrapCount = scrap,
            AdjustmentCount = adj, StockTakeCount = takes, PendingVouchers = pending, RecentProducts = recent,
            Resynced = drifted.Count,
        };
    }

    // ── Stock lists ────────────────────────────────────────────────────────

    public sealed record StockRow(int Id, string Sku, string Name, string? Category, decimal? CurrentStock,
                                  int? ReorderLevel, decimal? CostPrice)
    {
        /// <summary>current_stock &lt;= reorder_level and reorder_level &gt; 0 (the list's low mark).</summary>
        public bool Low => (CurrentStock ?? 0) <= (ReorderLevel ?? 0) && (ReorderLevel ?? 0) > 0;
    }

    /// <summary>inv_stock.list_stock: search by name/SKU (ILIKE), low stock only, by name.</summary>
    public async Task<List<StockRow>> StockListAsync(string? q, bool lowOnly, CancellationToken ct = default)
    {
        var query = db.Set<InvProduct>().AsNoTracking();
        if (!string.IsNullOrEmpty(q))
        {
            string like = $"%{q}%";
            query = query.Where(p => EF.Functions.ILike(p.Name, like) || EF.Functions.ILike(p.Sku, like));
        }
        if (lowOnly)
            query = query.Where(p => p.CurrentStock <= (decimal?)p.ReorderLevel && p.ReorderLevel > 0);
        return await query.OrderBy(p => p.Name)
            .Select(p => new StockRow(p.Id, p.Sku, p.Name, p.Category != null ? p.Category.Name : null,
                                      p.CurrentStock, p.ReorderLevel, p.CostPrice))
            .ToListAsync(ct);
    }

    public sealed record ProductOption(int Id, string Name, string Sku);
    public sealed record MovementRow(DateTime? CreatedAt, string Product, string Type, decimal Quantity, string? ReferenceType,
                                     int? ReferenceId, string? Notes);

    /// <summary>Active products for a picker: by name (reports) or as stored (movements).</summary>
    public Task<List<ProductOption>> ActiveProductsAsync(bool byName, CancellationToken ct = default)
    {
        var q = db.Set<InvProduct>().AsNoTracking().Where(p => p.IsActive == true);
        q = byName ? q.OrderBy(p => p.Name) : q.OrderBy(p => p.Id);
        return q.Select(p => new ProductOption(p.Id, p.Name, p.Sku)).ToListAsync(ct);
    }

    /// <summary>inv_stock.list_movements: the latest 100 movements, optionally of one product.</summary>
    public async Task<List<MovementRow>> MovementsAsync(int? productId, CancellationToken ct = default)
    {
        var q = db.Set<InvStockMovement>().AsNoTracking();
        if (productId is int pid && pid != 0)
            q = q.Where(m => m.ProductId == pid);
        return await q.OrderByDescending(m => m.Id).Take(100)
            .Select(m => new MovementRow(m.CreatedAt, m.Product.Name, m.Type, m.Quantity, m.ReferenceType, m.ReferenceId, m.Notes))
            .ToListAsync(ct);
    }

    // ── Stock / product ledger ─────────────────────────────────────────────

    public sealed record ProductHead(int Id, string Sku, string Name, string? Unit);

    public Task<ProductHead?> ProductAsync(int id, CancellationToken ct = default) =>
        db.Set<InvProduct>().AsNoTracking().Where(p => p.Id == id)
            .Select(p => new ProductHead(p.Id, p.Sku, p.Name, p.Unit)).FirstOrDefaultAsync(ct);

    /// <summary>A product's stock-ledger rows in document-date order (txn_date, id).</summary>
    public Task<List<StockLedger>> LedgerAsync(int productId, CancellationToken ct = default) =>
        db.Set<StockLedger>().AsNoTracking().Where(r => r.ProductId == productId)
            .OrderBy(r => r.TxnDate).ThenBy(r => r.Id).ToListAsync(ct);

    /// <summary>The product's re-costings, newest first (product_ledger's adjustments).</summary>
    public Task<List<StockCostAdjustment>> AdjustmentsAsync(int productId, CancellationToken ct = default) =>
        db.Set<StockCostAdjustment>().AsNoTracking().Where(a => a.ProductId == productId)
            .OrderByDescending(a => a.Id).ToListAsync(ct);

    // ── Product sub-ledgers ────────────────────────────────────────────────

    public sealed record SubLedgerRow(int Id, string Sku, string Name, double Qty, double Cost, double Value, string? Unit);

    /// <summary>product_ledger_list: every active product's latest balance (two queries in all).</summary>
    public async Task<List<SubLedgerRow>> SubLedgersAsync(CancellationToken ct = default)
    {
        var products = await db.Set<InvProduct>().AsNoTracking().Where(p => p.IsActive == true).OrderBy(p => p.Name)
            .Select(p => new { p.Id, p.Sku, p.Name, p.Unit }).ToListAsync(ct);
        var balances = await LatestBalancesAsync(ct);
        return products.Select(p =>
        {
            var b = balances.GetValueOrDefault(p.Id);
            return new SubLedgerRow(p.Id, p.Sku, p.Name, (double)(b?.Qty ?? 0m), (double)(b?.Avg ?? 0m),
                                    Math.Round((double)(b?.Cost ?? 0m), 2), p.Unit);
        }).ToList();
    }

    // ── Low stock ──────────────────────────────────────────────────────────

    public sealed record LowRow(string Sku, string Name, decimal? CurrentStock, int? ReorderLevel, string? Unit);

    /// <summary>low_stock_report: active products at or below their reorder level, lowest stock first.</summary>
    public Task<List<LowRow>> LowStockAsync(CancellationToken ct = default) =>
        db.Set<InvProduct>().AsNoTracking()
            .Where(p => p.IsActive == true && p.CurrentStock <= (decimal?)p.ReorderLevel)
            .OrderBy(p => p.CurrentStock)      // ties in the order Postgres gives them, as Python's ORDER BY
            .Select(p => new LowRow(p.Sku, p.Name, p.CurrentStock, p.ReorderLevel, p.Unit))
            .ToListAsync(ct);

    // ── Valuation ──────────────────────────────────────────────────────────

    public sealed record ValuationRow(int Id, string Sku, string Name, string Unit,
                                      decimal OpenQty, decimal OpenVal, decimal InQty, decimal InVal,
                                      decimal OutQty, decimal OutVal, decimal CloseQty, decimal CloseVal, decimal Avg);

    public sealed class Valuation
    {
        public required DateOnly AsOf { get; init; }
        public required DateOnly Start { get; init; }
        public required IReadOnlyList<ValuationRow> Rows { get; init; }
        public decimal OpenVal { get; init; }
        public decimal InVal { get; init; }
        public decimal OutVal { get; init; }
        public decimal CloseVal { get; init; }
        public decimal GlBalance { get; init; }
        public decimal Difference => CloseVal - GlBalance;
    }

    /// <summary>
    /// valuation_report: opening / in / out / closing per product by DOCUMENT
    /// date, aggregated in SQL, and the inventory accounts' balance from
    /// gl_daily_balances — all in one REPEATABLE READ snapshot
    /// (consistent_snapshot), so a posting landing mid-report cannot open a
    /// difference the books do not have.
    /// </summary>
    public async Task<Valuation> ValuationAsync(DateOnly asOf, DateOnly start, ILedger ledger, CancellationToken ct = default)
    {
        if (start > asOf) start = asOf;
        var cut = start.AddDays(-1);
        bool own = db.Database.IsNpgsql() && db.Database.CurrentTransaction is null;
        // Never committed: a report writes nothing (an account posting_account had to create goes too, as in Python).
        await using var tx = own ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct) : null;

        var sums = await db.Set<StockLedger>().AsNoTracking()
            .Where(r => r.TxnDate <= asOf)
            .GroupBy(r => r.ProductId)
            .Select(g => new
            {
                Pid = g.Key,
                Oq = g.Sum(r => r.TxnDate <= cut ? (r.TransactionType == "IN" ? r.Quantity : -r.Quantity) : 0m),
                Ov = g.Sum(r => r.TxnDate <= cut ? (r.TransactionType == "IN" ? r.TotalCost : -r.TotalCost) : 0m),
                Iq = g.Sum(r => r.TxnDate > cut && r.TransactionType == "IN" ? r.Quantity : 0m),
                Iv = g.Sum(r => r.TxnDate > cut && r.TransactionType == "IN" ? r.TotalCost : 0m),
                Outq = g.Sum(r => r.TxnDate > cut && r.TransactionType != "IN" ? r.Quantity : 0m),
                Outv = g.Sum(r => r.TxnDate > cut && r.TransactionType != "IN" ? r.TotalCost : 0m),
            })
            .ToListAsync(ct);
        var products = await db.Set<InvProduct>().AsNoTracking()
            .Select(p => new { p.Id, p.Sku, p.Name, p.Unit }).ToDictionaryAsync(p => p.Id, ct);

        var rows = new List<ValuationRow>();
        decimal ov = 0, iv = 0, outv = 0, cv = 0;
        foreach (var s in sums.OrderBy(s => s.Pid))
        {
            decimal cq = s.Oq + s.Iq - s.Outq, cval = s.Ov + s.Iv - s.Outv;
            if (s.Oq == 0 && s.Iq == 0 && s.Outq == 0 && cval == 0)
                continue;
            var p = products.GetValueOrDefault(s.Pid);
            rows.Add(new ValuationRow(s.Pid, p?.Sku ?? "", p?.Name ?? $"#{s.Pid}", p?.Unit ?? "",
                                      s.Oq, s.Ov, s.Iq, s.Iv, s.Outq, s.Outv, cq, cval, cq > 0 ? cval / cq : 0m));
            ov += s.Ov;
            iv += s.Iv;
            outv += s.Outv;
            cv += cval;
        }
        // rows.sort(key=lambda r: r["name"].lower()) — stable, so equal names keep product order.
        rows = rows.OrderBy(r => r.Name.ToLowerInvariant(), StringComparer.Ordinal).ToList();

        decimal gl = await GlInventoryBalanceAsync(asOf, ledger, ct);
        return new Valuation
        {
            AsOf = asOf, Start = start, Rows = rows, OpenVal = ov, InVal = iv, OutVal = outv, CloseVal = cv, GlBalance = gl,
        };
    }

    /// <summary>_gl_inventory_balance: the Inventories subtree (1-01-04…) plus the inventory posting account, at as_of.</summary>
    public async Task<decimal> GlInventoryBalanceAsync(DateOnly asOf, ILedger ledger, CancellationToken ct = default)
    {
        var ids = (await db.Set<ChartOfAccount>().AsNoTracking().Where(a => a.Code.StartsWith("1-01-04"))
            .Select(a => a.Id).ToListAsync(ct)).ToHashSet();
        try
        {
            ids.Add((await ledger.PostingAccountAsync("inventory", ct)).Id);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // as Python's try/except around posting_account
        }
        if (ids.Count == 0)
            return 0m;
        var idList = ids.ToList();
        var t = await db.Set<GlDailyBalance>().AsNoTracking()
            .Where(g => g.Day <= asOf && idList.Contains(g.AccountId))
            .GroupBy(_ => 1)
            .Select(g => new { Dr = g.Sum(x => x.Debit), Cr = g.Sum(x => x.Credit) })
            .FirstOrDefaultAsync(ct);
        return (t?.Dr ?? 0m) - (t?.Cr ?? 0m);
    }

    // ── Export tables (shared/report_export.export_table's inputs) ─────────

    public static readonly string[] LedgerHeaders =
        ["Date", "Voucher", "Type", "Qty In", "Qty Out", "Unit Cost", "Value", "Running Qty", "Running Value", "Avg Cost", "Notes"];

    private static IReadOnlyList<ReportColumn> Columns(IReadOnlyList<string> headers, IReadOnlyDictionary<int, ReportColumnKind> kinds) =>
        headers.Select((h, i) => new ReportColumn(h, kinds.GetValueOrDefault(i, ReportColumnKind.Text))).ToList();

    private static object? DocDate(DateOnly? txn, DateTime? created) =>
        txn is DateOnly d ? d : created is DateTime c ? DateOnly.FromDateTime(c) : null;

    /// <summary>stock_ledger_rows: a product's lines as export rows.</summary>
    public static List<IReadOnlyList<object?>> LedgerRows(IEnumerable<StockLedger> entries) =>
        entries.Select(e =>
        {
            bool inbound = e.TransactionType == "IN";
            double q = (double)e.Quantity;
            return (IReadOnlyList<object?>)new object?[]
            {
                DocDate(e.TxnDate, e.CreatedAt),
                string.Join(" ", new[] { e.VoucherType, e.VoucherNumber }.Where(x => !string.IsNullOrEmpty(x))),
                inbound ? "In" : "Out", inbound ? q : 0d, inbound ? 0d : q,
                (double)e.UnitCost, (double)e.TotalCost, (double)e.RunningQty, (double)e.RunningCost,
                (double)e.RunningAvg, e.Notes ?? "",
            };
        }).ToList();

    /// <summary>export_stock_ledger: stock / product ledger; Excel carries the re-costing history on a second sheet.</summary>
    public static ReportTable LedgerTable(string format, ProductHead product, IReadOnlyList<StockLedger> entries,
                                          IReadOnlyList<StockCostAdjustment>? adjustments = null)
    {
        var extra = new List<(string, IReadOnlyList<string>, IReadOnlyList<IReadOnlyList<object?>>)>();
        string fmt = (format ?? "").ToLowerInvariant();
        if (adjustments is { Count: > 0 } && fmt is "excel" or "xlsx")
            extra.Add(("Re-costing history", ["Date", "Voucher", "Old Cost", "New Cost", "Difference", "Trigger"],
                adjustments.Select(a => (IReadOnlyList<object?>)new object?[]
                {
                    DocDate(a.EntryDate, a.CreatedAt),
                    string.Join(" ", new[] { a.VoucherType, a.VoucherNumber }.Where(x => !string.IsNullOrEmpty(x))),
                    (double)a.OldCost, (double)a.NewCost, (double)a.Delta, a.Trigger ?? "",
                }).ToList()));
        return new ReportTable
        {
            Title = $"Stock Ledger — {product.Name}",
            Period = $"SKU {product.Sku} · unit {(string.IsNullOrEmpty(product.Unit) ? "-" : product.Unit)} · document-date order",
            FilePeriod = product.Sku,
            Columns = Columns(LedgerHeaders, new Dictionary<int, ReportColumnKind>
            {
                [0] = ReportColumnKind.Date, [3] = ReportColumnKind.Quantity, [4] = ReportColumnKind.Quantity,
                [7] = ReportColumnKind.Quantity,
            }),
            Rows = LedgerRows(entries).Select(r => new ReportRow(r)).ToList(),
            ExtraSheets = extra,
        };
    }

    public static readonly string[] ValuationHeaders =
        ["SKU", "Product", "Unit", "Opening Qty", "Opening Value", "In Qty", "In Value", "Out Qty", "Out Value",
         "Closing Qty", "Avg Cost", "Closing Value"];

    public static ReportTable ValuationTable(Valuation v)
    {
        var rows = v.Rows.Select(r => new ReportRow(new object?[]
        {
            r.Sku, r.Name, r.Unit, r.OpenQty, r.OpenVal, r.InQty, r.InVal, r.OutQty, r.OutVal, r.CloseQty, r.Avg, r.CloseVal,
        }, "plain")).ToList();
        rows.Add(new ReportRow(new object?[] { "", "Total", "", "", v.OpenVal, "", v.InVal, "", v.OutVal, "", "", v.CloseVal }, "grand"));
        rows.Add(new ReportRow(new object?[] { "", "Inventory accounts (general ledger)", "", "", "", "", "", "", "", "", "", v.GlBalance }, "total"));
        rows.Add(new ReportRow(new object?[] { "", "Difference (stock ledger − GL)", "", "", "", "", "", "", "", "", "", v.Difference }, "total"));
        return new ReportTable
        {
            Title = "Stock Valuation",
            Period = $"Movement {D(v.Start)} to {D(v.AsOf)} · valued as at {D(v.AsOf)}",
            FilePeriod = $"as_at_{v.AsOf.ToString("yyyy-MM-dd", Inv)}",
            Columns = Columns(ValuationHeaders, new[] { 3, 5, 7, 9 }.ToDictionary(i => i, _ => ReportColumnKind.Quantity)),
            Rows = rows,
        };
    }

    public static ReportTable LowStockTable(IReadOnlyList<LowRow> products, DateOnly today) => new()
    {
        Title = "Low Stock Alert",
        Period = $"Products at or below reorder level, {D(today)}",
        FilePeriod = today.ToString("yyyy-MM-dd", Inv),
        Columns = Columns(["SKU", "Product", "Current Stock", "Reorder Level", "Short By", "Unit", "Status"],
                          new[] { 2, 3, 4 }.ToDictionary(i => i, _ => ReportColumnKind.Quantity)),
        Rows = products.Select(p =>
        {
            double cur = (double)(p.CurrentStock ?? 0m), reorder = p.ReorderLevel ?? 0;
            return new ReportRow(new object?[]
            {
                p.Sku, p.Name, cur, reorder, Math.Max(reorder - cur, 0), p.Unit ?? "", cur <= 0 ? "Out of stock" : "Low",
            });
        }).ToList(),
    };

    public static ReportTable SubLedgerTable(IReadOnlyList<SubLedgerRow> rows, DateOnly today)
    {
        var data = rows.Select(r => new ReportRow(new object?[] { r.Sku, r.Name, r.Unit ?? "", r.Qty, r.Cost, r.Value }, "plain")).ToList();
        data.Add(new ReportRow(new object?[] { "", "Total", "", "", "", Math.Round(rows.Sum(r => r.Value), 2) }, "grand"));
        return new ReportTable
        {
            Title = "Product Sub-Ledgers",
            Period = $"Balances as at {D(today)}",
            FilePeriod = today.ToString("yyyy-MM-dd", Inv),
            Columns = Columns(["SKU", "Product", "Unit", "Qty", "Avg Cost", "Value"],
                              new Dictionary<int, ReportColumnKind> { [3] = ReportColumnKind.Quantity }),
            Rows = data,
        };
    }

    /// <summary>"%d %b %Y" (05 Mar 2026).</summary>
    public static string D(DateOnly d) => d.ToString("dd MMM yyyy", Inv);
}

/// <summary>How the Python templates print values, where it shows (Jinja's str() of the column's Python type).</summary>
public static class PyText
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>str(Decimal) of a NUMERIC column ("10.0000"); None prints "None".</summary>
    public static string Dec(decimal? d) => d is decimal v ? v.ToString(Inv) : "None";

    /// <summary>repr(float): "10.0", "2.5", "1e-05".</summary>
    public static string Float(double v)
    {
        if (double.IsNaN(v)) return "nan";
        if (double.IsInfinity(v)) return v > 0 ? "inf" : "-inf";
        if (v == Math.Floor(v) && Math.Abs(v) < 1e16)
            return v.ToString("F0", Inv) + ".0";
        string s = v.ToString("R", Inv);
        int e = s.IndexOfAny(['E', 'e']);
        if (e < 0) return s;
        string mant = s[..e], exp = s[(e + 1)..];
        char sign = exp[0] == '-' ? '-' : '+';
        string digits = exp.TrimStart('+', '-').TrimStart('0');
        if (digits.Length < 2) digits = digits.PadLeft(2, '0');
        return $"{mant}e{sign}{digits}";
    }

    public static string Float(decimal? v) => v is decimal d ? Float((double)d) : "None";

    /// <summary>The Quantity column type: whole numbers as int ("10"), else the float ("2.5"); None → "None".</summary>
    public static string Qty(decimal? d)
    {
        if (d is not decimal v) return "None";
        double x = Math.Round((double)v, 4);
        return x == Math.Floor(x) && Math.Abs(x) < 1e16 ? x.ToString("F0", Inv) : Float(x);
    }

    /// <summary>str(x) of a value printed by Jinja (None → "None").</summary>
    public static string Str(string? s) => s ?? "None";
}
