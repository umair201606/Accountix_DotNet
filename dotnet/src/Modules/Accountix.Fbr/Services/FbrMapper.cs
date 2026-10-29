namespace Accountix.Fbr.Services;

/// <summary>
/// The FBR payload for a sales invoice (fbr_app/services/fbr_mapper.py
/// build_fbr_invoice), with the money from shared/invoice_totals.sales_totals
/// — the chain the journal was posted from.
///
/// Python does this arithmetic on floats, and json.dumps prints an int and
/// a float differently, so every figure here is a Python value (see
/// <see cref="Py"/>): the same invoice gives the same bytes in both apps.
/// </summary>
public sealed class FbrMapper(AccountixDbContext db, CompanyClock clock)
{
    /// <summary>Thrown where Python's build_fbr_invoice raises (the route answers "Failed to build payload: …").</summary>
    public sealed class BuildException(string message) : Exception(message);

    /// <summary>build_fbr_invoice(invoice_id); null when the invoice is not this company's (Python: 404).</summary>
    public async Task<PyDict?> BuildAsync(int invoiceId, FbrConfig config, CancellationToken ct = default)
    {
        var invoice = await db.Set<InvInvoice>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId, ct);
        if (invoice is null)
            return null;
        // No ORDER BY, as Python's queries: the rows come back in the order Python reads them.
        var items = await db.Set<InvInvoiceItem>().AsNoTracking().Where(i => i.InvoiceId == invoiceId).ToListAsync(ct);
        var charges = await db.Set<AdditionalCharge>().AsNoTracking()
            .Where(c => c.DocType == "SI" && c.DocId == invoiceId).ToListAsync(ct);
        var customer = await db.Set<InvCustomer>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == invoice.CustomerId, ct);
        var productIds = items.Where(i => i.ProductId is not null).Select(i => i.ProductId!.Value).Distinct().ToList();
        var products = productIds.Count == 0 ? new Dictionary<int, InvProduct>()
            : await db.Set<InvProduct>().AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        return Build(invoice, items, charges, customer, products, config, clock.UtcNow);
    }

    /// <summary>The payload from rows already loaded (pure: used by the parity tests on a full year).</summary>
    public static PyDict Build(InvInvoice invoice, IReadOnlyList<InvInvoiceItem> items, IReadOnlyList<AdditionalCharge> charges,
                               InvCustomer? customer, IReadOnlyDictionary<int, InvProduct> products, FbrConfig config, DateTime utcNow)
    {
        string invoiceType = "S";
        if (!string.IsNullOrEmpty(invoice.InvoiceNumber) && PyUpper(invoice.InvoiceNumber).Contains("DN"))
            invoiceType = "DN";
        else if (!string.IsNullOrEmpty(invoice.InvoiceNumber) && PyUpper(invoice.InvoiceNumber).Contains("CN"))
            invoiceType = "CN";

        var totals = SalesTotals(invoice, charges);

        var lineItems = new List<object?>();
        foreach (var item in items)
        {
            InvProduct? product = item.ProductId is int pid && products.TryGetValue(pid, out var p) ? p : null;
            object unitValue = Py.OrInt(item.UnitPrice, 0);
            object qty = Py.OrInt(item.Quantity, 1);
            object lineTotal = Py.Mul(unitValue, qty);
            object discAmount = Py.OrInt(item.DiscountAmount, 0);
            object taxRate = Py.OrInt(item.SalesTaxPct, 0);
            object taxCharged = 0L;
            if (Py.GreaterThanZero(taxRate))
            {
                object baseForTax = Py.Sub(lineTotal, discAmount);
                taxCharged = Py.Round(Py.Div(Py.Mul(baseForTax, taxRate), 100L), 2);
            }
            lineItems.Add(new PyDict
            {
                ["itemCode"] = product is not null ? product.Sku : "",
                ["itemName"] = !string.IsNullOrEmpty(item.Description) ? item.Description : product is not null ? product.Name : "",
                ["quantity"] = qty,
                ["unitOfMeasurement"] = !string.IsNullOrEmpty(item.Unit) ? item.Unit : "pcs",
                ["unitValue"] = unitValue,
                ["totalAmount"] = Py.Round(lineTotal, 2),
                ["discountAmount"] = Py.Round(discAmount, 2),
                ["taxRate"] = taxRate,
                ["taxCharged"] = taxCharged,
                ["hSCode"] = product is not null ? product.HsCode : "",
                ["otherCharges"] = Py.Round(Py.Add(Py.OrInt(item.Delivery, 0), Py.OrInt(item.Installation, 0)), 2),
            });
        }

        DateTime invoiceDate = invoice.InvoiceDate ?? utcNow;
        // getattr(invoice, "payment_status", "unpaid").upper(): a NULL status raises in Python.
        string paymentStatus = invoice.PaymentStatus
            ?? throw new BuildException("'NoneType' object has no attribute 'upper'");

        return new PyDict
        {
            ["fbrInvoice"] = new PyDict
            {
                ["sellerNTN"] = Or(config.SellerNtn),
                ["sellerSTRN"] = Or(config.SellerStrn),
                ["sellerCNIC"] = Or(config.SellerCnic),
                ["sellerName"] = Or(config.SellerName),
                ["sellerBusinessName"] = Or(config.SellerBusinessName),
                ["sellerAddress"] = Or(config.SellerAddress),
                ["sellerPhoneNumber"] = Or(config.SellerPhone),
                ["sellerEmail"] = Or(config.SellerEmail),
                ["buyerNTN"] = Or(customer?.TaxId),
                ["buyerSTRN"] = "",
                ["buyerCNIC"] = "",
                ["buyerName"] = customer is not null ? customer.Name : "",
                ["buyerAddress"] = Or(customer?.Address),
                ["buyerPhoneNumber"] = Or(customer?.Phone),
                ["buyerEmail"] = Or(customer?.Email),
                ["invoiceNumber"] = Or(invoice.InvoiceNumber),
                ["invoiceDate"] = invoiceDate.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                ["invoiceType"] = invoiceType,
                ["totalSaleValue"] = Py.Round(totals.SalesTaxBase, 2),
                ["totalTaxCharged"] = Py.Round(totals.SalesTax, 2),
                ["totalBillAmount"] = Py.Round(totals.NetReceivable, 2),
                ["discount"] = Py.Round(Py.OrInt(invoice.TotalDiscount, 0), 2),
                ["furtherTax"] = totals.FurtherTax,
                ["withholdingTax"] = totals.Wht,
                ["totalOtherCharges"] = totals.Billed,
                ["otherCharges"] = totals.BilledRows.Select(row => (object?)new PyDict
                {
                    ["chargeType"] = !string.IsNullOrEmpty(row.Description) ? row.Description : "Charge",
                    ["amount"] = Py.Round(Py.Float0(row.Amount), 2),
                    ["inSalesTaxBase"] = row.StTaxable == true,
                }).ToList(),
                ["deliveryCharges"] = Py.Round(Py.OrInt(invoice.GlobalDelivery, 0), 2),
                ["installationCharges"] = Py.Round(Py.OrInt(invoice.GlobalInstallation, 0), 2),
                ["paymentInfo"] = new PyDict
                {
                    ["paymentMode"] = paymentStatus == "paid" ? "C" : "O",
                    ["paymentStatus"] = PyUpper(paymentStatus),
                },
                ["items"] = lineItems,
            },
        };
    }

    private static string Or(string? s) => string.IsNullOrEmpty(s) ? "" : s;

    /// <summary>str.upper() (full case mapping: ß → SS).</summary>
    private static string PyUpper(string s) => s.ToUpperInvariant().Replace("ß", "SS");

    // ── shared/invoice_totals.py ──────────────────────────────────────────

    public sealed record Totals(double Subtotal, double Discount, double EffectiveSubtotal, double SalesTaxBase, double SalesTax,
                                double FurtherTax, double WhtBase, double Wht, double Billed, double NetReceivable,
                                IReadOnlyList<AdditionalCharge> BilledRows);

    /// <summary>charge_pools_from_rows: billed / absorbed / expense, and the tax-base switches of billed charges.</summary>
    public static (double Billed, double Absorb, double Expense, double StBase, double WhtBase, List<AdditionalCharge> BilledRows)
        ChargePools(IEnumerable<AdditionalCharge> rows)
    {
        double billed = 0, absorb = 0, expense = 0, stBase = 0, whtBase = 0;
        var billedRows = new List<AdditionalCharge>();
        foreach (var r in rows)
        {
            double amt = Py.Float0(r.Amount);
            if (amt <= 0)
                continue;
            string treatment = r.Treatment is "bill" or "absorb" or "expense" ? r.Treatment : "bill";
            if (treatment == "absorb")
                absorb += amt;
            else if (treatment == "expense")
                expense += amt;
            else
            {
                billed += amt;
                billedRows.Add(r);
                if (r.StTaxable == true) stBase += amt;
                if (r.WhtTaxable == true) whtBase += amt;
            }
        }
        return (Py.Round(billed, 2), Py.Round(absorb, 2), Py.Round(expense, 2), Py.Round(stBase, 2), Py.Round(whtBase, 2), billedRows);
    }

    /// <summary>sales_totals(inv): the §8 chain, on floats as Python computes it.</summary>
    public static Totals SalesTotals(InvInvoice inv, IEnumerable<AdditionalCharge> charges)
    {
        var pools = ChargePools(charges);
        double subtotal = Py.Round(Py.Float0(inv.Subtotal), 2);
        double discount = Py.Round(Py.Float0(inv.TotalDiscount), 2);
        double effective = Py.Round(subtotal + pools.Absorb, 2);
        double salesTaxBase = Py.Round(effective - discount + pools.StBase, 2);
        double whtBase = Py.Round(effective - discount + pools.WhtBase, 2);
        double salesTax = (string.IsNullOrEmpty(inv.TaxMode) ? "general" : inv.TaxMode) == "general"
            ? Py.Round(salesTaxBase * Py.Float0(inv.GlobalSalesTaxPct) / 100, 2)
            : Py.Round(Py.Float0(inv.TotalTax), 2);
        double furtherTax = inv.ApplyFurtherTax == true ? Py.Round(salesTaxBase * Py.Float0(inv.FurtherTaxPct) / 100, 2) : 0.0;
        double wht = inv.ApplyWithholdingTax == true ? Py.Round(whtBase * Py.Float0(inv.WithholdingTaxPct) / 100, 2) : 0.0;
        double net = Py.Round(effective - discount + pools.Billed + salesTax + furtherTax - wht, 2);
        return new Totals(subtotal, discount, effective, salesTaxBase, salesTax, furtherTax, whtBase, wht, pools.Billed, net, pools.BilledRows);
    }

    /// <summary>payload.get("fbrInvoice", {}).get("invoiceType", "S").</summary>
    public static string InvoiceType(PyDict payload) =>
        payload.Get("fbrInvoice") is PyDict f && f.Get("invoiceType", "S") is string t ? t : "S";
}
