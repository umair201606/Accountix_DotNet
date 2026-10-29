using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

namespace Accountix.Platform.Audit;

/// <summary>A tracked table (shared/audit.py TRACKED).</summary>
internal sealed record Tracked(string Module, string Label, string[] RefAttrs, bool MonthYear, string[] Columns);

internal static partial class AuditTables
{
    /// <summary>Never logged: bookkeeping columns (SKIP_FIELDS).</summary>
    internal static readonly IReadOnlySet<string> SkipFields = new HashSet<string> { "updated_at", "last_login", "created_at" };

    private static readonly string[] SecretMarkers = ["password", "token", "secret", "api_key", "otp"];

    internal const int MaxValue = 300;
    internal const string Redacted = "•••";

    internal static bool IsSecret(string field)
    {
        string f = field.ToLowerInvariant();
        return SecretMarkers.Any(m => f.Contains(m, StringComparison.Ordinal));
    }

    /// <summary>_jsonable: the value as Python's json sees it (Decimal → float, dates → isoformat, long text cut).</summary>
    internal static object? Jsonable(object? v, bool quantity = false)
    {
        switch (v)
        {
            case null: return null;
            case bool b: return b;
            case int or long or short: return Convert.ToInt64(v, CultureInfo.InvariantCulture);
            case string s: return Cut(s);
            case decimal m:
                if (quantity)
                {
                    double q = Math.Round((double)m, 4);
                    return q == Math.Floor(q) && Math.Abs(q) < 9e15 ? (object)(long)q : q;
                }
                return (double)m;
            case double d: return d;
            case float f: return (double)f;
            case DateOnly d: return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case DateTime dt: return IsoFormat(dt);
            case TimeOnly t: return t.Ticks % TimeSpan.TicksPerSecond / 10 == 0
                ? t.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                : t.ToString("HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
            default:
                string text = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
                return text.Length > MaxValue ? text[..MaxValue] : text;
        }
    }

    /// <summary>Python's datetime.isoformat() for a naive datetime (microseconds shown only when non-zero).</summary>
    internal static string IsoFormat(DateTime dt)
    {
        long micro = dt.Ticks % TimeSpan.TicksPerSecond / 10;
        return micro == 0
            ? dt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
            : dt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "." + micro.ToString("000000", CultureInfo.InvariantCulture);
    }

    private static string Cut(string s)
    {
        // Python counts code points, not UTF-16 units.
        var runes = s.EnumerateRunes().ToList();
        if (runes.Count <= MaxValue) return s;
        var sb = new StringBuilder();
        foreach (var r in runes.Take(MaxValue)) sb.Append(r.ToString());
        return sb + "…";
    }

    /// <summary>Python's == between two jsonable values (1 == 1.0, True == 1).</summary>
    internal static bool SameJson(object? a, object? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (IsNumber(a) && IsNumber(b))
            return ToDouble(a) == ToDouble(b);
        return Equals(a, b);
    }

    internal static bool IsNumber(object? v) => v is bool or int or long or short or double or float or decimal;

    internal static double ToDouble(object v) => v switch
    {
        bool b => b ? 1 : 0,
        decimal m => (double)m,
        _ => Convert.ToDouble(v, CultureInfo.InvariantCulture),
    };

    // ── per-entity helpers ──────────────────────────────────────────────

    internal static IProperty? Column(IEntityType type, string column)
    {
        foreach (var p in type.GetProperties())
            if (string.Equals(p.GetColumnName(), column, StringComparison.Ordinal))
                return p;
        return null;
    }

    internal static object? Value(EntityEntry e, string column, bool original = false)
    {
        var p = Column(e.Metadata, column);
        if (p is null) return null;
        var pe = e.Property(p.Name);
        return original ? pe.OriginalValue : pe.CurrentValue;
    }

    /// <summary>_snapshot: every column but id and the bookkeeping ones, falsy values left out, secrets redacted.</summary>
    internal static PyDict Snapshot(string table, Tracked t, EntityEntry e)
    {
        var dict = new PyDict();
        foreach (string col in t.Columns)
        {
            if (SkipFields.Contains(col) || col == "id") continue;
            object? v = Value(e, col);
            if (IsFalsy(v)) continue;
            dict.Add(col, IsSecret(col) ? Redacted : Jsonable(v, QuantityColumns.Contains(table + "." + col)));
        }
        return dict;
    }

    internal static bool IsFalsy(object? v) => v switch
    {
        null => true,
        string s => s.Length == 0,
        bool b => !b,
        int i => i == 0,
        long l => l == 0,
        short s => s == 0,
        decimal m => m == 0,
        double d => d == 0,
        float f => f == 0,
        _ => false,
    };

    /// <summary>_diff: [{field, old, new}] for every column whose value really changed.</summary>
    internal static List<PyDict> Diff(string table, Tracked t, EntityEntry e)
    {
        var changes = new List<PyDict>();
        foreach (string col in t.Columns)
        {
            if (SkipFields.Contains(col) || PropertyColumns.Contains(table + "." + col)) continue;
            var p = Column(e.Metadata, col);
            if (p is null) continue;
            var pe = e.Property(p.Name);
            if (!pe.IsModified) continue;
            bool qty = QuantityColumns.Contains(table + "." + col);
            object? oldRaw = pe.OriginalValue, newRaw = pe.CurrentValue;
            object? oldV = Jsonable(oldRaw, qty), newV = Jsonable(newRaw, qty);
            if (SameJson(oldV, newV)) continue;
            if (IsNumber(oldRaw) && IsNumber(newRaw) && oldRaw is not bool && newRaw is not bool
                && Math.Abs(ToDouble(oldRaw!) - ToDouble(newRaw!)) < 1e-9)
                continue;
            if (IsSecret(col))
                changes.Add(new PyDict { { "field", col }, { "old", Redacted }, { "new", Redacted } });
            else
                changes.Add(new PyDict { { "field", col }, { "old", oldV }, { "new", newV } });
        }
        return changes;
    }

    /// <summary>_classify_update: post / unpost (journal entries), approve / unapprove (status), else update.</summary>
    internal static string Classify(string table, List<PyDict> changes)
    {
        var fields = changes.ToDictionary(c => (string)c.First(kv => kv.Key == "field").Value!, c => c);
        object? Get(PyDict c, string key) => c.First(kv => kv.Key == key).Value;
        if (table == "journal_entries" && fields.TryGetValue("is_posted", out var posted))
            return IsFalsy(Get(posted, "new")) ? "unpost" : "post";
        foreach (string sf in new[] { "status", "voucher_status" })
        {
            if (!fields.TryGetValue(sf, out var c)) continue;
            object? oldV = Get(c, "old"), newV = Get(c, "new");
            if (Equals(newV, "approved")) return "approve";
            if (Equals(oldV, "approved")) return "unapprove";
        }
        return "update";
    }

    /// <summary>_reference: "month/year" for payroll runs, else the first non-empty reference attribute (120 chars).</summary>
    internal static string? Reference(Tracked t, EntityEntry e)
    {
        if (t.MonthYear)
            return $"{PyStr(Value(e, "month"))}/{PyStr(Value(e, "year"))}";
        foreach (string a in t.RefAttrs)
        {
            object? v = Value(e, a);
            if (v is null || v is string { Length: 0 }) continue;
            string s = PyStr(v);
            return s.Length > 120 ? s[..120] : s;
        }
        return null;
    }

    /// <summary>Python's str() of a column value.</summary>
    internal static string PyStr(object? v) => v switch
    {
        null => "",
        bool b => b ? "True" : "False",
        decimal m => PyJson.FloatRepr((double)m),
        double d => PyJson.FloatRepr(d),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dt => IsoFormat(dt).Replace('T', ' '),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    private static readonly Dictionary<string, string> Verbs = new()
    {
        ["create"] = "created", ["delete"] = "deleted", ["approve"] = "approved", ["unapprove"] = "unapproved",
        ["post"] = "posted", ["unpost"] = "un-posted", ["update"] = "edited",
    };

    /// <summary>_summary.</summary>
    internal static string Summary(string action, string label, string? reference, List<PyDict>? changes, string? description)
    {
        string refTxt = string.IsNullOrEmpty(reference) ? "" : " " + reference;
        string text = $"{label}{refTxt} {Verbs.GetValueOrDefault(action, action)}";
        if (action == "update" && changes is { Count: > 0 })
        {
            string names = string.Join(", ", changes.Take(4).Select(c => ((string)c.First(kv => kv.Key == "field").Value!).Replace('_', ' ')));
            string more = changes.Count > 4 ? $" +{changes.Count - 4} more" : "";
            text += $" ({names}{more})";
        }
        if (action is "post" or "unpost" && !string.IsNullOrEmpty(description))
            text += " — " + (description.Length > 150 ? description[..150] : description);
        return text.Length > 500 ? text[..500] : text;
    }
}

/// <summary>One audit_logs row about to be written.</summary>
public sealed record AuditRow(
    int? CompanyId, DateTime CreatedAt, int? UserId, string? UserName, string Action, string? Module,
    string? EntityType, int? EntityId, string? Reference, string? Summary, string? Changes, string? IpAddress, string? Path);

/// <summary>Who, from where (shared/audit.py _context): the signed-in person, client IP, path.</summary>
public sealed record AuditWho(int? UserId, string? UserName, string? Ip, string? Path)
{
    public static readonly AuditWho None = new(null, null, null, null);

    public static AuditWho From(HttpContext? http)
    {
        if (http is null) return None;
        int? uid = null;
        string? name = null;
        if (http.Items.TryGetValue(AuditTrail.SignedInKey, out var signedIn) && signedIn is (int sid, string sname))
        {
            uid = sid;
            name = sname;
        }
        else if (http.User.Identity?.IsAuthenticated == true
                 && int.TryParse(http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out int id))
        {
            uid = id;
            var u = http.Access()?.User;
            name = u is not null ? (string.IsNullOrEmpty(u.FullName) ? u.Email : u.FullName)
                                 : http.User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value;
        }
        string path = http.Items.TryGetValue(Controllers.HubPath.OriginalPathKey, out var op) && op is string o ? o : http.Request.Path.Value ?? "";
        return new AuditWho(uid, Trim(name, 200), ClientIp(http), path.Length > 300 ? path[..300] : path);
    }

    /// <summary>account_security.client_ip: the first X-Forwarded-For address, else the peer (64 chars).</summary>
    public static string? ClientIp(HttpContext http)
    {
        // UseForwardedHeaders moves the original header to X-Original-For.
        string fwd = http.Request.Headers["X-Original-For"].ToString();
        if (string.IsNullOrEmpty(fwd)) fwd = http.Request.Headers["X-Forwarded-For"].ToString();
        string? ip = !string.IsNullOrEmpty(fwd) ? fwd.Split(',')[0].Trim() : http.Connection.RemoteIpAddress?.ToString();
        if (ip is not null && ip.StartsWith("::ffff:", StringComparison.Ordinal)) ip = ip[7..];
        if (string.IsNullOrEmpty(ip)) return null;
        return ip.Length > 64 ? ip[..64] : ip;
    }

    private static string? Trim(string? s, int n) => s is null ? null : s.Length > n ? s[..n] : s;
}

/// <summary>Writes audit rows: one multi-row INSERT.</summary>
internal static class AuditWriter
{
    private const string Columns =
        "company_id, created_at, user_id, user_name, action, module, entity_type, entity_id, reference, summary, changes, ip_address, path";

    internal static NpgsqlCommand Command(NpgsqlConnection conn, NpgsqlTransaction? tx, IReadOnlyList<AuditRow> rows)
    {
        var (sql, parameters) = Sql(rows);
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddRange(parameters);
        return cmd;
    }

    internal static (string Sql, NpgsqlParameter[] Parameters) Sql(IReadOnlyList<AuditRow> rows)
    {
        var parameters = new List<NpgsqlParameter>();
        var sb = new StringBuilder("INSERT INTO audit_logs (").Append(Columns).Append(") VALUES ");
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (i > 0) sb.Append(", ");
            sb.Append('(');
            object?[] values =
            [
                r.CompanyId, r.CreatedAt, r.UserId, r.UserName, r.Action, r.Module, r.EntityType, r.EntityId,
                r.Reference, r.Summary, r.Changes, r.IpAddress, r.Path,
            ];
            for (int j = 0; j < values.Length; j++)
            {
                if (j > 0) sb.Append(", ");
                string name = $"p{i}_{j}";
                sb.Append('@').Append(name);
                var p = new NpgsqlParameter(name, values[j] ?? DBNull.Value);
                if (values[j] is DateTime dt)
                {
                    p.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Timestamp;
                    p.Value = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
                }
                else if (values[j] is null)
                    p.NpgsqlDbType = j is 0 or 2 or 7 ? NpgsqlTypes.NpgsqlDbType.Integer : NpgsqlTypes.NpgsqlDbType.Text;
                parameters.Add(p);
            }
            sb.Append(')');
        }
        return (sb.ToString(), parameters.ToArray());
    }
}
