using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Accountix.Platform.Audit;

/// <summary>
/// The automatic audit trail (shared/audit.py's session hooks): every
/// insert, update and delete of a tracked table is logged with the user,
/// time, IP, path, the record's reference and, for updates, each changed
/// field's old and new value. A status change to/from "approved" is
/// approve/unapprove; a journal entry insert is "post" and is_posted going
/// off is "unpost". Secrets are redacted.
///
/// Before a save it notes what will change (and the update diffs, which need
/// the original values); after the save, when new rows have their ids, it
/// writes every audit row of that save with ONE multi-row INSERT on the same
/// connection and transaction, so a save that rolls back logs nothing. A save
/// with no transaction gets one here, committed after the audit rows.
///
/// Bulk ExecuteUpdate / ExecuteDelete bypass it, as Query.update/delete
/// bypass Python's hook.
/// </summary>
public sealed class AuditInterceptor(IHttpContextAccessor http, TimeProvider time) : SaveChangesInterceptor
{
    private sealed record Change(string Action, string Table, Tracked Tracked, EntityEntry Entry, object? Payload);

    private sealed class Pending
    {
        public List<Change> Changes { get; } = [];
        public IDbContextTransaction? OwnTransaction { get; set; }
    }

    private readonly ConditionalWeakTable<DbContext, Pending> _pending = new();

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        if (eventData.Context is { } db)
        {
            _pending.Remove(db);
            var p = Collect(db);
            if (p is not null)
            {
                if (db.Database.CurrentTransaction is null && db.Database.IsRelational())
                    p.OwnTransaction = await db.Database.BeginTransactionAsync(ct);
                _pending.Add(db, p);
            }
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
                var rows = Rows(db, p);
                if (rows.Count > 0 && db.Database.IsNpgsql())
                {
                    // One multi-row INSERT, through EF so it runs in the save's transaction (and is seen by command interceptors).
                    var (sql, parameters) = AuditWriter.Sql(rows);
                    await db.Database.ExecuteSqlRawAsync(sql, parameters, ct);
                }
                if (p.OwnTransaction is not null)
                {
                    await p.OwnTransaction.CommitAsync(ct);
                    await p.OwnTransaction.DisposeAsync();
                }
            }
            catch
            {
                await AbandonAsync(p);
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
            await AbandonAsync(p);
        }
    }

    public override async Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken ct = default)
    {
        if (eventData.Context is { } db && _pending.TryGetValue(db, out var p))
        {
            _pending.Remove(db);
            await AbandonAsync(p);
        }
    }

    private static async Task AbandonAsync(Pending p)
    {
        if (p.OwnTransaction is null) return;
        try { await p.OwnTransaction.RollbackAsync(CancellationToken.None); }
        catch { /* connection already gone */ }
        await p.OwnTransaction.DisposeAsync();
        p.OwnTransaction = null;
    }

    // Synchronous saves: same work, blocking.
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) =>
        SavingChangesAsync(eventData, result).AsTask().GetAwaiter().GetResult();

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result) =>
        SavedChangesAsync(eventData, result).AsTask().GetAwaiter().GetResult();

    public override void SaveChangesFailed(DbContextErrorEventData eventData) =>
        SaveChangesFailedAsync(eventData).GetAwaiter().GetResult();

    /// <summary>_collect (before_flush): creates, real updates with their diff, deletes with a snapshot.</summary>
    private static Pending? Collect(DbContext db)
    {
        db.ChangeTracker.DetectChanges();
        Pending? p = null;
        var adds = new List<Change>();
        var updates = new List<Change>();
        var deletes = new List<Change>();
        foreach (var e in db.ChangeTracker.Entries())
        {
            if (e.State is EntityState.Unchanged or EntityState.Detached) continue;
            string? table = e.Metadata.GetTableName();
            if (table is null || !AuditTables.All.TryGetValue(table, out var t)) continue;
            switch (e.State)
            {
                case EntityState.Added:
                    adds.Add(new Change("create", table, t, e, null));
                    break;
                case EntityState.Modified:
                    var diff = AuditTables.Diff(table, t, e);
                    if (diff.Count > 0)
                        updates.Add(new Change(AuditTables.Classify(table, diff), table, t, e, diff));
                    break;
                case EntityState.Deleted:
                    // Snapshot now: the row and its key are gone after the save. The reference too.
                    deletes.Add(new Change("delete", table, t, e,
                        (AuditTables.Snapshot(table, t, e), AuditTables.Reference(t, e), KeyOf(e), CompanyOf(db, e), Description(e))));
                    break;
            }
        }
        if (adds.Count + updates.Count + deletes.Count == 0) return null;
        p = new Pending();
        p.Changes.AddRange(adds);
        p.Changes.AddRange(updates);
        p.Changes.AddRange(deletes);
        return p;
    }

    /// <summary>_write (after_flush_postexec): the rows, with the ids the save assigned.</summary>
    private List<AuditRow> Rows(DbContext db, Pending p)
    {
        var who = AuditWho.From(http.HttpContext);
        var now = time.GetUtcNow().UtcDateTime;
        var rows = new List<AuditRow>(p.Changes.Count);
        foreach (var c in p.Changes)
        {
            string action = c.Action;
            string? reference;
            int? entityId;
            int? companyId;
            string? description;
            object? payload;
            if (c.Payload is ValueTuple<PyDict, string?, int?, int?, string?> deleted)
            {
                (payload, reference, entityId, companyId, description) = deleted;
            }
            else
            {
                payload = c.Payload;
                if (action == "create")
                {
                    if (c.Table == "journal_entries")
                        action = AuditTables.Value(c.Entry, "is_posted") is true ? "post" : "create";
                    payload = AuditTables.Snapshot(c.Table, c.Tracked, c.Entry);
                }
                reference = AuditTables.Reference(c.Tracked, c.Entry);
                entityId = KeyOf(c.Entry);
                companyId = CompanyOf(db, c.Entry);
                description = Description(c.Entry);
            }
            var changes = payload switch
            {
                PyDict d when d.Count > 0 => PyJson.Dumps(d),
                List<PyDict> l when l.Count > 0 => PyJson.Dumps(l),
                _ => null,
            };
            rows.Add(new AuditRow(companyId, now, who.UserId, who.UserName, action, c.Tracked.Module, c.Tracked.Label,
                entityId, reference,
                AuditTables.Summary(action, c.Tracked.Label, reference, action == "update" ? payload as List<PyDict> : null, description),
                changes, who.Ip, who.Path));
        }
        return rows;
    }

    private static int? KeyOf(EntityEntry e) => AuditTables.Value(e, "id") is int id && id > 0 ? id : null;

    /// <summary>_company_for: the row's company_id, else the active company.</summary>
    private static int? CompanyOf(DbContext db, EntityEntry e) =>
        AuditTables.Value(e, "company_id") is int cid ? cid : (db as AccountixDbContext)?.Company.CompanyId;

    private static string? Description(EntityEntry e) =>
        AuditTables.Column(e.Metadata, "description") is not null ? AuditTables.Value(e, "description") as string : null;
}
