using System.Globalization;
using System.Text.Json;
using Accountix.Invoicing.Models;
using Accountix.Invoicing.Services;
using Microsoft.Extensions.Logging;

namespace Accountix.Invoicing.Controllers;

/// <summary>Sales invoices (invoicing_app/routes/invoices.py), under the Python app's URL /inventory/invoices.</summary>
[Area("Invoicing")]
[Module(Modules.Invoicing)]
[Route("inventory/invoices")]
public sealed class SalesInvoicesController(
    AccountixDbContext db, ILedger ledger, IStockCosting stock, CompanyClock clock, DocSettings settings,
    OrderLinkage orders, EditVersions versions, InvoicePrint print, TrackingSync tracking, InvoiceLookups lookups,
    ILogger<SalesInvoicesController> log) : Controller
{
    public const string Section = "sales_invoices";
    private EffectiveAccess Access => HttpContext.Access()!;

    // ── The editor ──────────────────────────────────────────────────────

    [HttpGet("")]
    [HttpGet("{id:int}")]
    [AnyMember]   // rights_guard _doc_page: create for a blank form, view for a saved one
    public async Task<IActionResult> Form(int? id, CancellationToken ct)
    {
        if (this.Deny(Section, id is > 0 ? Rights.View : Rights.Create) is { } refused) return refused;
        InvInvoice? invoice = id is int iid
            ? await db.Set<InvInvoice>().AsNoTracking()
                .Include(i => i.Customer).Include(i => i.SalesOrder).Include(i => i.ApprovedByNavigation)
                .FirstOrDefaultAsync(i => i.Id == iid, ct)
            : null;

        var items = new List<Dictionary<string, object?>>();
        var charges = new List<Dictionary<string, object?>>();
        string? rendered = null;
        if (invoice is not null)
        {
            var rows = await db.Set<InvInvoiceItem>().AsNoTracking().Where(i => i.InvoiceId == invoice.Id).OrderBy(i => i.Id).ToListAsync(ct);
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
                    ["delivery"] = Py.FN(it.Delivery),
                    ["installation"] = Py.FN(it.Installation),
                    ["sales_tax_pct"] = Py.FN(it.SalesTaxPct),
                    ["total_before_discount"] = Py.FN(it.TotalBeforeDiscount),
                    ["total_after_discount"] = Py.FN(it.TotalAfterDiscount),
                    ["source_order_id"] = it.SourceOrderId,
                    ["source_order_item_id"] = it.SourceOrderItemId,
                    ["order_ref"] = it.SourceOrderNumber ?? "",
                });
            }
            var chargeRows = await db.Set<AdditionalCharge>().AsNoTracking().Include(c => c.ChargeAccount)
                .Where(c => c.DocType == "SI" && c.DocId == invoice.Id).OrderBy(c => c.Id).ToListAsync(ct);
            foreach (var chg in chargeRows)
            {
                string code = chg.ChargeAccount?.Code ?? "", name = chg.ChargeAccount?.Name ?? "";
                charges.Add(new Dictionary<string, object?>
                {
                    ["id"] = chg.Id,
                    ["charge_account_id"] = chg.ChargeAccountId,
                    ["account_code"] = code,
                    ["account_name"] = name,
                    ["_display"] = code.Length > 0 ? $"{code} — {name}" : name,
                    ["description"] = chg.Description,
                    ["amount"] = Py.FN(chg.Amount),
                    ["scope"] = chg.Scope,
                    ["distribution"] = string.IsNullOrEmpty(chg.Distribution) ? "pro_rata_value" : chg.Distribution,
                    ["manual"] = ReadManual(chg.ManualAllocations),
                    ["treatment"] = string.IsNullOrEmpty(chg.Treatment) ? "bill" : chg.Treatment,
                    ["st_taxable"] = chg.StTaxable == true,
                    ["wht_taxable"] = chg.WhtTaxable == true,
                    ["extra_taxable"] = chg.ExtraTaxable == true,
                    ["taxable"] = chg.Taxable,
                    ["tax_base"] = chg.TaxBase,
                });
            }
            rendered = await print.SalesAsync(invoice, ct, rows, products, chargeRows,
                numberFormat: Access.Profile?.NumberFormat);
        }
        var inventory = await settings.InventoryAsync(ct);
        var rs = await settings.ReportAsync(ct);
        return View("Form", new SalesInvoiceFormModel
        {
            Invoice = invoice,
            InvoiceItemsJson = items.Count > 0 ? PyJson.Dumps(items, htmlSafe: true) : null,
            InvoiceChargesJson = charges.Count > 0 ? PyJson.Dumps(charges, htmlSafe: true) : null,
            PerLineLabeling = inventory.PerLineLabelingInvoice == true,
            OrderSourced = items.Any(i => i["source_order_id"] is int x && x != 0),
            PartyMode = DocSettings.PartyMode(rs, "sales"),
            RenderedTemplate = rendered,
            ProjectLabels = await settings.ActiveLabelsAsync(ct),
            DefaultLabel = await settings.DefaultInvoiceLabelAsync(ct),
            ShowSourceGate = inventory.SalesFlow == "with_so",
            CanApprove = Access.IsAdmin || Access.IsManager,
        });
    }

    /// <summary>_read_manual: the stored manual split, or [] when absent or unreadable.</summary>
    public static List<double> ReadManual(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return [];
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.EnumerateArray().Select(v => PyData.Truthy(v) ? PyData.ToFloat(v, "manual") : 0.0).ToList();
        }
        catch (Exception e) when (e is JsonException or PyValueException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>_manual_allocations: per-line amounts of a "Manual per line" charge, as json.dumps writes them.</summary>
    public static string ManualAllocations(PyData chg)
    {
        if (chg.Str("distribution", null) != "manual") return "";
        try
        {
            var values = new List<string>();
            if (chg.Get("manual") is { } m && PyData.Truthy(m))
            {
                if (m.ValueKind != JsonValueKind.Array) return "";
                foreach (var v in m.EnumerateArray())
                    values.Add(Py.Repr(Py.Round(PyData.Truthy(v) ? PyData.ToFloat(v, "manual") : 0.0, 2)));
            }
            return "[" + string.Join(", ", values) + "]";
        }
        catch (PyValueException)
        {
            return "";
        }
    }

    // ── The list ────────────────────────────────────────────────────────

    [HttpGet("list")]
    [Right(Section, Rights.View)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] string? from, [FromQuery] string? to,
                                          [FromQuery] string? month, [FromQuery] int page = 1, CancellationToken ct = default)
    {
        var q = db.Set<InvInvoice>().AsNoTracking();
        if (!string.IsNullOrEmpty(status)) q = q.Where(i => i.VoucherStatus == status);
        var (fromD, toD) = InvoiceLookups.Range(from, to, month);
        if (fromD is DateTime f) q = q.Where(i => i.InvoiceDate >= f);
        if (toD is DateTime t) q = q.Where(i => i.InvoiceDate < t);
        int total = await q.CountAsync(ct);
        var pager = Pager.For(total, page);
        var rows = await q.OrderByDescending(i => i.Id).Skip(pager.Skip).Take(Pager.Size)
            .Select(i => new SalesInvoiceRow(i.Id, i.InvoiceNumber, i.Customer.Name, i.CreatedAt, i.DueDate, i.Subtotal, i.TotalTax,
                i.TotalFurtherTax, i.TotalWithholdingTax, i.TotalAmount, i.PaidAmount, i.VoucherStatus, i.PaymentStatus, i.ApprovedAt))
            .ToListAsync(ct);
        return View("List", new InvoiceListModel<SalesInvoiceRow>
        {
            Rows = rows, Pager = pager, Status = status ?? "", From = from ?? "", To = to ?? "",
        });
    }

    // ── Save / approve ──────────────────────────────────────────────────

    /// <summary>validate_invoice.</summary>
    public static List<string> Validate(PyData data)
    {
        var errors = new List<string>();
        if (!data.Truthy("customer_id")) errors.Add("Customer is required");
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

    [HttpPost("save")]
    [AnyMember]   // rights_guard _json_save: they follow the payload's id and action
    public async Task<IActionResult> Save(CancellationToken ct)
    {
        PyData data;
        try { data = await PyData.ReadAsync(Request, ct); }
        catch (PyBadRequestException) { return BadRequest(); }
        int? invId;
        try { invId = data.Id("id"); }
        catch (PyValueException e) { return Fail($"Server error: {e.Message}", 500); }
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

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var result = await SaveCoreAsync(data, invId, action, approve, ct);
            if (result.Response is not null)
                return result.Response;
            await tx.CommitAsync(ct);
            var inv = result.Invoice!;
            await tracking.SyncInvoiceAsync(TrackingSync.SalesDoc, inv.Id, ct);
            string? paymentStatus = await db.Set<InvInvoice>().AsNoTracking().Where(i => i.Id == inv.Id)
                .Select(i => i.PaymentStatus).FirstOrDefaultAsync(ct);
            string msg = approve ? "approved and locked" : invId is not null ? "changes saved" : "saved";
            return PyJson.Result(new Dictionary<string, object?>
            {
                ["ok"] = true, ["id"] = inv.Id, ["voucher_status"] = inv.VoucherStatus, ["edit_version"] = inv.EditVersion,
                ["payment_status"] = paymentStatus, ["number"] = inv.InvoiceNumber, ["voucher"] = inv.VoucherNumber,
                ["message"] = $"Invoice {msg}",
            });
        }
        catch (Exception e) when (e is NegativeStockException or ClosedPeriodException or DocumentDateException)
        {
            // A business refusal, not a crash: nothing was committed.
            return Fail(e.Message, 400);
        }
        catch (Exception e) when (ConflictMessages.For(e) is string conflict)
        {
            return Fail(conflict, 409);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "Sales invoice save failed");
            return Fail($"Server error: {e.Message}", 500);
        }
    }

    private sealed record SaveResult(InvInvoice? Invoice, IActionResult? Response);

    private async Task<SaveResult> SaveCoreAsync(PyData data, int? invId, string? action, bool approve, CancellationToken ct)
    {
        int userId = Access.User.Id;
        InvInvoice inv;
        if (invId is int id)
        {
            // Locked until commit: a second user saving or approving the same invoice waits,
            // then finds it approved instead of posting it again.
            var found = await db.LockAsync<InvInvoice>(id, ct);
            if (found is null)
                return new(null, Fail("Server error: 404 Not Found: The requested URL was not found on the server. If you entered " +
                                      "the URL manually please check your spelling and try again.", 500));
            inv = found;
            if (inv.VoucherStatus == "approved")
                return new(null, Fail("Cannot modify approved invoice", 400));
            string? stale = await versions.StaleAsync("inv_invoices", inv.Id, inv.CompanyId, inv.EditVersion, data.Get("edit_version"),
                $"Invoice {inv.InvoiceNumber}", v => inv.EditVersion = v, ct);
            if (stale is not null)
                return new(null, Fail(stale, 409, stale: true));
        }
        else
        {
            string? number = data.Get("invoice_number") is { ValueKind: JsonValueKind.String } n ? n.GetString() : data.Str("invoice_number", null);
            if (string.IsNullOrEmpty(number) || number == "(auto)") number = DocNumbers.Placeholder();
            inv = new InvInvoice
            {
                VoucherNumber = number, InvoiceNumber = number, CreatedBy = userId, CreatedAt = clock.UtcNow,
                InvoiceDate = null, VoucherStatus = "unapproved", PaymentStatus = "unpaid",
                DiscountMode = "general", ChargesMode = "general", TaxMode = "general",
                ApplyFurtherTax = false, ApplyWithholdingTax = false, PaidAmount = 0, EditVersion = 0,
            };
            db.Add(inv);
        }

        var items = data.List("items").ToList();
        if (approve)
        {
            var errors = Validate(data);
            if (errors.Count > 0)
                return new(null, Fail(string.Join("; ", errors), 400));
            var over = await orders.CheckOverInvoicingAsync(OrderLinkage.Sales,
                items.Select(r => new Billing(r.Id("source_order_item_id"), r.FloatOr0("quantity"))), ct);
            if (over.Count > 0)
                return new(null, Fail("Over-invoicing blocked — " + string.Join("; ", over), 400));
        }

        var defaultLabel = await settings.DefaultInvoiceLabelAsync(ct);
        int? defaultLid = defaultLabel?.Id;
        int? customerId = data.Id("customer_id");
        if (customerId is null)
            return new(null, Fail("Server error: (psycopg2.errors.NotNullViolation) null value in column \"customer_id\" of relation " +
                                  "\"inv_invoices\" violates not-null constraint", 500));
        inv.CustomerId = customerId.Value;
        inv.PartyAccountId = data.Id("party_account_id");
        inv.LabelId = data.Id("label_id") ?? defaultLid;
        inv.SalesOrderId = data.Id("sales_order_id");
        string? due = data.Truthy("due_date") ? data.Str("due_date") : null;
        if (due is not null)
        {
            if (!DateTime.TryParseExact(due, "yyyy-M-d", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dd))
                throw new PyValueException($"time data '{due}' does not match format '%Y-%m-%d'");
            inv.DueDate = dd;
        }
        else
            inv.DueDate = null;
        // The invoice posts — journal and stock — on ITS date.
        inv.InvoiceDate = ParseDocDate(data.Get("invoice_date"), inv.InvoiceDate);
        if (inv.DueDate is DateTime d1 && d1 < inv.InvoiceDate)
            return new(null, Fail("Due date cannot be before the invoice date", 400));
        inv.DiscountMode = data.Str("discount_mode", "general");
        inv.ChargesMode = data.Str("charges_mode", "general");
        inv.TaxMode = data.Str("tax_mode", "general");

        double gdp = data.Float("global_discount_pct"), gdv = data.Float("global_discount_value"), gdel = data.Float("global_delivery"),
               ginst = data.Float("global_installation"), gst = data.Float("global_sales_tax_pct"), ftp = data.Float("further_tax_pct");
        bool applyFt = data.Bool("apply_further_tax");
        double whtp = data.Float("withholding_tax_pct");
        bool applyWht = data.Bool("apply_withholding_tax");
        inv.GlobalDiscountPct = Py.Dec(gdp);
        inv.GlobalDiscountValue = Py.Dec(gdv);
        inv.GlobalDelivery = Py.Dec(gdel);
        inv.GlobalInstallation = Py.Dec(ginst);
        inv.GlobalSalesTaxPct = Py.Dec(gst);
        inv.FurtherTaxPct = Py.Dec(ftp);
        inv.ApplyFurtherTax = applyFt;
        inv.WithholdingTaxPct = Py.Dec(whtp);
        inv.ApplyWithholdingTax = applyWht;
        inv.Notes = data.Str("notes", "");
        double subtotal = data.Float("subtotal"), totalDiscount = data.Float("total_discount");
        inv.Subtotal = Py.Dec(subtotal);
        inv.TotalDiscount = Py.Dec(totalDiscount);
        inv.TotalCharges = Py.Dec(data.Float("total_charges"));
        double totalTax = data.Float("total_tax");
        inv.TotalTax = Py.Dec(totalTax);
        inv.TotalFurtherTax = Py.Dec(data.Float("total_further_tax"));
        inv.TotalWithholdingTax = Py.Dec(data.Float("total_withholding_tax"));
        inv.TotalAmount = Py.Dec(data.Float("total_amount"));

        if (approve)
        {
            inv.VoucherStatus = "approved";
            inv.ApprovedBy = userId;
            inv.ApprovedAt = clock.UtcNow;
        }
        else if (inv.VoucherStatus != "approved")
            inv.VoucherStatus = "unapproved";

        await db.SaveChangesAsync(ct);
        if (DocNumbers.IsPlaceholder(inv.InvoiceNumber))
        {
            string number = DocNumbers.FromId("SI", clock.Now, inv.Id);
            inv.InvoiceNumber = number;
            inv.VoucherNumber = number;
            await db.SaveChangesAsync(ct);
        }
        if (approve)
        {
            // Every product this invoice moves, locked together and in one order before any stock moves.
            var pids = items.Select(r => SafeId(r.Get("product_id"))).Where(p => p is not null).Select(p => p!.Value);
            await stock.LockProductsAsync(pids, ct);
        }

        // Lines: replaced whole.
        await db.Set<InvInvoiceItem>().Where(i => i.InvoiceId == inv.Id).ExecuteDeleteAsync(ct);
        foreach (var e in db.ChangeTracker.Entries<InvInvoiceItem>().ToList()) e.State = EntityState.Detached;

        decimal totalCogs = 0m;
        var cogsByLabel = new List<(int? Label, decimal Cogs)>();
        var saved = new List<(InvInvoiceItem Item, double Qty, double Price, double After, double Before)>();
        var productCache = new Dictionary<int, InvProduct?>();
        foreach (var row in items)
        {
            double qty = row.Float("quantity", 1), price = row.Float("unit_price");
            double before = row.Float("total_before_discount"), after = row.Float("total_after_discount");
            var item = new InvInvoiceItem
            {
                InvoiceId = inv.Id,
                ProductId = row.Id("product_id"),
                Description = row.Str("description", ""),
                Quantity = Py.Dec(qty),
                Unit = row.Str("unit", "pcs"),
                UnitPrice = Py.Dec(price),
                LabelId = row.Id("label_id") ?? inv.LabelId ?? defaultLid,
                SourceOrderId = row.Id("source_order_id"),
                SourceOrderItemId = row.Id("source_order_item_id"),
                SourceOrderNumber = row.Truthy("source_order_number") ? row.Str("source_order_number", "") : "",
                DiscountPct = Py.Dec(row.Float("discount_pct")),
                DiscountAmount = Py.Dec(row.Float("discount_amount")),
                Delivery = Py.Dec(row.Float("delivery")),
                Installation = Py.Dec(row.Float("installation")),
                SalesTaxPct = Py.Dec(row.Float("sales_tax_pct")),
                TotalBeforeDiscount = Py.Dec(before),
                TotalAfterDiscount = Py.Dec(after),
                Comments = row.Str("comments", ""),
            };
            db.Add(item);
            saved.Add((item, qty, price, after, before));

            if (approve && item.ProductId is int pid)
            {
                if (!productCache.TryGetValue(pid, out var prod))
                    productCache[pid] = prod = await db.Set<InvProduct>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid, ct);
                if (prod is not null)
                {
                    db.Add(new InvStockMovement
                    {
                        ProductId = pid, Type = "sale_out", Quantity = Py.Dec(qty), ReferenceType = "sales_invoice",
                        ReferenceId = inv.Id, Notes = $"Approved invoice {inv.InvoiceNumber}", CreatedBy = userId, CreatedAt = clock.UtcNow,
                    });
                    await db.SaveChangesAsync(ct);
                    var (_, lineCogs) = await stock.IssueAsync(pid, "SI", inv.Id, inv.VoucherNumber, Py.Dec(qty),
                        notes: $"Sale {inv.InvoiceNumber}", createdBy: userId, txnDate: inv.InvoiceDate, ct: ct);
                    totalCogs += lineCogs;
                    int idx = cogsByLabel.FindIndex(x => x.Label == item.LabelId);
                    if (idx < 0) cogsByLabel.Add((item.LabelId, lineCogs));
                    else cogsByLabel[idx] = (item.LabelId, cogsByLabel[idx].Cogs + lineCogs);
                }
            }
        }
        await db.SaveChangesAsync(ct);

        if (approve)
            await orders.ApplyWritebackAsync(OrderLinkage.Sales,
                saved.Select(s => new Billing(s.Item.SourceOrderItemId, s.Qty)).ToList(), 1, ct);

        // Charges: replaced whole.
        await db.Set<AdditionalCharge>().Where(c => c.DocType == "SI" && c.DocId == inv.Id).ExecuteDeleteAsync(ct);
        foreach (var e in db.ChangeTracker.Entries<AdditionalCharge>().ToList()) e.State = EntityState.Detached;
        var chargeRows = new List<TCharge>();
        foreach (var chg in data.List("charges"))
        {
            double amount = chg.Float("amount");
            if (!(amount > 0) || !chg.Truthy("charge_account_id")) continue;
            string treatment = chg.Str("treatment", "bill") ?? "None";
            if (treatment is not ("bill" or "absorb" or "expense")) treatment = "bill";
            bool billed = treatment == "bill";
            bool st = billed && (chg.Has("st_taxable") ? chg.Bool("st_taxable") : chg.Bool("taxable", true));
            var row = new AdditionalCharge
            {
                DocType = "SI", DocId = inv.Id,
                ChargeAccountId = chg.Id("charge_account_id")!.Value,
                Description = chg.Str("description", ""),
                Amount = Py.Dec(amount),
                Scope = chg.Str("scope", "general"),
                Distribution = chg.Str("distribution", "pro_rata_value"),
                ManualAllocations = ManualAllocations(chg),
                Treatment = treatment,
                StTaxable = st,
                WhtTaxable = billed && chg.Bool("wht_taxable"),
                ExtraTaxable = chg.Bool("extra_taxable"),
                Taxable = st,
                TaxBase = chg.Str("tax_base", "after_discount"),
                CreatedAt = clock.UtcNow,
            };
            db.Add(row);
            chargeRows.Add(new TCharge
            {
                ChargeAccountId = row.ChargeAccountId, Description = row.Description, Amount = amount, Scope = row.Scope,
                Distribution = row.Distribution, ManualAllocations = row.ManualAllocations, Treatment = treatment,
                StTaxable = st, WhtTaxable = row.WhtTaxable == true,
            });
        }
        await db.SaveChangesAsync(ct);

        var doc = new TDoc
        {
            Subtotal = subtotal, TotalDiscount = totalDiscount, TotalTax = totalTax, GlobalSalesTaxPct = gst,
            FurtherTaxPct = ftp, ApplyFurtherTax = applyFt, WithholdingTaxPct = whtp, ApplyWithholdingTax = applyWht, TaxMode = inv.TaxMode,
        };
        var t = InvoiceTotals.Sales(doc, chargeRows);
        inv.TotalCharges = Py.Dec(t.Billed);
        inv.TotalTax = Py.Dec(t.SalesTax);
        inv.TotalFurtherTax = Py.Dec(t.FurtherTax);
        inv.TotalWithholdingTax = Py.Dec(t.Wht);
        inv.TotalAmount = Py.Dec(t.NetReceivable);

        if (approve)
            await PostAsync(inv, saved, chargeRows, doc, t, totalCogs, cogsByLabel, userId, ct);

        await db.SaveChangesAsync(ct);
        return new(inv, null);
    }

    private static int? SafeId(JsonElement? e)
    {
        try { return PyData.ToId(e); }
        catch (PyValueException) { return null; }
    }

    /// <summary>parse_doc_date with a JSON value (str(value).strip()[:10]).</summary>
    private DateTime ParseDocDate(JsonElement? value, DateTime? fallback)
    {
        if (value is null || value.Value.ValueKind == JsonValueKind.Null
            || value.Value.ValueKind == JsonValueKind.String && value.Value.GetString() == "")
            return DocumentDates.Parse(null, fallback, clock);
        string s = value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString()! : value.Value.GetRawText();
        return DocumentDates.Parse(s, fallback, clock);
    }

    /// <summary>The §12 sales journal, line for line as invoices.save_invoice builds it.</summary>
    private async Task PostAsync(InvInvoice inv, List<(InvInvoiceItem Item, double Qty, double Price, double After, double Before)> saved,
                                 List<TCharge> chargeRows, TDoc doc, SalesTotals t, decimal totalCogs,
                                 List<(int? Label, decimal Cogs)> cogsByLabel, int userId, CancellationToken ct)
    {
        var customer = await db.Set<InvCustomer>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == inv.CustomerId, ct);
        var arAcc = await ledger.PartyAccountAsync("customer", inv.CustomerId, customer?.Name ?? "", inv.PartyAccountId, ct);
        var revAcc = await ledger.PostingAccountAsync("revenue", ct);
        var cogsAcc = await ledger.PostingAccountAsync("cogs", ct);
        var invAcc = await ledger.PostingAccountAsync("inventory", ct);
        var outTaxAcc = await ledger.PostingAccountAsync("sales_tax_payable", ct);
        int? plid = inv.LabelId;

        var weights = LabelSplit.Weights(saved.Select(s =>
        {
            double w = s.After != 0 ? s.After : s.Qty * s.Price;
            return (s.Item.LabelId ?? plid, w);
        }));
        if (weights.Count == 0) weights = [(plid, 1m)];

        var lines = new List<JournalLineInput>
        {
            new(arAcc.Id, Py.Dec(t.NetReceivable), 0m, $"AR - {inv.InvoiceNumber}", plid),
        };
        void Split(int accountId, double debit, double credit, string text)
        {
            double amt = debit != 0 ? debit : credit;
            foreach (var (lid, share) in LabelSplit.Split(Py.Dec(amt), weights))
                lines.Add(new JournalLineInput(accountId, debit != 0 ? share : 0m, debit != 0 ? 0m : (credit != 0 ? share : 0m), text, lid));
        }

        // Lines and products as the revenue / tax splits read them (inv.items.all()).
        var pids = saved.Where(s => s.Item.ProductId != null).Select(s => s.Item.ProductId!.Value).Distinct().ToList();
        var products = await db.Set<InvProduct>().AsNoTracking().Where(p => pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var tlines = saved.Select(s => new TLine
        {
            ProductId = s.Item.ProductId, Quantity = s.Qty, UnitPrice = s.Price, DiscountPct = Py.F(s.Item.DiscountPct),
            DiscountAmount = Py.F(s.Item.DiscountAmount), SalesTaxPct = Py.F(s.Item.SalesTaxPct),
            TotalBeforeDiscount = s.Before, TotalAfterDiscount = s.After,
            ProductWeight = s.Item.ProductId is int p && products.TryGetValue(p, out var prod) ? Py.F(prod.Weight) : 0,
            CategoryId = s.Item.ProductId is int p2 && products.TryGetValue(p2, out var prod2) ? prod2.CategoryId : null,
        }).ToList();
        var categoryAccounts = await lookups.CategoryRevenueAccountsAsync(ct);
        foreach (var (accountId, amount) in InvoiceTotals.RevenueSplits(tlines, categoryAccounts, t.EffectiveSubtotal))
            Split(accountId ?? revAcc.Id, 0, amount, $"Revenue - {inv.InvoiceNumber}");
        if (t.Discount > 0)
        {
            var discAcc = await db.Set<ChartOfAccount>().AsNoTracking().FirstOrDefaultAsync(a => a.Code == "4-02-02-01-0001", ct)
                          ?? await ledger.PostingAccountAsync("sales_returns", ct);
            Split(discAcc.Id, t.Discount, 0, $"Discount allowed - {inv.InvoiceNumber}");
        }
        foreach (var row in t.Pools.BilledRows)
            Split(row.ChargeAccountId, 0, Py.Round(row.Amount, 2),
                $"{(string.IsNullOrEmpty(row.Description) ? "Charge" : row.Description)} - {inv.InvoiceNumber}");
        if (t.SalesTax > 0 && outTaxAcc is not null)
        {
            var rateAccounts = await lookups.TaxRateAccountsAsync(ct);
            foreach (var (accountId, amount) in InvoiceTotals.OutputTaxSplits(doc, tlines, chargeRows, rateAccounts, t.SalesTax))
                lines.Add(new JournalLineInput(accountId ?? outTaxAcc.Id, 0m, Py.Dec(amount), $"Output Tax - {inv.InvoiceNumber}"));
        }
        if (t.FurtherTax > 0)
        {
            var ftAcc = await ledger.PostingAccountAsync("further_tax_payable", ct);
            lines.Add(new JournalLineInput(ftAcc.Id, 0m, Py.Dec(t.FurtherTax), $"Further Tax - {inv.InvoiceNumber}"));
        }
        if (t.Wht > 0)
        {
            var whtAcc = await db.Set<ChartOfAccount>().AsNoTracking().FirstOrDefaultAsync(a => a.Code == "1-01-05-02-0001", ct)
                         ?? await ledger.GetOrCreateAccountAsync("1-01-05-02-0001", "WHT Receivable", "asset", ct: ct);
            lines.Add(new JournalLineInput(whtAcc.Id, Py.Dec(t.Wht), 0m, $"WHT Receivable - {inv.InvoiceNumber}"));
        }
        foreach (var row in t.Pools.ExpenseRows)
        {
            double amt = Py.Round(row.Amount, 2);
            var accrued = await ledger.PostingAccountAsync("accrued", ct);
            string d = string.IsNullOrEmpty(row.Description) ? "Charge" : row.Description;
            Split(row.ChargeAccountId, amt, 0, $"{d} (absorbed cost) - {inv.InvoiceNumber}");
            lines.Add(new JournalLineInput(accrued.Id, 0m, Py.Dec(amt), $"{d} accrued - {inv.InvoiceNumber}"));
        }
        if (totalCogs > 0 && cogsAcc is not null && invAcc is not null)
        {
            foreach (var (lab, cogs) in cogsByLabel)
            {
                if (cogs == 0) continue;
                int? lid = lab ?? plid;
                lines.Add(new JournalLineInput(cogsAcc.Id, cogs, 0m, $"COGS - {inv.InvoiceNumber}", lid));
                lines.Add(new JournalLineInput(invAcc.Id, 0m, cogs, $"Inventory - {inv.InvoiceNumber}", lid));
            }
        }
        await ledger.PostAsync("SI", inv.Id, inv.VoucherNumber,
            $"Sales Invoice {inv.InvoiceNumber} - {customer?.Name ?? ""}", lines, inv.InvoiceDate, userId, ct);
    }

    // ── Unapprove / delete / pay ────────────────────────────────────────

    [HttpPost("unapprove/{id:int}")]
    [AnyMember]   // sales_invoices approve, answered in JSON
    public async Task<IActionResult> Unapprove(int id, CancellationToken ct)
    {
        if (this.Deny(Section, Rights.Approve, json: true) is { } refused) return refused;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var inv = await db.LockAsync<InvInvoice>(id, ct);
        if (inv is null) return NotFound();
        if (inv.VoucherStatus != "approved")
            return Fail("Only approved invoices can be unapproved", 400);
        // A posted return credits this invoice.
        var returns = await db.Set<InvSalesReturn>().AsNoTracking()
            .Where(r => r.OriginalInvoiceId == inv.Id && r.Status == "approved").OrderBy(r => r.Id).Select(r => r.ReturnNumber).ToListAsync(ct);
        if (returns.Count > 0)
            return Fail("Cannot unapprove: approved sales return(s) " + string.Join(", ", returns) +
                        " credit this invoice. Unapprove them first.", 400);
        // Reported to FBR: the correction is a sales return (credit note).
        var reported = await db.Set<FbrInvoiceRecord>().AsNoTracking()
            .Where(r => r.InvInvoiceId == inv.Id && r.Status == "success").OrderBy(r => r.Id).FirstOrDefaultAsync(ct);
        if (reported is not null)
            return Fail("Cannot unapprove: this invoice was reported to FBR" +
                        (!string.IsNullOrEmpty(reported.FbrInvoiceNumber) ? " as " + reported.FbrInvoiceNumber : "") +
                        ". Correct it with a sales return (credit note).", 400);
        try
        {
            await ledger.ReverseAsync("SI", inv.Id, Access.User.Id, ct);
        }
        catch (ClosedPeriodException e)
        {
            await tx.RollbackAsync(ct);
            return Fail(e.Message, 400);
        }
        var itemRows = await db.Set<InvInvoiceItem>().AsNoTracking().Where(i => i.InvoiceId == inv.Id).OrderBy(i => i.Id).ToListAsync(ct);
        await orders.ApplyWritebackAsync(OrderLinkage.Sales,
            itemRows.Select(i => new Billing(i.SourceOrderItemId, Py.F(i.Quantity))).ToList(), -1, ct);
        inv.VoucherStatus = "unapproved";
        inv.PaymentStatus = "unpaid";
        inv.ApprovedBy = null;
        inv.ApprovedAt = null;
        await db.Set<InvStockMovement>().Where(m => m.ReferenceType == "sales_invoice" && m.ReferenceId == inv.Id).ExecuteDeleteAsync(ct);
        await stock.ReverseVoucherStockAsync("SI", inv.Id, Access.User.Id, ct);
        inv.EditVersion = EditVersions.Bump(inv.EditVersion);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await tracking.SyncInvoiceAsync(TrackingSync.SalesDoc, inv.Id, ct);
        return PyJson.Result(new Dictionary<string, object?>
        {
            ["ok"] = true, ["voucher_status"] = "unapproved", ["edit_version"] = inv.EditVersion,
            ["message"] = "Invoice unapproved and unlocked",
        });
    }

    [HttpPost("delete/{id:int}")]
    [AnyMember]   // sales_invoices delete, answered in JSON
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        if (this.Deny(Section, Rights.Delete, json: true) is { } refused) return refused;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var inv = await db.LockAsync<InvInvoice>(id, ct);
        if (inv is null) return NotFound();
        if (inv.VoucherStatus == "approved")
            return Fail("Cannot delete an approved invoice. Unapprove it first.", 400);
        try
        {
            await db.Set<InvInvoiceItem>().Where(i => i.InvoiceId == inv.Id).ExecuteDeleteAsync(ct);
            await tracking.DropAllocationsAsync(TrackingSync.SalesDoc, inv.Id, ct);
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
        // Retired: receipts are vouchers, assigned in Invoicing > Tracking. Posts nothing.
        var inv = await db.Set<InvInvoice>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
        if (inv is null) return NotFound();
        this.Flash("Record receipts as a Cash/Bank Receipt Voucher first.", "info");
        return Redirect($"/invoicing/tracking/invoice/SI/{inv.Id}");
    }

    // ── Lookups the editor makes ────────────────────────────────────────

    [HttpGet("api/products")]
    [AnyRight("sales_invoices:view", "sales_invoices:create", "sales_invoices:edit")]
    public async Task<IActionResult> ApiProducts([FromQuery] string? q, CancellationToken ct) =>
        PyJson.Result(await lookups.ProductsAsync(q, withWeight: true, ct));

    [HttpGet("api/customers")]
    [AnyRight("sales_invoices:view", "sales_invoices:create", "sales_invoices:edit")]
    public async Task<IActionResult> ApiCustomers([FromQuery] string? q, CancellationToken ct) =>
        PyJson.Result(await lookups.CustomersAsync(q, withTax: true, ct));

    [HttpGet("api/orders/{customerId:int}")]
    [AnyRight("sales_invoices:view", "sales_invoices:create", "sales_invoices:edit")]
    public async Task<IActionResult> ApiOrders(int customerId, CancellationToken ct) =>
        PyJson.Result(await lookups.SalesOrdersForPickerAsync(customerId, ct));

    [HttpGet("api/accounts")]
    [AnyRight("sales_invoices:view", "sales_invoices:create", "sales_invoices:edit")]
    public async Task<IActionResult> ApiAccounts([FromQuery] string? q, CancellationToken ct) =>
        PyJson.Result(await lookups.ChargeAccountsAsync(q, withDefaults: "sales", ct));
}
