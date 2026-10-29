using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Accountix.Invoicing.Services;

namespace Accountix.Orders.Services;

/// <summary>A JSON answer as the Python route returns it: status code + jsonify body.</summary>
public sealed record OrderAnswer(int Status, Dictionary<string, object?> Body)
{
    public static OrderAnswer Ok(Dictionary<string, object?> body) => new(200, body);

    public static OrderAnswer Error(int status, string error, bool stale = false)
    {
        var body = new Dictionary<string, object?> { ["ok"] = false, ["error"] = error };
        if (stale) body["stale"] = true;
        return new OrderAnswer(status, body);
    }

    public ContentResult ToResult() => PyJson.Result(Body, Status);
}

/// <summary>
/// The request could not be read the way Python reads it (float("abc"),
/// strptime of a bad date, None into a NOT NULL column): the route answers
/// the 500 "Server error" page, as the Python app's handle_all does.
/// </summary>
public sealed class OrderInputException(string message) : Exception(message);

/// <summary>Reads the order JSON exactly as sales.py / purchases.py do.</summary>
internal static partial class OrderInput
{
    /// <summary>The body as request.get_json(force=True) reads it; bad JSON → 400 (Flask's BadRequest).</summary>
    public static async Task<PyData?> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        try
        {
            return await PyData.ReadAsync(request, ct);
        }
        catch (PyBadRequestException)
        {
            return null;
        }
    }

    /// <summary>action == "approve" (any other value, or none, is a save).</summary>
    public static bool IsApprove(PyData data) =>
        data.Get("action") is { ValueKind: JsonValueKind.String } a && a.GetString() == "approve";

    /// <summary>
    /// A value Python hands to an INTEGER column as it came (data.get("customer_id")):
    /// Postgres casts "12" and 12.0; None violates NOT NULL and text that is no
    /// number fails the cast, both a 500 in Python.
    /// </summary>
    public static int Int(JsonElement? e, string what)
    {
        if (e is not { } v || v.ValueKind == JsonValueKind.Null)
            throw new OrderInputException($"null value in column \"{what}\" violates not-null constraint");
        switch (v.ValueKind)
        {
            case JsonValueKind.Number:
                if (v.TryGetInt32(out int i)) return i;
                return (int)Math.Round(v.GetDouble(), MidpointRounding.AwayFromZero);
            case JsonValueKind.String:
                if (int.TryParse(v.GetString()!.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int s))
                    return s;
                break;
        }
        throw new OrderInputException($"invalid input syntax for type integer: {v.GetRawText()}");
    }

    /// <summary>data.get(key) or None, as an id.</summary>
    public static int? OptionalId(PyData data, string key) =>
        PyData.Truthy(data.Get(key)) ? Int(data.Get(key), key) : null;

    /// <summary>float(data.get(key, default)): ValueError / TypeError → 500.</summary>
    public static double Float(PyData data, string key, double @default)
    {
        try
        {
            return data.Float(key, @default);
        }
        catch (PyValueException e)
        {
            throw new OrderInputException(e.Message);
        }
    }

    /// <summary>
    /// data.get(key, default) stored in a text column: None stays NULL, a
    /// number is stored as its text (the form's g() sends 0 for an empty field,
    /// and Python stores "0").
    /// </summary>
    public static string? Text(PyData data, string key, string? @default) => data.Str(key, @default) switch
    {
        "True" when data.Get(key)?.ValueKind == JsonValueKind.True => "true",
        "False" when data.Get(key)?.ValueKind == JsonValueKind.False => "false",
        var s => s,
    };

    [GeneratedRegex(@"^(\d{4})-(1[0-2]|0[1-9]|[1-9])-(3[01]|[12]\d|0[1-9]|[1-9]| [1-9])$")]
    private static partial Regex YmdPattern();

    /// <summary>
    /// datetime.strptime(v, "%Y-%m-%d").date() when v is truthy, else null.
    /// Not a string, or not that format, or not a real day: ValueError → 500.
    /// </summary>
    public static DateOnly? StrpDate(JsonElement? e)
    {
        if (!PyData.Truthy(e)) return null;
        if (e!.Value.ValueKind != JsonValueKind.String)
            throw new OrderInputException("strptime() argument 1 must be str");
        string s = e.Value.GetString()!;
        var m = YmdPattern().Match(s);
        if (!m.Success)
            throw new OrderInputException($"time data '{s}' does not match format '%Y-%m-%d'");
        int y = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int mo = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        int d = int.Parse(m.Groups[3].Value.Trim(), CultureInfo.InvariantCulture);
        if (y < 1 || d > DateTime.DaysInMonth(y, mo))
            throw new OrderInputException("day is out of range for month");
        return new DateOnly(y, mo, d);
    }

    /// <summary>The order's lines: data.get("items", []) — anything but a list of objects is a 500.</summary>
    public static IReadOnlyList<PyData> Items(PyData data)
    {
        if (data.Get("items") is not { } items) return [];
        if (items.ValueKind == JsonValueKind.Object)
            throw new OrderInputException("'str' object has no attribute 'get'");
        if (items.ValueKind != JsonValueKind.Array)
            throw new OrderInputException($"'{items.ValueKind}' object is not iterable");
        var rows = items.EnumerateArray().Select(x => new PyData(x)).ToList();
        if (rows.Any(r => !r.IsObject))
            throw new OrderInputException("row has no attribute 'get'");
        return rows;
    }

    /// <summary>One order line, as InvSalesOrderItem(...) / InvPurchaseOrderItem(...) is built from a row.</summary>
    public static OrderLineValues Line(PyData row) => new(
        ProductId: Int(row.Get("product_id"), "product_id"),
        Description: Text(row, "description", ""),
        Quantity: Py.Dec(Float(row, "quantity", 1)),
        Unit: Text(row, "unit", "pcs"),
        UnitPrice: Py.Dec(Float(row, "unit_price", 0)),
        SalesTaxPct: Py.Dec(Float(row, "sales_tax_pct", 0)),
        TotalBeforeDiscount: Py.Dec(OrderTotals.CheckedAmount(Float(row, "total_before_discount", 0), row)),
        TotalAfterDiscount: Py.Dec(Float(row, "total_after_discount", 0)),
        TotalPrice: Py.Dec(OrderTotals.CheckedAmount(Float(row, "total_price", 0), row)));

    /// <summary>The header values every order save writes (sales.py / purchases.py, same lines).</summary>
    public static OrderHeaderValues Header(PyData data, string partyKey, CompanyClock clock)
    {
        int party = Int(data.Get(partyKey), partyKey);
        int? partyAccount = OptionalId(data, "party_account_id");
        string? taxMode = Text(data, "tax_mode", "general");
        // Python: date.today() (the server's date) when no date is sent; the company's
        // today here (docs/modules/orders.md, deliberate differences).
        DateOnly orderDate = StrpDate(data.Get("order_date")) ?? clock.Today;
        DateOnly? expected = StrpDate(data.Get("expected_date"));
        double pct = Float(data, "global_sales_tax_pct", 0);
        string? notes = Text(data, "notes", "");
        // shared/order_totals.checked_totals: the browser's totals unless they disagree with the lines.
        var sent = (Float(data, "subtotal", 0), Float(data, "total_tax", 0), Float(data, "total_amount", 0));
        var (subtotal, tax, total) = OrderTotals.Checked(Items(data), taxMode, pct, sent);
        return new(party, partyAccount, taxMode, orderDate, expected, Py.Dec(pct), notes,
                   Py.Dec(subtotal), Py.Dec(tax), Py.Dec(total));
    }

    /// <summary>The number a new order is created with: what was sent, unless blank or "(auto)" (then a placeholder).</summary>
    public static string NewNumber(PyData data, string key)
    {
        if (data.Get(key) is not { } v || v.ValueKind == JsonValueKind.Null) return DocNumbers.Placeholder();
        string? s = Text(data, key, null);
        return s is null or "" or "(auto)" ? DocNumbers.Placeholder() : s;
    }
}

public sealed record OrderHeaderValues(
    int PartyId, int? PartyAccountId, string? TaxMode, DateOnly OrderDate, DateOnly? ExpectedDate,
    decimal GlobalSalesTaxPct, string? Notes, decimal Subtotal, decimal TotalTax, decimal TotalAmount);

public sealed record OrderLineValues(
    int ProductId, string? Description, decimal Quantity, string? Unit, decimal UnitPrice, decimal SalesTaxPct,
    decimal TotalBeforeDiscount, decimal TotalAfterDiscount, decimal TotalPrice);

/// <summary>
/// shared/order_totals.py: an order's totals checked against its own lines. Recomputed the way
/// the editors compute them (amount = quantity × rate, Math.round(x*100)/100; subtotal = their
/// plain running sum; tax on the subtotal, or per line at the line's rate; no discount, charges
/// or withholding on an order); the request's figures are kept while they agree to the paisa.
/// </summary>
public static class OrderTotals
{
    private const double Tolerance = 0.01 + 1e-9;

    /// <summary>The editors' rounding: Math.round(x * 100) / 100 (math.floor(x * 100 + 0.5) / 100).</summary>
    public static double R2(double x) => Math.Floor(x * 100 + 0.5) / 100;

    private static bool Differs(double sent, double computed) => Math.Abs(sent - computed) > Tolerance;

    /// <summary>quantity × rate, rounded as the editor rounds it (the row's own defaults).</summary>
    public static double LineAmount(PyData row) =>
        R2(OrderInput.Float(row, "quantity", 1) * OrderInput.Float(row, "unit_price", 0));

    /// <summary>A line's amount as sent, unless it disagrees with quantity × rate.</summary>
    public static double CheckedAmount(double sent, PyData row)
    {
        double amount = LineAmount(row);
        return Differs(sent, amount) ? amount : sent;
    }

    /// <summary>(subtotal, total_tax, total_amount) to store: as sent while every figure agrees with the lines.</summary>
    public static (double Subtotal, double Tax, double Total) Checked(IReadOnlyList<PyData> rows, string? taxMode,
                                                                    double globalSalesTaxPct, (double, double, double) sent)
    {
        var amounts = rows.Select(LineAmount).ToList();
        double subtotal = 0.0;
        foreach (double a in amounts)       // a plain running sum, as the editor adds
            subtotal += a;
        subtotal = R2(subtotal);
        double tax;
        if (taxMode == "individual")
        {
            tax = 0.0;
            for (int i = 0; i < rows.Count; i++)
                tax += amounts[i] * OrderInput.Float(rows[i], "sales_tax_pct", 0) / 100;
        }
        else
            tax = subtotal * globalSalesTaxPct / 100;
        tax = R2(tax);
        double total = R2(subtotal + tax);
        var (s1, s2, s3) = sent;
        return Differs(s1, subtotal) || Differs(s2, tax) || Differs(s3, total) ? (subtotal, tax, total) : sent;
    }
}

/// <summary>The words both sides answer with (sales.py / purchases.py).</summary>
internal static class OrderWords
{
    public const string CannotModifyApproved = "Cannot modify approved order";
    public const string OnlyApprovedUnapprove = "Only approved orders can be unapproved";
    public const string CannotDeleteApproved = "Cannot delete approved order. Unapprove first.";

    public static string CannotChange(string number, IEnumerable<string> billed) =>
        $"Cannot change {number}: invoice(s) {string.Join(", ", billed)} were billed from it. " +
        "Unapprove and delete those invoices first.";

    public static string CannotUnapprove(string number, IEnumerable<string> billed) =>
        $"Cannot unapprove {number}: invoice(s) {string.Join(", ", billed)} were billed from it. " +
        "Unapprove and delete those invoices first, or cancel the order to stop further billing.";

    public static string CannotDelete(string number, IEnumerable<string> billed) =>
        $"Cannot delete {number}: invoice(s) {string.Join(", ", billed)} were billed from it.";

    public static string Saved(bool approve, bool existing) =>
        "Order " + (approve ? "approved and locked" : existing ? "changes saved" : "saved as unapproved");

    /// <summary>Statuses an order can be cancelled from.</summary>
    public static bool Cancellable(string? status) => status is "unapproved" or "approved" or "new";
}

/// <summary>
/// InventorySettings purchase_flow / sales_flow for the sidebar gates (the
/// nav context app.py builds), read once per request.
/// </summary>
public sealed record OrderFlows(string? PurchaseFlow, string? SalesFlow)
{
    private static readonly object Key = new();

    public static OrderFlows For(HttpContext http)
    {
        if (http.Items.TryGetValue(Key, out var cached) && cached is OrderFlows f) return f;
        OrderFlows flows;
        try
        {
            var db = http.RequestServices.GetRequiredService<AccountixDbContext>();
            var row = db.Set<InventorySetting>().AsNoTracking().OrderBy(s => s.Id)
                .Select(s => new { s.PurchaseFlow, s.SalesFlow }).FirstOrDefault();
            // InventorySettings.get() makes the row on first use, with the column defaults.
            flows = row is null ? new OrderFlows("with_po", "with_so") : new OrderFlows(row.PurchaseFlow, row.SalesFlow);
        }
        catch (Exception e) when (e is InvalidOperationException or NoActiveCompanyException or DbUpdateException)
        {
            flows = new OrderFlows(null, null);   // Python: the try/except leaves the flows out → links hidden
        }
        http.Items[Key] = flows;
        return flows;
    }
}

/// <summary>Jinja's rendering of values the order templates print.</summary>
public static class Jinja
{
    /// <summary>{{ value }}: None prints "None".</summary>
    public static string Str(string? s) => s ?? "None";

    /// <summary>{{ date }} (str(date)), or "None".</summary>
    public static string Date(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "None";

    /// <summary>{{ money }}: a Money column comes back as a float ("17.0"), None as "None".</summary>
    public static string Float(decimal? d) => d is decimal v ? Py.Repr((double)v) : "None";

    /// <summary>|title.</summary>
    public static string Title(string? s) => ViewHelpers.Title(s ?? "None");
}
