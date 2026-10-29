using System.Globalization;
using System.Text.Json;

namespace Accountix.Platform.Services;

/// <summary>
/// shared/audit.py search / decode_changes / history_for: the filtered audit
/// trail of one company. Rows of the company itself, plus sign-in events
/// (company NULL) of its own members — never another company's activity, nor
/// a member's visits to the super admin console.
/// </summary>
public static class AuditSearch
{
    public static readonly IReadOnlyList<string> ActionNames =
        ["create", "update", "delete", "approve", "unapprove", "post", "unpost", "refused", "login", "login_failed", "logout"];

    public static readonly IReadOnlyList<string> ModuleNames =
        ["invoicing", "inventory", "accounting", "fixed_assets", "hr", "settings", "admin"];

    private static readonly string[] SignIns = ["login", "logout", "login_failed"];

    /// <summary>The filter (from, to, user_id, module, action, q) over audit_logs, newest first.</summary>
    public static IQueryable<AuditLog> Search(AccountixDbContext db, int companyId, IQueryCollection args, IReadOnlyCollection<int> memberIds)
    {
        var ids = memberIds.ToList();
        IQueryable<AuditLog> q = db.AuditLogs.AsNoTracking();
        q = ids.Count > 0
            ? q.Where(a => a.CompanyId == companyId
                           || (a.CompanyId == null && a.UserId != null && ids.Contains(a.UserId.Value) && SignIns.Contains(a.Action)
                               && (a.Path == null || !a.Path.StartsWith("/superadmin"))))
            : q.Where(a => a.CompanyId == companyId);
        if (Day(args["from"]) is DateTime from)
            q = q.Where(a => a.CreatedAt >= from);
        if (Day(args["to"]) is DateTime to)
        {
            var end = to.AddDays(1).AddTicks(-10);     // time.max (microseconds)
            q = q.Where(a => a.CreatedAt <= end);
        }
        string uid = args["user_id"].ToString();
        if (uid.Length > 0 && uid.All(char.IsAsciiDigit) && int.TryParse(uid, out int u))
            q = q.Where(a => a.UserId == u);
        string module = args["module"].ToString();
        if (ModuleNames.Contains(module))
            q = q.Where(a => a.Module == module);
        string action = args["action"].ToString();
        if (ActionNames.Contains(action))
            q = q.Where(a => a.Action == action);
        string term = args["q"].ToString().Trim();
        if (term.Length > 0)
        {
            string like = "%" + term + "%";
            q = q.Where(a => EF.Functions.ILike(a.Reference!, like) || EF.Functions.ILike(a.Summary!, like)
                             || EF.Functions.ILike(a.EntityType!, like) || EF.Functions.ILike(a.UserName!, like));
        }
        return q.OrderByDescending(a => a.Id);
    }

    private static DateTime? Day(string? v) =>
        DateTime.TryParseExact((v ?? "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;

    /// <summary>One decoded change: field (underscores as spaces), old (HasOld false: None), new — as Python prints them.</summary>
    public sealed record Change(string Field, bool HasOld, string Old, string New, object? OldValue, object? NewValue);

    /// <summary>decode_changes: updates as {field, old, new}; snapshots as (field, value).</summary>
    public static List<Change> DecodeChanges(AuditLog row)
    {
        var output = new List<Change>();
        if (string.IsNullOrEmpty(row.Changes)) return output;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(row.Changes); }
        catch (JsonException) { return output; }
        using (doc)
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var c in doc.RootElement.EnumerateArray())
                {
                    if (c.ValueKind != JsonValueKind.Object) continue;
                    string field = c.TryGetProperty("field", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString()! : "";
                    var old = c.TryGetProperty("old", out var o) ? Value(o) : null;
                    var neu = c.TryGetProperty("new", out var n) ? Value(n) : null;
                    output.Add(new Change(field.Replace('_', ' '), old is not null, PyStr(old), PyStr(neu), old, neu));
                }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    var v = Value(p.Value);
                    output.Add(new Change(p.Name.Replace('_', ' '), false, "None", PyStr(v), null, v));
                }
        }
        return output;
    }

    private static object? Value(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out long l) && !e.GetRawText().Contains('.') && !e.GetRawText().Contains('e')
            && !e.GetRawText().Contains('E') ? l : e.GetDouble(),
        _ => e.GetRawText(),
    };

    /// <summary>Python's str() of a json.loads value.</summary>
    public static string PyStr(object? v) => v switch
    {
        null => "None",
        bool b => b ? "True" : "False",
        double d => Audit.PyJson.FloatRepr(d),
        long l => l.ToString(CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    /// <summary>history_for: audit rows of one record, newest first.</summary>
    public static Task<List<AuditLog>> HistoryForAsync(AccountixDbContext db, string entityType, int entityId, int companyId, CancellationToken ct = default) =>
        db.AuditLogs.AsNoTracking().Where(a => a.CompanyId == companyId && a.EntityType == entityType && a.EntityId == entityId)
            .OrderByDescending(a => a.Id).ToListAsync(ct);

    /// <summary>The CSV/Excel "Changes" cell: "field: old -> new" or "field: new", joined by "; ".</summary>
    public static string ChangesText(AuditLog row) =>
        string.Join("; ", DecodeChanges(row).Select(c => c.HasOld ? $"{c.Field}: {c.Old} -> {c.New}" : $"{c.Field}: {c.New}"));
}

/// <summary>shared/tenancy.py login_change_refusal: why this company may not change a person's login, or null.</summary>
public static class LoginChanges
{
    public static async Task<string?> RefusalAsync(AccountixDbContext db, User? user, int? companyId, int? currentUserId, CancellationToken ct = default)
    {
        if (user is null) return "That person is not a member of this company.";
        string name = string.IsNullOrEmpty(user.FullName) ? user.Email : user.FullName;
        if (user.SuperAdmin)
            return $"{name} is a platform administrator; that login is managed from the console.";
        if (user.Id == currentUserId) return null;
        if (companyId is int cid && await db.Companies.AnyAsync(c => c.Id == cid && c.CreatedBy == user.Id, ct))
            return $"{name} owns this company; only they can change their own login.";
        bool elsewhere = await db.Memberships.AnyAsync(m => m.UserId == user.Id && m.CompanyId != companyId
                                                           && m.Status != CompanyMembership.Removed, ct);
        return elsewhere ? $"{name} also uses this login in another company, so only they can change it." : null;
    }
}
