using Accountix.Invoicing.Services;
using Accountix.Returns.Models;
using Accountix.Returns.Services;
using static Accountix.Returns.Services.ReturnSupport;

namespace Accountix.Returns.Controllers;

/// <summary>
/// Purchase returns / debit notes (invoicing_app/routes/purchase_return.py,
/// blueprint inv_purchase_return, /inventory/purchase-return). The goods leave
/// stock at the landed cost they came in at, drawn from the invoice's own layers;
/// the journal mirrors the purchase for the returned share (AP, WHT, inventory,
/// input tax, and any price difference).
/// </summary>
[Area("Returns")]
[Module(Modules.Invoicing)]
[Route("inventory/purchase-return")]
public sealed class PurchaseReturnsController(
    AccountixDbContext db, ILedger ledger, IStockCosting stock, CompanyClock clock,
    EditVersions versions, TrackingSync tracking, OrderLinkage orders) : Controller
{
    private const string Section = "purchase_returns";
    private EffectiveAccess Access => HttpContext.Access()!;

    // ── The form ────────────────────────────────────────────────────────

    [HttpGet("")]
    [HttpGet("{id:int:min(0)}")]
    [AnyMember]   // rights_guard _doc_page: "view" for a saved return, "create" for a blank form
    public async Task<IActionResult> Form(int? id, CancellationToken ct)
    {
        if (this.Deny(Section, id is > 0 ? Rights.View : Rights.Create) is { } refused) return refused;
        var ret = id is > 0
            ? await db.Set<InvPurchaseReturn>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct)
            : null;
        var model = new ReturnFormModel { Side = ReturnSide.Purchase };
        if (ret is not null)
        {
            model.Id = ret.Id;
            model.Status = ret.Status ?? "new";
            model.ReturnNumber = ret.ReturnNumber;
            model.OriginalInvoiceId = ret.OriginalInvoiceId;
            model.ReturnDate = ret.ReturnDate;
            model.Notes = ret.Notes;
            model.Reverse = ret.ReverseExpenses == true;
            model.EditVersion = ret.EditVersion ?? 0;
            model.ApprovedAt = ret.ApprovedAt;
            model.ApproverName = ret.ApprovedBy is int ab
                ? await db.Set<User>().AsNoTracking().Where(u => u.Id == ab).Select(u => u.FullName).FirstOrDefaultAsync(ct)
                : null;
            model.Items = await db.Set<InvPurchaseReturnItem>().AsNoTracking().Where(i => i.ReturnId == ret.Id)
                .OrderBy(i => i.Id).Select(i => new ReturnFormModel.SavedItem(i.ProductId, i.CurrentReturnQty)).ToListAsync(ct);
        }
        // Every approved purchase invoice, newest first, dated by created_at as Python's template does.
        model.Invoices = await db.Set<InvPurchaseInvoice>().AsNoTracking()
            .Where(i => i.Status == "approved")
            .OrderByDescending(i => i.Id)
            .Select(i => new ReturnFormModel.InvoiceOption(i.Id, i.InvoiceNumber, i.Supplier.Name, i.CreatedAt))
            .ToListAsync(ct);
        return View("~/Areas/Returns/Views/Returns/PurchaseForm.cshtml", model);
    }

    // ── JSON for the form ───────────────────────────────────────────────

    [HttpGet("api/invoice/{invoiceId:int:min(0)}")]
    [AnyRight("purchase_returns:view", "purchase_returns:create", "purchase_returns:edit")]
    public async Task<IActionResult> ApiInvoiceDetail(int invoiceId, [FromQuery(Name = "return_id")] string? returnId,
                                                      CancellationToken ct)
    {
        // The return being edited (the form sends its id) is not "previously returned" against
        // itself; every other draft or approved return of the invoice counts.
        int editing = ReturnSupport.ArgInt(returnId) ?? 0;
        var inv = await db.Set<InvPurchaseInvoice>().AsNoTracking()
            .Where(i => i.Id == invoiceId && i.Status == "approved")
            .Select(i => new
            {
                i.Id, i.InvoiceNumber, i.VoucherNumber, i.CreatedAt, i.SupplierId, SupplierName = i.Supplier.Name,
                i.DriverName, i.DriverContact, i.VehicleNumber, i.GatePass,
                i.Subtotal, i.TotalDiscount, i.TotalExpenses, i.TotalTax, i.NetPayable, i.DiscountMode, i.ExpensesMode, i.TaxMode,
            })
            .FirstOrDefaultAsync(ct);
        if (inv is null)
            return Error("Invoice not found or not approved", 404);

        var lines = await db.Set<InvPurchaseInvoiceItem>().AsNoTracking().Where(i => i.InvoiceId == inv.Id).OrderBy(i => i.Id).ToListAsync(ct);
        var productIds = lines.Where(l => l.ProductId is not null).Select(l => l.ProductId!.Value).Distinct().ToList();
        var returned = await db.Set<InvPurchaseReturnItem>().AsNoTracking()
            .Where(ri => ri.Return.OriginalInvoiceId == inv.Id && (ri.Return.Status == "unapproved" || ri.Return.Status == "approved")
                         && ri.ReturnId != editing)
            .GroupBy(ri => ri.ProductId)
            .Select(g => new { ProductId = g.Key, Qty = g.Sum(x => x.CurrentReturnQty) })
            .ToListAsync(ct);
        var skus = await db.Set<InvProduct>().AsNoTracking().Where(p => productIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Sku }).ToDictionaryAsync(p => p.Id, p => p.Sku, ct);

        var items = lines.Select(orig =>
        {
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
                ["commission"] = Py.FN(orig.Commission),
                ["freight"] = Py.FN(orig.Freight),
                ["loading_unloading"] = Py.FN(orig.LoadingUnloading),
                ["sales_tax_pct"] = Py.FN(orig.SalesTaxPct),
                ["withholding_tax_pct"] = Py.FN(orig.WithholdingTaxPct),
                ["total_before_discount"] = Py.FN(orig.TotalBeforeDiscount),
                ["total_after_discount"] = Py.FN(orig.TotalAfterDiscount),
                ["proportional_ratio"] = 0,
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
                ["date"] = inv.CreatedAt?.ToString("yyyy-MM-dd") ?? "",
                ["supplier_id"] = inv.SupplierId,
                ["supplier_name"] = inv.SupplierName ?? "",
                ["driver_name"] = inv.DriverName ?? "",
                ["driver_contact"] = inv.DriverContact ?? "",
                ["vehicle_number"] = inv.VehicleNumber ?? "",
                ["gate_pass"] = inv.GatePass ?? "",
                ["subtotal"] = Py.FN(inv.Subtotal),
                ["total_discount"] = Py.FN(inv.TotalDiscount),
                ["total_expenses"] = Py.FN(inv.TotalExpenses),
                ["total_tax"] = Py.FN(inv.TotalTax),
                ["net_payable"] = Py.FN(inv.NetPayable),
                ["discount_mode"] = inv.DiscountMode,
                ["expenses_mode"] = inv.ExpensesMode,
                ["tax_mode"] = inv.TaxMode,
            },
            ["items"] = items,
        });
    }

    [HttpGet("api/invoices")]
    [AnyRight("purchase_returns:view", "purchase_returns:create", "purchase_returns:edit")]
    public async Task<IActionResult> ApiInvoices([FromQuery] string? q, CancellationToken ct)
    {
        q = (q ?? "").Trim();
        var query = db.Set<InvPurchaseInvoice>().AsNoTracking().Where(i => i.Status == "approved");
        if (q.Length > 0)
        {
            // ilike(f"%{q}%"): % and _ in the search stay wildcards, as in Python.
            string like = "%" + q + "%";
            query = query.Where(i => EF.Functions.ILike(i.InvoiceNumber, like) || EF.Functions.ILike(i.Supplier.Name, like));
        }
        var rows = await query.OrderByDescending(i => i.Id).Take(30)
            .Select(i => new { i.Id, i.InvoiceNumber, SupplierName = i.Supplier.Name, i.CreatedAt, i.NetPayable })
            .ToListAsync(ct);
        return PyJson.Result(rows.Select(i => new Dictionary<string, object?>
        {
            ["id"] = i.Id,
            ["invoice_number"] = i.InvoiceNumber,
            ["supplier_name"] = i.SupplierName ?? "",
            ["date"] = i.CreatedAt?.ToString("yyyy-MM-dd") ?? "",
            ["net_payable"] = Py.FN(i.NetPayable),
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
        InvPurchaseReturn ret;
        if (hasId)
        {
            // Locked until commit: a second user approving the same return waits,
            // then finds it approved instead of posting it again.
            var locked = await db.LockAsync<InvPurchaseReturn>(data.Id("id")!.Value, ct);
            if (locked is null) return NotFound();
            ret = locked;
            if (ret.Status == "approved")
                return Error("Cannot modify approved return", 400);
            string? stale = await versions.StaleAsync("inv_purchase_returns", ret.Id, ret.CompanyId, ret.EditVersion,
                data.Get("edit_version"), $"Return {ret.ReturnNumber}", v => ret.EditVersion = v, ct);
            if (stale is not null)
                return Error(stale, 409, stale: true);
        }
        else
        {
            // Python's column defaults land when the row is first flushed: status "new", edit_version 0.
            ret = new InvPurchaseReturn
            {
                ReturnNumber = DocNumbers.Placeholder(), CreatedBy = uid, CreatedAt = clock.UtcNow,
                Status = "new", EditVersion = 0,
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
                                ?? throw new InvalidOperationException("inv_purchase_returns.original_invoice_id cannot be NULL");
        ret.SupplierId = data.Id("supplier_id")
                         ?? throw new InvalidOperationException("inv_purchase_returns.supplier_id cannot be NULL");
        // The debit note posts on its own date — and can never precede the purchase.
        ret.ReturnDate = DocumentDates.Parse(DateText(data.Get("date")), ret.ReturnDate, clock);
        InvPurchaseInvoice? orig = null;
        if (approve)
        {
            // Approving locks the invoice being returned against: two returns raised
            // on it at the same instant both passed the over-return check below.
            orig = await db.LockAsync<InvPurchaseInvoice>(ret.OriginalInvoiceId, ct);
            if (orig is null || orig.Status != "approved")
                return Error("A return can only be raised against an approved invoice", 400);
            if (orig.InvoiceDate is DateTime invDate && ret.ReturnDate.Value.Date < invDate.Date)
                return Error($"Return date cannot be before the invoice date ({DayMonYear(invDate)})", 400);
            var over = await OverReturnedAsync(ret, data, ct);
            if (over.Count > 0)
                return Error(string.Join("; ", over), 400);
        }
        ret.Notes = data.Str("notes", "");
        ret.ReverseExpenses = Bool(data.Get("reverse_expenses"), true);
        double grossValue = data.Float("gross_return_value");
        double totalDiscount = data.Float("total_discount");
        double totalExpenses = data.Float("total_expenses");
        double totalTax = data.Float("total_tax");
        double netReturn = data.Float("net_return_amount");
        ret.GrossReturnValue = Py.Dec(grossValue);
        ret.TotalDiscount = Py.Dec(totalDiscount);
        ret.TotalExpenses = Py.Dec(totalExpenses);
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
            ret.ReturnNumber = DocNumbers.FromId("DN", clock.Now, ret.Id);
            await db.SaveChangesAsync(ct);
        }

        var invoiceLines = approve
            ? await db.Set<InvPurchaseInvoiceItem>().AsNoTracking().Where(i => i.InvoiceId == ret.OriginalInvoiceId).OrderBy(i => i.Id)
                .Select(i => new { i.ProductId, i.SourceOrderItemId, i.Quantity }).ToListAsync(ct)
            : null;
        if (approve)
        {
            // Lock order: document, order lines, products — as every posting does.
            await orders.LockOrderLinesAsync(OrderLinkage.Purchase, invoiceLines!.Select(i => i.SourceOrderItemId), ct);
            await stock.LockProductsAsync(ReturnRules.ProductIdsToLock(data), ct);
        }

        await db.Set<InvPurchaseReturnItem>().Where(i => i.ReturnId == ret.Id).ExecuteDeleteAsync(ct);
        foreach (var e in db.ChangeTracker.Entries<InvPurchaseReturnItem>().ToList())
            e.State = EntityState.Detached;

        var rows = data.List("items").ToList();
        var known = approve ? await KnownProductsAsync(rows, ct) : [];
        var returnedByProduct = new List<(int ProductId, double Qty)>();
        double stockCostOut = 0.0;
        foreach (var row in rows)
        {
            double qty = row.Float("current_return_qty");
            if (qty <= 0) continue;
            var item = new InvPurchaseReturnItem
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
                Commission = Py.Dec(row.Float("commission")),
                Freight = Py.Dec(row.Float("freight")),
                LoadingUnloading = Py.Dec(row.Float("loading_unloading")),
                SalesTaxPct = Py.Dec(row.Float("sales_tax_pct")),
                WithholdingTaxPct = Py.Dec(row.Float("withholding_tax_pct")),
                TotalBeforeDiscount = Py.Dec(row.Float("total_before_discount")),
                TotalAfterDiscount = Py.Dec(row.Float("total_after_discount")),
                ProportionalDiscount = Py.Dec(row.Float("proportional_discount")),
                ProportionalSalesTax = Py.Dec(row.Float("proportional_sales_tax")),
                ProportionalWithholdingTax = Py.Dec(row.Float("proportional_withholding_tax")),
                ProportionalCommission = Py.Dec(row.Float("proportional_commission")),
                ProportionalFreight = Py.Dec(row.Float("proportional_freight")),
                ProportionalLoading = Py.Dec(row.Float("proportional_loading")),
                NetReturnValue = Py.Dec(row.Float("net_return_value")),
            };
            db.Add(item);
            if (item.ProductId is not int pid) continue;
            int at = returnedByProduct.FindIndex(r => r.ProductId == pid);
            if (at < 0) returnedByProduct.Add((pid, 0.0 + qty));
            else returnedByProduct[at] = (pid, returnedByProduct[at].Qty + qty);

            if (!approve || !known.Contains(pid)) continue;
            db.Add(new InvStockMovement
            {
                ProductId = pid, Type = "purchase_return_out", Quantity = Py.Dec(Py.Round(-qty, 4)),
                ReferenceType = "purchase_return", ReferenceId = ret.Id,
                Notes = $"Approved return {ret.ReturnNumber}", CreatedBy = uid, CreatedAt = clock.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            // Returned goods leave stock at the LANDED cost they came in at on the
            // original invoice, drawn from that invoice's own layers, so the inventory
            // credit undoes exactly the inventory debit. Any gap between that cost and
            // what the supplier credits is a price difference, posted below.
            decimal? basis = await stock.OriginalReceiptCostAsync("PI", ret.OriginalInvoiceId, pid, ct);
            var (_, lineCost) = await stock.IssueAsync(pid, "PRV", ret.Id, ret.ReturnNumber, Py.Dec(qty),
                $"Purchase return {ret.ReturnNumber}", uid, basis, ret.ReturnDate, ("PI", ret.OriginalInvoiceId), ct);
            stockCostOut += F(lineCost);
        }
        await db.SaveChangesAsync(ct);

        if (approve)
        {
            // Mirror of the purchase posting, for the returned share:
            //   Dr Supplier (AP)        debit note value (what they now owe back)
            //   Dr WHT Payable          withholding no longer owed on the goods
            //     Cr Inventory          landed cost of the stock that left
            //     Cr Input Sales Tax    input tax no longer recoverable
            //     Cr/Dr Price variance  supplier credit vs landed cost
            // Debit the same supplier account the original invoice credited.
            string? supplierName = await db.Set<InvSupplier>().AsNoTracking().Where(s => s.Id == ret.SupplierId)
                .Select(s => s.Name).FirstOrDefaultAsync(ct);
            var apAcc = await ledger.PartyAccountAsync("supplier", ret.SupplierId, supplierName!, orig!.PartyAccountId, ct);
            var invAcc = await ledger.PostingAccountAsync("inventory", ct);
            double net = Py.Round(netReturn, 2);
            double tax = Py.Round(totalTax, 2);
            double wht = Py.Round(grossValue - totalDiscount + totalExpenses + tax - net, 2);
            wht = wht > 0.005 ? wht : 0.0;
            double cost = Py.Round(stockCostOut, 2);
            double diff = Py.Round(net + wht - tax - cost, 2);
            int? labelId = orig.LabelId;
            string num = ret.ReturnNumber;
            var lines = new List<JournalLineInput> { new(apAcc.Id, Py.Dec(net), 0m, $"AP - {num}", labelId) };
            if (wht != 0)
            {
                var whtAcc = await ledger.PostingAccountAsync("wht_payable", ct);
                lines.Add(new(whtAcc.Id, Py.Dec(wht), 0m, $"WHT reversal - {num}"));
            }
            if (cost != 0)
                lines.Add(new(invAcc.Id, 0m, Py.Dec(cost), $"Inventory - {num}", labelId));
            if (tax != 0)
            {
                var inputTax = await ledger.PostingAccountAsync("input_tax", ct);
                lines.Add(new(inputTax.Id, 0m, Py.Dec(tax), $"Input tax reversal - {num}"));
            }
            if (Math.Abs(diff) >= 0.01)
            {
                var varAcc = await ledger.PostingAccountAsync("inventory_variance", ct);
                lines.Add(new(varAcc.Id, diff < 0 ? Py.Dec(-diff) : 0m, diff > 0 ? Py.Dec(diff) : 0m,
                              $"Purchase price difference - {num}", labelId));
            }
            await ledger.PostAsync("PR", ret.Id, num, $"Purchase Return {num}", lines, ret.ReturnDate, uid, ct);

            // Crediting a posted invoice restores the order balances it consumed (§4.4).
            await orders.ApplyReturnWritebackAsync(OrderLinkage.Purchase,
                invoiceLines!.Select(i => (i.ProductId, i.SourceOrderItemId, F(i.Quantity))), returnedByProduct, -1, ct);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        // A return settles part of its invoice: its paid/unpaid status moves with it.
        await tracking.SyncInvoiceAsync(TrackingSync.PurchaseDoc, ret.OriginalInvoiceId, ct);

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

    /// <summary>_over_returned: purchased quantity less what OTHER approved returns already took.</summary>
    private async Task<List<string>> OverReturnedAsync(InvPurchaseReturn ret, PyData data, CancellationToken ct)
    {
        int invoiceId = ret.OriginalInvoiceId;
        int self = ret.Id;   // 0 for a new return (Python: ret.id or 0)
        var bought = await db.Set<InvPurchaseInvoiceItem>().AsNoTracking().Where(i => i.InvoiceId == invoiceId).OrderBy(i => i.Id)
            .Select(i => new { i.ProductId, i.Quantity }).ToListAsync(ct);
        var already = await db.Set<InvPurchaseReturnItem>().AsNoTracking()
            .Where(i => i.Return.OriginalInvoiceId == invoiceId && i.Return.Status == "approved" && i.ReturnId != self)
            .Select(i => new { i.ProductId, i.CurrentReturnQty }).ToListAsync(ct);
        return ReturnRules.OverReturned(bought.Select(s => (s.ProductId, F(s.Quantity))),
                                        already.Select(a => (a.ProductId, F(a.CurrentReturnQty))), data);
    }

    // ── Unapprove ───────────────────────────────────────────────────────

    [HttpPost("unapprove/{id:int:min(0)}")]
    [AnyMember]   // ("purchase_returns", "approve"), answered in JSON whatever the request asks for (a JSON endpoint)
    public Task<IActionResult> Unapprove(int id, CancellationToken ct) =>
        this.Deny(Section, Rights.Approve, json: true) is { } refused
            ? Task.FromResult(refused)
            : GuardedAsync(HttpContext, () => UnapproveAsync(id, ct));

    private async Task<IActionResult> UnapproveAsync(int id, CancellationToken ct)
    {
        int uid = Access.User.Id;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ret = await db.LockAsync<InvPurchaseReturn>(id, ct);
        if (ret is null) return NotFound();
        if (ret.Status != "approved")
            return Error("Only approved returns can be unapproved", 400);

        await ledger.ReverseAsync("PR", ret.Id, uid, ct);

        // Withdrawing the debit note re-consumes the order balances it released (§4.4).
        var invoiceLines = await db.Set<InvPurchaseInvoiceItem>().AsNoTracking().Where(i => i.InvoiceId == ret.OriginalInvoiceId)
            .OrderBy(i => i.Id).Select(i => new { i.ProductId, i.SourceOrderItemId, i.Quantity }).ToListAsync(ct);
        var returnLines = await db.Set<InvPurchaseReturnItem>().AsNoTracking().Where(i => i.ReturnId == ret.Id).OrderBy(i => i.Id)
            .Select(i => new { i.ProductId, i.CurrentReturnQty }).ToListAsync(ct);
        await orders.ApplyReturnWritebackAsync(OrderLinkage.Purchase,
            invoiceLines.Select(i => (i.ProductId, i.SourceOrderItemId, F(i.Quantity))),
            OrderLinkage.Tally(returnLines.Select(r => (r.ProductId, F(r.CurrentReturnQty)))), 1, ct);

        ret.Status = "unapproved";
        ret.ApprovedBy = null;
        ret.ApprovedAt = null;
        await db.SaveChangesAsync(ct);

        await db.Set<InvStockMovement>().Where(m => m.ReferenceType == "purchase_return" && m.ReferenceId == ret.Id).ExecuteDeleteAsync(ct);
        // Remove the return's stock rows and rebuild running balances.
        await stock.ReverseVoucherStockAsync("PRV", ret.Id, uid, ct);

        ret.EditVersion = EditVersions.Bump(ret.EditVersion);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await tracking.SyncInvoiceAsync(TrackingSync.PurchaseDoc, ret.OriginalInvoiceId, ct);
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
        var rows = await db.Set<InvPurchaseReturn>().AsNoTracking().OrderByDescending(r => r.Id)
            .Select(r => new ReturnListModel.Row(r.Id, r.ReturnNumber, r.OriginalInvoice.InvoiceNumber, r.Supplier.Name,
                r.CreatedAt, r.GrossReturnValue, r.NetReturnAmount, null, r.Status))
            .ToListAsync(ct);
        return View("~/Areas/Returns/Views/Returns/PurchaseList.cshtml", new ReturnListModel { Side = ReturnSide.Purchase, Rows = rows });
    }
}
