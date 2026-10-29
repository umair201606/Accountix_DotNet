using Accountix.Invoicing.Services;
using Accountix.Orders.Services;

namespace Accountix.Orders.Controllers;

/// <summary>Sales orders: invoicing_app/routes/sales.py (blueprint inv_sales, /inventory/sales).</summary>
[Area("Orders")]
[Module(Modules.Invoicing)]
[Route("inventory/sales")]
public sealed class SalesOrdersController(AccountixDbContext db, SalesOrderBook book, OrderReads reads) : Controller
{
    private const string Section = SalesOrderBook.Section;
    private EffectiveAccess Access => HttpContext.Access()!;

    // ── The editor ──────────────────────────────────────────────────────

    [HttpGet("")]
    [Right(Section, Rights.Create)]      // rights_guard _doc_page: a blank form needs "create"
    public Task<IActionResult> New(CancellationToken ct) => FormAsync(null, ct);

    [HttpGet("{id:int:min(0)}")]
    [Right(Section, Rights.View)]        // ... a saved one "view"
    public Task<IActionResult> Edit(int id, CancellationToken ct) => FormAsync(id, ct);

    private async Task<IActionResult> FormAsync(int? id, CancellationToken ct)
    {
        // scoped_get (not _404): an unknown id opens a blank order.
        var order = id is int oid
            ? await db.Set<InvSalesOrder>().AsNoTracking().Where(o => o.Id == oid)
                .Select(o => new
                {
                    o.Id, o.Status, o.SoNumber, o.CustomerId, CustomerName = o.Customer.Name, CustomerAddress = o.Customer.Address,
                    o.PartyAccountId, o.OrderDate, o.ExpectedDate, o.TaxMode, o.GlobalSalesTaxPct, o.Notes, o.ApprovedAt, o.EditVersion,
                    HasApprover = o.ApprovedByNavigation != null, ApproverName = o.ApprovedByNavigation!.FullName,
                }).FirstOrDefaultAsync(ct)
            : null;
        string itemsJson = "[]";
        if (order is not null)
        {
            // order.items.all(): no ORDER BY, as Python (the scan's order); products read in one query by id.
            var items = await db.Set<InvSalesOrderItem>().AsNoTracking().Where(i => i.SoId == order.Id)
                .Select(i => new
                {
                    i.ProductId, i.Description, i.Quantity, i.Unit, i.UnitPrice, i.SalesTaxPct, i.TotalBeforeDiscount, i.TotalAfterDiscount,
                }).ToListAsync(ct);
            var products = await reads.ProductsByIdAsync(items.Select(i => i.ProductId), ct);
            itemsJson = OrderReads.ItemsJson(items.Select(i =>
            {
                var p = products.GetValueOrDefault(i.ProductId);
                return (i.ProductId, p?.Sku, p?.Weight, p is not null, i.Description, i.Quantity, i.Unit, i.UnitPrice, i.SalesTaxPct,
                        i.TotalBeforeDiscount, i.TotalAfterDiscount);
            }));
        }
        string partyMode = await reads.PartyModeAsync("sales", ct);
        string productsJson = await reads.ProductsJsonAsync(ct);
        var model = order is null
            ? new OrderFormModel { PartyMode = partyMode, ProductsJson = productsJson }
            : new OrderFormModel
            {
                Id = order.Id, Status = order.Status, Number = order.SoNumber, PartyId = order.CustomerId,
                PartyName = order.CustomerName, PartyAddress = Jinja.Str(order.CustomerAddress),
                PartyAccountId = order.PartyAccountId, OrderDate = order.OrderDate, ExpectedDate = order.ExpectedDate,
                TaxMode = order.TaxMode, GlobalSalesTaxPct = order.GlobalSalesTaxPct, Notes = order.Notes,
                HasApprover = order.HasApprover, ApproverName = order.ApproverName, ApprovedAt = order.ApprovedAt,
                EditVersion = order.EditVersion, OrderItemsJson = itemsJson, PartyMode = partyMode, ProductsJson = productsJson,
            };
        return View("Form", model);
    }

    // ── Save / approve, unapprove, delete (JSON) ────────────────────────

    [HttpPost("save")]
    [AnyMember]   // rights checked below (rights_guard _json_save), answered in JSON
    public async Task<IActionResult> Save(CancellationToken ct)
    {
        var data = await OrderInput.ReadAsync(Request, ct);
        if (OrderAnswers.DenySave(this, Section, data) is { } refused) return refused;
        if (data is null) return OrderAnswers.BadRequest();
        if (!data.IsObject) return OrderAnswers.ServerError();
        try
        {
            return OrderAnswers.From(await book.SaveAsync(data, Access.User.Id, ct));
        }
        catch (OrderInputException)
        {
            return OrderAnswers.ServerError();
        }
    }

    [HttpPost("unapprove/{id:int:min(0)}")]
    [AnyMember]   // rights checked below: approve, answered in JSON (rights_guard JSON_ENDPOINTS)
    public async Task<IActionResult> Unapprove(int id, CancellationToken ct)
    {
        if (this.Deny(Section, Rights.Approve, json: true) is { } refused) return refused;
        return OrderAnswers.From(await book.UnapproveAsync(id, ct));
    }

    [HttpPost("delete/{id:int:min(0)}")]
    [AnyMember]   // rights checked below: delete, answered in JSON (rights_guard JSON_ENDPOINTS)
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        if (this.Deny(Section, Rights.Delete, json: true) is { } refused) return refused;
        return OrderAnswers.From(await book.DeleteAsync(id, ct));
    }

    // ── List, Bill, Cancel ──────────────────────────────────────────────

    [HttpGet("list")]
    [Right(Section, Rights.View)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        string status = Request.Query["status"].FirstOrDefault() ?? "";
        var q = db.Set<InvSalesOrder>().AsNoTracking();
        if (status.Length > 0) q = q.Where(o => o.Status == status);
        var rows = await q.OrderByDescending(o => o.Id)
            .Select(o => new OrderListRow(o.Id, o.SoNumber, o.Customer.Name, o.OrderDate, o.ExpectedDate, o.TotalAmount, o.Status))
            .ToListAsync(ct);
        return View("List", new OrderListModel(rows, Request.Query["status"].FirstOrDefault(), Access.NumberFormat));
    }

    /// <summary>Retired "Deliver": an order is fulfilled by billing it (stock, COGS and revenue move on the invoice).</summary>
    [HttpGet("deliver/{id:int:min(0)}")]
    [Right(Section, Rights.View)]
    public async Task<IActionResult> Deliver(int id, CancellationToken ct)
    {
        string? number = await db.Set<InvSalesOrder>().AsNoTracking().Where(o => o.Id == id).Select(o => o.SoNumber).FirstOrDefaultAsync(ct);
        if (number is null) return OrderAnswers.NotFound();
        this.Flash($"Bill {number} with a sales invoice (Import from order): stock, cost of " +
                   "sales and revenue are posted when the invoice is approved.", "info");
        return Redirect("/inventory/invoices/");
    }

    [HttpPost("cancel/{id:int:min(0)}")]
    [Right(Section, Rights.Edit)]        // as the route checks
    public async Task<IActionResult> Cancel(int id, CancellationToken ct)
    {
        var (found, category, message) = await book.CancelAsync(id, ct);
        if (!found) return OrderAnswers.NotFound();
        this.Flash(message, category);
        return Redirect("/inventory/sales/list");
    }

    // ── Lookups the form makes ──────────────────────────────────────────

    [HttpGet("api/products")]
    [AnyRight("sales_orders:view", "sales_orders:create", "sales_orders:edit")]
    public async Task<IActionResult> ApiProducts(CancellationToken ct) =>
        PyJson.Result(await reads.ProductsAsync(OrderAnswers.Q(Request), ct));

    [HttpGet("api/customers")]
    [AnyRight("sales_orders:view", "sales_orders:create", "sales_orders:edit")]
    public async Task<IActionResult> ApiCustomers(CancellationToken ct)
    {
        string q = OrderAnswers.Q(Request);
        var query = db.Set<InvCustomer>().AsNoTracking().Where(c => c.IsActive == true);
        if (q.Length > 0)
        {
            string like = $"%{q}%";
            query = query.Where(c => EF.Functions.ILike(c.Name, like));
        }
        var rows = await query.OrderBy(c => c.Name).Take(20).Select(c => new { c.Id, c.Name, c.City, c.Phone, c.Address }).ToListAsync(ct);
        return PyJson.Result(rows.Select(c => OrderReads.Party(c.Id, c.Name, c.City, c.Phone, c.Address)).ToList());
    }

    [HttpGet("api/accounts")]
    [AnyRight("sales_orders:view", "sales_orders:create", "sales_orders:edit")]
    public async Task<IActionResult> ApiAccounts(CancellationToken ct) =>
        PyJson.Result(await reads.AccountsAsync(OrderAnswers.Q(Request), ct));

    [HttpGet("api/orders/{customer_id:int:min(0)}")]
    [AnyRight("sales_orders:view", "sales_orders:create", "sales_orders:edit")]
    public async Task<IActionResult> ApiOrders([FromRoute(Name = "customer_id")] int customerId, CancellationToken ct)
    {
        var orders = await db.Set<InvSalesOrder>().AsNoTracking()
            .Where(o => o.CustomerId == customerId && o.Status == "approved")   // .all(): no ORDER BY, as Python (heap order)
            .Select(o => new { o.Id, o.SoNumber, o.OrderDate, o.TotalAmount }).ToListAsync(ct);
        var ids = orders.Select(o => o.Id).ToList();
        var items = (await db.Set<InvSalesOrderItem>().AsNoTracking().Where(i => ids.Contains(i.SoId))
                .Select(i => new { i.Id, i.SoId, i.ProductId, i.Quantity, i.UnitPrice }).ToListAsync(ct))
            .ToLookup(i => i.SoId);
        var products = await reads.ProductsByIdAsync(items.SelectMany(g => g).Select(i => i.ProductId), ct);
        return PyJson.Result(orders.Select(o => new Dictionary<string, object?>
        {
            ["id"] = o.Id,
            ["so_number"] = o.SoNumber,
            ["order_date"] = o.OrderDate?.ToString("yyyy-MM-dd") ?? "",
            ["total_amount"] = Py.FN(o.TotalAmount),
            ["items"] = items[o.Id].Select(i =>
            {
                var p = products.GetValueOrDefault(i.ProductId);
                return OrderReads.PickerItem(i.Id, i.ProductId, p is not null, p?.Name, p?.Sku, i.Quantity, i.UnitPrice);
            }).ToList(),
        }).ToList());
    }
}
