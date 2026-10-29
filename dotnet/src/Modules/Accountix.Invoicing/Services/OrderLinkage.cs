namespace Accountix.Invoicing.Services;

/// <summary>An order line, sales or purchase, behind one face (the two tables are symmetrical).</summary>
public sealed class OrderLine
{
    public required object Entity { get; init; }
    public int Id { get; init; }
    public int OrderId { get; init; }
    public int ProductId { get; init; }
    public string? Description { get; init; }
    public string? Unit { get; init; }
    public decimal? Quantity { get; init; }
    public decimal? UnitPrice { get; init; }
    public decimal? SalesTaxPct { get; init; }
    public decimal? TotalAfterDiscount { get; init; }
    public required Func<decimal?> Invoiced { get; init; }
    public required Action<decimal> SetInvoiced { get; init; }

    public static OrderLine Of(InvSalesOrderItem x) => new()
    {
        Entity = x, Id = x.Id, OrderId = x.SoId, ProductId = x.ProductId, Description = x.Description, Unit = x.Unit,
        Quantity = x.Quantity, UnitPrice = x.UnitPrice, SalesTaxPct = x.SalesTaxPct, TotalAfterDiscount = x.TotalAfterDiscount,
        Invoiced = () => x.InvoicedQty, SetInvoiced = v => x.InvoicedQty = v,
    };

    public static OrderLine Of(InvPurchaseOrderItem x) => new()
    {
        Entity = x, Id = x.Id, OrderId = x.PoId, ProductId = x.ProductId, Description = x.Description, Unit = x.Unit,
        Quantity = x.Quantity, UnitPrice = x.UnitPrice, SalesTaxPct = x.SalesTaxPct, TotalAfterDiscount = x.TotalAfterDiscount,
        Invoiced = () => x.InvoicedQty, SetInvoiced = v => x.InvoicedQty = v,
    };
}

/// <summary>An invoice line as the write-back reads it: (source order line, quantity).</summary>
public readonly record struct Billing(int? SourceOrderItemId, double Quantity);

/// <summary>
/// Order → invoice linkage (shared/order_linkage.py): balances, write-back
/// on approve / unapprove / credit notes, and the over-invoicing control.
/// Lock order: document, then order lines (ascending), then products.
/// </summary>
public sealed class OrderLinkage(AccountixDbContext db)
{
    public const string Sales = "sales", Purchase = "purchase";

    /// <summary>lock_order_lines: FOR NO KEY UPDATE on these order lines, in id order, then read them fresh.</summary>
    public async Task LockOrderLinesAsync(string side, IEnumerable<int?> orderItemIds, CancellationToken ct)
    {
        var ids = orderItemIds.Where(i => i is > 0).Select(i => i!.Value).Distinct().Order().ToList();
        if (ids.Count == 0) return;
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlRawAsync(side == Sales
                ? "SELECT 1 FROM inv_sales_order_items WHERE id = ANY({0}) ORDER BY id FOR NO KEY UPDATE"
                : "SELECT 1 FROM inv_purchase_order_items WHERE id = ANY({0}) ORDER BY id FOR NO KEY UPDATE",
                [ids.ToArray()], ct);
        // populate_existing: rows already in the context are refreshed.
        foreach (var e in db.ChangeTracker.Entries().ToList())
        {
            if (e.Entity is InvSalesOrderItem s && side == Sales && ids.Contains(s.Id)
                || e.Entity is InvPurchaseOrderItem p && side == Purchase && ids.Contains(p.Id))
                if (e.State is EntityState.Unchanged or EntityState.Modified)
                    await e.ReloadAsync(ct);
        }
    }

    /// <summary>The order lines with these ids (tracked).</summary>
    public async Task<Dictionary<int, OrderLine>> LinesAsync(string side, IEnumerable<int> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return [];
        if (side == Sales)
            return (await db.Set<InvSalesOrderItem>().Where(x => list.Contains(x.Id)).ToListAsync(ct))
                .ToDictionary(x => x.Id, OrderLine.Of);
        return (await db.Set<InvPurchaseOrderItem>().Where(x => list.Contains(x.Id)).ToListAsync(ct))
            .ToDictionary(x => x.Id, OrderLine.Of);
    }

    /// <summary>All lines of an order, in id order (tracked).</summary>
    public async Task<List<OrderLine>> OrderLinesAsync(string side, int orderId, CancellationToken ct)
    {
        if (side == Sales)
            return (await db.Set<InvSalesOrderItem>().Where(x => x.SoId == orderId).OrderBy(x => x.Id).ToListAsync(ct))
                .Select(OrderLine.Of).ToList();
        return (await db.Set<InvPurchaseOrderItem>().Where(x => x.PoId == orderId).OrderBy(x => x.Id).ToListAsync(ct))
            .Select(OrderLine.Of).ToList();
    }

    /// <summary>invoices_drawing_on: numbers of the invoices (draft or approved) billed from this order.</summary>
    public async Task<List<string>> InvoicesDrawingOnAsync(string side, int orderId, CancellationToken ct)
    {
        List<int> lineIds = side == Sales
            ? await db.Set<InvSalesOrderItem>().Where(x => x.SoId == orderId).Select(x => x.Id).ToListAsync(ct)
            : await db.Set<InvPurchaseOrderItem>().Where(x => x.PoId == orderId).Select(x => x.Id).ToListAsync(ct);
        if (lineIds.Count == 0) return [];
        await LockOrderLinesAsync(side, lineIds.Select(i => (int?)i), ct);
        var numbers = side == Sales
            ? await db.Set<InvInvoiceItem>().Where(i => i.SourceOrderItemId != null && lineIds.Contains(i.SourceOrderItemId.Value))
                .Select(i => i.Invoice.InvoiceNumber).Distinct().ToListAsync(ct)
            : await db.Set<InvPurchaseInvoiceItem>().Where(i => i.SourceOrderItemId != null && lineIds.Contains(i.SourceOrderItemId.Value))
                .Select(i => i.Invoice.InvoiceNumber).Distinct().ToListAsync(ct);
        numbers.Sort(string.CompareOrdinal);
        return numbers;
    }

    private async Task<double> TolerancePctAsync(CancellationToken ct)
    {
        var s = await db.Set<InvoiceSetting>().AsNoTracking().OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        return Py.F(s?.OverInvoiceTolerancePct);
    }

    /// <summary>line_balance: uninvoiced quantity, never negative.</summary>
    public static double LineBalance(OrderLine it) =>
        Math.Max(0.0, Py.Round(Py.F(it.Quantity) - Py.F(it.Invoiced()), 6));

    /// <summary>order_progress: (ordered value, uninvoiced value, status).</summary>
    public static (double Ordered, double Uninvoiced, string Status) Progress(IEnumerable<OrderLine> items)
    {
        double ordered = 0.0, uninvoiced = 0.0;
        bool allDone = true, anyInvoiced = false;
        foreach (var it in items)
        {
            double qty = Py.F(it.Quantity);
            double invQty = Py.F(it.Invoiced());
            double net = Py.F(it.TotalAfterDiscount);
            if (net == 0) net = qty * Py.F(it.UnitPrice);
            double unitNet = qty != 0 ? net / qty : 0.0;
            ordered += net;
            uninvoiced += unitNet * LineBalance(it);
            if (invQty > 0) anyInvoiced = true;
            if (LineBalance(it) > 1e-6) allDone = false;
        }
        string status = allDone ? "invoiced" : anyInvoiced ? "partial" : "open";
        return (Py.Round(ordered, 2), Py.Round(uninvoiced, 2), status);
    }

    private async Task RefreshOrderStatusAsync(string side, int orderId, CancellationToken ct)
    {
        var lines = await OrderLinesAsync(side, orderId, ct);
        var (_, _, status) = Progress(lines);
        if (side == Sales)
        {
            var o = await db.Set<InvSalesOrder>().FirstOrDefaultAsync(x => x.Id == orderId, ct);
            if (o is not null) o.FulfilmentStatus = status;
        }
        else
        {
            var o = await db.Set<InvPurchaseOrder>().FirstOrDefaultAsync(x => x.Id == orderId, ct);
            if (o is not null) o.FulfilmentStatus = status;
        }
    }

    /// <summary>picker_payload: one order as the create-from-invoice picker reads it.</summary>
    public static Dictionary<string, object?> PickerPayload(string number, DateOnly? orderDate, decimal? totalAmount,
                                                             string? taxMode, decimal? globalSalesTaxPct, int orderId,
                                                             IReadOnlyList<OrderLine> items, IReadOnlyDictionary<int, InvProduct> products)
    {
        var (ordered, uninvoiced, status) = Progress(items);
        var lines = items.Select(it =>
        {
            products.TryGetValue(it.ProductId, out var product);
            return (object)new Dictionary<string, object?>
            {
                ["order_item_id"] = it.Id,
                ["product_id"] = it.ProductId,
                ["product_name"] = !string.IsNullOrEmpty(it.Description) ? it.Description : product?.Name ?? "",
                ["product_sku"] = product?.Sku ?? "",
                ["unit"] = string.IsNullOrEmpty(it.Unit) ? "pcs" : it.Unit,
                ["ordered_qty"] = Py.F(it.Quantity),
                ["invoiced_qty"] = Py.F(it.Invoiced()),
                ["balance_qty"] = LineBalance(it),
                ["unit_price"] = Py.F(it.UnitPrice),
                ["sales_tax_pct"] = Py.F(it.SalesTaxPct),
            };
        }).ToList();
        return new Dictionary<string, object?>
        {
            ["id"] = orderId,
            ["number"] = number,
            ["so_number"] = number,
            ["po_number"] = number,
            ["order_date"] = orderDate?.ToString("yyyy-MM-dd") ?? "",
            ["order_value"] = ordered,
            ["uninvoiced_value"] = uninvoiced,
            ["total_amount"] = Py.F(totalAmount),
            ["status"] = status,
            ["selectable"] = status != "invoiced",
            ["pools"] = new Dictionary<string, object?>
            {
                ["tax_mode"] = string.IsNullOrEmpty(taxMode) ? "general" : taxMode,
                ["global_sales_tax_pct"] = Py.F(globalSalesTaxPct),
                ["discount_mode"] = "general",
                ["global_discount_pct"] = 0.0,
                ["global_discount_value"] = 0.0,
                ["further_tax_pct"] = 0.0,
                ["apply_further_tax"] = false,
                ["withholding_tax_pct"] = 0.0,
                ["apply_withholding_tax"] = false,
            },
            ["items"] = lines,
        };
    }

    /// <summary>check_over_invoicing: refuse lines billed past the order balance (beyond the tolerance).</summary>
    public async Task<List<string>> CheckOverInvoicingAsync(string side, IEnumerable<Billing> invoiceItems, CancellationToken ct)
    {
        var wanted = new List<(int Id, double Qty)>();
        foreach (var it in invoiceItems)
        {
            if (it.SourceOrderItemId is not int oid || oid == 0) continue;
            int idx = wanted.FindIndex(w => w.Id == oid);
            if (idx < 0) wanted.Add((oid, 0.0 + it.Quantity));
            else wanted[idx] = (oid, wanted[idx].Qty + it.Quantity);
        }
        var errors = new List<string>();
        await LockOrderLinesAsync(side, wanted.Select(w => (int?)w.Id), ct);
        double tol = await TolerancePctAsync(ct);
        var lines = await LinesAsync(side, wanted.Select(w => w.Id), ct);
        foreach (var (oid, qty) in wanted)
        {
            if (!lines.TryGetValue(oid, out var oi)) continue;
            double balance = LineBalance(oi);
            double allowed = balance * (1 + tol / 100.0);
            if (qty - allowed > 1e-6)
            {
                string name = !string.IsNullOrEmpty(oi.Description) ? oi.Description : $"item #{oi.ProductId}";
                errors.Add($"{name}: billing {Py.G(qty)} exceeds the uninvoiced balance {Py.G(balance)}"
                           + (tol != 0 ? $" (tolerance {Py.G(tol)}%)" : ""));
            }
        }
        return errors;
    }

    /// <summary>apply_writeback: move invoiced quantities on the source orders and restage their status.</summary>
    public async Task<HashSet<int>> ApplyWritebackAsync(string side, IReadOnlyList<Billing> invoiceItems, int sign, CancellationToken ct)
    {
        await LockOrderLinesAsync(side, invoiceItems.Select(i => i.SourceOrderItemId), ct);
        var lines = await LinesAsync(side, invoiceItems.Where(i => i.SourceOrderItemId is > 0).Select(i => i.SourceOrderItemId!.Value), ct);
        var touched = new List<int>();
        foreach (var it in invoiceItems)
        {
            if (it.SourceOrderItemId is not int oid || oid == 0) continue;
            if (!lines.TryGetValue(oid, out var oi)) continue;
            double current = Py.F(oi.Invoiced());
            oi.SetInvoiced(Py.Dec(Math.Max(0.0, Py.Round(current + sign * it.Quantity, 6))));
            if (!touched.Contains(oi.OrderId)) touched.Add(oi.OrderId);
        }
        foreach (int orderId in touched)
            if (orderId != 0)
                await RefreshOrderStatusAsync(side, orderId, ct);
        await db.SaveChangesAsync(ct);
        return [.. touched];
    }

    /// <summary>tally_returned_quantities: returned quantity per product across a credit note's rows.</summary>
    public static List<(int ProductId, double Qty)> Tally(IEnumerable<(int? ProductId, double Qty)> rows)
    {
        var totals = new List<(int ProductId, double Qty)>();
        foreach (var (pid, q) in rows)
        {
            if (pid is not int p || p == 0) continue;
            int idx = totals.FindIndex(t => t.ProductId == p);
            if (idx < 0) totals.Add((p, 0.0 + q));
            else totals[idx] = (p, totals[idx].Qty + q);
        }
        return totals;
    }

    /// <summary>allocate_return_to_order_lines: spread returned quantities back over the lines that billed them.</summary>
    public static List<Billing> AllocateReturn(IEnumerable<(int? ProductId, int? SourceOrderItemId, double Quantity)> originalItems,
                                              IReadOnlyList<(int ProductId, double Qty)> returned)
    {
        var remaining = returned.Where(r => r.Qty > 0).ToDictionary(r => r.ProductId, r => r.Qty);
        var releases = new List<Billing>();
        foreach (var it in originalItems)
        {
            if (it.SourceOrderItemId is not int oid || oid == 0) continue;
            double left = it.ProductId is int pid ? remaining.GetValueOrDefault(pid, 0.0) : 0.0;
            if (left <= 0) continue;
            double take = Math.Min(left, it.Quantity);
            if (take <= 0) continue;
            remaining[it.ProductId!.Value] = Py.Round(left - take, 6);
            releases.Add(new Billing(oid, take));
        }
        return releases;
    }

    public async Task ApplyReturnWritebackAsync(string side, IEnumerable<(int? ProductId, int? SourceOrderItemId, double Quantity)> originalItems,
                                                IReadOnlyList<(int ProductId, double Qty)> returned, int sign, CancellationToken ct)
    {
        var releases = AllocateReturn(originalItems, returned);
        if (releases.Count == 0) return;
        await ApplyWritebackAsync(side, releases, sign, ct);
    }
}
