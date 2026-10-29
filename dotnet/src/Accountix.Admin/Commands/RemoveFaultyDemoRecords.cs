using Accountix.Accounting.Services;

namespace Accountix.Admin.Commands;

/// <summary>
/// tools/remove_faulty_demo_records.py: remove the three faulty July-2026 demo records from
/// Default Company (id 1), found on the live database on 15 Oct 2026 (Books Integrity: Default
/// Company 9/11, every other company 11/11; on a copy, every company passes 11/11 afterwards):
/// 1. journal entry BRV-00001: unposted, both lines debits, its voucher is gone;
/// 2. debit note DN-202607-0001: "approved" with no stock movement and no journal;
/// 3. purchase invoice PINV-202607-0001: Inventory debited 105,600 while its stock layers hold
///    92,250 (the old discount-sign bug); nothing drew on it.
///
/// Every record is checked to be exactly what was found before anything is deleted; any surprise
/// aborts the whole transaction. Raw SQL, as Python's (no audit rows). Report totals
/// (gl_daily_balances) and on-hand quantities for company 1 are rebuilt afterwards. Books Integrity
/// for company 1 is shown before and after; a check that passed before and fails after rolls it all back.
/// </summary>
public sealed class RemoveFaultyDemoRecords : IAdminCommand
{
    public const int CompanyId = 1;

    public string Name => "remove-faulty-demo-records";
    public string Arguments => "[--apply]";
    public string Summary => "Remove the three faulty July-2026 demo records of Default Company (dry run unless --apply).";

    private sealed class Abort(string message) : Exception(message);

    private static void Expect(bool ok, string message)
    {
        if (!ok)
            throw new Abort(message);
    }

    public async Task<int> RunAsync(IServiceProvider sp, AdminArgs args, TextWriter output, CancellationToken ct)
    {
        if (args.Companies.Count > 0)
        {
            output.WriteLine($"{Name} works on company {CompanyId} only; it takes no company id.");
            return AdminExit.Usage;
        }
        var db = sp.GetRequiredService<AccountixDbContext>();
        db.Company.Set(CompanyId);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await RemoveAsync(sp, db, output, ct);
        }
        catch (Abort e)
        {
            await tx.RollbackAsync(ct);
            output.WriteLine($"ABORT: {e.Message}");
            return AdminExit.Refused;
        }
        if (args.Apply)
        {
            await tx.CommitAsync(ct);
            output.WriteLine("COMMITTED");
        }
        else
        {
            await tx.RollbackAsync(ct);
            output.WriteLine("dry run - rolled back (add --apply to commit)");
        }
        return AdminExit.Ok;
    }

    private static async Task RemoveAsync(IServiceProvider sp, AccountixDbContext db, TextWriter output, CancellationToken ct)
    {
        const int c = CompanyId;
        var comp = await db.RowsAsync("select name from companies where id = @p0", ct, c);
        Expect(comp.Count == 1 && (string?)comp[0][0] == "Default Company",
               $"company {c} is {(comp.Count == 0 ? "missing" : $"'{comp[0][0]}'")}");

        // 1 ── orphan BRV journal
        var je1 = await db.RowsAsync("select id, voucher_type, voucher_number, is_posted, voucher_id from journal_entries " +
                                     "where company_id = @p0 and voucher_type = 'BRV' and voucher_number = 'BRV-00001'", ct, c);
        Expect(je1.Count == 1 && je1[0][3] is false, $"BRV-00001 journal: {Rows(je1)}");
        Expect((await db.RowsAsync("select 1 from accounting_vouchers where id = @p0", ct, je1[0][4])).Count == 0,
               "BRV-00001's voucher exists after all");
        int je1Id = (int)je1[0][0]!;

        // 2 ── debit note
        var pr = await db.RowsAsync("select id, original_invoice_id, status from inv_purchase_returns " +
                                    "where company_id = @p0 and return_number = 'DN-202607-0001'", ct, c);
        Expect(pr.Count == 1 && (string?)pr[0][2] == "approved", $"DN-202607-0001: {Rows(pr)}");
        int prId = (int)pr[0][0]!;
        Expect((await db.RowsAsync("select 1 from journal_entries where voucher_type = 'PR' and voucher_id = @p0", ct, prId)).Count == 0,
               "DN-202607-0001 has a journal");
        Expect((await db.RowsAsync("select 1 from stock_ledger where voucher_type = 'PRV' and voucher_id = @p0", ct, prId)).Count == 0,
               "DN-202607-0001 has stock rows");

        // 3 ── purchase invoice
        var pi = await db.RowsAsync("select id, status, purchase_order_id from inv_purchase_invoices " +
                                    "where company_id = @p0 and invoice_number = 'PINV-202607-0001'", ct, c);
        Expect(pi.Count == 1 && (string?)pi[0][1] == "approved" && pi[0][2] is null, $"PI: {Rows(pi)}");
        int piId = (int)pi[0][0]!;
        Expect(pr[0][1] is int orig && orig == piId, "the debit note is not against this invoice");
        var others = await db.RowsAsync("select return_number from inv_purchase_returns where original_invoice_id = @p0 and id <> @p1",
                                        ct, piId, prId);
        Expect(others.Count == 0, $"other returns against the PI: {Rows(others)}");
        var piJe = await db.RowsAsync("select id, is_posted from journal_entries where voucher_type = 'PI' and voucher_id = @p0 " +
                                      "and company_id = @p1", ct, piId, c);
        Expect(piJe.Count == 1, $"PI journals: {Rows(piJe)}");
        var rows = await db.RowsAsync("select id, product_id, transaction_type, total_cost from stock_ledger " +
                                      "where voucher_type = 'PI' and voucher_id = @p0 and company_id = @p1 order by id", ct, piId, c);
        Expect(rows.Count == 2 && rows.All(r => (string?)r[2] == "IN"), $"PI stock rows: {Rows(rows)}");
        int[] rowIds = rows.Select(r => (int)r[0]!).ToArray();
        int[] prods = rows.Select(r => (int)r[1]!).Distinct().Order().ToArray();
        int[] layers = (await db.RowsAsync("select id from stock_layers where source_ledger_id = any(@p0) order by id", ct, rowIds))
            .Select(r => (int)r[0]!).ToArray();
        if (layers.Length > 0)
            Expect(Convert.ToInt64(await db.ScalarAsync("select count(*) from layer_consumptions where layer_id = any(@p0)", ct, layers)) == 0,
                   "stock from the PI was issued");
        Expect(Convert.ToInt64(await db.ScalarAsync(
                   "select count(*) from stock_ledger where product_id = any(@p0) and not (voucher_type = 'PI' and voucher_id = @p1)",
                   ct, prods, piId)) == 0,
               "the PI's products have other movements");
        Expect((await db.RowsAsync("select 1 from payment_allocations where doc_type = 'PI' and doc_id = @p0", ct, piId)).Count == 0,
               "payments are assigned to the PI");
        int piJeId = (int)piJe[0][0]!;

        output.WriteLine($"found: BRV journal {je1Id} | DN {prId} | PI {piId} journal {piJeId} stock rows {AdminSql.List(rowIds)} " +
                         $"layers {AdminSql.List(layers)} products {AdminSql.List(prods)}");
        var before = await AdminSql.FailuresAsync(sp, c, ct);
        output.WriteLine(before.Count == 0 ? "Books Integrity before: passes"
                                           : $"Books Integrity before: {before.Count} failing — {AdminSql.Describe(before)}");

        // ── delete
        int[] jids = [je1Id, piJeId];
        var n = new List<(string Key, int Count)>
        {
            ("journal_lines", await db.ExecAsync("delete from journal_lines where journal_entry_id = any(@p0)", ct, jids)),
            ("journal_entries", await db.ExecAsync("delete from journal_entries where id = any(@p0) and company_id = @p1", ct, jids, c)),
            ("pr_items", await db.ExecAsync("delete from inv_purchase_return_items where return_id = @p0", ct, prId)),
            ("pr", await db.ExecAsync("delete from inv_purchase_returns where id = @p0 and company_id = @p1", ct, prId, c)),
        };
        if (layers.Length > 0)
            n.Add(("stock_layers", await db.ExecAsync("delete from stock_layers where id = any(@p0)", ct, layers)));
        n.Add(("stock_ledger", await db.ExecAsync("delete from stock_ledger where id = any(@p0)", ct, rowIds)));
        n.Add(("stock_movements", await db.ExecAsync(
            "delete from inv_stock_movements where reference_type = 'purchase_invoice' and reference_id = @p0 and company_id = @p1",
            ct, piId, c)));
        bool charges = await db.ScalarAsync("select to_regclass('additional_charges') is not null", ct) is true;
        n.Add(("charges", charges ? await db.ExecAsync("delete from additional_charges where doc_type = 'PI' and doc_id = @p0", ct, piId) : 0));
        n.Add(("pi_items", await db.ExecAsync("delete from inv_purchase_invoice_items where invoice_id = @p0", ct, piId)));
        n.Add(("pi", await db.ExecAsync("delete from inv_purchase_invoices where id = @p0 and company_id = @p1", ct, piId, c)));
        foreach (int p in prods)
            await db.ExecAsync(
                "update inv_products set current_stock = (select coalesce(sum(case when s.transaction_type = 'IN' " +
                "then s.quantity else -s.quantity end), 0) from stock_ledger s where s.product_id = @p0) where id = @p0", ct, p);

        // report totals for company 1, from its remaining posted lines
        await GlSummary.RebuildAsync(db, c, ct);
        output.WriteLine("deleted: {" + string.Join(", ", n.Select(x => $"'{x.Key}': {x.Count}")) + "}");
        int Count(string key) => n.First(x => x.Key == key).Count;
        Expect(Count("journal_entries") == 2 && Count("pr") == 1 && Count("pi") == 1, "unexpected delete counts");

        long left = Convert.ToInt64(await db.ScalarAsync("select count(*) from journal_entries where company_id = @p0", ct, c));
        decimal invGl = AdminSql.Dec(await db.ScalarAsync(
            "select coalesce(sum(debit) - sum(credit), 0) from gl_daily_balances g join chart_of_accounts a on a.id = g.account_id " +
            "where g.company_id = @p0 and a.code like '1-01-04%'", ct, c));
        decimal stock = AdminSql.Dec(await db.ScalarAsync(
            "select coalesce(sum(case when transaction_type = 'IN' then total_cost else -total_cost end), 0) from stock_ledger " +
            "where company_id = @p0", ct, c));
        output.WriteLine($"company {c} after: journals {left}, inventory GL {AdminSql.Num(invGl)}, stock value {AdminSql.Num(stock)}");

        var after = await AdminSql.FailuresAsync(sp, c, ct);
        var beforeKeys = before.Select(f => f.Key).ToHashSet();
        var worse = after.Where(f => !beforeKeys.Contains(f.Key)).ToList();
        Expect(worse.Count == 0, "Books Integrity would fail after the removal — " + AdminSql.Describe(worse));
        output.WriteLine(after.Count == 0 ? "Books Integrity after: passes"
                                          : $"Books Integrity after: {after.Count} still failing — {AdminSql.Describe(after)}");
    }

    /// <summary>Python's repr of fetchall(): [(1, 'BRV', None), ('x',)].</summary>
    private static string Rows(List<object?[]> rows) =>
        "[" + string.Join(", ", rows.Select(r => "(" + string.Join(", ", r.Select(v => v switch
        {
            null => "None",
            bool b => b ? "True" : "False",
            string s => $"'{s}'",
            _ => Convert.ToString(v, CultureInfo.InvariantCulture),
        })) + (r.Length == 1 ? ",)" : ")"))) + "]";
}
