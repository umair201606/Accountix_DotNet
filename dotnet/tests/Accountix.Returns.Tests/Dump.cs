using System.Globalization;
using System.Text.RegularExpressions;
using Accountix.TestSupport;
using Npgsql;

namespace Accountix.Returns.Tests;

/// <summary>
/// What the golden tests compare: rows read with plain SQL (the same SQL text the capture
/// scripts ran against the Python app's writes) and JSON answers, written as canonical text
/// with run-specific values replaced by roles. Golden/capture/_world.py has the same rules.
/// <list type="bullet">
/// <item>Values: None, True/False, integers, decimals without trailing zeros, dates yyyy-MM-dd,
/// timestamps yyyy-MM-dd HH:mm:ss.</item>
/// <item>Ids → roles by column / JSON key (product_id 145 → "P0"); unknown ids stay numbers, so
/// a new id-shaped value shows up as a difference instead of passing silently.</item>
/// <item>Text: the world's tag → TAG, document numbers → their role (SI-202610-0078 → &lt;SI&gt;).</item>
/// </list>
/// </summary>
public sealed class Roles
{
    private readonly Dictionary<string, Dictionary<long, string>> _ids = new(StringComparer.Ordinal);
    private readonly List<(string From, string To)> _text = [];

    public Roles Id(string key, long id, string role)
    {
        if (!_ids.TryGetValue(key, out var map)) _ids[key] = map = [];
        map[id] = role;
        return this;
    }

    public Roles Text(string from, string to)
    {
        if (from.Length > 0) _text.Add((from, to));
        return this;
    }

    /// <summary>The world's parties and products: product_id → P0.., customer, supplier, owner, tag.</summary>
    public static Roles For(World w)
    {
        var r = new Roles().Id("customer_id", w.Customer, "CUSTOMER").Id("supplier_id", w.Supplier, "SUPPLIER")
            .Id("created_by", World.Owner, "OWNER").Id("approved_by", World.Owner, "OWNER").Text(w.Tag, "TAG");
        for (int i = 0; i < w.Products.Length; i++)
            r.Id("product_id", w.Products[i], "P" + i);
        return r;
    }

    public string Str(string? s)
    {
        if (s is null) return "None";
        foreach (var (from, to) in _text.OrderByDescending(t => t.From.Length))
            s = s.Replace(from, to, StringComparison.Ordinal);
        return s;
    }

    public string Value(string column, object? v) => v switch
    {
        null or DBNull => "None",
        bool b => b ? "True" : "False",
        int or long or short => _ids.TryGetValue(column, out var map) && map.TryGetValue(Convert.ToInt64(v), out var role)
            ? role : Convert.ToInt64(v).ToString(CultureInfo.InvariantCulture),
        decimal d => d == 0 ? "0" : d.ToString("0.############", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime t => t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        string s => Str(s),
        _ => throw new InvalidOperationException($"{column}: no canonical form for {v.GetType().Name}"),
    };

    /// <summary>A payload with its ids written as roles (the fixture's "inputs"; Golden.SameInputs compares them).</summary>
    public object? Mask(object? v, IReadOnlyDictionary<string, IReadOnlyDictionary<long, string>>? extra = null, string? key = null) => v switch
    {
        IDictionary<string, object?> d => d.ToDictionary(kv => kv.Key, kv => Mask(kv.Value, extra, kv.Key)),
        string s => s,
        System.Collections.IEnumerable e => e.Cast<object?>().Select(x => Mask(x, extra)).ToList(),
        int or long when key is not null =>
            extra is not null && extra.TryGetValue(key, out var x) && x.TryGetValue(Convert.ToInt64(v), out var r1) ? r1
            : _ids.TryGetValue(key, out var map) && map.TryGetValue(Convert.ToInt64(v), out var r2) ? r2 : v,
        _ => v,
    };

    /// <summary>A JSON answer (Python's sorted compact form): ids by key, then the text rules.</summary>
    public string Json(string json, IReadOnlyDictionary<string, IReadOnlyDictionary<long, string>>? extra = null)
    {
        string s = json.Trim();
        var keys = _ids.Keys.Concat(extra?.Keys ?? []).Distinct();
        foreach (string key in keys)
            s = Regex.Replace(s, "\"" + Regex.Escape(key) + "\":(\\d+)(?=[,}\\]])", m =>
            {
                long id = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                string? role = extra is not null && extra.TryGetValue(key, out var e) && e.TryGetValue(id, out var x) ? x
                             : _ids.TryGetValue(key, out var map) && map.TryGetValue(id, out var y) ? y : null;
                return role is null ? m.Value : $"\"{key}\":\"{role}\"";
            });
        return Str(s);
    }
}

public static class Dump
{
    /// <summary>
    /// Runs <paramref name="sql"/> (named @parameters) on the test database and writes each row as
    /// "<paramref name="label"/>: col=value|col=value". Date columns come back as dates.
    /// </summary>
    public static async Task<List<string>> RowsAsync(Roles roles, string label, string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new NpgsqlConnection(Reference.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        await using var reader = await cmd.ExecuteReaderAsync();
        var lines = new List<string>();
        while (await reader.ReadAsync())
        {
            var cells = new List<string>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                string col = reader.GetName(i);
                object? v = reader.IsDBNull(i) ? null
                          : reader.GetDataTypeName(i) == "date" ? reader.GetFieldValue<DateOnly>(i)
                          : reader.GetValue(i);
                cells.Add($"{col}={roles.Value(col, v)}");
            }
            lines.Add($"{label}: {string.Join("|", cells)}");
        }
        if (lines.Count == 0)
            lines.Add($"{label}: (none)");
        return lines;
    }

    // ── The SQL both apps' writes were read with (copied verbatim into Golden/capture/_world.py) ──

    public const string Journal =
        "SELECT e.voucher_number, e.description AS entry, e.entry_date, e.is_posted, " +
        "CASE WHEN a.name LIKE @like THEN regexp_replace(a.code, '-[0-9]+$', '-*') || ' ' || a.name ELSE a.code END AS account, " +
        "l.debit, l.credit, l.description, l.label_id " +
        "FROM journal_entries e JOIN journal_lines l ON l.journal_entry_id = e.id JOIN chart_of_accounts a ON a.id = l.account_id " +
        "WHERE e.company_id = @cid AND e.voucher_type = @vt AND e.voucher_id = @id ORDER BY e.id, l.id";

    public const string StockRows =
        "SELECT product_id, voucher_number, transaction_type, quantity, unit_cost, total_cost, running_qty, running_cost, running_avg, " +
        "valuation_method, txn_date, created_by, notes FROM stock_ledger WHERE company_id = @cid AND voucher_type = @vt AND voucher_id = @id ORDER BY id";

    public const string Layers =
        "SELECT product_id, unit_cost, qty_original, qty_remaining, value_remaining, method, is_revaluation, notes " +
        "FROM stock_layers WHERE product_id = ANY(@pids) ORDER BY product_id, id";

    public const string Products =
        "SELECT id AS product_id, current_stock, cost_price FROM inv_products WHERE id = ANY(@pids) ORDER BY id";

    public const string Movements =
        "SELECT product_id, type, quantity, reference_type, notes, created_by FROM inv_stock_movements " +
        "WHERE reference_type = @rt AND reference_id = @id ORDER BY id";

    public const string SalesReturn =
        "SELECT return_number, original_invoice_id, customer_id, party_account_id, return_date, notes, gross_return_value, total_discount, " +
        "total_charges, total_tax, net_return_amount, total_cost_returned, reverse_charges, status, created_by, approved_by, " +
        "approved_at IS NOT NULL AS approved, edit_version FROM inv_sales_returns WHERE id = @id";

    public const string SalesReturnItems =
        "SELECT product_id, description, original_quantity, previously_returned_qty, max_returnable_qty, current_return_qty, unit, unit_price, " +
        "discount_pct, discount_amount, delivery, installation, sales_tax_pct, total_before_discount, total_after_discount, proportional_discount, " +
        "proportional_sales_tax, proportional_delivery, proportional_installation, net_return_value, cost_basis, total_cost_returned " +
        "FROM inv_sales_return_items WHERE return_id = @id ORDER BY id";

    public const string PurchaseReturn =
        "SELECT return_number, original_invoice_id, supplier_id, return_date, notes, gross_return_value, total_discount, total_expenses, " +
        "total_tax, net_return_amount, reverse_expenses, status, created_by, approved_by, approved_at IS NOT NULL AS approved, edit_version " +
        "FROM inv_purchase_returns WHERE id = @id";

    public const string PurchaseReturnItems =
        "SELECT product_id, description, original_quantity, previously_returned_qty, max_returnable_qty, current_return_qty, unit, unit_price, " +
        "discount_pct, discount_amount, commission, freight, loading_unloading, sales_tax_pct, withholding_tax_pct, total_before_discount, " +
        "total_after_discount, proportional_discount, proportional_sales_tax, proportional_withholding_tax, proportional_commission, " +
        "proportional_freight, proportional_loading, net_return_value FROM inv_purchase_return_items WHERE return_id = @id ORDER BY id";

    public const string SalesInvoiceStatus = "SELECT payment_status, paid_amount FROM inv_invoices WHERE id = @id";

    public const string PurchaseInvoiceStatus = "SELECT payment_status, paid_amount FROM inv_purchase_invoices WHERE id = @id";

    /// <summary>
    /// A return's footprint after a step: the return and its lines, its journal (every entry, posted or not),
    /// its stock rows and movements, the products' layers and figures, and the invoice's settled cache.
    /// </summary>
    public static async Task<List<string>> ReturnStateAsync(Roles roles, World w, bool sales, int returnId, int invoiceId)
    {
        var lines = new List<string>();
        int[] pids = w.Products;
        lines.AddRange(await RowsAsync(roles, "return", sales ? SalesReturn : PurchaseReturn, ("id", returnId)));
        lines.AddRange(await RowsAsync(roles, "item", sales ? SalesReturnItems : PurchaseReturnItems, ("id", returnId)));
        lines.AddRange(await RowsAsync(roles, "journal", Journal, ("like", "%" + w.Tag + "%"), ("cid", World.CompanyB),
                                       ("vt", sales ? "SR" : "PR"), ("id", returnId)));
        lines.AddRange(await RowsAsync(roles, "stock", StockRows, ("cid", World.CompanyB), ("vt", sales ? "SRV" : "PRV"), ("id", returnId)));
        lines.AddRange(await RowsAsync(roles, "movement", Movements, ("rt", sales ? "sales_return" : "purchase_return"), ("id", returnId)));
        lines.AddRange(await RowsAsync(roles, "layer", Layers, ("pids", pids)));
        lines.AddRange(await RowsAsync(roles, "product", Products, ("pids", pids)));
        lines.AddRange(await RowsAsync(roles, "invoice", sales ? SalesInvoiceStatus : PurchaseInvoiceStatus, ("id", invoiceId)));
        return lines;
    }
}
