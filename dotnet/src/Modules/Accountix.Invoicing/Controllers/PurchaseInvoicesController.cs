using System.Text.Json;
using Accountix.Invoicing.Models;
using Accountix.Invoicing.Services;

namespace Accountix.Invoicing.Controllers;

/// <summary>Purchase invoices (invoicing_app/routes/purchase_invoice.py), under /inventory/purchase-invoice.</summary>
[Area("Invoicing")]
[Module(Modules.Invoicing)]
[Route("inventory/purchase-invoice")]
public sealed class PurchaseInvoicesController(
    AccountixDbContext db, ILedger ledger, IStockCosting stock, CompanyClock clock, DocSettings settings,
    OrderLinkage orders, EditVersions versions, InvoicePrint print, TrackingSync tracking, InvoiceLookups lookups) : Controller
{
    public const string Section = "purchase_invoices";
    private EffectiveAccess Access => HttpContext.Access()!;

    // ── The editor ──────────────────────────────────────────────────────

    [HttpGet("")]
    [HttpGet("{id:int}")]
    [AnyMember]   // rights_guard _doc_page: create for a blank form, view for a saved one
    public async Task<IActionResult> Form(int? id, CancellationToken ct)
    {
        if (this.Deny(Section, id is > 0 ? Rights.View : Rights.Create) is { } refused) return refused;
        InvPurchaseInvoice? invoice = id is int iid
            ? await db.Set<InvPurchaseInvoice>().AsNoTracking()
                .Include(i => i.Supplier).Include(i => i.ApprovedByNavigation)
                .FirstOrDefaultAsync(i => i.Id == iid, ct)
            : null;
        var items = new List<Dictionary<string, object?>>();
        var charges = new List<Dictionary<string, object?>>();
        string? rendered = null;
        if (invoice is not null)
        {
            var rows = await db.Set<InvPurchaseInvoiceItem>().AsNoTracking().Where(i => i.InvoiceId == invoice.Id).OrderBy(i => i.Id).ToListAsync(ct);
            var pids = rows.Where(r => r.ProductId != null).Select(r => r.ProductId!.Value).Distinct().ToList();
            var products = await db.Set<InvProduct>().AsNoTracking().Where(p => pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
            foreach (var it in rows)
            {
                products.TryGetValue(it.ProductId ?? 0, out var product);
                items.Add(new Dictionary<string, object?>
                {
                    ["product_id"] = it.ProductId,
                    ["product"] = new Dictionary<string, object?> { ["sku"] = product?.Sku ?? "" },
                    ["weight"] = product is not null ? (object)(product.Weight is decimal w && w != 0 ? Py.F(w) : 0) : 0,
                    ["description"] = it.Description,
                    ["quantity"] = Py.FN(it.Quantity),
                    ["unit"] = it.Unit,
                    ["unit_price"] = Py.FN(it.UnitPrice),
                    ["label_id"] = it.LabelId,
                    ["discount_pct"] = Py.FN(it.DiscountPct),
                    ["discount_amount"] = Py.FN(it.DiscountAmount),
                    ["commission"] = Py.FN(it.Commission),
                    ["freight"] = Py.FN(it.Freight),
                    ["loading_unloading"] = Py.FN(it.LoadingUnloading),
                    ["sales_tax_pct"] = Py.FN(it.SalesTaxPct),
                    ["total_before_discount"] = Py.FN(it.TotalBeforeDiscount),
                    ["total_after_discount"] = Py.FN(it.TotalAfterDiscount),
                });
            }
            var chargeRows = await db.Set<AdditionalCharge>().AsNoTracking().Include(c => c.ChargeAccount)
                .Where(c => c.DocType == "PI" && c.DocId == invoice.Id).OrderBy(c => c.Id).ToListAsync(ct);
            foreach (var chg in chargeRows)
            {
                string? treatment = chg.Treatment;
                if (treatment is not ("bill" or "absorb" or "expense"))
                    treatment = chg.TaxBase is "bill" or "absorb" or "expense" ? chg.TaxBase : "bill";
                charges.Add(new Dictionary<string, object?>
                {
                    ["id"] = chg.Id,
                    ["charge_account_id"] = chg.ChargeAccountId,
                    ["account_code"] = chg.ChargeAccount?.Code ?? "",
                    ["account_name"] = chg.ChargeAccount?.Name ?? "",
                    ["_display"] = chg.ChargeAccount is { } a ? $"{a.Code} — {a.Name}" : "",
                    ["description"] = chg.Description,
                    ["amount"] = Py.FN(chg.Amount),
                    ["scope"] = chg.Scope,
                    ["treatment"] = treatment,
                    ["st_taxable"] = chg.StTaxable == true,
                    ["wht_taxable"] = chg.WhtTaxable == true,
                    ["extra_taxable"] = chg.ExtraTaxable == true,
                });
            }
            rendered = await print.PurchaseAsync(invoice, ct, rows, products, numberFormat: Access.Profile?.NumberFormat);
        }
        var inventory = await settings.InventoryAsync(ct);
        var rs = await settings.ReportAsync(ct);
        return View("Form", new PurchaseInvoiceFormModel
        {
            Invoice = invoice,
            InvoiceItemsJson = items.Count > 0 ? PyJson.Dumps(items, htmlSafe: true) : null,
            InvoiceChargesJson = charges.Count > 0 ? PyJson.Dumps(charges, htmlSafe: true) : null,
            PerLineLabeling = inventory.PerLineLabelingInvoice == true,
            PartyMode = DocSettings.PartyMode(rs, "purchase"),
            RenderedTemplate = rendered,
            ProjectLabels = await settings.ActiveLabelsAsync(ct),
            DefaultLabel = await settings.DefaultInvoiceLabelAsync(ct),
            ShowSourceGate = inventory.PurchaseFlow == "with_po",
            CanApprove = Access.IsAdmin || Access.IsManager,
        });
    }

    // ── The list ────────────────────────────────────────────────────────

    [HttpGet("list")]
    [Right(Section, Rights.View)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] string? from, [FromQuery] string? to,
                                          [FromQuery] string? month, [FromQuery] int page = 1, CancellationToken ct = default)
    {
        // The status select is shown but, as in Python, the purchase list is not filtered by it.
        var q = db.Set<InvPurchaseInvoice>().AsNoTracking();
        var (fromD, toD) = InvoiceLookups.Range(from, to, month);
        if (fromD is DateTime f) q = q.Where(i => i.InvoiceDate >= f);
        if (toD is DateTime t) q = q.Where(i => i.InvoiceDate < t);
        int total = await q.CountAsync(ct);
        var pager = Pager.For(total, page);
        var rows = await q.OrderByDescending(i => i.Id).Skip(pager.Skip).Take(Pager.Size)
            .Select(i => new PurchaseInvoiceRow(i.Id, i.InvoiceNumber, i.VoucherNumber, i.Supplier.Name, i.CreatedAt, i.Subtotal, i.TotalTax,
                i.TotalFurtherTax, i.TotalWithholdingTax, i.TotalAmount, i.PaidAmount, i.Status, i.PaymentStatus, i.ApprovedAt))
            .ToListAsync(ct);
        return View("List", new InvoiceListModel<PurchaseInvoiceRow>
        {
            Rows = rows, Pager = pager, Status = status ?? "", From = from ?? "", To = to ?? "",
        });
    }

    // ── Save / approve ──────────────────────────────────────────────────

    /// <summary>validate_approve.</summary>
    public static List<string> Validate(PyData data)
    {
        var errors = new List<string>();
        if (!data.Truthy("supplier_id")) errors.Add("Supplier is required");
        var items = data.List("items").ToList();
        if (!data.Has("items") || items.Count == 0)
            errors.Add("At least one item is required");
        else
        {
            for (int i = 0; i < items.Count; i++)
            {
                var row = items[i];
                if (!row.Truthy("product_id")) errors.Add($"Row {i + 1}: Product is required");
                double qty;
                try { qty = row.FloatOr0("quantity"); }
                catch (PyValueException) { qty = 0; }
                if (qty <= 0) errors.Add($"Row {i + 1}: Quantity must be greater than 0");
            }
        }
        errors.AddRange(DocChecks.AmountErrors(data));
        return errors;
    }

    private static ContentResult Fail(string error, int status, bool stale = false) =>
        PyJson.Result(stale ? new Dictionary<string, object?> { ["ok"] = false, ["error"] = error, ["stale"] = true }
                            : new Dictionary<string, object?> { ["ok"] = false, ["error"] = error }, status);

    /// <summary>charge_buckets: absorb / bill / expense totals, and the billed and expense rows.</summary>
    public static (double Absorb, double Bill, double Expense, List<PyData> Billed, List<PyData> Expensed) ChargeBuckets(IEnumerable<PyData> charges)
    {
        double absorb = 0, bill = 0, expense = 0;
        var billed = new List<PyData>();
        var expensed = new List<PyData>();
        foreach (var c in charges)
        {
            double amt = c.FloatOr0("amount");
            if (amt <= 0 || !c.Truthy("charge_account_id")) continue;
            string? t = c.Str("treatment", "bill");
            if (t == "absorb") absorb += amt;
            else if (t == "expense") { expense += amt; expensed.Add(c); }
            else { bill += amt; billed.Add(c); }
        }
        return (absorb, bill, expense, billed, expensed);
    }

    [HttpPost("save")]
    [AnyMember]   // rights_guard _json_save: they follow the payload's id and action
    public async Task<IActionResult> Save(CancellationToken ct)
    {
        PyData data;
        try { data = await PyData.ReadAsync(Request, ct); }
        catch (PyBadRequestException) { return BadRequest(); }
        int? invId = data.Id("id");
        string? action = data.Str("action", "save");
        bool approve = action == "approve";
        if (invId is not null)
        {
            if (this.Deny(Section, approve ? Rights.Approve : Rights.Edit, json: true) is { } refused) return refused;
        }
        else
        {
            if (this.Deny(Section, Rights.Create, json: true) is { } refused) return refused;
            if (approve && this.Deny(Section, Rights.Approve, json: true) is { } refusedApprove) return refusedApprove;
        }
        int userId = Access.User.Id;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        InvPurchaseInvoice inv;
        if (invId is int id)
        {
            var found = await db.LockAsync<InvPurchaseInvoice>(id, ct);
            if (found is null) return NotFound();
            inv = found;
            if (inv.Status == "approved")
                return Fail("Cannot modify approved invoice", 400);
            string? stale = await versions.StaleAsync("inv_purchase_invoices", inv.Id, inv.CompanyId, inv.EditVersion, data.Get("edit_version"),
                $"Invoice {inv.InvoiceNumber}", v => inv.EditVersion = v, ct);
            if (stale is not null)
                return Fail(stale, 409, stale: true);
        }
        else
        {
            string? number = data.Str("invoice_number", null);
            if (string.IsNullOrEmpty(number) || number == "(auto)") number = DocNumbers.Placeholder();
            inv = new InvPurchaseInvoice
            {
                VoucherNumber = number, InvoiceNumber = number, CreatedBy = userId, CreatedAt = clock.UtcNow,
                // Python's status default is applied at the insert: a new draft is saved "new".
                Status = null, PaymentStatus = "unpaid", DiscountMode = "general", ExpensesMode = "general", TaxMode = "general",
                ApplyFurtherTax = false, ApplyWithholdingTax = false, EditVersion = 0,
            };
            db.Add(inv);
        }

        var items = data.List("items").ToList();
        if (approve)
        {
            var errors = Validate(data);
            if (errors.Count > 0)
                return Fail(string.Join("; ", errors), 400);
            var over = await orders.CheckOverInvoicingAsync(OrderLinkage.Purchase,
                items.Select(r => new Billing(r.Id("source_order_item_id"), r.FloatOr0("quantity"))), ct);
            if (over.Count > 0)
                return Fail("Over-invoicing blocked — " + string.Join("; ", over), 400);
        }

        int? supplierId = data.Id("supplier_id");
        if (supplierId is null)
            throw new InvalidOperationException("null value in column \"supplier_id\" of relation \"inv_purchase_invoices\" violates not-null constraint");
        inv.SupplierId = supplierId.Value;
        inv.InvoiceDate = ParseDocDate(data.Get("invoice_date"), inv.InvoiceDate);
        inv.PartyAccountId = data.Id("party_account_id");
        var defaultLabel = await settings.DefaultInvoiceLabelAsync(ct);
        int? defaultLid = defaultLabel?.Id;
        inv.LabelId = data.Id("label_id") ?? defaultLid;
        inv.DriverName = data.Str("driver_name", "");
        inv.DriverContact = data.Str("driver_contact", "");
        inv.VehicleNumber = data.Str("vehicle_number", "");
        inv.GatePass = data.Str("gate_pass", "");
        inv.DiscountMode = data.Str("discount_mode", "general");
        inv.ExpensesMode = data.Str("expenses_mode", "general");
        inv.TaxMode = data.Str("tax_mode", "general");

        double gst = data.Float("global_sales_tax_pct"), gwht = data.Float("global_withholding_tax_pct");
        inv.GlobalDiscountPct = Py.Dec(data.Float("global_discount_pct"));
        inv.GlobalDiscountValue = Py.Dec(data.Float("global_discount_value"));
        inv.GlobalCommission = Py.Dec(data.Float("global_commission"));
        inv.GlobalFreight = Py.Dec(data.Float("global_freight"));
        inv.GlobalLoading = Py.Dec(data.Float("global_loading"));
        inv.GlobalSalesTaxPct = Py.Dec(gst);
        inv.GlobalWithholdingTaxPct = Py.Dec(gwht);
        // A purchase invoice never carries further tax (a sales-side levy, §8).
        inv.FurtherTaxPct = 0;
        inv.ApplyFurtherTax = false;
        bool applyWht = data.Bool("apply_withholding_tax");
        inv.ApplyWithholdingTax = applyWht;
        inv.Notes = data.Str("notes", "");
        inv.Subtotal = Py.Dec(data.Float("subtotal"));
        double totalDiscount = data.Float("total_discount");
        inv.TotalDiscount = Py.Dec(totalDiscount);
        inv.TotalExpenses = Py.Dec(data.Float("total_expenses"));
        double totalTax = data.Float("total_tax");
        inv.TotalTax = Py.Dec(totalTax);
        inv.TotalFurtherTax = Py.Dec(data.Float("total_further_tax"));
        inv.TotalWithholdingTax = Py.Dec(data.Float("total_withholding_tax"));
        inv.NetPayable = Py.Dec(data.Float("net_payable"));
        inv.TotalAmount = Py.Dec(data.Float("total_amount"));
        inv.PaidAmount = Py.Dec(data.Float("paid_amount"));
        inv.PaymentStatus = data.Str("payment_status", "unpaid");
        inv.PurchaseOrderId = data.Id("purchase_order_id");

        if (approve)
        {
            inv.Status = "approved";
            inv.ApprovedBy = userId;
            inv.ApprovedAt = clock.UtcNow;
        }
        else if (inv.Status == "new")
            inv.Status = "unapproved";
        inv.Status ??= "new";

        await db.SaveChangesAsync(ct);
        if (DocNumbers.IsPlaceholder(inv.InvoiceNumber))
        {
            string number = DocNumbers.FromId("PI", clock.Now, inv.Id);
            inv.InvoiceNumber = number;
            inv.VoucherNumber = number;
            await db.SaveChangesAsync(ct);
        }
        if (approve)
            await stock.LockProductsAsync(items.Select(r => SafeId(r.Get("product_id"))).Where(p => p is not null).Select(p => p!.Value), ct);

        // Additional charges by treatment: absorb = carriage inward, bill = the supplier's own line, expense = we bear it.
        var chargesData = data.List("charges").ToList();
        var (absorbTotal, billTotal, expenseTotal, billedCharges, expenseCharges) = ChargeBuckets(chargesData);
        double globalDiscount = inv.DiscountMode == "general" ? totalDiscount : 0.0;

        await db.Set<InvPurchaseInvoiceItem>().Where(i => i.InvoiceId == inv.Id).ExecuteDeleteAsync(ct);
        foreach (var e in db.ChangeTracker.Entries<InvPurchaseInvoiceItem>().ToList()) e.State = EntityState.Detached;
        var costRows = new List<(InvPurchaseInvoiceItem Item, double Qty, double After, double Comm, double Freight, double Load)>();
        foreach (var row in items)
        {
            double qty = row.Float("quantity", 1), after = row.Float("total_after_discount");
            double comm = row.Float("commission"), freight = row.Float("freight"), load = row.Float("loading_unloading");
            var item = new InvPurchaseInvoiceItem
            {
                InvoiceId = inv.Id,
                ProductId = row.Id("product_id"),
                Description = row.Str("description", ""),
                Quantity = Py.Dec(qty),
                Unit = row.Str("unit", "pcs"),
                UnitPrice = Py.Dec(row.Float("unit_price")),
                LabelId = row.Id("label_id") ?? inv.LabelId ?? defaultLid,
                SourceOrderId = row.Id("source_order_id"),
                SourceOrderItemId = row.Id("source_order_item_id"),
                SourceOrderNumber = row.Truthy("source_order_number") ? row.Str("source_order_number", "") : "",
                DiscountPct = Py.Dec(row.Float("discount_pct")),
                DiscountAmount = Py.Dec(row.Float("discount_amount")),
                Commission = Py.Dec(comm),
                Freight = Py.Dec(freight),
                LoadingUnloading = Py.Dec(load),
                SalesTaxPct = Py.Dec(row.Float("sales_tax_pct")),
                WithholdingTaxPct = Py.Dec(row.Float("withholding_tax_pct")),
                TotalBeforeDiscount = Py.Dec(row.Float("total_before_discount")),
                TotalAfterDiscount = Py.Dec(after),
                Comments = row.Str("comments", ""),
            };
            db.Add(item);
            costRows.Add((item, qty, after, comm, freight, load));
        }
        await db.SaveChangesAsync(ct);

        // Per-item absorbed expenses already capitalise; tax figures are re-derived, never trusted from the browser.
        double perItemAbsorb = Py.Sum(costRows.Select(r => r.Comm + r.Freight + r.Load));
        var baseValues = costRows.Select(r => r.After).ToList();
        double totalBase = Py.Sum(baseValues);
        double stCharges = Py.Sum(billedCharges.Where(c => c.Has("st_taxable") ? c.Truthy("st_taxable") : (!c.Has("taxable") || c.Truthy("taxable")))
            .Select(c => c.FloatOr0("amount")));
        double whtCharges = Py.Sum(billedCharges.Where(c => c.Truthy("wht_taxable")).Select(c => c.FloatOr0("amount")));
        double effectiveGoods = Py.Round(totalBase + perItemAbsorb + absorbTotal - globalDiscount, 2);
        double inputTaxBase = Py.Round(effectiveGoods + stCharges, 2);
        if ((string.IsNullOrEmpty(inv.TaxMode) ? "general" : inv.TaxMode) == "general")
            totalTax = Py.Round(inputTaxBase * gst / 100, 2);
        double totalWht = applyWht ? Py.Round((effectiveGoods + whtCharges) * gwht / 100, 2) : 0.0;
        double netPayable = Py.Round(effectiveGoods + billTotal + totalTax - totalWht, 2);
        inv.TotalTax = Py.Dec(totalTax);
        inv.TotalWithholdingTax = Py.Dec(totalWht);
        inv.TotalFurtherTax = 0;
        inv.NetPayable = Py.Dec(netPayable);
        inv.TotalAmount = Py.Dec(netPayable);

        var landedByLabel = new List<(int? Label, double Value)>();
        if (approve)
        {
            // Document-level absorbed carriage (less the combined discount) spread pro-rata by value; the
            // last receipt takes the rounding remainder so the layers sum to the Inventory debit.
            double spread = Py.Round(absorbTotal - globalDiscount, 2);
            double spreadLeft = spread;
            int n = costRows.Count;
            double denom = totalBase != 0 ? totalBase : 1.0;
            double inventoryTarget = Py.Round(totalBase + perItemAbsorb + (absorbTotal - globalDiscount), 2);
            bool allStock = costRows.All(r => r.Item.ProductId is > 0);
            double received = 0.0;
            for (int idx = 0; idx < n; idx++)
            {
                var (item, qty, after, comm, freight, load) = costRows[idx];
                if (item.ProductId is not int pid) continue;
                bool exists = await db.Set<InvProduct>().AsNoTracking().AnyAsync(p => p.Id == pid, ct);
                if (!exists) continue;
                db.Add(new InvStockMovement
                {
                    ProductId = pid, Type = "purchase_in", Quantity = Py.Dec(qty), ReferenceType = "purchase_invoice",
                    ReferenceId = inv.Id, Notes = $"Approved invoice {inv.InvoiceNumber}", CreatedBy = userId, CreatedAt = clock.UtcNow,
                });
                await db.SaveChangesAsync(ct);
                double share;
                if (idx == n - 1) share = spreadLeft;
                else
                {
                    share = Py.Round(spread * baseValues[idx] / denom, 2);
                    spreadLeft = Py.Round(spreadLeft - share, 2);
                }
                double landedTotal = after + comm + freight + load + share;
                int? key = item.LabelId ?? inv.LabelId;
                int li = landedByLabel.FindIndex(x => x.Label == key);
                if (li < 0) landedByLabel.Add((key, 0.0 + landedTotal));
                else landedByLabel[li] = (key, landedByLabel[li].Value + landedTotal);
                if (qty > 0)
                {
                    double lineValue = Py.Round(landedTotal, 2);
                    if (allStock && idx == n - 1) lineValue = Py.Round(inventoryTarget - received, 2);
                    received = Py.Round(received + lineValue, 2);
                    await stock.RecordInAsync(pid, "PI", inv.Id, inv.VoucherNumber, Py.Dec(qty), Py.Dec(lineValue / qty),
                        $"Purchase {inv.InvoiceNumber}", userId, inv.InvoiceDate, Py.Dec(lineValue), ct);
                }
            }
            await orders.ApplyWritebackAsync(OrderLinkage.Purchase,
                costRows.Select(r => new Billing(r.Item.SourceOrderItemId, r.Qty)).ToList(), 1, ct);
        }

        await db.Set<AdditionalCharge>().Where(c => c.DocType == "PI" && c.DocId == inv.Id).ExecuteDeleteAsync(ct);
        foreach (var e in db.ChangeTracker.Entries<AdditionalCharge>().ToList()) e.State = EntityState.Detached;
        foreach (var chg in chargesData)
            if (chg.FloatOr0("amount") > 0 && chg.Truthy("charge_account_id"))
                db.Add(BuildCharge(inv.Id, chg));
        await db.SaveChangesAsync(ct);

        if (approve)
        {
            var invAcc = await ledger.PostingAccountAsync("inventory", ct);
            var supplier = await db.Set<InvSupplier>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == inv.SupplierId, ct);
            var apAcc = await ledger.PartyAccountAsync("supplier", inv.SupplierId, supplier?.Name ?? "", inv.PartyAccountId, ct);
            double inventoryDr = Py.Round(totalBase + perItemAbsorb + (absorbTotal - globalDiscount), 2);
            double inputTax = Py.Round(totalTax, 2);
            double wht = Py.Round(totalWht, 2);
            double invoiceGross = Py.Round(inventoryDr + billTotal + inputTax, 2);
            double amountPayable = Py.Round(invoiceGross - wht, 2);
            int? plid = inv.LabelId;
            int? llid = costRows.Count > 0 ? costRows[0].Item.LabelId : plid;
            var weights = LabelSplit.Weights(landedByLabel);
            if (weights.Count == 0) weights = [(llid, 1m)];
            var lines = LabelSplit.Split(Py.Dec(inventoryDr), weights)
                .Select(s => new JournalLineInput(invAcc.Id, s.Amount, 0m, $"Inventory - {inv.InvoiceNumber}", s.Label)).ToList();
            if (inputTax > 0)
            {
                var inTax = await ledger.PostingAccountAsync("input_tax", ct);
                lines.Add(new JournalLineInput(inTax.Id, Py.Dec(inputTax), 0m, $"Input Tax - {inv.InvoiceNumber}"));
            }
            foreach (var c in billedCharges)
                lines.Add(new JournalLineInput(c.Id("charge_account_id")!.Value, Py.Dec(Py.Round(c.Float("amount"), 2)), 0m,
                    $"{(c.Truthy("description") ? c.Str("description") : "Charge")} - {inv.InvoiceNumber}", llid));
            foreach (var c in expenseCharges)
                lines.Add(new JournalLineInput(c.Id("charge_account_id")!.Value, Py.Dec(Py.Round(c.Float("amount"), 2)), 0m,
                    $"{(c.Truthy("description") ? c.Str("description") : "Expense")} - {inv.InvoiceNumber}", llid));
            lines.Add(new JournalLineInput(apAcc.Id, 0m, Py.Dec(amountPayable), $"AP - {inv.InvoiceNumber}", plid));
            if (wht > 0.005)
            {
                var whtAcc = await ledger.PostingAccountAsync("wht_payable", ct);
                lines.Add(new JournalLineInput(whtAcc.Id, 0m, Py.Dec(wht), $"WHT - {inv.InvoiceNumber}"));
            }
            if (expenseTotal > 0.005)
            {
                var accrued = await ledger.PostingAccountAsync("accrued", ct);
                lines.Add(new JournalLineInput(accrued.Id, 0m, Py.Dec(Py.Round(expenseTotal, 2)), $"Charges payable - {inv.InvoiceNumber}"));
            }
            await ledger.PostAsync("PI", inv.Id, inv.VoucherNumber, $"Purchase Invoice {inv.InvoiceNumber} - {supplier?.Name ?? ""}",
                lines, inv.InvoiceDate, userId, ct);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await tracking.SyncInvoiceAsync(TrackingSync.PurchaseDoc, inv.Id, ct);
        string msg = approve ? "approved and locked" : invId is not null ? "changes saved" : "saved as unapproved";
        return PyJson.Result(new Dictionary<string, object?>
        {
            ["ok"] = true, ["id"] = inv.Id, ["status"] = inv.Status, ["edit_version"] = inv.EditVersion,
            ["voucher"] = inv.VoucherNumber, ["message"] = $"Invoice {msg}",
        });
    }

    /// <summary>build_charge: one purchase-invoice charge row (treatment and the three tax-base switches).</summary>
    private AdditionalCharge BuildCharge(int docId, PyData chg)
    {
        string? treatment = chg.Str("treatment", "bill");
        if (treatment is not ("bill" or "absorb" or "expense")) treatment = "bill";
        bool billed = treatment == "bill";
        return new AdditionalCharge
        {
            DocType = "PI", DocId = docId,
            ChargeAccountId = chg.Id("charge_account_id")!.Value,
            Description = chg.Str("description", ""),
            Amount = Py.Dec(chg.Float("amount")),
            Scope = chg.Str("scope", "general"),
            Treatment = treatment,
            StTaxable = billed && chg.Bool("st_taxable", true),
            WhtTaxable = billed && chg.Bool("wht_taxable"),
            ExtraTaxable = chg.Bool("extra_taxable"),
            // Column defaults Python leaves to the insert.
            Distribution = "pro_rata_value", ManualAllocations = "", Taxable = true, TaxBase = "after_discount",
            CreatedAt = clock.UtcNow,
        };
    }

    private static int? SafeId(JsonElement? e)
    {
        try { return PyData.ToId(e); }
        catch (PyValueException) { return null; }
    }

    private DateTime ParseDocDate(JsonElement? value, DateTime? fallback)
    {
        if (value is null || value.Value.ValueKind == JsonValueKind.Null
            || value.Value.ValueKind == JsonValueKind.String && value.Value.GetString() == "")
            return DocumentDates.Parse(null, fallback, clock);
        string s = value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString()! : value.Value.GetRawText();
        return DocumentDates.Parse(s, fallback, clock);
    }

    // ── Unapprove / delete / pay ────────────────────────────────────────

    [HttpPost("unapprove/{id:int}")]
    [AnyMember]   // purchase_invoices approve, answered in JSON
    public async Task<IActionResult> Unapprove(int id, CancellationToken ct)
    {
        if (this.Deny(Section, Rights.Approve, json: true) is { } refused) return refused;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var inv = await db.LockAsync<InvPurchaseInvoice>(id, ct);
        if (inv is null) return NotFound();
        if (inv.Status != "approved")
            return Fail("Only approved invoices can be unapproved", 400);
        var returns = await db.Set<InvPurchaseReturn>().AsNoTracking()
            .Where(r => r.OriginalInvoiceId == inv.Id && r.Status == "approved").OrderBy(r => r.Id).Select(r => r.ReturnNumber).ToListAsync(ct);
        if (returns.Count > 0)
            return Fail("Cannot unapprove: approved purchase return(s) " + string.Join(", ", returns) +
                        " were raised against this invoice. Unapprove them first.", 400);
        int userId = Access.User.Id;
        await ledger.ReverseAsync("PI", inv.Id, userId, ct);
        var itemRows = await db.Set<InvPurchaseInvoiceItem>().AsNoTracking().Where(i => i.InvoiceId == inv.Id).OrderBy(i => i.Id).ToListAsync(ct);
        await orders.ApplyWritebackAsync(OrderLinkage.Purchase,
            itemRows.Select(i => new Billing(i.SourceOrderItemId, Py.F(i.Quantity))).ToList(), -1, ct);
        inv.Status = "unapproved";
        inv.ApprovedBy = null;
        inv.ApprovedAt = null;
        await db.Set<InvStockMovement>().Where(m => m.ReferenceType == "purchase_invoice" && m.ReferenceId == inv.Id).ExecuteDeleteAsync(ct);
        var moved = await stock.ReverseVoucherStockAsync("PI", inv.Id, allowVariance: true, createdBy: userId, ct: ct);
        inv.EditVersion = EditVersions.Bump(inv.EditVersion);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await tracking.SyncInvoiceAsync(TrackingSync.PurchaseDoc, inv.Id, ct);

        string message = "Invoice has been unapproved and unlocked for editing";
        var pids = itemRows.Where(i => i.ProductId != null).Select(i => i.ProductId!.Value).Distinct().ToList();
        var names = await db.Set<InvProduct>().AsNoTracking().Where(p => pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var shortList = new List<string>();
        foreach (var it in itemRows)
            if (it.ProductId is int pid && names.TryGetValue(pid, out var name) && await stock.OnHandAsync(pid, ct: ct) < 0)
                shortList.Add(name);
        decimal movedSum = moved?.Values.Sum() ?? 0m;
        if (moved is { Count: > 0 })
            message += $". Later issues were re-costed in date order (net {Py.Amount(movedSum, 2, Access.Profile?.NumberFormat ?? "en")}) and adjusted in the ledger";
        if (shortList.Count > 0)
            message += ". Stock from this invoice was already issued (" + string.Join(", ", shortList.Take(5)) +
                       "); re-approve the corrected invoice on its date to re-cost those issues";
        return PyJson.Result(new Dictionary<string, object?>
        {
            ["ok"] = true, ["status"] = "unapproved", ["message"] = message, ["edit_version"] = inv.EditVersion,
            ["variance"] = moved is { Count: > 0 } ? Py.F(movedSum) : 0,
        });
    }

    [HttpPost("delete/{id:int}")]
    [AnyMember]   // purchase_invoices delete, answered in JSON
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        if (this.Deny(Section, Rights.Delete, json: true) is { } refused) return refused;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var inv = await db.LockAsync<InvPurchaseInvoice>(id, ct);
        if (inv is null) return NotFound();
        if (inv.Status == "approved")
            return Fail("Cannot delete an approved invoice. Unapprove it first.", 400);
        try
        {
            await db.Set<InvPurchaseInvoiceItem>().Where(i => i.InvoiceId == inv.Id).ExecuteDeleteAsync(ct);
            await tracking.DropAllocationsAsync(TrackingSync.PurchaseDoc, inv.Id, ct);
            db.Remove(inv);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return PyJson.Result(new Dictionary<string, object?> { ["ok"] = true, ["message"] = "Invoice deleted successfully" });
        }
        catch (DbUpdateException e)
        {
            await tx.RollbackAsync(ct);
            return Fail(e.InnerException?.Message ?? e.Message, 500);
        }
    }

    [HttpPost("pay/{id:int}")]
    [Right(Section, Rights.Approve)]
    public async Task<IActionResult> Pay(int id, CancellationToken ct)
    {
        var inv = await db.Set<InvPurchaseInvoice>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
        if (inv is null) return NotFound();
        this.Flash("Record payments as a Cash/Bank Payment Voucher first.", "info");
        return Redirect($"/invoicing/tracking/invoice/PI/{inv.Id}");
    }

    // ── Lookups ─────────────────────────────────────────────────────────

    [HttpGet("api/products")]
    [AnyRight("purchase_invoices:view", "purchase_invoices:create", "purchase_invoices:edit")]
    public async Task<IActionResult> ApiProducts([FromQuery] string? q, CancellationToken ct) =>
        PyJson.Result(await lookups.ProductsAsync(q, withWeight: true, ct));

    [HttpGet("api/suppliers")]
    [AnyRight("purchase_invoices:view", "purchase_invoices:create", "purchase_invoices:edit")]
    public async Task<IActionResult> ApiSuppliers([FromQuery] string? q, CancellationToken ct) =>
        PyJson.Result(await lookups.SuppliersAsync(q, ct));

    [HttpGet("api/orders/{supplierId:int}")]
    [AnyRight("purchase_invoices:view", "purchase_invoices:create", "purchase_invoices:edit")]
    public async Task<IActionResult> ApiOrders(int supplierId, CancellationToken ct) =>
        PyJson.Result(await lookups.PurchaseOrdersForPickerAsync(supplierId, ct));

    [HttpGet("api/accounts")]
    [AnyRight("purchase_invoices:view", "purchase_invoices:create", "purchase_invoices:edit")]
    public async Task<IActionResult> ApiAccounts([FromQuery] string? q, CancellationToken ct) =>
        PyJson.Result(await lookups.ChargeAccountsAsync(q, withDefaults: null, ct));

    [HttpPost("api/new-product")]
    [AnyMember]   // products create, answered in JSON
    public async Task<IActionResult> ApiNewProduct(CancellationToken ct)
    {
        if (this.Deny("products", Rights.Create, json: true) is { } refused) return refused;
        var data = await PyData.ReadAsync(Request, ct);
        string sku = (data.Str("sku", "") ?? throw new PyValueException("'NoneType' object has no attribute 'strip'")).Trim();
        string name = (data.Str("name", "") ?? throw new PyValueException("'NoneType' object has no attribute 'strip'")).Trim();
        if (sku.Length == 0 || name.Length == 0)
            return Fail("SKU and Name required", 400);
        if (await db.Set<InvProduct>().AnyAsync(p => p.Sku == sku, ct))
            return Fail("SKU already exists", 400);
        var p = new InvProduct
        {
            Sku = sku, Name = name, UnitPrice = Py.Dec(data.Float("unit_price")), CostPrice = Py.Dec(data.Float("cost_price")),
            Unit = data.Str("unit", "pcs"), CurrentStock = 0, ReorderLevel = 0, HsCode = "", Weight = 0, IsActive = true,
            CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow,
        };
        db.Add(p);
        await db.SaveChangesAsync(ct);
        return PyJson.Result(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["product"] = new Dictionary<string, object?>
            {
                ["id"] = p.Id, ["name"] = p.Name, ["sku"] = p.Sku, ["unit_price"] = Py.FN(p.UnitPrice),
                ["current_stock"] = 0, ["unit"] = p.Unit,
            },
        });
    }
}
