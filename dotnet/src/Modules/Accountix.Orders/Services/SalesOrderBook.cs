using Accountix.Invoicing.Services;

namespace Accountix.Orders.Services;

/// <summary>
/// Sales orders (invoicing_app/routes/sales.py): save, approve, unapprove,
/// delete, cancel. Every state change locks the order first (FOR NO KEY
/// UPDATE, as scoped_get(..., for_update=True)) and checks its status after
/// the lock; the order's lines are locked next when invoices drawing on them
/// are looked up (order_linkage.invoices_drawing_on). Lock order: document →
/// order lines.
/// </summary>
public sealed class SalesOrderBook(AccountixDbContext db, CompanyClock clock, EditVersions versions, OrderLinkage linkage)
{
    public const string Prefix = "SO";
    public const string Section = "sales_orders";
    public const string Table = "inv_sales_orders";

    /// <summary>POST /inventory/sales/save. Null: the order is not found (404).</summary>
    public async Task<OrderAnswer?> SaveAsync(PyData data, int userId, CancellationToken ct)
    {
        var idEl = data.Get("id");
        bool approve = OrderInput.IsApprove(data);
        bool existing = PyData.Truthy(idEl);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        InvSalesOrder? order;
        if (existing)
        {
            order = await db.LockAsync<InvSalesOrder>(OrderInput.Int(idEl, "id"), ct);
            if (order is null) return null;
            if (order.Status == "approved")
                return OrderAnswer.Error(400, OrderWords.CannotModifyApproved);
            string? stale = await versions.StaleAsync(Table, order.Id, order.CompanyId, order.EditVersion,
                data.Get("edit_version"), $"Order {order.SoNumber}", v => order.EditVersion = v, ct);
            if (stale is not null)
                return OrderAnswer.Error(409, stale, stale: true);
            var billed = await linkage.InvoicesDrawingOnAsync(OrderLinkage.Sales, order.Id, ct);
            if (billed.Count > 0)
                return OrderAnswer.Error(400, OrderWords.CannotChange(order.SoNumber, billed));
        }
        else
        {
            order = new InvSalesOrder
            {
                SoNumber = OrderInput.NewNumber(data, "so_number"),
                CreatedBy = userId,
                // Column defaults Python's model supplies at insert.
                Status = "unapproved", FulfilmentStatus = "open", EditVersion = 0, CreatedAt = clock.UtcNow,
            };
            db.Add(order);
        }

        var h = OrderInput.Header(data, "customer_id", clock);
        order.CustomerId = h.PartyId;
        order.PartyAccountId = h.PartyAccountId;
        order.TaxMode = h.TaxMode;
        order.OrderDate = h.OrderDate;
        order.ExpectedDate = h.ExpectedDate;
        order.GlobalSalesTaxPct = h.GlobalSalesTaxPct;
        order.Notes = h.Notes;
        order.Subtotal = h.Subtotal;
        order.TotalTax = h.TotalTax;
        order.TotalAmount = h.TotalAmount;
        var lines = OrderInput.Items(data).Select(OrderInput.Line).ToList();

        if (approve)
        {
            order.Status = "approved";
            order.ApprovedBy = userId;
            order.ApprovedAt = clock.UtcNow;
        }
        else if (order.Status == "new")
            order.Status = "unapproved";

        await db.SaveChangesAsync(ct);
        if (DocNumbers.IsPlaceholder(order.SoNumber))
        {
            // SO-YYYYMM-<the order's own id>: two users saving at once never share a number.
            order.SoNumber = DocNumbers.FromId(Prefix, clock.Now, order.Id);
            await db.SaveChangesAsync(ct);
        }

        await db.Set<InvSalesOrderItem>().Where(i => i.SoId == order.Id).ExecuteDeleteAsync(ct);
        foreach (var l in lines)
            db.Add(new InvSalesOrderItem
            {
                SoId = order.Id, ProductId = l.ProductId, Description = l.Description, Quantity = l.Quantity,
                Unit = l.Unit, UnitPrice = l.UnitPrice, SalesTaxPct = l.SalesTaxPct,
                TotalBeforeDiscount = l.TotalBeforeDiscount, TotalAfterDiscount = l.TotalAfterDiscount,
                TotalPrice = l.TotalPrice, InvoicedQty = 0,
            });
        // Charges are not eligible on an order; rows an older build wrote are cleared on re-save.
        await db.Set<AdditionalCharge>().Where(c => c.DocType == Prefix && c.DocId == order.Id).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return OrderAnswer.Ok(new()
        {
            ["ok"] = true, ["id"] = order.Id, ["status"] = order.Status, ["edit_version"] = order.EditVersion,
            ["number"] = order.SoNumber, ["message"] = OrderWords.Saved(approve, existing),
        });
    }

    /// <summary>POST /inventory/sales/unapprove/&lt;id&gt;.</summary>
    public async Task<OrderAnswer?> UnapproveAsync(int id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await db.LockAsync<InvSalesOrder>(id, ct);
        if (order is null) return null;
        if (order.Status != "approved")
            return OrderAnswer.Error(400, OrderWords.OnlyApprovedUnapprove);
        var billed = await linkage.InvoicesDrawingOnAsync(OrderLinkage.Sales, order.Id, ct);
        if (billed.Count > 0)
            return OrderAnswer.Error(400, OrderWords.CannotUnapprove(order.SoNumber, billed));
        order.Status = "unapproved";
        order.ApprovedBy = null;
        order.ApprovedAt = null;
        order.EditVersion = EditVersions.Bump(order.EditVersion);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return OrderAnswer.Ok(new()
        {
            ["ok"] = true, ["status"] = "unapproved", ["edit_version"] = order.EditVersion, ["message"] = "Order unapproved",
        });
    }

    /// <summary>POST /inventory/sales/delete/&lt;id&gt;.</summary>
    public async Task<OrderAnswer?> DeleteAsync(int id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await db.LockAsync<InvSalesOrder>(id, ct);
        if (order is null) return null;
        if (order.Status == "approved")
            return OrderAnswer.Error(400, OrderWords.CannotDeleteApproved);
        var billed = await linkage.InvoicesDrawingOnAsync(OrderLinkage.Sales, order.Id, ct);
        if (billed.Count > 0)
            return OrderAnswer.Error(400, OrderWords.CannotDelete(order.SoNumber, billed));
        try
        {
            await db.Set<InvSalesOrderItem>().Where(i => i.SoId == order.Id).ExecuteDeleteAsync(ct);
            await db.Set<AdditionalCharge>().Where(c => c.DocType == Prefix && c.DocId == order.Id).ExecuteDeleteAsync(ct);
            // The model's `invoices` relationship (no delete cascade): SQLAlchemy sets the
            // invoices' sales_order_id to NULL when the order goes.
            await db.Set<InvInvoice>().Where(i => i.SalesOrderId == order.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.SalesOrderId, (int?)null), ct);
            db.Remove(order);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return OrderAnswer.Ok(new() { ["ok"] = true, ["message"] = "Order deleted" });
        }
        catch (Exception e) when (OrderErrors.Database(e) is string message)
        {
            return OrderAnswer.Error(500, message);
        }
    }

    /// <summary>POST /inventory/sales/cancel/&lt;id&gt;: (found, flash category, flash text).</summary>
    public async Task<(bool Found, string Category, string Message)> CancelAsync(int id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await db.LockAsync<InvSalesOrder>(id, ct);
        if (order is null) return (false, "", "");
        if (!OrderWords.Cancellable(order.Status))
            return (true, "error", $"SO {order.SoNumber} is {Jinja.Str(order.Status)}; it cannot be cancelled.");
        order.Status = "cancelled";
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (true, "warning", $"SO {order.SoNumber} cancelled; what is still unbilled can no longer be invoiced.");
    }
}

/// <summary>The delete routes' except-clause: a database refusal answered as JSON 500 with its text.</summary>
internal static class OrderErrors
{
    public static string? Database(Exception e)
    {
        var pg = e as Npgsql.PostgresException ?? e.InnerException as Npgsql.PostgresException;
        if (pg is null) return e is DbUpdateException ? e.Message : null;
        return string.IsNullOrEmpty(pg.Detail) ? pg.MessageText : $"{pg.MessageText}\nDETAIL:  {pg.Detail}";
    }
}
