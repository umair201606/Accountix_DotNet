using System.Data.Common;
using System.Globalization;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Invoicing.Tests;

/// <summary>Who is who in one golden scenario: the ids .NET made, and the role each one plays.</summary>
public sealed class Scene
{
    public required string Tag { get; init; }
    public required int Owner { get; init; }
    public List<(int Id, string Role)> Products { get; } = [];
    public Dictionary<int, string> Customers { get; } = [];
    public Dictionary<int, string> Suppliers { get; } = [];
    public Dictionary<int, string> Labels { get; } = [];
    /// <summary>Documents in the order they were made.</summary>
    public List<(string Type, int Id, string Role)> Docs { get; } = [];

    public static Scene Of(Fx.Parties p)
    {
        var s = new Scene { Tag = p.Tag, Owner = Fx.OwnerB };
        s.Products.Add((p.P1, "P1"));
        s.Products.Add((p.P2, "P2"));
        s.Customers[p.Customer] = "Cust";
        s.Suppliers[p.Supplier] = "Supp";
        return s;
    }
}

/// <summary>
/// Everything an invoice writes, read with plain SQL and normalised to roles, so the .NET answer
/// can be compared with the Python app's captured answer (Golden/capture/_snapshot.py runs the
/// same SQL with the same normalisation): the documents, their lines and charges, stock_ledger,
/// stock_layers, layer_consumptions, stock_cost_adjustments, the products' figures, and every
/// journal of the documents (CADJ re-costings included) with account CODES (the test's own party
/// accounts by name), document numbers as roles and the run's tag as TAG.
/// </summary>
public static class Snapshot
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>A column value as both apps print it: exact decimals, ISO dates, True/False, "" for NULL.</summary>
    public static string V(object? v) => v switch
    {
        null or DBNull => "",
        bool b => b ? "True" : "False",
        decimal d => d.ToString("0.############################", Inv),
        DateTime t => t.TimeOfDay == TimeSpan.Zero ? t.ToString("yyyy-MM-dd", Inv) : t.ToString("yyyy-MM-ddTHH:mm:ss", Inv),
        DateOnly d => d.ToString("yyyy-MM-dd", Inv),
        IFormattable f => f.ToString(null, Inv),
        _ => v.ToString() ?? "",
    };

    private const string SiHead =
        "SELECT invoice_number, voucher_number, sales_order_id, customer_id, party_account_id, label_id, invoice_date, due_date, " +
        "voucher_status, payment_status, discount_mode, charges_mode, tax_mode, global_discount_pct, global_discount_value, " +
        "global_delivery, global_installation, global_sales_tax_pct, further_tax_pct, apply_further_tax, withholding_tax_pct, " +
        "apply_withholding_tax, subtotal, total_discount, total_charges, total_tax, total_further_tax, total_withholding_tax, " +
        "total_amount, paid_amount, notes, created_by, approved_by, approved_at, edit_version FROM inv_invoices WHERE id = @id";
    private const string SiHeadKinds = "num num v cust acct label v v v v v v v v v v v v v v v v v v v v v v v v num user user set v";

    private const string SiItems =
        "SELECT product_id, description, quantity, unit, unit_price, label_id, source_order_id, source_order_item_id, " +
        "source_order_number, discount_pct, discount_amount, delivery, installation, sales_tax_pct, total_before_discount, " +
        "total_after_discount, comments FROM inv_invoice_items WHERE invoice_id = @id ORDER BY id";
    private const string SiItemKinds = "prod num v v v label v v num v v v v v v v num";

    private const string PiHead =
        "SELECT invoice_number, voucher_number, invoice_date, supplier_id, party_account_id, label_id, driver_name, driver_contact, " +
        "vehicle_number, gate_pass, discount_mode, expenses_mode, tax_mode, global_discount_pct, global_discount_value, " +
        "global_commission, global_freight, global_loading, global_sales_tax_pct, global_withholding_tax_pct, further_tax_pct, " +
        "apply_further_tax, apply_withholding_tax, subtotal, total_discount, total_expenses, total_tax, total_further_tax, " +
        "total_withholding_tax, net_payable, total_amount, paid_amount, payment_status, purchase_order_id, notes, status, " +
        "created_by, approved_by, approved_at, edit_version FROM inv_purchase_invoices WHERE id = @id";
    private const string PiHeadKinds = "num num v supp acct label v v v v v v v v v v v v v v v v v v v v v v v v v v v v num v user user set v";

    private const string PiItems =
        "SELECT product_id, description, quantity, unit, unit_price, label_id, source_order_id, source_order_item_id, " +
        "source_order_number, discount_pct, discount_amount, commission, freight, loading_unloading, sales_tax_pct, " +
        "withholding_tax_pct, total_before_discount, total_after_discount, comments FROM inv_purchase_invoice_items " +
        "WHERE invoice_id = @id ORDER BY id";
    private const string PiItemKinds = "prod num v v v label v v num v v v v v v v v v num";

    private const string Charges =
        "SELECT charge_account_id, description, amount, scope, distribution, manual_allocations, treatment, st_taxable, " +
        "wht_taxable, extra_taxable, taxable, tax_base FROM additional_charges WHERE doc_type = @type AND doc_id = @id ORDER BY id";
    private const string ChargeKinds = "acct num v v v v v v v v v v";

    public static async Task<List<string>> TakeAsync(Scene s)
    {
        await using var db = Fx.Db();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        var pids = s.Products.Select(p => p.Id).ToArray();
        var prodRole = s.Products.ToDictionary(p => p.Id, p => p.Role);
        var prodOrder = s.Products.Select((p, i) => (p.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var docRole = s.Docs.ToDictionary(d => (d.Type, d.Id), d => d.Role);

        // Document numbers → roles (longest first, so no number is replaced inside another).
        var numbers = new List<(string Number, string Role)>();
        foreach (var (type, id, role) in s.Docs)
        {
            string? table = type switch { "SI" => "inv_invoices", "PI" => "inv_purchase_invoices", _ => null };
            if (table is null) continue;
            foreach (var row in await RowsAsync(conn, $"SELECT invoice_number, voucher_number FROM {table} WHERE id = @id", ("id", id)))
                foreach (var n in row)
                    if (n is string str && str.Length > 0 && numbers.All(x => x.Number != str))
                        numbers.Add((str, role));
        }
        numbers = numbers.OrderByDescending(n => n.Number.Length).ThenBy(n => n.Number, StringComparer.Ordinal).ToList();
        string Num(object? v)
        {
            string t = V(v);
            foreach (var (n, role) in numbers) t = t.Replace(n, role);
            t = t.Replace(s.Tag, "TAG");
            foreach (var (id, role) in s.Products) t = t.Replace($"#{id}", $"#{role}");
            return t;
        }
        string Doc(string type, int id)
        {
            if (docRole.TryGetValue((type, id), out var r)) return r;
            foreach (var d in s.Docs)
                if (d.Id == id && type == "CADJ-" + d.Type) return "CADJ-" + d.Role;
            return $"{type}#?";
        }

        // Rows first, accounts afterwards (their codes in one query).
        var raw = new List<(string Prefix, string Kinds, object?[] Row)>();
        foreach (var (type, id, role) in s.Docs)
        {
            var (head, headKinds, items, itemKinds, prefix) = type == "SI"
                ? (SiHead, SiHeadKinds, SiItems, SiItemKinds, "si")
                : (PiHead, PiHeadKinds, PiItems, PiItemKinds, "pi");
            foreach (var row in await RowsAsync(conn, head, ("id", id))) raw.Add(($"{prefix} {role}", headKinds, row));
            foreach (var row in await RowsAsync(conn, items, ("id", id))) raw.Add(($"{prefix}_item {role}", itemKinds, row));
            foreach (var row in await RowsAsync(conn, Charges, ("type", type), ("id", id))) raw.Add(($"charge {role}", ChargeKinds, row));
        }

        var ledger = await RowsAsync(conn,
            "SELECT id, product_id, voucher_type, voucher_id, transaction_type, quantity, unit_cost, total_cost, running_qty, running_cost, " +
            "running_avg, valuation_method, txn_date, voucher_number, notes, created_by FROM stock_ledger WHERE product_id = ANY(@pids)",
            ("pids", pids));
        ledger = ledger.OrderBy(r => prodOrder[(int)r[1]!]).ThenBy(r => r[12] is DateTime d ? DateOnly.FromDateTime(d) : (DateOnly)r[12]!)
            .ThenBy(r => (int)r[0]!).ToList();
        var ledgerRole = ledger.ToDictionary(r => (int)r[0]!, r => $"{prodRole[(int)r[1]!]}:{Doc((string)r[2]!, (int)r[3]!)}:{r[4]}");
        var layers = (await RowsAsync(conn,
                "SELECT id, product_id, source_ledger_id, unit_cost, qty_original, qty_remaining, value_remaining, method, is_revaluation, notes " +
                "FROM stock_layers WHERE product_id = ANY(@pids)", ("pids", pids)))
            .OrderBy(r => prodOrder[(int)r[1]!]).ThenBy(r => (int)r[0]!).ToList();
        var layerRole = new Dictionary<int, string>();
        foreach (var g in layers.GroupBy(r => (int)r[1]!))
        {
            int i = 0;
            foreach (var l in g) layerRole[(int)l[0]!] = $"{prodRole[g.Key]}:layer{i++}";
        }
        var docIds = s.Docs.Select(d => d.Id).Distinct().ToArray();
        var entries = (await RowsAsync(conn,
                "SELECT id, voucher_type, voucher_id, is_posted, entry_date, voucher_number, description, created_by FROM journal_entries " +
                "WHERE voucher_id = ANY(@ids) ORDER BY id", ("ids", docIds)))
            .Where(e => docRole.ContainsKey(((string)e[1]!, (int)e[2]!))
                        || s.Docs.Any(d => d.Id == (int)e[2]! && (string)e[1]! == "CADJ-" + d.Type))
            .ToList();
        var jeRole = entries.Select((e, i) => ((int)e[0]!, i)).ToDictionary(x => x.Item1, x => $"je{x.i}");
        var lines = await RowsAsync(conn,
            "SELECT journal_entry_id, account_id, debit, credit, label_id, description FROM journal_lines " +
            "WHERE journal_entry_id = ANY(@ids) ORDER BY id", ("ids", entries.Select(e => (int)e[0]!).ToArray()));

        var accountIds = raw.SelectMany(r => r.Kinds.Split(' ').Zip(r.Row).Where(x => x.First == "acct" && x.Second is int).Select(x => (int)x.Second!))
            .Concat(lines.Select(l => (int)l[1]!)).Distinct().ToArray();
        var accounts = (await RowsAsync(conn, "SELECT id, code, name FROM chart_of_accounts WHERE id = ANY(@ids)", ("ids", accountIds)))
            .ToDictionary(r => (int)r[0]!, r => ((string)r[1]!, (string)r[2]!));
        string Acct(object? v)
        {
            if (v is not int id) return V(v);
            var (code, name) = accounts[id];
            return name.Contains(s.Tag, StringComparison.Ordinal) ? "@" + name.Replace(s.Tag, "TAG") : code;
        }
        string Kind(string kind, object? v) => kind switch
        {
            "num" => Num(v),
            "prod" => v is int p ? prodRole.GetValueOrDefault(p, $"product#?") : V(v),
            "cust" => v is int c ? s.Customers.GetValueOrDefault(c, "customer#?") : V(v),
            "supp" => v is int c ? s.Suppliers.GetValueOrDefault(c, "supplier#?") : V(v),
            "label" => v is int l ? s.Labels.GetValueOrDefault(l, "label#?") : V(v),
            "acct" => Acct(v),
            "user" => v is int u ? (u == s.Owner ? "U" : "user#?") : V(v),
            "set" => v is null or DBNull ? "" : "set",
            _ => V(v),
        };
        string Join(string kinds, object?[] row) => string.Join("|", kinds.Split(' ').Zip(row).Select(x => Kind(x.First, x.Second)));

        var outL = raw.Select(r => $"{r.Prefix}|{Join(r.Kinds, r.Row)}").ToList();
        foreach (var r in ledger)
            outL.Add($"ledger {ledgerRole[(int)r[0]!]}|{Join("v v v v v v v v num num user", r[5..])}");
        foreach (var l in layers)
            outL.Add($"layer {layerRole[(int)l[0]!]}|{(l[2] is int sl ? ledgerRole.GetValueOrDefault(sl, "?") : "-")}|{Join("v v v v v v num", l[3..])}");
        foreach (var c in await RowsAsync(conn,
                     "SELECT layer_id, out_ledger_id, qty, unit_cost, total_cost FROM layer_consumptions WHERE product_id = ANY(@pids) ORDER BY id",
                     ("pids", pids)))
            outL.Add($"consume {layerRole.GetValueOrDefault((int)c[0]!, "?")}|{ledgerRole.GetValueOrDefault((int)c[1]!, "?")}|{Join("v v v", c[2..])}");
        foreach (var a in await RowsAsync(conn,
                     "SELECT product_id, voucher_type, voucher_id, ledger_id, old_cost, new_cost, delta, trigger, entry_date, journal_entry_id, " +
                     "voucher_number FROM stock_cost_adjustments WHERE product_id = ANY(@pids) ORDER BY id", ("pids", pids)))
            outL.Add($"cadj {prodRole[(int)a[0]!]}|{Doc((string)a[1]!, (int)a[2]!)}|{(a[3] is int li ? ledgerRole.GetValueOrDefault(li, "?") : "-")}|" +
                     $"{Join("v v v num v", a[4..9])}|{(a[9] is int je ? jeRole.GetValueOrDefault(je, "other journal") : "no journal")}|{Num(a[10])}");
        foreach (var p in (await RowsAsync(conn, "SELECT id, current_stock, cost_price FROM inv_products WHERE id = ANY(@pids)", ("pids", pids)))
                     .OrderBy(p => prodRole[(int)p[0]!], StringComparer.Ordinal))
            outL.Add($"product {prodRole[(int)p[0]!]}|{V(p[1])}|{V(p[2])}");
        foreach (var e in entries)
        {
            outL.Add($"journal {jeRole[(int)e[0]!]} {Doc((string)e[1]!, (int)e[2]!)}|{Join("v v num num user", e[3..])}");
            foreach (var l in lines.Where(l => (int)l[0]! == (int)e[0]!))
                outL.Add($"  line {Acct(l[1])}|{V(l[2])}|{V(l[3])}|{Kind("label", l[4])}|{Num(l[5])}");
        }
        return outL;
    }

    private static async Task<List<object?[]>> RowsAsync(DbConnection conn, string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }
        var rows = new List<object?[]>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (int i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }
}
