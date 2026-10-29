using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Accountix.Accounting.Services;

/// <summary>
/// Keeps <c>gl_daily_balances</c> in step with the general ledger, inside the
/// same transaction as every posting (shared/gl_summary.py).
///
/// Before a save it notes every journal entry whose lines or posted state are
/// about to change and reads what those entries contribute to each
/// (company, account, label, day) key; after the save it reads them again and
/// adds the difference with one INSERT … ON CONFLICT DO UPDATE SET debit =
/// debit + …, keys in sorted order, then removes keys no line reaches any
/// more. Adding a difference commutes, so two users posting to one key at
/// once both land (the second waits on the row), and Python and .NET
/// postings add up on the same rows.
///
/// Registered as an EF interceptor for every <see cref="AccountixDbContext"/>
/// the app builds, so a save that touches journal_entries / journal_lines
/// from any module is covered. A context built without it (a bare test
/// context) is covered by <see cref="SaveChangesAsync"/>, which the ledger
/// services use for every save.
///
/// Bulk ExecuteUpdate / ExecuteDelete on the journal tables bypass a save;
/// call <see cref="RebuildAsync"/> after one (Python's do_orm_execute hook).
/// </summary>
public static class GlSummary
{
    public sealed record Key(int CompanyId, int AccountId, int LabelId, DateOnly Day) : IComparable<Key>
    {
        public int CompareTo(Key? o)
        {
            if (o is null) return 1;
            int c = CompanyId.CompareTo(o.CompanyId);
            if (c != 0) return c;
            c = AccountId.CompareTo(o.AccountId);
            if (c != 0) return c;
            c = LabelId.CompareTo(o.LabelId);
            return c != 0 ? c : Day.CompareTo(o.Day);
        }
    }

    public sealed class Amounts
    {
        public decimal Debit;
        public decimal Credit;
        public int Lines;
    }

    /// <summary>What a pending save will change: entry ids known now, and new rows whose entry id the save assigns.</summary>
    public sealed class Pending
    {
        internal HashSet<int> Ids { get; } = [];
        internal List<EntityEntry> New { get; } = [];
        internal Dictionary<Key, Amounts> Before { get; set; } = [];
        internal IDbContextTransaction? OwnTransaction { get; set; }
        public bool IsEmpty => Ids.Count == 0 && New.Count == 0;
    }

    /// <summary>_touched_entry_ids + the "before" read. Null when the save touches no journal.</summary>
    public static async Task<Pending?> CaptureAsync(DbContext db, bool ensureTransaction, CancellationToken ct = default)
    {
        db.ChangeTracker.DetectChanges();
        var p = new Pending();
        foreach (var e in db.ChangeTracker.Entries())
        {
            if (e.State is EntityState.Unchanged or EntityState.Detached)
                continue;
            if (e.Entity is JournalLine)
            {
                var prop = e.Property(nameof(JournalLine.JournalEntryId));
                int current = (int)(prop.CurrentValue ?? 0);
                if (e.State == EntityState.Added)
                {
                    if (current > 0 && !prop.IsTemporary)
                        p.Ids.Add(current);
                    else
                        p.New.Add(e);
                }
                else
                {
                    if (current > 0 && !prop.IsTemporary) p.Ids.Add(current);
                    else p.New.Add(e);
                    int original = (int)(prop.OriginalValue ?? 0);
                    if (original > 0) p.Ids.Add(original);
                }
            }
            else if (e.Entity is JournalEntry)
            {
                var idProp = e.Property(nameof(JournalEntry.Id));
                int id = (int)(idProp.CurrentValue ?? 0);
                if (e.State == EntityState.Added || idProp.IsTemporary || id <= 0)
                    p.New.Add(e);
                else
                    p.Ids.Add(id);
            }
        }
        if (p.IsEmpty)
            return null;
        if (ensureTransaction && db.Database.CurrentTransaction is null)
            p.OwnTransaction = await db.Database.BeginTransactionAsync(ct);
        else
            await db.Database.OpenConnectionAsync(ct);
        p.Before = p.Ids.Count > 0 ? await ContributionsAsync(db, p.Ids, ct) : [];
        return p;
    }

    /// <summary>The "after" read, the difference added to the summary, and the commit of a transaction the capture opened.</summary>
    public static async Task ApplyAsync(DbContext db, Pending p, CancellationToken ct = default)
    {
        var ids = new HashSet<int>(p.Ids);
        foreach (var e in p.New)
        {
            object? v = e.Entity is JournalLine
                ? e.Property(nameof(JournalLine.JournalEntryId)).CurrentValue
                : e.Property(nameof(JournalEntry.Id)).CurrentValue;
            if (v is int id && id > 0)
                ids.Add(id);
        }
        var after = ids.Count > 0 ? await ContributionsAsync(db, ids, ct) : [];
        var delta = new Dictionary<Key, Amounts>();
        foreach (var (k, v) in after)
            Add(delta, k, v.Debit, v.Credit, v.Lines);
        foreach (var (k, v) in p.Before)
            Add(delta, k, -v.Debit, -v.Credit, -v.Lines);
        await ApplyDeltaAsync(db, delta, ct);
        if (p.OwnTransaction is not null)
        {
            await p.OwnTransaction.CommitAsync(ct);
            await p.OwnTransaction.DisposeAsync();
            p.OwnTransaction = null;
        }
    }

    public static async Task AbandonAsync(Pending p, CancellationToken ct = default)
    {
        if (p.OwnTransaction is not null)
        {
            await p.OwnTransaction.RollbackAsync(ct);
            await p.OwnTransaction.DisposeAsync();
            p.OwnTransaction = null;
        }
    }

    /// <summary>
    /// db.SaveChangesAsync, with the summary kept: through the interceptor when
    /// the context has it, otherwise here (same transaction as the save).
    /// </summary>
    public static async Task<int> SaveChangesAsync(DbContext db, CancellationToken ct = default)
    {
        if (HasInterceptor(db))
            return await db.SaveChangesAsync(ct);
        var pending = await CaptureAsync(db, ensureTransaction: true, ct);
        try
        {
            int n = await db.SaveChangesAsync(ct);
            if (pending is not null)
                await ApplyAsync(db, pending, ct);
            return n;
        }
        catch
        {
            if (pending is not null)
                await AbandonAsync(pending, CancellationToken.None);
            throw;
        }
    }

    public static bool HasInterceptor(DbContext db)
    {
        var core = db.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>();
        return core?.Interceptors?.Any(i => i is GlSummaryInterceptor) == true;
    }

    private static void Add(Dictionary<Key, Amounts> map, Key k, decimal dr, decimal cr, int n)
    {
        if (!map.TryGetValue(k, out var a))
            map[k] = a = new Amounts();
        a.Debit += dr;
        a.Credit += cr;
        a.Lines += n;
    }

    private static (DbConnection Conn, DbTransaction? Tx) Connection(DbContext db) =>
        (db.Database.GetDbConnection(), db.Database.CurrentTransaction?.GetDbTransaction());

    /// <summary>_contributions: what the entries' posted lines add to each key right now.</summary>
    public static async Task<Dictionary<Key, Amounts>> ContributionsAsync(DbContext db, IEnumerable<int> entryIds, CancellationToken ct = default)
    {
        var (conn, tx) = Connection(db);
        if (conn.State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);
        var output = new Dictionary<Key, Amounts>();
        var ids = entryIds.Distinct().OrderBy(i => i).ToArray();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "SELECT jl.company_id, e.company_id, jl.account_id, jl.label_id, e.entry_date, jl.debit, jl.credit " +
            "FROM journal_lines jl JOIN journal_entries e ON jl.journal_entry_id = e.id " +
            "WHERE e.id = ANY(@ids) AND e.is_posted = TRUE";
        cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = ids });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            int? lc = r.IsDBNull(0) ? null : r.GetInt32(0);
            int? ec = r.IsDBNull(1) ? null : r.GetInt32(1);
            int? acc = r.IsDBNull(2) ? null : r.GetInt32(2);
            int lab = r.IsDBNull(3) ? 0 : r.GetInt32(3);
            DateTime? d = r.IsDBNull(4) ? null : r.GetDateTime(4);
            int? company = lc ?? ec;
            if (d is null || acc is null || company is null)
                continue;
            decimal dr = r.IsDBNull(5) ? 0m : r.GetDecimal(5);
            decimal cr = r.IsDBNull(6) ? 0m : r.GetDecimal(6);
            Add(output, new Key(company.Value, acc.Value, lab, DateOnly.FromDateTime(d.Value)), dr, cr, 1);
        }
        return output;
    }

    /// <summary>apply_delta: add {key: amounts} to the summary atomically, keys in sorted order.</summary>
    public static async Task ApplyDeltaAsync(DbContext db, IReadOnlyDictionary<Key, Amounts> delta, CancellationToken ct = default)
    {
        var rows = delta.Where(kv => kv.Value.Debit != 0 || kv.Value.Credit != 0 || kv.Value.Lines != 0)
                        .OrderBy(kv => kv.Key).ToList();
        if (rows.Count == 0)
            return;
        var (conn, tx) = Connection(db);
        if (conn.State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);
        int[] c = rows.Select(r => r.Key.CompanyId).ToArray();
        int[] a = rows.Select(r => r.Key.AccountId).ToArray();
        int[] l = rows.Select(r => r.Key.LabelId).ToArray();
        DateOnly[] d = rows.Select(r => r.Key.Day).ToArray();
        decimal[] dr = rows.Select(r => r.Value.Debit).ToArray();
        decimal[] cr = rows.Select(r => r.Value.Credit).ToArray();
        int[] n = rows.Select(r => r.Value.Lines).ToArray();

        await using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText =
                "INSERT INTO gl_daily_balances (company_id, account_id, label_id, day, debit, credit, line_count) " +
                "SELECT k.c, k.a, k.l, k.d, k.dr, k.cr, k.n " +
                "FROM unnest(@c, @a, @l, @d, @dr, @cr, @n) WITH ORDINALITY AS k(c, a, l, d, dr, cr, n, ord) " +
                "ORDER BY k.ord " +
                "ON CONFLICT (company_id, account_id, label_id, day) DO UPDATE SET " +
                "debit = gl_daily_balances.debit + excluded.debit, " +
                "credit = gl_daily_balances.credit + excluded.credit, " +
                "line_count = gl_daily_balances.line_count + excluded.line_count";
            AddArrays(ins, c, a, l, d);
            ins.Parameters.Add(new NpgsqlParameter("dr", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = dr });
            ins.Parameters.Add(new NpgsqlParameter("cr", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = cr });
            ins.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = n });
            await ins.ExecuteNonQueryAsync(ct);
        }
        await using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText =
                "DELETE FROM gl_daily_balances g USING unnest(@c, @a, @l, @d) AS k(c, a, l, d) " +
                "WHERE g.company_id = k.c AND g.account_id = k.a AND g.label_id = k.l AND g.day = k.d " +
                "AND g.line_count <= 0";
            AddArrays(del, c, a, l, d);
            await del.ExecuteNonQueryAsync(ct);
        }
    }

    private static void AddArrays(DbCommand cmd, int[] c, int[] a, int[] l, DateOnly[] d)
    {
        cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = c });
        cmd.Parameters.Add(new NpgsqlParameter("a", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = a });
        cmd.Parameters.Add(new NpgsqlParameter("l", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = l });
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Array | NpgsqlDbType.Date) { Value = d });
    }

    /// <summary>rebuild(conn, company_id): recompute the summary (one company, or all) from journal_lines.</summary>
    public static async Task RebuildAsync(DbContext db, int? companyId, CancellationToken ct = default)
    {
        if (companyId is int cid)
        {
            await db.Database.ExecuteSqlAsync($"DELETE FROM gl_daily_balances WHERE company_id = {cid}", ct);
            await db.Database.ExecuteSqlRawAsync(RebuildSelect + " AND " + Comp + " = {0} " + RebuildGroup, [cid], ct);
        }
        else
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM gl_daily_balances", ct);
            await db.Database.ExecuteSqlRawAsync(RebuildSelect + " " + RebuildGroup, ct);
        }
    }

    // The rebuild's SQL is constant text; only the company id varies, and it goes as a parameter.
    private const string Comp = "COALESCE(jl.company_id, e.company_id)";
    private const string RebuildSelect =
        "INSERT INTO gl_daily_balances (company_id, account_id, label_id, day, debit, credit, line_count) " +
        "SELECT " + Comp + ", jl.account_id, COALESCE(jl.label_id, 0), CAST(e.entry_date AS DATE), " +
        "COALESCE(SUM(jl.debit), 0), COALESCE(SUM(jl.credit), 0), COUNT(*) " +
        "FROM journal_lines jl JOIN journal_entries e ON jl.journal_entry_id = e.id " +
        "WHERE e.is_posted = TRUE AND " + Comp + " IS NOT NULL";
    private const string RebuildGroup =
        "GROUP BY " + Comp + ", jl.account_id, COALESCE(jl.label_id, 0), CAST(e.entry_date AS DATE) " +
        "ON CONFLICT (company_id, account_id, label_id, day) DO UPDATE SET " +
        "debit = excluded.debit, credit = excluded.credit, line_count = excluded.line_count";
}

/// <summary>The EF side of <see cref="GlSummary"/>: before/after every save of every context.</summary>
public sealed class GlSummaryInterceptor : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, GlSummary.Pending> _pending = new();

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        if (eventData.Context is { } db && db.Database.IsNpgsql())
        {
            _pending.Remove(db);
            var p = await GlSummary.CaptureAsync(db, ensureTransaction: true, ct);
            if (p is not null)
                _pending.Add(db, p);
        }
        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
    {
        if (eventData.Context is { } db && _pending.TryGetValue(db, out var p))
        {
            _pending.Remove(db);
            try
            {
                await GlSummary.ApplyAsync(db, p, ct);
            }
            catch
            {
                await GlSummary.AbandonAsync(p, CancellationToken.None);
                throw;
            }
        }
        return result;
    }

    public override async Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken ct = default)
    {
        if (eventData.Context is { } db && _pending.TryGetValue(db, out var p))
        {
            _pending.Remove(db);
            await GlSummary.AbandonAsync(p, CancellationToken.None);
        }
    }

    public override async Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken ct = default)
    {
        if (eventData.Context is { } db && _pending.TryGetValue(db, out var p))
        {
            _pending.Remove(db);
            await GlSummary.AbandonAsync(p, CancellationToken.None);
        }
    }

    // Synchronous saves: same work, blocking (the app saves asynchronously).
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) =>
        SavingChangesAsync(eventData, result).AsTask().GetAwaiter().GetResult();

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result) =>
        SavedChangesAsync(eventData, result).AsTask().GetAwaiter().GetResult();

    public override void SaveChangesFailed(DbContextErrorEventData eventData) =>
        SaveChangesFailedAsync(eventData).GetAwaiter().GetResult();
}
