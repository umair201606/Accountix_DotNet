using Accountix.Invoicing.Services;
using Accountix.Returns.Models;
using Accountix.Returns.Services;
using static Accountix.Returns.Services.ReturnSupport;

namespace Accountix.Returns.Controllers;

/// <summary>
/// Sales returns / credit notes (invoicing_app/routes/sales_return.py, blueprint
/// inv_sales_return, /invoicing/sales-return). Goods come back at the cost they
/// left at on the invoice; the value side goes to the Sales Returns contra-revenue
/// account and the customer's own ledger is credited.
/// </summary>
[Area("Returns")]
[Module(Modules.Invoicing)]
[Route("invoicing/sales-return")]
public sealed class SalesReturnsController(
    AccountixDbContext db, ILedger ledger, IStockCosting stock, CompanyClock clock,
    EditVersions versions, TrackingSync tracking, OrderLinkage orders) : Controller
{
    private const string Section = "sales_returns";
    private EffectiveAccess Access => HttpContext.Access()!;

    // ── The form ────────────────────────────────────────────────────────

    [HttpGet("")]
    [HttpGet("{id:int:min(0)}")]
    [AnyMember]   // rights_guard _doc_page: "view" for a saved return, "create" for a blank form
    public async Task<IActionResult> Form(int? id, CancellationToken ct)
    {
        if (this.Deny(Section, id is > 0 ? Rights.View : Rights.Create) is { } refused) return refused;
        // scoped_get: a missing (or another company's) id opens a blank form, as in Python.
        var ret = id is > 0
            ? await db.Set<InvSalesReturn>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct)
            : null;
        var model = new ReturnFormModel { Side = ReturnSide.Sales };
        if (ret is not null)
        {
            model.Id = ret.Id;
            model.Status = ret.Status ?? "new";
            model.ReturnNumber = ret.ReturnNumber;
            model.OriginalInvoiceId = ret.OriginalInvoiceId;
            model.ReturnDate = ret.ReturnDate;
            model.Notes = ret.Notes;
            model.Reverse = ret.ReverseCharges == true;
            model.EditVersion = ret.EditVersion ?? 0;
            model.ApprovedAt = ret.ApprovedAt;
            model.ApproverName = ret.ApprovedBy is int ab
                ? await db.Set<User>().AsNoTracking().Where(u => u.Id == ab).Select(u => u.FullName).FirstOrDefaultAsync(ct)
                : null;
            model.Items = await db.Set<InvSalesReturnItem>().AsNoTracking().Where(i => i.ReturnId == ret.Id)
                .OrderBy(i => i.Id).Select(i => new ReturnFormModel.SavedItem(i.ProductId, i.CurrentReturnQty)).ToListAsync(ct);
        }
        // Every approved sales invoice, newest first, as "<number> - <customer> (<date>)".
        model.Invoices = await db.Set<InvInvoice>().AsNoTracking()
            .Where(i => i.VoucherStatus == "approved")
            .OrderByDescending(i => i.Id)
            .Select(i => new ReturnFormModel.InvoiceOption(i.Id, i.InvoiceNumber, i.Customer.Name, i.InvoiceDate))
            .ToListAsync(ct);
        return View("~/Areas/Returns/Views/Returns/SalesForm.cshtml", model);
    }

    // ── JSON for the form ───────────────────────────────────────────────

    [HttpGet("api/invoice/{invoiceId:int:min(0)}")]
    [AnyRight("sales_returns:view", "sales_returns:create", "sales_returns:edit")]
    public async Task<IActionResult> ApiInvoiceDetail(int invoiceId, [FromQuery(Name = "return_id")] string? returnId,
                                                      CancellationToken ct)
    {
        // The return being edited (the form sends its id) is not "previously returned" against
        // itself; every other draft or approved return of the invoice counts.
        int editing = ReturnSupport.ArgInt(returnId) ?? 0;
        var inv = await db.Set<InvInvoice>().AsNoTracking()
            .Where(i => i.Id == invoiceId && i.VoucherStatus == "approved")
            .Select(i => new
            {
                i.Id, i.InvoiceNumber, i.VoucherNumber, i.InvoiceDate, i.CustomerId, CustomerName = i.Customer.Name,
                i.Subtotal, i.TotalDiscount, i.TotalCharges, i.TotalTax, i.TotalAmount, i.DiscountMode, i.ChargesMode, i.TaxMode,
            })
            .FirstOrDefaultAsync(ct);
        if (inv is null)
            return Error("Invoice not found or not approved", 404);

        var lines = await db.Set<InvInvoiceItem>().AsNoTracking().Where(i => i.InvoiceId == inv.Id).OrderBy(i => i.Id).ToListAsync(ct);
        var productIds = lines.Where(l => l.ProductId is not null).Select(l => l.ProductId!.Value).Distinct().ToList();
        // What draft and approved returns of this invoice already hold, per product.
        var returned = await db.Set<InvSalesReturnItem>().AsNoTracking()
            .Where(ri => ri.Return.OriginalInvoiceId == inv.Id && (ri.Return.Status == "unapproved" || ri.Return.Status == "approved")
                         && ri.ReturnId != editing)
            .GroupBy(ri => ri.ProductId)
            .Select(g => new { ProductId = g.Key, Qty = g.Sum(x => x.CurrentReturnQty) })
            .ToListAsync(ct);
        var skus = await db.Set<InvProduct>().AsNoTracking().Where(p => productIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Sku }).ToDictionaryAsync(p => p.Id, p => p.Sku, ct);
        // original_issue_cost("SI", invoice, product): the first OUT row's frozen unit cost.
        var basis = await db.Set<StockLedger>().AsNoTracking()
            .Where(s => s.VoucherType == "SI" && s.VoucherId == inv.Id && s.TransactionType == "OUT" && productIds.Contains(s.ProductId))
            .GroupBy(s => s.ProductId)
            .Select(g => new { ProductId = g.Key, UnitCost = g.OrderBy(s => s.Id).Select(s => s.UnitCost).First() })
            .ToDictionaryAsync(x => x.ProductId, x => x.UnitCost, ct);

        var items = lines.Select(orig =>
        {
            // SUM over rows WHERE product_id = orig.product_id (NULL matches nothing).
            double previously = orig.ProductId is int pid ? F(returned.FirstOrDefault(r => r.ProductId == pid)?.Qty) : 0.0;
            double maxQty = F(orig.Quantity) - previously;
            return (object)new Dictionary<string, object?>
            {
                ["product_id"] = orig.ProductId,
                ["sku"] = orig.ProductId is int p && skus.TryGetValue(p, out var sku) ? sku : "",
                ["description"] = orig.Description,
                ["original_quantity"] = Py.FN(orig.Quantity),
                ["previously_returned"] = previously,
                ["max_returnable_qty"] = maxQty >= 0 ? (object)maxQty : 0,   // max(max_qty, 0): the int 0 when negative
                ["unit"] = orig.Unit,
                ["unit_price"] = Py.FN(orig.UnitPrice),
                ["discount_pct"] = Py.FN(orig.DiscountPct),
                ["discount_amount"] = Py.FN(orig.DiscountAmount),
                ["delivery"] = Py.FN(orig.Delivery),
                ["installation"] = Py.FN(orig.Installation),
                ["sales_tax_pct"] = Py.FN(orig.SalesTaxPct),
                ["total_before_discount"] = Py.FN(orig.TotalBeforeDiscount),
                ["total_after_discount"] = Py.FN(orig.TotalAfterDiscount),
                ["cost_basis"] = orig.ProductId is int b && basis.TryGetValue(b, out var c) ? F(c) : (object)0,
            };
        }).ToList();

        return Answer(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["invoice"] = new Dictionary<string, object?>
            {
                ["id"] = inv.Id,
                ["invoice_number"] = inv.InvoiceNumber,
                ["voucher_number"] = inv.VoucherNumber,
                ["date"] = inv.InvoiceDate?.ToString("yyyy-MM-dd") ?? "",
                ["customer_id"] = inv.CustomerId,
                ["customer_name"] = inv.CustomerName ?? "",
                ["subtotal"] = Py.FN(inv.Subtotal),
                ["total_discount"] = Py.FN(inv.TotalDiscount),
                ["total_charges"] = Py.FN(inv.TotalCharges),
                ["total_tax"] = Py.FN(inv.TotalTax),
                ["total_amount"] = Py.FN(inv.TotalAmount),
                ["discount_mode"] = inv.DiscountMode,
                ["charges_mode"] = inv.ChargesMode,
                ["tax_mode"] = inv.TaxMode,
            },
            ["items"] = items,
        });
    }

    [HttpGet("api/invoices")]
    [AnyRight("sales_returns:view", "sales_returns:create", "sales_returns:edit")]
    public async Task<IActionResult> ApiInvoices([FromQuery] string? q, CancellationToken ct)
    {
        q = (q ?? "").Trim();
        var query = db.Set<InvInvoice>().AsNoTracking().Where(i => i.VoucherStatus == "approved");
        if (q.Length > 0)
        {
            // ilike(f"%{q}%"): % and _ in the search stay wildcards, as in Python.
            string like = "%" + q + "%";
            query = query.Where(i => EF.Functions.ILike(i.InvoiceNumber, like) || EF.Functions.ILike(i.Customer.Name, like));
        }
        var rows = await query.OrderByDescending(i => i.Id).Take(30)
            .Select(i => new { i.Id, i.InvoiceNumber, CustomerName = i.Customer.Name, i.InvoiceDate, i.TotalAmount })
            .ToListAsync(ct);
        return PyJson.Result(rows.Select(i => new Dictionary<string, object?>
        {
            ["id"] = i.Id,
            ["invoice_number"] = i.InvoiceNumber,
            ["customer_name"] = i.CustomerName ?? "",
            ["date"] = i.InvoiceDate?.ToString("yyyy-MM-dd") ?? "",
            ["total_amount"] = Py.FN(i.TotalAmount),
        }).ToList());
    }

    // ── Save / approve ──────────────────────────────────────────────────

    [HttpPost("save")]
    [AnyMember]   // rights below: they follow the id and the action (rights_guard _json_save + the route's deny_json)
    public async Task<IActionResult> Save(CancellationToken ct)
    {
        string body = await BodyAsync(Request, ct);
        var root = TryParse(body);
        if (DenySave(this, Section, root) is { } refused) return refused;
        if (root is null) return BadRequestPage();
        var data = new PyData(root.Value);
        return await GuardedAsync(HttpContext, () => SaveAsync(data, ct));
    }

    private async Task<IActionResult> SaveAsync(PyData data, CancellationToken ct)
    {
        bool hasId = data.Truthy("id");
        string action = data.Str("action", "save") ?? "";
        bool approve = action == "approve";
        int uid = Access.User.Id;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        InvSalesReturn ret;
        if (hasId)
        {
            // Locked until commit: a second user approving the same return waits,
            // then finds it approved instead of posting it again.
            var locked = await db.LockAsync<InvSalesReturn>(data.Id("id")!.Value, ct);
            if (locked is null) return NotFound();
            ret = locked;
            if (ret.Status == "approved")
                return Error("Cannot modify approved return", 400);
            string? stale = await versions.StaleAsync("inv_sales_returns", ret.Id, ret.CompanyId, ret.EditVersion,
                data.Get("edit_version"), $"Return {ret.ReturnNumber}", v => ret.EditVersion = v, ct);
            if (stale is not null)
                return Error(stale, 409, stale: true);
        }
        else
        {
            // Python's column defaults land when the row is first flushed (an autoflush
            // ahead of the invoice lookup): status "new", edit_version 0, created_at now.
            ret = new InvSalesReturn
            {
                ReturnNumber = DocNumbers.Placeholder(), CreatedBy = uid, CreatedAt = clock.UtcNow,
                Status = "new", EditVersion = 0, TotalCostReturned = 0,
            };
            db.Add(ret);
        }

        if (approve)
        {
            var errors = ReturnRules.Validate(data);
            if (errors.Count > 0)
                return Error(string.Join("; ", errors), 400);
        }

        ret.OriginalInvoiceId = data.Id("original_invoice_id")
                                ?? throw new InvalidOperationException("inv_sales_returns.original_invoice_id cannot be NULL");
        ret.CustomerId = data.Id("customer_id")
                         ?? throw new InvalidOperationException("inv_sales_returns.customer_id cannot be NULL");
        // The return posts on its own date — and can never precede the sale.
        ret.ReturnDate = DocumentDates.Parse(DateText(data.Get("date")), ret.ReturnDate, clock);
        if (approve)
        {
            // Approving locks the invoice being returned against: two returns raised
            // on it at the same instant both passed the over-return check below.
            var orig = await db.LockAsync<InvInvoice>(ret.OriginalInvoiceId, ct);
            if (orig is null || orig.VoucherStatus != "approved")
                return Error("A return can only be raised against an approved invoice", 400);
            if (orig.InvoiceDate is DateTime invDate && ret.ReturnDate.Value.Date < invDate.Date)
                return Error($"Return date cannot be before the invoice date ({DayMonYear(invDate)})", 400);
            var over = await OverReturnedAsync(ret, data, ct);
            if (over.Count > 0)
                return Error(string.Join("; ", over), 400);
        }
        ret.Notes = data.Str("notes", "");
        ret.ReverseCharges = Bool(data.Get("reverse_charges"), true);
        double grossValue = data.Float("gross_return_value");
        double totalDiscount = data.Float("total_discount");
        double totalCharges = data.Float("total_charges");
        double totalTax = data.Float("total_tax");
        double netReturn = data.Float("net_return_amount");
        ret.GrossReturnValue = Py.Dec(grossValue);
        ret.TotalDiscount = Py.Dec(totalDiscount);
        ret.TotalCharges = Py.Dec(totalCharges);
        ret.TotalTax = Py.Dec(totalTax);
        ret.NetReturnAmount = Py.Dec(netReturn);

        if (approve)
        {
            ret.Status = "approved";
            ret.ApprovedBy = uid;
            ret.ApprovedAt = clock.UtcNow;
        }
        else if (ret.Status == "new")
            ret.Status = "unapproved";

        await db.SaveChangesAsync(ct);
        if (DocNumbers.IsPlaceholder(ret.ReturnNumber))
        {
            ret.ReturnNumber = DocNumbers.FromId("CN", clock.Now, ret.Id);
            await db.SaveChangesAsync(ct);
        }

        var invoiceLines = approve
            ? await db.Set<InvInvoiceItem>().AsNoTracking().Where(i => i.InvoiceId == ret.OriginalInvoiceId).OrderBy(i => i.Id)
                .Select(i => new { i.ProductId, i.SourceOrderItemId, i.Quantity }).ToListAsync(ct)
            : null;
        if (approve)
        {
            // Lock order: document, order lines, products — as every posting does.
            await orders.LockOrderLinesAsync(OrderLinkage.Sales, invoiceLines!.Select(i => i.SourceOrderItemId), ct);
            await stock.LockProductsAsync(ReturnRules.ProductIdsToLock(data), ct);
        }

        await db.Set<InvSalesReturnItem>().Where(i => i.ReturnId == ret.Id).ExecuteDeleteAsync(ct);
        foreach (var e in db.ChangeTracker.Entries<InvSalesReturnItem>().ToList())
            e.State = EntityState.Detached;

        var rows = data.List("items").ToList();
        var known = approve ? await KnownProductsAsync(rows, ct) : [];
        double totalCostReturned = 0.0;
        var returnedByProduct = new List<(int ProductId, double Qty)>();
        foreach (var row in rows)
        {
            double qty = row.Float("current_return_qty");
            if (qty <= 0) continue;
            var item = new InvSalesReturnItem
            {
                ReturnId = ret.Id,
                ProductId = row.Id("product_id"),
                Description = row.Str("description", ""),
                OriginalQuantity = Py.Dec(row.Float("original_quantity")),
                PreviouslyReturnedQty = Py.Dec(row.Float("previously_returned")),
                MaxReturnableQty = Py.Dec(row.Float("max_returnable_qty")),
                CurrentReturnQty = Py.Dec(qty),
                Unit = row.Str("unit", "pcs"),
                UnitPrice = Py.Dec(row.Float("unit_price")),
                DiscountPct = Py.Dec(row.Float("discount_pct")),
                DiscountAmount = Py.Dec(row.Float("discount_amount")),
                Delivery = Py.Dec(row.Float("delivery")),
                Installation = Py.Dec(row.Float("installation")),
                SalesTaxPct = Py.Dec(row.Float("sales_tax_pct")),
                TotalBeforeDiscount = Py.Dec(row.Float("total_before_discount")),
                TotalAfterDiscount = Py.Dec(row.Float("total_after_discount")),
                ProportionalDiscount = Py.Dec(row.Float("proportional_discount")),
                ProportionalSalesTax = Py.Dec(row.Float("proportional_sales_tax")),
                ProportionalDelivery = Py.Dec(row.Float("proportional_delivery")),
                ProportionalInstallation = Py.Dec(row.Float("proportional_installation")),
                NetReturnValue = Py.Dec(row.Float("net_return_value")),
                CostBasis = 0,
                TotalCostReturned = 0,
            };
            db.Add(item);
            if (item.ProductId is not int pid) continue;
            // Tallied here rather than from the cost block below, which skips lines
            // that never moved stock: a service line still consumed an order balance.
            int at = returnedByProduct.FindIndex(r => r.ProductId == pid);
            if (at < 0) returnedByProduct.Add((pid, 0.0 + qty));
            else returnedByProduct[at] = (pid, returnedByProduct[at].Qty + qty);

            if (!approve || !known.Contains(pid)) continue;
            db.Add(new InvStockMovement
            {
                ProductId = pid, Type = "sales_return_in", Quantity = Py.Dec(Py.Round(qty, 4)),
                ReferenceType = "sales_return", ReferenceId = ret.Id,
                Notes = $"Approved return {ret.ReturnNumber}", CreatedBy = uid, CreatedAt = clock.UtcNow,
            });
            // Returned goods re-enter stock at the cost they LEFT at on the original
            // invoice — never at today's valuation, so a sale and its return cancel out.
            decimal? basis = await stock.OriginalIssueCostAsync("SI", ret.OriginalInvoiceId, pid, ct);
            if (basis is not decimal b)
                continue;   // nothing left stock for this line (a service), so nothing comes back
            double fb = F(b);
            item.CostBasis = Py.Dec(fb);
            double lineCost = fb * qty;
            item.TotalCostReturned = Py.Dec(lineCost);
            totalCostReturned += lineCost;
            await db.SaveChangesAsync(ct);
            await stock.RecordInAsync(pid, "SRV", ret.Id, ret.ReturnNumber, Py.Dec(qty), b,
                $"Sales return {ret.ReturnNumber}", uid, ret.ReturnDate, ct: ct);
        }
        ret.TotalCostReturned = Py.Dec(totalCostReturned);
        await db.SaveChangesAsync(ct);

        if (approve)
        {
            // Crediting a posted invoice restores the order balances it consumed (§4.4).
            await orders.ApplyReturnWritebackAsync(OrderLinkage.Sales,
                invoiceLines!.Select(i => (i.ProductId, i.SourceOrderItemId, F(i.Quantity))), returnedByProduct, -1, ct);

            // Credit the same customer account the original invoice debited.
            string? customerName = await db.Set<InvCustomer>().AsNoTracking().Where(c => c.Id == ret.CustomerId)
                .Select(c => c.Name).FirstOrDefaultAsync(ct);
            var arAcc = await ledger.PartyAccountAsync("customer", ret.CustomerId, customerName!, ret.PartyAccountId, ct);
            var returnsAcc = await ledger.PostingAccountAsync("sales_returns", ct);
            var cogsAcc = await ledger.PostingAccountAsync("cogs", ct);
            var invAcc = await ledger.PostingAccountAsync("inventory", ct);

            double gross = netReturn;
            double tax = totalTax;
            double netOfTax = Py.Round(gross - tax, 2);
            string num = ret.ReturnNumber;
            // Value side: the contra-revenue account carries the return, so Sales stays gross.
            var lines = new List<JournalLineInput> { new(returnsAcc.Id, Py.Dec(netOfTax), 0m, $"Sales Return - {num}") };
            if (tax > 0)
            {
                var outTax = await ledger.PostingAccountAsync("sales_tax_payable", ct);
                lines.Add(new(outTax.Id, Py.Dec(tax), 0m, $"Output Tax reversal - {num}"));
            }
            lines.Add(new(arAcc.Id, 0m, Py.Dec(gross), $"AR - {num}"));
            // Cost side: stock comes back at the basis it left at, and the same comes off COGS.
            if (totalCostReturned > 0)
            {
                decimal cost = Py.Dec(Py.Round(totalCostReturned, 2));
                lines.Add(new(invAcc.Id, cost, 0m, $"Inventory - {num}"));
                lines.Add(new(cogsAcc.Id, 0m, cost, $"COGS reversal - {num}"));
            }
            await ledger.PostAsync("SR", ret.Id, num, $"Sales Return {num} - {customerName ?? ""}", lines,
                ret.ReturnDate, uid, ct);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        // A return settles part of its invoice: its paid/unpaid status moves with it.
        await tracking.SyncInvoiceAsync(TrackingSync.SalesDoc, ret.OriginalInvoiceId, ct);

        string msg = approve ? "approved and posted" : hasId ? "changes saved" : "saved as unapproved";
        return Answer(new Dictionary<string, object?>
        {
            ["ok"] = true, ["id"] = ret.Id, ["status"] = ret.Status, ["edit_version"] = ret.EditVersion,
            ["return_number"] = ret.ReturnNumber, ["message"] = $"Return {msg}",
        });
    }

    /// <summary>scoped_get(InvProduct, id) for every line: the products of this company, in one read.</summary>
    private async Task<HashSet<int>> KnownProductsAsync(List<PyData> rows, CancellationToken ct)
    {
        var ids = new List<int>();
        foreach (var r in rows)
            if (r.FloatOr0("current_return_qty") > 0 && r.Id("product_id") is int pid)
                ids.Add(pid);
        if (ids.Count == 0) return [];
        return (await db.Set<InvProduct>().AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => p.Id).ToListAsync(ct)).ToHashSet();
    }

    /// <summary>_over_returned: sold quantity less what OTHER approved returns already took.</summary>
    private async Task<List<string>> OverReturnedAsync(InvSalesReturn ret, PyData data, CancellationToken ct)
    {
        int invoiceId = ret.OriginalInvoiceId;
        int self = ret.Id;   // 0 for a new return (Python: ret.id or 0)
        var sold = await db.Set<InvInvoiceItem>().AsNoTracking().Where(i => i.InvoiceId == invoiceId).OrderBy(i => i.Id)
            .Select(i => new { i.ProductId, i.Quantity }).ToListAsync(ct);
        var already = await db.Set<InvSalesReturnItem>().AsNoTracking()
            .Where(i => i.Return.OriginalInvoiceId == invoiceId && i.Return.Status == "approved" && i.ReturnId != self)
            .Select(i => new { i.ProductId, i.CurrentReturnQty }).ToListAsync(ct);
        return ReturnRules.OverReturned(sold.Select(s => (s.ProductId, F(s.Quantity))),
                                        already.Select(a => (a.ProductId, F(a.CurrentReturnQty))), data);
    }

    // ── Unapprove ───────────────────────────────────────────────────────

    [HttpPost("unapprove/{id:int:min(0)}")]
    [AnyMember]   // ("sales_returns", "approve"), answered in JSON whatever the request asks for (a JSON endpoint)
    public Task<IActionResult> Unapprove(int id, CancellationToken ct) =>
        this.Deny(Section, Rights.Approve, json: true) is { } refused
            ? Task.FromResult(refused)
            : GuardedAsync(HttpContext, () => UnapproveAsync(id, ct));

    private async Task<IActionResult> UnapproveAsync(int id, CancellationToken ct)
    {
        int uid = Access.User.Id;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ret = await db.LockAsync<InvSalesReturn>(id, ct);
        if (ret is null) return NotFound();
        if (ret.Status != "approved")
            return Error("Only approved returns can be unapproved", 400);

        await ledger.ReverseAsync("SR", ret.Id, uid, ct);

        // Withdrawing the credit note re-consumes the order balances it released (§4.4).
        var invoiceLines = await db.Set<InvInvoiceItem>().AsNoTracking().Where(i => i.InvoiceId == ret.OriginalInvoiceId)
            .OrderBy(i => i.Id).Select(i => new { i.ProductId, i.SourceOrderItemId, i.Quantity }).ToListAsync(ct);
        var returnLines = await db.Set<InvSalesReturnItem>().AsNoTracking().Where(i => i.ReturnId == ret.Id).OrderBy(i => i.Id)
            .Select(i => new { i.ProductId, i.CurrentReturnQty }).ToListAsync(ct);
        await orders.ApplyReturnWritebackAsync(OrderLinkage.Sales,
            invoiceLines.Select(i => (i.ProductId, i.SourceOrderItemId, F(i.Quantity))),
            OrderLinkage.Tally(returnLines.Select(r => (r.ProductId, F(r.CurrentReturnQty)))), 1, ct);

        ret.Status = "unapproved";
        ret.ApprovedBy = null;
        ret.ApprovedAt = null;
        await db.SaveChangesAsync(ct);

        await db.Set<InvStockMovement>().Where(m => m.ReferenceType == "sales_return" && m.ReferenceId == ret.Id).ExecuteDeleteAsync(ct);
        // Withdraw the returned stock. Refused if it has since been re-sold.
        await stock.ReverseVoucherStockAsync("SRV", ret.Id, uid, ct);

        ret.EditVersion = EditVersions.Bump(ret.EditVersion);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await tracking.SyncInvoiceAsync(TrackingSync.SalesDoc, ret.OriginalInvoiceId, ct);
        return Answer(new Dictionary<string, object?>
        {
            ["ok"] = true, ["status"] = "unapproved", ["edit_version"] = ret.EditVersion,
            ["message"] = "Return has been unapproved and unlocked for editing",
        });
    }

    // ── List ────────────────────────────────────────────────────────────

    [HttpGet("list")]
    [Right(Section, Rights.View)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var rows = await db.Set<InvSalesReturn>().AsNoTracking().OrderByDescending(r => r.Id)
            .Select(r => new ReturnListModel.Row(r.Id, r.ReturnNumber, r.OriginalInvoice.InvoiceNumber, r.Customer.Name,
                r.CreatedAt, r.GrossReturnValue, r.NetReturnAmount, r.TotalCostReturned, r.Status))
            .ToListAsync(ct);
        return View("~/Areas/Returns/Views/Returns/SalesList.cshtml", new ReturnListModel { Side = ReturnSide.Sales, Rows = rows });
    }
}
