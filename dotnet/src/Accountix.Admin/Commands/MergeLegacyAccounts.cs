using Accountix.Accounting.Services;

namespace Accountix.Admin.Commands;

/// <summary>
/// tools/merge_parked_legacy_accounts.py. Before the fix to get_or_create_account, a legacy
/// flat code with no slot in the fixed chart (5710 impairment loss, 2125 / 5123 EOBI payable /
/// employer expense, 5124 gratuity, 5125 leave encashment, 5126 PF profit credited) was parked
/// under its type's catch-all head with the next free code and never found again by that code:
/// every impairment and every payroll run opened another account of the same name.
///
/// For each company, the level-5, non-fixed accounts of those names under their catch-all
/// head that share (parent, name, type) are merged into the one with the lowest id:
/// 1. every column that refers to chart_of_accounts.id is repointed to it (database foreign
///    keys, the EF model's foreign keys, and any integer *account_id column). A row that would
///    then clash with a unique key the kept account already holds is dropped and reported;
/// 2. the emptied duplicates are deleted;
/// 3. the company's reporting table (gl_daily_balances) is rebuilt.
///
/// All in one transaction. Refuses to start when Books Integrity already fails for a company
/// that has duplicates, checks every merged company again (and the posted totals of each
/// merged name) before committing, and rolls back on any difference. A second run finds nothing.
/// </summary>
public sealed class MergeLegacyAccounts : IAdminCommand
{
    public string Name => "merge-legacy-accounts";
    public string Arguments => "[--apply] [company id ...]";
    public string Summary => "Merge the duplicate accounts parked for legacy codes 5710, 2125, 5123-5126 (dry run unless --apply).";

    /// <summary>Name → the legacy code that parked it (for the report).</summary>
    public static readonly IReadOnlyList<(string Name, string Code)> Parked =
    [
        ("Impairment Loss on Fixed Assets", "5710"),
        ("EOBI Payable", "2125"),
        ("EOBI Employer Expense", "5123"),
        ("Gratuity Expense", "5124"),
        ("Leave Encashment Expense", "5125"),
        ("PF Profit Credited to Members", "5126"),
    ];

    /// <summary>Rebuilt from the journal lines afterwards instead of repointed (its key is per account and day).</summary>
    private const string Rebuilt = "gl_daily_balances";

    public sealed class Refused(string message) : Exception(message);

    public sealed record AccountMerge(string Name, string Code, int KeepId, string Kept, List<string> Merged,
                                      SortedDictionary<string, int> Moved, SortedDictionary<string, int> Dropped)
    {
        public (decimal Dr, decimal Cr) Posted { get; set; }
    }

    public sealed record CompanyMerge(string Name, List<AccountMerge> Accounts);

    public async Task<int> RunAsync(IServiceProvider services, AdminArgs args, TextWriter output, CancellationToken ct)
    {
        try
        {
            await MergeAsync(services, args.Apply, args.Companies, output.WriteLine, ct);
            return AdminExit.Ok;
        }
        catch (Refused e)
        {
            output.WriteLine($"REFUSED: {e.Message}");
            return AdminExit.Refused;
        }
    }

    /// <summary>merge(): {company id: report}. Throws <see cref="Refused"/> when nothing was written.</summary>
    public static async Task<Dictionary<int, CompanyMerge>> MergeAsync(IServiceProvider sp, bool apply, IReadOnlyCollection<int> only,
                                                                     Action<string> output, CancellationToken ct = default)
    {
        var db = sp.GetRequiredService<AccountixDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var plan = new List<(int Id, string Name, List<(ChartOfAccount Keep, List<ChartOfAccount> Dups)> Groups)>();
        foreach (var (cid, name) in await AdminSql.CompaniesAsync(db, ct))
        {
            if (only.Count > 0 && !only.Contains(cid))
                continue;
            db.Company.Set(cid);
            var groups = await DuplicateGroupsAsync(db, cid, ct);
            if (groups.Count > 0)
                plan.Add((cid, name, groups));
            else
                output($"{name} (company {cid}): no duplicate parked accounts");
        }
        if (plan.Count == 0)
        {
            output("Nothing to merge.");
            await tx.RollbackAsync(ct);
            return [];
        }

        // Refuse before writing anything when the books are already off.
        foreach (var (cid, name, _) in plan)
        {
            var bad = await AdminSql.FailuresAsync(sp, cid, ct);
            if (bad.Count > 0)
            {
                await tx.RollbackAsync(ct);
                throw new Refused($"{name} (company {cid}): Books Integrity fails before the merge — "
                                  + AdminSql.Describe(bad) + ". Fix that first; nothing was changed.");
            }
        }

        var refs = await ReferencesAsync(db, ct);
        var uniques = new Dictionary<(string, string), List<string[]>>();
        var report = new Dictionary<int, CompanyMerge>();
        foreach (var (cid, name, groups) in plan)
        {
            db.Company.Set(cid);
            var lines = new List<AccountMerge>();
            var before = new Dictionary<int, (decimal, decimal)>();
            foreach (var (keep, dups) in groups)
            {
                before[keep.Id] = await PostedTotalsAsync(db, [keep.Id, .. dups.Select(d => d.Id)], ct);
                var moved = new SortedDictionary<string, int>(StringComparer.Ordinal);
                var dropped = new SortedDictionary<string, int>(StringComparer.Ordinal);
                foreach (var d in dups)
                {
                    foreach (var (table, column) in refs)
                    {
                        if (!uniques.TryGetValue((table, column), out var sets))
                            uniques[(table, column)] = sets = await UniqueSetsAsync(db, table, column, ct);
                        string t = AdminSql.Quote(table), c = AdminSql.Quote(column);
                        foreach (var others in sets)
                        {
                            // The kept account already holds this key: the duplicate's row goes.
                            string cond = string.Join(" AND ", others.Select(o => $"t2.{AdminSql.Quote(o)} = {t}.{AdminSql.Quote(o)}"));
                            int n = await db.ExecAsync($"DELETE FROM {t} WHERE {c} = @p0 AND EXISTS " +
                                                       $"(SELECT 1 FROM {t} t2 WHERE t2.{c} = @p1 AND {cond})", ct, d.Id, keep.Id);
                            if (n > 0)
                                dropped[$"{table}.{column}"] = dropped.GetValueOrDefault($"{table}.{column}") + n;
                        }
                        int m = await db.ExecAsync($"UPDATE {t} SET {c} = @p0 WHERE {c} = @p1", ct, keep.Id, d.Id);
                        if (m > 0)
                            moved[$"{table}.{column}"] = moved.GetValueOrDefault($"{table}.{column}") + m;
                    }
                    await db.ExecAsync("DELETE FROM chart_of_accounts WHERE id = @p0 AND company_id = @p1", ct, d.Id, cid);
                }
                string code = Parked.First(p => p.Name == keep.Name).Code;
                lines.Add(new AccountMerge(keep.Name, code, keep.Id, $"{keep.Code} (#{keep.Id})",
                                           dups.Select(x => $"{x.Code} (#{x.Id})").ToList(), moved, dropped));
            }
            await GlSummary.RebuildAsync(db, cid, ct);
            db.ChangeTracker.Clear();
            foreach (var g in lines)
            {
                var after = await PostedTotalsAsync(db, [g.KeepId], ct);
                if (after != before[g.KeepId])
                {
                    await tx.RollbackAsync(ct);
                    throw new Refused($"{name}: {g.Name} posted {Pair(before[g.KeepId])} before, {Pair(after)} after; rolled back.");
                }
                g.Posted = after;
            }
            report[cid] = new CompanyMerge(name, lines);
        }

        foreach (var (cid, r) in report)
        {
            var bad = await AdminSql.FailuresAsync(sp, cid, ct);
            if (bad.Count > 0)
            {
                await tx.RollbackAsync(ct);
                throw new Refused($"{r.Name} (company {cid}): Books Integrity fails after the merge — "
                                  + AdminSql.Describe(bad) + ". Rolled back; nothing was changed.");
            }
        }

        foreach (var (cid, r) in report)
        {
            output($"{r.Name} (company {cid}):");
            foreach (var g in r.Accounts)
            {
                output($"  {g.Name} [{g.Code}]: keep {g.Kept}, merge {string.Join(", ", g.Merged)}; " +
                       $"posted Dr {AdminSql.Money(g.Posted.Dr)} Cr {AdminSql.Money(g.Posted.Cr)}");
                foreach (var (k, n) in g.Moved)
                    output($"    repointed {n} row(s) of {k}");
                foreach (var (k, n) in g.Dropped)
                    output($"    dropped {n} row(s) of {k} the kept account already had");
            }
            output("  gl_daily_balances rebuilt; Books Integrity passes");
        }
        if (apply)
        {
            await tx.CommitAsync(ct);
            output("COMMITTED");
        }
        else
        {
            await tx.RollbackAsync(ct);
            output("Dry run — rolled back. Re-run with --apply to commit.");
        }
        return report;
    }

    private static string Pair((decimal Dr, decimal Cr) v) => $"({AdminSql.Num(v.Dr)}, {AdminSql.Num(v.Cr)})";

    /// <summary>duplicate_groups: [(keep, duplicates)] of parked accounts sharing (parent, name, type), lowest id kept.</summary>
    internal static async Task<List<(ChartOfAccount Keep, List<ChartOfAccount> Dups)>> DuplicateGroupsAsync(
        AccountixDbContext db, int cid, CancellationToken ct)
    {
        var headCodes = Coa.CatchallParentCodes.Values.ToList();
        var heads = new Dictionary<string, int>();
        foreach (var h in await db.Set<ChartOfAccount>().AsNoTracking()
                     .Where(a => a.CompanyId == cid && headCodes.Contains(a.Code)).OrderBy(a => a.Id).ToListAsync(ct))
            heads[h.Code] = h.Id;
        var names = Parked.Select(p => p.Name).ToList();
        var rows = await db.Set<ChartOfAccount>().AsNoTracking()
            .Where(a => a.CompanyId == cid && a.Level == 5 && names.Contains(a.Name) && (a.IsFixed == null || a.IsFixed == false))
            .OrderBy(a => a.Id).ToListAsync(ct);
        var groups = new List<((int?, string, string) Key, List<ChartOfAccount> Accounts)>();
        foreach (var a in rows)
        {
            int? head = heads.TryGetValue(Coa.CatchallParentCodes.GetValueOrDefault(a.Type, "5-04-01-01"), out int h) ? h : null;
            if (a.ParentId != head)
                continue;
            var key = (a.ParentId, a.Name, a.Type);
            int i = groups.FindIndex(g => g.Key == key);
            if (i < 0)
                groups.Add((key, [a]));
            else
                groups[i].Accounts.Add(a);
        }
        return groups.Where(g => g.Accounts.Count > 1).Select(g => (g.Accounts[0], g.Accounts.Skip(1).ToList())).ToList();
    }

    /// <summary>
    /// references(): every (table, column) that holds a chart_of_accounts id — database foreign keys,
    /// the EF model's foreign keys, and any integer column named *account_id — minus gl_daily_balances.
    /// </summary>
    internal static async Task<List<(string Table, string Column)>> ReferencesAsync(AccountixDbContext db, CancellationToken ct)
    {
        var columns = (await db.RowsAsync(
                "SELECT c.table_name, c.column_name, c.data_type FROM information_schema.columns c " +
                "JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name " +
                "WHERE c.table_schema = current_schema() AND t.table_type = 'BASE TABLE'", ct))
            .Select(r => (Table: (string)r[0]!, Column: (string)r[1]!, Type: (string)r[2]!)).ToList();
        var exists = columns.Select(c => (c.Table, c.Column)).ToHashSet();
        var found = new List<(string, string)>();
        void Add(string table, string column)
        {
            if (table != Rebuilt && exists.Contains((table, column)) && !found.Contains((table, column)))
                found.Add((table, column));
        }

        foreach (var r in await db.RowsAsync(
                     "SELECT t.relname, a.attname FROM pg_constraint k " +
                     "JOIN pg_class t ON t.oid = k.conrelid JOIN pg_namespace n ON n.oid = t.relnamespace " +
                     "JOIN pg_attribute a ON a.attrelid = k.conrelid AND a.attnum = k.conkey[1] " +
                     "JOIN pg_attribute ra ON ra.attrelid = k.confrelid AND ra.attnum = k.confkey[1] " +
                     "WHERE k.contype = 'f' AND n.nspname = current_schema() AND k.confrelid = 'chart_of_accounts'::regclass " +
                     "AND cardinality(k.conkey) = 1 AND ra.attname = 'id' ORDER BY t.relname, k.conname", ct))
            Add((string)r[0]!, (string)r[1]!);

        foreach (var et in db.Model.GetEntityTypes().OrderBy(e => e.GetTableName(), StringComparer.Ordinal))
            foreach (var fk in et.GetForeignKeys())
                if (fk.PrincipalEntityType.ClrType == typeof(ChartOfAccount) && fk.Properties.Count == 1
                    && fk.PrincipalKey.Properties.Single().Name == nameof(ChartOfAccount.Id) && et.GetTableName() is { } table)
                    Add(table, fk.Properties[0].GetColumnName());

        foreach (var c in columns.Where(c => c.Column.EndsWith("account_id", StringComparison.Ordinal)
                                             && c.Type is "integer" or "bigint" or "smallint")
                                 .OrderBy(c => c.Table, StringComparer.Ordinal).ThenBy(c => c.Column, StringComparer.Ordinal))
            Add(c.Table, c.Column);
        return found;
    }

    /// <summary>unique_sets(): the other columns of each unique key (primary key, unique constraint or index) of the table that includes the column.</summary>
    internal static async Task<List<string[]>> UniqueSetsAsync(AccountixDbContext db, string table, string column, CancellationToken ct)
    {
        var sets = new List<string[]>();
        foreach (var r in await db.RowsAsync(
                     "SELECT ARRAY(SELECT a.attname::text FROM unnest(i.indkey::int2[]) WITH ORDINALITY k(attnum, ord) " +
                     "             JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum ORDER BY k.ord), " +
                     "       0 = ANY(i.indkey::int2[]) " +
                     "FROM pg_index i JOIN pg_class t ON t.oid = i.indrelid JOIN pg_class ix ON ix.oid = i.indexrelid " +
                     "JOIN pg_namespace n ON n.oid = t.relnamespace " +
                     "WHERE n.nspname = current_schema() AND t.relname = @p0 AND i.indisunique " +
                     "ORDER BY i.indisprimary DESC, ix.relname", ct, table))
        {
            if (r[1] is true)
                continue;       // an expression index (its column names are unknown)
            var cols = (string[])r[0]!;
            if (!cols.Contains(column))
                continue;
            var others = cols.Where(c => c != column).ToArray();
            if (others.Length > 0 && !sets.Any(s => s.SequenceEqual(others)))
                sets.Add(others);
        }
        return sets;
    }

    /// <summary>posted_totals(): posted (debit, credit) of these accounts, whatever the company, to 2 dp.</summary>
    internal static async Task<(decimal Dr, decimal Cr)> PostedTotalsAsync(AccountixDbContext db, int[] accountIds, CancellationToken ct)
    {
        var r = (await db.RowsAsync(
            "SELECT COALESCE(SUM(l.debit), 0), COALESCE(SUM(l.credit), 0) FROM journal_lines l " +
            "JOIN journal_entries e ON l.journal_entry_id = e.id WHERE e.is_posted = TRUE AND l.account_id = ANY(@p0)",
            ct, accountIds))[0];
        return (Math.Round(AdminSql.Dec(r[0]), 2), Math.Round(AdminSql.Dec(r[1]), 2));
    }
}
