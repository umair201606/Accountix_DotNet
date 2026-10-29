using Accountix.Invoicing.Services;

namespace Accountix.Parties.Services;

/// <summary>
/// The register's bulk print and its workbook (registers.py _full_invoices, _bulk_invoice_workbook;
/// shared/invoice_print.py render_invoice_print_many / prefetch_invoice_print_data). A chunk of invoices is
/// rendered through Invoicing's own print code with everything preloaded: the invoices with their party,
/// then the lines, products and charges of the whole chunk in one query each. Nothing is read per invoice.
/// </summary>
public sealed class BulkInvoices(AccountixDbContext db, InvoiceRegister register, InvoicePrint print, PaymentTracking tracking)
{
    /// <summary>_full_invoices: (total, the invoices of the chunk) in date then id order, party loaded.</summary>
    public async Task<(int Total, List<object> Invoices)> InvoicesAsync(string doc, string status, DateOnly? from, DateOnly? to,
        int? limit = null, int offset = 0, int? knownTotal = null, CancellationToken ct = default)
    {
        if (doc == "purchase")
        {
            var q = register.PurchaseQuery(status, from, to);
            int total = knownTotal ?? await q.CountAsync(ct);
            var ordered = q.Include(i => i.Supplier).OrderBy(i => i.InvoiceDate).ThenBy(i => i.Id).AsQueryable();
            if (limit is int l) ordered = ordered.Skip(offset).Take(l);
            return (total, (await ordered.ToListAsync(ct)).Cast<object>().ToList());
        }
        else
        {
            var q = register.SalesQuery(status, from, to);
            int total = knownTotal ?? await q.CountAsync(ct);
            var ordered = q.Include(i => i.Customer).OrderBy(i => i.InvoiceDate).ThenBy(i => i.Id).AsQueryable();
            if (limit is int l) ordered = ordered.Skip(offset).Take(l);
            return (total, (await ordered.ToListAsync(ct)).Cast<object>().ToList());
        }
    }

    /// <summary>prefetch_invoice_print_data: lines (by invoice, in id order), products, and charges (by invoice).</summary>
    public sealed record Prefetch(
        Dictionary<int, List<InvInvoiceItem>> SalesItems, Dictionary<int, List<InvPurchaseInvoiceItem>> PurchaseItems,
        Dictionary<int, InvProduct> Products, Dictionary<int, List<AdditionalCharge>> Charges);

    public async Task<Prefetch> PrefetchAsync(string doc, IReadOnlyList<object> invoices, bool purchaseCharges, CancellationToken ct)
    {
        var ids = invoices.Select(IdOf).ToList();
        var salesItems = new Dictionary<int, List<InvInvoiceItem>>();
        var purchaseItems = new Dictionary<int, List<InvPurchaseInvoiceItem>>();
        var charges = new Dictionary<int, List<AdditionalCharge>>();
        var productIds = new HashSet<int>();
        if (ids.Count == 0)
            return new Prefetch(salesItems, purchaseItems, [], charges);
        if (doc == "purchase")
        {
            foreach (var it in await db.Set<InvPurchaseInvoiceItem>().AsNoTracking().Where(i => ids.Contains(i.InvoiceId))
                         .OrderBy(i => i.InvoiceId).ThenBy(i => i.Id).ToListAsync(ct))
            {
                (purchaseItems.TryGetValue(it.InvoiceId, out var list) ? list : purchaseItems[it.InvoiceId] = []).Add(it);
                if (it.ProductId is int p && p != 0) productIds.Add(p);
            }
        }
        else
        {
            foreach (var it in await db.Set<InvInvoiceItem>().AsNoTracking().Where(i => ids.Contains(i.InvoiceId))
                         .OrderBy(i => i.InvoiceId).ThenBy(i => i.Id).ToListAsync(ct))
            {
                (salesItems.TryGetValue(it.InvoiceId, out var list) ? list : salesItems[it.InvoiceId] = []).Add(it);
                if (it.ProductId is int p && p != 0) productIds.Add(p);
            }
        }
        var products = productIds.Count == 0 ? [] : await db.Set<InvProduct>().AsNoTracking()
            .Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        // The print reads sales charges only; the workbook also needs a purchase invoice's own rows.
        if (doc != "purchase" || purchaseCharges)
        {
            string dt = doc == "purchase" ? "PI" : "SI";
            foreach (var c in await db.Set<AdditionalCharge>().AsNoTracking().Where(c => c.DocType == dt && ids.Contains(c.DocId))
                         .OrderBy(c => c.DocId).ThenBy(c => c.Id).ToListAsync(ct))
                (charges.TryGetValue(c.DocId, out var list) ? list : charges[c.DocId] = []).Add(c);
        }
        return new Prefetch(salesItems, purchaseItems, products, charges);
    }

    private static int IdOf(object inv) => inv is InvInvoice s ? s.Id : ((InvPurchaseInvoice)inv).Id;

    /// <summary>render_invoice_print_many: one print document per invoice; the shared A4 assets only on the first.</summary>
    public async Task<List<string>> RenderAsync(string doc, IReadOnlyList<object> invoices, bool includeSharedAssets, CancellationToken ct)
    {
        var output = new List<string>();
        if (invoices.Count == 0) return output;
        string kind = doc == "purchase" ? "purchase" : "sales";
        var template = await print.TemplateForAsync(kind, ct);
        var company = await db.Set<CompanyInfo>().AsNoTracking().OrderBy(c => c.Id).FirstOrDefaultAsync(ct) ?? new CompanyInfo();
        var pre = await PrefetchAsync(kind, invoices, purchaseCharges: false, ct);
        for (int i = 0; i < invoices.Count; i++)
        {
            string html = invoices[i] switch
            {
                InvInvoice si => await print.SalesAsync(si, ct, pre.SalesItems.GetValueOrDefault(si.Id) ?? [], pre.Products,
                    pre.Charges.GetValueOrDefault(si.Id) ?? [], template, templateGiven: true, company: company),
                InvPurchaseInvoice pi => await print.PurchaseAsync(pi, ct, pre.PurchaseItems.GetValueOrDefault(pi.Id) ?? [], pre.Products,
                    template, templateGiven: true, company: company),
                _ => "",
            };
            if (i > 0 || !includeSharedAssets)
                html = SheetAssets.Strip(html);
            output.Add(html);
        }
        return output;
    }

    private static object? R2(double v) => Py.Round(v, 2);

    private static string TitleCase(string s) => ViewHelpers.Title(s);

    /// <summary>_bulk_invoice_workbook: the invoices themselves (headers, lines, charges), not the register summary.</summary>
    public async Task<ExportFile> WorkbookAsync(string doc, IReadOnlyList<object> invoices, string period, IReportExport export, CancellationToken ct)
    {
        bool sales = doc != "purchase";
        string partyLabel = sales ? "Customer" : "Supplier";
        var ids = invoices.Select(IdOf).ToList();
        var returned = await tracking.CreditedByDocAsync(sales ? TrackingRules.Sales : TrackingRules.Purchase, ids, ct);
        var pre = await PrefetchAsync(sales ? "sales" : "purchase", invoices, purchaseCharges: true, ct);

        string[] invHeaders = sales
            ? ["Date", "Invoice #", "Voucher #", partyLabel, "Status", "Due Date", "Subtotal", "Discount", "Charges", "Sales Tax",
               "Further Tax", "WHT", "Total", "Paid", "Returned", "Balance", "Notes"]
            : ["Date", "Invoice #", "Voucher #", partyLabel, "Status", "Subtotal", "Discount", "Expenses", "Tax", "WHT", "Net Payable",
               "Paid", "Returned", "Balance", "Notes"];
        string[] itemHeaders = sales
            ? ["Invoice #", "Voucher #", "Date", partyLabel, "Line", "SKU", "Description", "Qty", "Unit", "Unit Price", "Discount %",
               "Discount", "Delivery", "Installation", "Tax %", "Excl. Tax", "Sales Tax", "Incl. Tax", "Line Total"]
            : ["Invoice #", "Voucher #", "Date", partyLabel, "Line", "SKU", "Description", "Qty", "Unit", "Unit Price", "Discount %",
               "Discount", "Commission", "Freight", "Loading/Unloading", "Tax %", "Excl. Tax", "Tax", "Incl. Tax", "Line Total"];

        var invRows = new List<List<object?>>();
        var itemRows = new List<IReadOnlyList<object?>>();
        foreach (var o in invoices)
        {
            if (o is InvInvoice inv)
            {
                string party = inv.Customer?.Name ?? "";
                double total = Py.F(inv.TotalAmount), paid = Py.F(inv.PaidAmount), back = Py.F(returned.GetValueOrDefault(inv.Id));
                invRows.Add([inv.InvoiceDate, inv.InvoiceNumber, inv.VoucherNumber, party, (inv.VoucherStatus ?? "") == "approved" ? "Approved" : "Unapproved",
                    inv.DueDate, R2(Py.F(inv.Subtotal)), R2(Py.F(inv.TotalDiscount)), R2(Py.F(inv.TotalCharges)), R2(Py.F(inv.TotalTax)),
                    R2(Py.F(inv.TotalFurtherTax)), R2(Py.F(inv.TotalWithholdingTax)), R2(total), R2(paid), R2(back), R2(total - paid - back), inv.Notes ?? ""]);
                var lines = pre.SalesItems.GetValueOrDefault(inv.Id) ?? [];
                var tl = lines.Select(it => TLine.From(it, pre.Products.GetValueOrDefault(it.ProductId ?? 0))).ToList();
                var lineTax = InvoiceTotals.LineTaxes(TDoc.From(inv), tl, (pre.Charges.GetValueOrDefault(inv.Id) ?? []).Select(TCharge.From));
                bool combined = (string.IsNullOrEmpty(inv.TaxMode) ? "general" : inv.TaxMode) == "general";
                int n = 0;
                foreach (var it in lines)
                {
                    n++;
                    var product = pre.Products.GetValueOrDefault(it.ProductId ?? 0);
                    double before = Py.F(it.TotalBeforeDiscount), discount = Py.F(it.DiscountAmount);
                    double excl = Py.Round(before - discount, 2);
                    double taxPct = combined ? Py.F(inv.GlobalSalesTaxPct) : Py.F(it.SalesTaxPct);
                    double tax = n <= lineTax.Count ? lineTax[n - 1] : 0.0;
                    itemRows.Add([inv.InvoiceNumber, inv.VoucherNumber, inv.InvoiceDate, party, n, product?.Sku ?? "", it.Description ?? "",
                        Py.F(it.Quantity), it.Unit ?? "", R2(Py.F(it.UnitPrice)), R2(Py.F(it.DiscountPct)), R2(discount),
                        R2(Py.F(it.Delivery)), R2(Py.F(it.Installation)), R2(taxPct), excl, tax, Py.Round(excl + tax, 2), R2(before)]);
                }
                if (n == 0)
                    itemRows.Add([inv.InvoiceNumber, inv.VoucherNumber, inv.InvoiceDate, party, 0, "", "", 0, "", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            }
            else if (o is InvPurchaseInvoice pi)
            {
                string party = pi.Supplier?.Name ?? "";
                double net = pi.NetPayable is not null ? Py.F(pi.NetPayable) : Py.F(pi.TotalAmount);
                double paid = Py.F(pi.PaidAmount), back = Py.F(returned.GetValueOrDefault(pi.Id));
                invRows.Add([pi.InvoiceDate, pi.InvoiceNumber, pi.VoucherNumber, party, (pi.Status ?? "") == "approved" ? "Approved" : "Unapproved",
                    R2(Py.F(pi.Subtotal)), R2(Py.F(pi.TotalDiscount)), R2(Py.F(pi.TotalExpenses)), R2(Py.F(pi.TotalTax)),
                    R2(Py.F(pi.TotalWithholdingTax)), R2(net), R2(paid), R2(back), R2(net - paid - back), pi.Notes ?? ""]);
                var lines = pre.PurchaseItems.GetValueOrDefault(pi.Id) ?? [];
                var tl = lines.Select(it => TLine.From(it, pre.Products.GetValueOrDefault(it.ProductId ?? 0))).ToList();
                var lineTax = InvoiceTotals.LineTaxes(new TDoc { TotalTax = Py.F(pi.TotalTax), TaxMode = pi.TaxMode }, tl,
                    (pre.Charges.GetValueOrDefault(pi.Id) ?? []).Select(TCharge.From));
                bool combined = (string.IsNullOrEmpty(pi.TaxMode) ? "general" : pi.TaxMode) == "general";
                int n = 0;
                foreach (var it in lines)
                {
                    n++;
                    var product = pre.Products.GetValueOrDefault(it.ProductId ?? 0);
                    double before = Py.F(it.TotalBeforeDiscount), discount = Py.F(it.DiscountAmount);
                    double excl = Py.Round(before - discount, 2);
                    double taxPct = combined ? Py.F(pi.GlobalSalesTaxPct) : Py.F(it.SalesTaxPct);
                    double tax = n <= lineTax.Count ? lineTax[n - 1] : 0.0;
                    itemRows.Add([pi.InvoiceNumber, pi.VoucherNumber, pi.InvoiceDate, party, n, product?.Sku ?? "", it.Description ?? "",
                        Py.F(it.Quantity), it.Unit ?? "", R2(Py.F(it.UnitPrice)), R2(Py.F(it.DiscountPct)), R2(discount),
                        R2(Py.F(it.Commission)), R2(Py.F(it.Freight)), R2(Py.F(it.LoadingUnloading)), R2(taxPct), excl, tax,
                        Py.Round(excl + tax, 2), R2(before)]);
                }
                if (n == 0)
                    itemRows.Add([pi.InvoiceNumber, pi.VoucherNumber, pi.InvoiceDate, party, 0, "", "", 0, "", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            }
        }

        var chargeRows = new List<IReadOnlyList<object?>>();
        var allCharges = pre.Charges.Values.SelectMany(c => c).OrderBy(c => c.DocId).ThenBy(c => c.Id).ToList();
        if (allCharges.Count > 0)
        {
            var accountIds = allCharges.Select(c => c.ChargeAccountId).Where(a => a != 0).Distinct().ToList();
            var accounts = accountIds.Count == 0 ? [] : await db.Set<ChartOfAccount>().AsNoTracking().Where(a => accountIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, a => a.Name, ct);
            var byId = invoices.ToDictionary(IdOf);
            foreach (var chg in allCharges)
            {
                if (!byId.TryGetValue(chg.DocId, out var o)) continue;
                var (number, voucher, date, party) = o is InvInvoice si
                    ? (si.InvoiceNumber, si.VoucherNumber, si.InvoiceDate, si.Customer?.Name ?? "")
                    : (((InvPurchaseInvoice)o).InvoiceNumber, ((InvPurchaseInvoice)o).VoucherNumber, ((InvPurchaseInvoice)o).InvoiceDate,
                       ((InvPurchaseInvoice)o).Supplier?.Name ?? "");
                chargeRows.Add([number, voucher, date, party, chg.Description ?? "", accounts.GetValueOrDefault(chg.ChargeAccountId, ""),
                    R2(Py.F(chg.Amount)), TitleCase(string.IsNullOrEmpty(chg.Treatment) ? "bill" : chg.Treatment),
                    TitleCase(string.IsNullOrEmpty(chg.Scope) ? "general" : chg.Scope), chg.StTaxable == true ? "Yes" : "No",
                    chg.WhtTaxable == true ? "Yes" : "No"]);
            }
        }

        var (numFrom, numTo) = sales ? (6, 16) : (5, 14);
        var totalVals = Enumerable.Range(numFrom, numTo - numFrom)
            .Select(i => (object?)Py.Round(Py.Sum(invRows.Select(r => r[i] is double d ? d : 0.0)), 2)).ToList();
        var rows = invRows.Select(r => new ReportRow(r, "account")).ToList();
        rows.Add(new ReportRow([.. new object?[] { "", "", "", "TOTAL" }, .. sales ? new object?[] { "", "" } : [""], .. totalVals, ""], "grand"));

        var columns = invHeaders.Select((h, i) => new ReportColumn(h,
            h is "Date" or "Due Date" ? ReportColumnKind.Date
            : i >= numFrom && i < numTo ? ReportColumnKind.Money
            : ReportColumnKind.Text)).ToList();
        return await export.ExportTableAsync("excel", new ReportTable
        {
            Title = "Bulk Invoices",
            SheetTitle = "Invoices",
            Period = period,
            Filters = [("", $"{invoices.Count} invoices")],
            Columns = columns,
            Rows = rows,
            ExtraSheets =
            [
                ("Items", itemHeaders, itemRows),
                ("Charges", ["Invoice #", "Voucher #", "Date", partyLabel, "Description", "Account", "Amount", "Treatment", "Scope",
                             "In sales-tax base", "In WHT base"], chargeRows),
            ],
        }, ct);
    }
}
