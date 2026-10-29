using System.Text.Json;

namespace Accountix.Invoicing.Services;

/// <summary>shared/doc_checks.py: range checks on the figures an invoice is approved with.</summary>
public static class DocChecks
{
    /// <summary>_num: 0 for None/"", the number, or null when it is not one.</summary>
    private static double? Num(JsonElement? v)
    {
        if (v is null) return 0.0;
        var e = v.Value;
        switch (e.ValueKind)
        {
            case JsonValueKind.Null: return 0.0;
            case JsonValueKind.String:
                string s = e.GetString()!;
                if (s.Length == 0) return 0.0;
                return Py.TryParseFloat(s.Replace(",", ""), out double d) ? d : null;
            case JsonValueKind.Number:
                // float(str(v).replace(",", "")): str of a JSON number is its Python repr.
                return double.Parse(e.GetRawText(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
            case JsonValueKind.True: return null;   // float("True") fails
            case JsonValueKind.False: return null;
            default: return null;
        }
    }

    public static List<string> AmountErrors(PyData data)
    {
        var errors = new List<string>();

        void Pct(string label, JsonElement? v)
        {
            double? n = Num(v);
            if (n is null) errors.Add($"{label} must be a number");
            else if (!(0 <= n && n <= 100)) errors.Add($"{label} must be between 0 and 100%");
        }

        double? NonNegative(string label, JsonElement? v)
        {
            double? n = Num(v);
            if (n is null) errors.Add($"{label} must be a number");
            else if (n < 0) errors.Add($"{label} cannot be negative");
            return n is null || n == 0 ? 0.0 : n;
        }

        foreach (var (key, label) in new[] { ("global_discount_pct", "Discount"), ("global_sales_tax_pct", "Sales tax"),
                                              ("further_tax_pct", "Further tax"), ("withholding_tax_pct", "Withholding tax") })
            Pct(label, data.Get(key));
        foreach (var (key, label) in new[] { ("global_discount_value", "Discount amount"), ("global_delivery", "Delivery"),
                                              ("global_installation", "Installation") })
            NonNegative(label, data.Get(key));

        var items = data.Get("items") is { ValueKind: JsonValueKind.Array } arr ? arr.EnumerateArray().ToList() : [];
        int i = 0;
        foreach (var rowEl in items)
        {
            i++;
            var row = new PyData(rowEl);
            double? qty = Num(row.Get("quantity"));
            if (qty is null)
            {
                errors.Add($"Row {i}: quantity must be a number");
                continue;
            }
            double? price = NonNegative($"Row {i}: unit price", row.Get("unit_price"));
            Pct($"Row {i}: discount", row.Get("discount_pct"));
            Pct($"Row {i}: sales tax", row.Get("sales_tax_pct"));
            double disc = NonNegative($"Row {i}: discount amount", row.Get("discount_amount")) ?? 0;
            NonNegative($"Row {i}: delivery", row.Get("delivery"));
            NonNegative($"Row {i}: installation", row.Get("installation"));
            if (qty != 0 && price is not null && disc > qty.Value * price.Value + 0.005)
                errors.Add($"Row {i}: the discount is more than the line's value");
        }
        var charges = data.Get("charges") is { ValueKind: JsonValueKind.Array } carr ? carr.EnumerateArray().ToList() : [];
        int j = 0;
        foreach (var chEl in charges)
        {
            j++;
            double? n = Num(new PyData(chEl).Get("amount"));
            if (n is null || n < 0) errors.Add($"Charge {j}: amount must be zero or more");
        }
        return errors;
    }
}
