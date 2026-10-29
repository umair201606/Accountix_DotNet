using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace Accountix.Migrations;

/// <summary>
/// A database's schema as one JSON document (Schema/describe_schema.sql):
/// tables, columns (type, default, nullability, position), constraints,
/// indexes, sequences, views, functions, triggers, types. Two databases with
/// equal descriptions have the same schema. <see cref="Expected"/> is the
/// schema this build creates, captured from a database the Python app built at
/// Alembic 0018; the tests prove the migrator's databases equal it.
/// </summary>
public static class SchemaDescription
{
    /// <summary>The schema at the end of <see cref="MigrationCatalog.Forward"/>. Re-capture with `migrate --describe` when a migration changes it.</summary>
    public static JsonNode Expected() => JsonNode.Parse(MigrationCatalog.Resource("Schema.expected_schema.json"))!;

    public static async Task<JsonNode> DescribeAsync(string connectionString, CancellationToken ct = default)
    {
        var csb = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        await using var conn = new NpgsqlConnection(csb.ConnectionString);
        await conn.OpenAsync(ct);
        return await DescribeAsync(conn, ct);
    }

    public static async Task<JsonNode> DescribeAsync(NpgsqlConnection conn, CancellationToken ct = default)
    {
        await using (var set = new NpgsqlCommand("SET search_path = public", conn))
            await set.ExecuteNonQueryAsync(ct);
        await using var cmd = new NpgsqlCommand(MigrationCatalog.Resource("Schema.describe_schema.sql"), conn) { CommandTimeout = 0 };
        string json = (string)(await cmd.ExecuteScalarAsync(ct))!;
        return JsonNode.Parse(json)!;
    }

    /// <summary>Stable text (keys sorted, indented), as the golden files are written.</summary>
    public static string ToText(JsonNode node) =>
        Sorted(node)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";

    /// <summary>Where two descriptions differ, one line per difference ("" paths are whole documents).</summary>
    public static List<string> Differences(JsonNode? expected, JsonNode? actual, int max = 200)
    {
        var list = new List<string>();
        Walk(expected, actual, "", list, max);
        return list;
    }

    private static void Walk(JsonNode? a, JsonNode? b, string path, List<string> list, int max)
    {
        if (list.Count >= max)
            return;
        switch (a, b)
        {
            case (JsonObject x, JsonObject y):
                foreach (string k in x.Select(p => p.Key).Union(y.Select(p => p.Key)).OrderBy(k => k, StringComparer.Ordinal))
                {
                    bool inX = x.ContainsKey(k), inY = y.ContainsKey(k);
                    if (inX && inY)
                        Walk(x[k], y[k], $"{path}/{k}", list, max);
                    else
                        list.Add($"{path}/{k}: {(inX ? "missing" : "unexpected")} {Short(inX ? x[k] : y[k])}");
                }
                break;
            case (JsonArray x, JsonArray y) when Named(x) && Named(y):
                // Lists of named things (columns, indexes...): compare by name.
                var xs = x.ToDictionary(Key, n => n);
                var ys = y.ToDictionary(Key, n => n);
                foreach (string k in xs.Keys.Union(ys.Keys).OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (xs.TryGetValue(k, out var xv) && ys.TryGetValue(k, out var yv))
                        Walk(xv, yv, $"{path}[{k}]", list, max);
                    else
                        list.Add($"{path}[{k}]: {(xs.ContainsKey(k) ? "missing" : "unexpected")} {Short(xs.GetValueOrDefault(k) ?? ys[k])}");
                }
                break;
            default:
                if (!JsonNode.DeepEquals(a, b))
                    list.Add($"{path}: expected {Short(a)}, found {Short(b)}");
                break;
        }
    }

    private static bool Named(JsonArray a) =>
        a.All(n => n is JsonObject o && o["name"] is JsonValue) &&
        a.Select(Key).Distinct().Count() == a.Count;

    private static string Key(JsonNode? n) =>
        n is JsonObject o ? (o["table"] is JsonValue t ? t + "." : "") + o["name"]!.ToString() : "";

    private static string Short(JsonNode? n)
    {
        string s = n?.ToJsonString() ?? "null";
        return s.Length > 240 ? s[..240] + "…" : s;
    }

    private static JsonNode? Sorted(JsonNode? n) => n switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Sorted(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Sorted).ToArray()),
        null => null,
        _ => JsonNode.Parse(n.ToJsonString()),
    };
}
