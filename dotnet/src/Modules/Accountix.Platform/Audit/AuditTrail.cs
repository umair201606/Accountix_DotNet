using Microsoft.Extensions.Logging;
using Npgsql;

namespace Accountix.Platform.Audit;

/// <summary>
/// shared/audit.py record_now: one audit row written at once on its own
/// connection, for events that must survive the request's rollback (a failed
/// sign-in, a refused posting) or touch no tracked row. Never throws.
/// </summary>
public sealed class AuditTrail(AccountixDbContext db, IHttpContextAccessor http, TimeProvider time, ILogger<AuditTrail> log) : IAuditTrail
{
    /// <summary>HttpContext.Items key: (int userId, string name) of the person just signed in by this request.</summary>
    public const string SignedInKey = "accountix.audit.signed_in";

    public Task RecordNowAsync(string action, string summary, string module = "admin", string? entityType = null,
                               int? entityId = null, string? reference = null, int? companyId = null,
                               object? changes = null, CancellationToken ct = default) =>
        RecordAsync(action, summary, module, entityType, entityId, reference, companyId, changes, null, null, ct);

    /// <summary>record_now with Python's user_id / user_name arguments (sign-in events name the person themselves).</summary>
    public async Task RecordAsync(string action, string summary, string module = "admin", string? entityType = null,
                                  int? entityId = null, string? reference = null, int? companyId = null,
                                  object? changes = null, int? userId = null, string? userName = null,
                                  CancellationToken ct = default)
    {
        try
        {
            var who = AuditWho.From(http.HttpContext);
            companyId ??= db.Company.CompanyId;
            string? name = userName ?? who.UserName;
            if (string.IsNullOrEmpty(name)) name = null;
            else if (name.Length > 200) name = name[..200];
            string? changesJson = changes switch
            {
                null => null,
                string s => s.Length == 0 ? null : PyJson.Dumps(s),
                PyDict { Count: 0 } => null,
                System.Collections.ICollection { Count: 0 } => null,
                _ => PyJson.Dumps(changes),
            };
            var row = new AuditRow(companyId, time.GetUtcNow().UtcDateTime, userId ?? who.UserId, name,
                action.Length > 30 ? action[..30] : action, module, entityType, entityId,
                string.IsNullOrEmpty(reference) ? null : reference,
                summary is null ? "" : summary.Length > 500 ? summary[..500] : summary,
                changesJson, who.Ip, who.Path);
            await using var conn = new NpgsqlConnection(db.Database.GetConnectionString());
            await conn.OpenAsync(ct);
            await using var cmd = AuditWriter.Command(conn, null, [row]);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception e)
        {
            log.LogWarning("audit record failed: {Error}", e.Message);
        }
    }
}
