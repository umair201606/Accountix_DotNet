using Accountix.Invoicing.Services;

namespace Accountix.Returns.Services;

/// <summary>
/// The checks both return routes make before approving (validate_return and
/// _over_returned in invoicing_app/routes/sales_return.py / purchase_return.py),
/// in Python's float arithmetic and words.
/// </summary>
public static class ReturnRules
{
    /// <summary>validate_return(data): the refusals, in order; empty when the return may be approved.</summary>
    public static List<string> Validate(PyData data)
    {
        var errors = new List<string>();
        if (!data.Truthy("original_invoice_id"))
            errors.Add("Original invoice is required");
        bool hasReturn = false;
        int i = 0;
        foreach (var row in data.List("items"))
        {
            double qty = row.Float("current_return_qty");
            double maxQ = row.Float("max_returnable_qty");
            if (qty < 0)
                errors.Add($"Row {i + 1}: Return quantity cannot be negative");
            if (qty > maxQ + 0.001)
                errors.Add($"Row {i + 1}: Return quantity ({Py.Repr(qty)}) exceeds max returnable ({Py.Repr(maxQ)})");
            if (qty > 0)
                hasReturn = true;
            i++;
        }
        if (!hasReturn)
            errors.Add("At least one item must have a return quantity greater than 0");
        return errors;
    }

    /// <summary>
    /// _over_returned: the server-side cap. What the invoice sold (or bought) per
    /// product, less what OTHER approved returns already took, must cover what
    /// this one asks for; the browser's own max_returnable_qty is never trusted.
    /// </summary>
    /// <param name="invoiced">The invoice's lines (product, quantity), in id order.</param>
    /// <param name="alreadyReturned">Lines of the invoice's other approved returns (product, quantity).</param>
    /// <param name="data">The request: its items' product_id and current_return_qty.</param>
    public static List<string> OverReturned(IEnumerable<(int? ProductId, double Quantity)> invoiced,
                                            IEnumerable<(int? ProductId, double Quantity)> alreadyReturned, PyData data)
    {
        var sold = Sum(invoiced);
        var already = Sum(alreadyReturned);
        var asked = new List<(int ProductId, double Qty)>();
        foreach (var row in data.List("items"))
        {
            var pidEl = row.Get("product_id");
            if (!PyData.Truthy(pidEl)) continue;
            int pid = PyInt(pidEl!.Value);
            double q = row.FloatOr0("current_return_qty");
            int idx = asked.FindIndex(a => a.ProductId == pid);
            if (idx < 0) asked.Add((pid, 0.0 + q));
            else asked[idx] = (pid, asked[idx].Qty + q);
        }
        var errors = new List<string>();
        foreach (var (pid, qty) in asked)
        {
            double room = sold.GetValueOrDefault(pid, 0.0) - already.GetValueOrDefault(pid, 0.0);
            if (qty > room + 0.0001)
                errors.Add($"Product #{pid}: returning {Py.G(qty)} but only {Py.G(room)} remain returnable on the invoice");
        }
        return errors;
    }

    private static Dictionary<int, double> Sum(IEnumerable<(int? ProductId, double Quantity)> rows)
    {
        var d = new Dictionary<int, double>();
        foreach (var (pid, q) in rows)
            if (pid is int p && p != 0)
                d[p] = d.GetValueOrDefault(p, 0.0) + q;
        return d;
    }

    /// <summary>int(x) of a JSON value: ints, floats (truncated) and numeric strings; anything else is Python's ValueError.</summary>
    public static int PyInt(System.Text.Json.JsonElement e)
    {
        switch (e.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Number:
                return e.TryGetInt32(out int i) ? i : (int)Math.Truncate(e.GetDouble());
            case System.Text.Json.JsonValueKind.True: return 1;
            case System.Text.Json.JsonValueKind.False: return 0;
            case System.Text.Json.JsonValueKind.String:
                if (int.TryParse(e.GetString()!.Trim(), System.Globalization.NumberStyles.AllowLeadingSign,
                                 System.Globalization.CultureInfo.InvariantCulture, out int s))
                    return s;
                break;
        }
        throw new PyValueException($"invalid literal for int() with base 10: {e.GetRawText()}");
    }

    /// <summary>lock_products' reading of the request's ids: int(p), junk skipped.</summary>
    public static List<int> ProductIdsToLock(PyData data)
    {
        var ids = new List<int>();
        foreach (var row in data.List("items"))
        {
            if (!(row.FloatOr0("current_return_qty") > 0)) continue;
            if (row.Get("product_id") is not { } pid) continue;
            try { ids.Add(PyInt(pid)); }
            catch (PyValueException) { /* a blank or junk id from a form: validation refuses it */ }
        }
        return ids;
    }
}
