using System.Data.Common;
using System.Globalization;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Inventory.Tests;

/// <summary>
/// What a stock voucher, a stock take or a product wrote, read with plain SQL that turns every value into
/// text inside Postgres, so this file and its Python twin (Golden/capture/_dump.py, the same SQL) print the
/// same characters. Then normalised: the test's products → roles (their own ledger accounts
/// "1-01-04-01-&lt;role&gt;"), generated numbers and the run's tag → roles, dates → days from the anchor
/// (today, the test's own dates being "n days ago").
/// </summary>
internal sealed class GoldenDump(int company, int owner, DateOnly anchor)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly List<(int Id, string Role)> _products = [];
    private readonly List<(string Find, string Role)> _texts = [];

    public GoldenDump Product(int id, string role) { _products.Add((id, role)); return this; }

    public GoldenDump Text(string find, string role) { if (!string.IsNullOrEmpty(find)) _texts.Add((find, role)); return this; }

    private string Role(string column) =>
        $"CASE {column} {string.Join(" ", _products.Select(p => $"WHEN {p.Id} THEN '{p.Role}'"))} ELSE 'other' END";

    private string Approver(string column) => $"CASE WHEN {column} IS NULL THEN '' WHEN {column} = {owner} THEN 'owner_b' ELSE 'other' END";

    private const string Label = "CASE WHEN label_id IS NULL THEN '' ELSE 'label' END";

    /// <summary>The voucher (CONS, SCRAP or ADJ), its items, its stock rows and its journals.</summary>
    public async Task<List<string>> VoucherAsync(string vtype, int id)
    {
        var lines = new List<string>();
        var (head, items) = vtype switch
        {
            "CONS" => ($"SELECT v.voucher_number, v.status, to_char(v.date, 'YYYY-MM-DD HH24:MI:SS'), coalesce(v.department, ''), coalesce(v.reason, ''), " +
                       $"coalesce(a.code, ''), CASE WHEN v.label_id IS NULL THEN '' ELSE 'label' END, {Approver("v.approved_by")} " +
                       $"FROM consumption_vouchers v LEFT JOIN chart_of_accounts a ON a.id = v.charge_account_id WHERE v.id = {id}",
                       $"SELECT {Role("product_id")}, coalesce(product_name, ''), quantity::text, unit_cost::text, total_cost::text " +
                       $"FROM consumption_items WHERE voucher_id = {id} ORDER BY id"),
            "SCRAP" => ($"SELECT v.voucher_number, v.status, to_char(v.date, 'YYYY-MM-DD HH24:MI:SS'), coalesce(v.reason, ''), " +
                        $"coalesce(a.code, ''), CASE WHEN v.label_id IS NULL THEN '' ELSE 'label' END, {Approver("v.approved_by")} " +
                        $"FROM scrap_vouchers v LEFT JOIN chart_of_accounts a ON a.id = v.charge_account_id WHERE v.id = {id}",
                        $"SELECT {Role("product_id")}, coalesce(product_name, ''), quantity::text, unit_cost::text, total_cost::text " +
                        $"FROM scrap_items WHERE voucher_id = {id} ORDER BY id"),
            "ADJ" => ($"SELECT voucher_number, status, to_char(date, 'YYYY-MM-DD HH24:MI:SS'), coalesce(reason, ''), {Label}, {Approver("approved_by")} " +
                      $"FROM stock_adjustment_vouchers WHERE id = {id}",
                      $"SELECT {Role("product_id")}, coalesce(product_name, ''), system_qty::text, physical_qty::text, difference::text, " +
                      $"unit_cost::text, total_cost::text FROM stock_adjustment_items WHERE voucher_id = {id} ORDER BY id"),
            _ => throw new ArgumentException(vtype),
        };
        var h = (await RowsAsync(head)).Single();
        Text(h[0], "N");
        lines.Add($"voucher {vtype}|" + string.Join("|", h.Skip(1)));
        lines.AddRange((await RowsAsync(items)).Select(r => "item " + string.Join("|", r)));
        lines.AddRange(await StockAsync(vtype, id));
        lines.AddRange(await JournalsAsync(vtype, id));
        return Norm(lines);
    }

    /// <summary>The stock take, its items, and the adjustment voucher it posted.</summary>
    public async Task<List<string>> StockTakeAsync(int id)
    {
        var t = (await RowsAsync($"SELECT reference, status, to_char(date, 'YYYY-MM-DD HH24:MI:SS'), coalesce(location, ''), " +
                                 $"{Approver("approved_by")}, coalesce(adjustment_voucher_id::text, '') FROM stock_takes WHERE id = {id}")).Single();
        Text(t[0], "R");
        var lines = new List<string> { "take " + string.Join("|", t[1..5]) };
        lines.AddRange((await RowsAsync($"SELECT {Role("product_id")}, coalesce(product_name, ''), system_qty::text, physical_qty::text, " +
                                        $"difference::text, unit_cost::text FROM stock_take_items WHERE stock_take_id = {id} ORDER BY id"))
            .Select(r => "take-item " + string.Join("|", r)));
        if (t[5] != "")
            lines.AddRange(await VoucherAsync("ADJ", int.Parse(t[5], Inv)));
        return Norm(lines);
    }

    /// <summary>The product's row and its own ledger account.</summary>
    public async Task<List<string>> ProductAsync(int id)
    {
        var lines = (await RowsAsync($"SELECT sku, name, coalesce(description, ''), coalesce(category_id::text, ''), unit_price::text, cost_price::text, " +
                                     $"reorder_level::text, current_stock::text, coalesce(unit, ''), coalesce(hs_code, ''), coalesce(weight::text, ''), " +
                                     $"coalesce(is_active::text, '') FROM inv_products WHERE id = {id}"))
            .Select(r => "product " + string.Join("|", r)).ToList();
        lines.AddRange((await RowsAsync($"SELECT a.code, a.name, a.type, a.level::text, coalesce(p.code, ''), coalesce(a.is_active::text, ''), " +
                                        $"coalesce(a.is_fixed::text, '') FROM chart_of_accounts a LEFT JOIN chart_of_accounts p ON p.id = a.parent_id " +
                                        $"WHERE a.company_id = {company} AND a.code = '1-01-04-01-' || lpad(({id} + 100)::text, 4, '0')"))
            .Select(r => "account " + string.Join("|", r)));
        return Norm(lines);
    }

    /// <summary>The product's stock rows and layers (all of them), and its opening-stock journal.</summary>
    public async Task<List<string>> ProductStockAsync(int id)
    {
        var lines = (await RowsAsync(StockSql($"product_id = {id}"))).Select(r => "row " + string.Join("|", r)).ToList();
        lines.AddRange((await RowsAsync($"SELECT unit_cost::text, qty_original::text, qty_remaining::text, value_remaining::text, method, " +
                                        $"coalesce(is_revaluation::text, ''), coalesce(notes, '') FROM stock_layers WHERE product_id = {id} ORDER BY id"))
            .Select(r => "layer " + string.Join("|", r)));
        lines.AddRange(await JournalsAsync("OPENING", id));
        return Norm(lines);
    }

    private string StockSql(string where) =>
        $"SELECT {Role("product_id")}, voucher_type, transaction_type, quantity::text, unit_cost::text, total_cost::text, running_qty::text, " +
        $"running_cost::text, running_avg::text, valuation_method, coalesce(txn_date::text, ''), voucher_number, coalesce(notes, '') " +
        $"FROM stock_ledger WHERE company_id = {company} AND {where} ORDER BY id";

    public async Task<List<string>> StockAsync(string vtype, int id) =>
        (await RowsAsync(StockSql($"voucher_type = '{vtype}' AND voucher_id = {id}"))).Select(r => "row " + string.Join("|", r)).ToList();

    public async Task<List<string>> JournalsAsync(string vtype, int id)
    {
        var lines = new List<string>();
        foreach (var e in await RowsAsync($"SELECT id::text, voucher_type, voucher_number, coalesce(description, ''), " +
                                          $"to_char(entry_date, 'YYYY-MM-DD HH24:MI:SS'), coalesce(is_posted::text, '') FROM journal_entries " +
                                          $"WHERE company_id = {company} AND voucher_type = '{vtype}' AND voucher_id = {id} ORDER BY id"))
        {
            lines.Add("journal " + string.Join("|", e.Skip(1)));
            lines.AddRange((await RowsAsync($"SELECT a.code, l.debit::text, l.credit::text, coalesce(l.description, ''), " +
                                            $"CASE WHEN l.label_id IS NULL THEN '' ELSE 'label' END FROM journal_lines l " +
                                            $"JOIN chart_of_accounts a ON a.id = l.account_id WHERE l.journal_entry_id = {e[0]} ORDER BY l.id"))
                .Select(r => "  line " + string.Join("|", r)));
        }
        return lines;
    }

    /// <summary>Product accounts, then numbers / tags (longest first), then dates.</summary>
    public List<string> Norm(IEnumerable<string> lines)
    {
        var texts = _texts.OrderByDescending(t => t.Find.Length).ToList();
        return lines.Select(l =>
        {
            foreach (var (pid, role) in _products)
                l = l.Replace($"1-01-04-01-{pid + 100:D4}", $"1-01-04-01-<{role}>");
            foreach (var (find, role) in texts)
                l = l.Replace(find, role);
            return Golden.RelDates(l, anchor);
        }).ToList();
    }

    public static async Task<List<string[]>> RowsAsync(string sql)
    {
        await using var db = Reference.NewDb();
        DbConnection conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var rows = new List<string[]>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var row = new string[r.FieldCount];
            for (int i = 0; i < r.FieldCount; i++)
                row[i] = r.IsDBNull(i) ? "" : r.GetString(i);
            rows.Add(row);
        }
        return rows;
    }
}
