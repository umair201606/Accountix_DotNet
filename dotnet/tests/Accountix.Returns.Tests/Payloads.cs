namespace Accountix.Returns.Tests;

/// <summary>Return payloads exactly as the forms' collectData() builds them (amounts read back from the 2dp summary).</summary>
public static class Payloads
{
    public sealed record Line(int ProductId, double Qty, double OriginalQty, double Price, double TaxPct = 0, double WhtPct = 0,
                              double DiscountPct = 0, double Freight = 0, string Description = "Part");

    private static double R2(double v) => Math.Round(v, 2, MidpointRounding.ToEven);

    /// <summary>The sales form: credit note value = gross − discount + charges + sales tax.</summary>
    public static Dictionary<string, object?> Sales(int invoiceId, int customerId, IReadOnlyList<Line> lines, string action,
                                                    int? id = null, int? editVersion = null, string date = World.ReturnDate,
                                                    string notes = "Damaged in transit")
    {
        double gross = 0, disc = 0, tax = 0;
        var items = new List<Dictionary<string, object?>>();
        foreach (var l in lines)
        {
            double orig = l.Qty * l.Price;
            double propDisc = l.DiscountPct > 0 ? orig * l.DiscountPct / 100 : 0;
            double after = Math.Max(0, orig - propDisc);
            double propTax = after * l.TaxPct / 100;
            gross += orig; disc += propDisc; tax += propTax;
            items.Add(new()
            {
                ["product_id"] = l.ProductId, ["description"] = l.Description, ["original_quantity"] = l.OriginalQty,
                ["previously_returned"] = 0.0, ["max_returnable_qty"] = l.OriginalQty, ["current_return_qty"] = l.Qty,
                ["unit"] = "pcs", ["unit_price"] = l.Price, ["discount_pct"] = l.DiscountPct, ["discount_amount"] = 0.0,
                ["delivery"] = 0.0, ["installation"] = 0.0, ["sales_tax_pct"] = l.TaxPct,
                ["total_before_discount"] = l.OriginalQty * l.Price, ["total_after_discount"] = l.OriginalQty * l.Price,
                ["proportional_discount"] = R2(propDisc), ["proportional_sales_tax"] = R2(propTax),
                ["proportional_delivery"] = 0, ["proportional_installation"] = 0, ["net_return_value"] = R2(after + propTax),
            });
        }
        var d = new Dictionary<string, object?>
        {
            ["id"] = id, ["original_invoice_id"] = invoiceId, ["customer_id"] = customerId, ["date"] = date, ["notes"] = notes,
            ["reverse_charges"] = true, ["gross_return_value"] = R2(gross), ["total_discount"] = R2(disc), ["total_charges"] = 0.0,
            ["total_tax"] = R2(tax), ["net_return_amount"] = R2(gross - disc + tax), ["items"] = items, ["action"] = action,
        };
        if (editVersion is int v) d["edit_version"] = v;
        return d;
    }

    /// <summary>The purchase form: debit note value = gross − discount + expenses + sales tax − withholding.</summary>
    public static Dictionary<string, object?> Purchase(int invoiceId, int supplierId, IReadOnlyList<Line> lines, string action,
                                                       int? id = null, int? editVersion = null, string date = World.ReturnDate,
                                                       string notes = "Wrong spec", bool reverseExpenses = true)
    {
        double gross = 0, disc = 0, exp = 0, tax = 0, wht = 0;
        var items = new List<Dictionary<string, object?>>();
        foreach (var l in lines)
        {
            double orig = l.Qty * l.Price;
            double ratio = l.OriginalQty > 0 ? l.Qty / l.OriginalQty : 0;
            double propDisc = l.DiscountPct > 0 ? orig * l.DiscountPct / 100 : 0;
            double after = Math.Max(0, orig - propDisc);
            double expShare = reverseExpenses ? l.Freight * ratio : 0;
            double propTax = after * l.TaxPct / 100;
            double propWht = after * l.WhtPct / 100;
            gross += orig; disc += propDisc; exp += expShare; tax += propTax; wht += propWht;
            items.Add(new()
            {
                ["product_id"] = l.ProductId, ["description"] = l.Description, ["original_quantity"] = l.OriginalQty,
                ["previously_returned"] = 0.0, ["max_returnable_qty"] = l.OriginalQty, ["current_return_qty"] = l.Qty,
                ["unit"] = "pcs", ["unit_price"] = l.Price, ["discount_pct"] = l.DiscountPct, ["discount_amount"] = 0.0,
                ["commission"] = 0.0, ["freight"] = l.Freight, ["loading_unloading"] = 0.0,
                ["sales_tax_pct"] = l.TaxPct, ["withholding_tax_pct"] = l.WhtPct,
                ["total_before_discount"] = l.OriginalQty * l.Price, ["total_after_discount"] = l.OriginalQty * l.Price,
                ["proportional_discount"] = R2(propDisc), ["proportional_sales_tax"] = 0, ["proportional_withholding_tax"] = 0,
                ["proportional_commission"] = 0, ["proportional_freight"] = 0, ["proportional_loading"] = 0,
                ["net_return_value"] = R2(after + expShare + propTax - propWht),
            });
        }
        var d = new Dictionary<string, object?>
        {
            ["id"] = id, ["original_invoice_id"] = invoiceId, ["supplier_id"] = supplierId, ["date"] = date, ["notes"] = notes,
            ["reverse_expenses"] = reverseExpenses, ["gross_return_value"] = R2(gross), ["total_discount"] = R2(disc), ["total_expenses"] = R2(exp),
            ["total_tax"] = R2(tax), ["net_return_amount"] = R2(gross - disc + exp + tax - wht), ["items"] = items, ["action"] = action,
        };
        if (editVersion is int v) d["edit_version"] = v;
        return d;
    }
}
