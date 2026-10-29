// Golden fixtures compared as JSON trees (Load / Match / ToNode / Write / FirstDifference).
// Shared by: Accountix.Fbr.Tests, Accountix.FixedAssets.Tests, Accountix.Parties.Tests, Accountix.Platform.Tests, Accountix.SuperAdmin.Tests (each aliases it as `Golden`).
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Accountix.TestSupport;

/// <summary>
/// Fixed expectations captured from the Python app on 29 Oct 2026, before it was retired.
/// Each <c>Golden/&lt;name&gt;.json</c> is Python's answer for exactly the data the test builds,
/// normalised by the same code the test applies to .NET's answer (ids → roles, dates relative to
/// the test's own dates, generated numbers → their pattern). The Python that produced it is kept in
/// <c>Golden/capture/&lt;name&gt;.py</c> for the record.
/// <para>ACCOUNTIX_GOLDEN_PERTURB=1 changes one value of the answer before comparing: every golden
/// test must then fail (proves the comparison really guards the figures).</para>
/// </summary>
public static class GoldenTree
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static JsonNode Load(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", name + ".json")))!;

    /// <summary>Any value (anonymous object, list, dictionary, JsonNode) as a parsed JSON tree.
    /// A string is a JSON string value; parse JSON text yourself (JsonNode.Parse) before passing it.</summary>
    public static JsonNode ToNode(object value) =>
        JsonNode.Parse(value is JsonNode n ? n.ToJsonString() : JsonSerializer.Serialize(value, Pretty))!;

    /// <summary>Assert that <paramref name="actual"/> equals the fixture <paramref name="name"/>.</summary>
    public static void Match(string name, object actual)
    {
        var node = ToNode(actual);
        if (Environment.GetEnvironmentVariable("ACCOUNTIX_GOLDEN_PERTURB") == "1")
        {
            Perturb(node);
            node = JsonNode.Parse(node.ToJsonString())!;
        }
        string? diff = FirstDifference(Load(name), node, "$");
        Assert.True(diff is null, $"{name} differs from Python's answer: {diff}\nactual: {Truncate(node.ToJsonString(Pretty))}");
    }

    /// <summary>Capture only: write a fixture into the source folder (Golden/&lt;name&gt;.json).</summary>
    public static void Write(string name, object value)
    {
        string dir = Path.Combine(SourceDirectory(), "Golden");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".json"), ToNode(value).ToJsonString(Pretty) + "\n");
    }

    private static string SourceDirectory([CallerFilePath] string file = "") => Path.GetDirectoryName(file)!;

    private static string Truncate(string s) => s.Length > 4000 ? s[..4000] + "…" : s;

    /// <summary>Change the first number (+1) or, without numbers, the first string (+"~").</summary>
    private static void Perturb(JsonNode root)
    {
        if (!Change(root, numbers: true))
            Change(root, numbers: false);
    }

    private static bool Change(JsonNode? node, bool numbers)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).ToList())
                {
                    var child = o[key];
                    if (child is JsonValue v && TryReplace(v, numbers, out var nv)) { o[key] = nv; return true; }
                    if (Change(child, numbers)) return true;
                }
                return false;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue v && TryReplace(v, numbers, out var nv)) { a[i] = nv; return true; }
                    if (Change(a[i], numbers)) return true;
                }
                return false;
            default:
                return false;
        }
    }

    private static bool TryReplace(JsonValue v, bool numbers, out JsonNode replacement)
    {
        var e = v.GetValue<JsonElement>();
        if (numbers && e.ValueKind == JsonValueKind.Number) { replacement = JsonValue.Create(e.GetDecimal() + 1); return true; }
        if (!numbers && e.ValueKind == JsonValueKind.String) { replacement = JsonValue.Create(e.GetString() + "~"); return true; }
        replacement = null!;
        return false;
    }

    /// <summary>The first place two JSON trees differ (numbers compared by value), or null.</summary>
    public static string? FirstDifference(JsonNode? expected, JsonNode? actual, string path)
    {
        if (expected is null || actual is null)
            return expected is null && actual is null ? null : $"{path}: expected {expected?.ToJsonString() ?? "null"}, got {actual?.ToJsonString() ?? "null"}";
        switch (expected)
        {
            case JsonObject eo when actual is JsonObject ao:
                foreach (var key in eo.Select(p => p.Key).Union(ao.Select(p => p.Key)))
                {
                    if (!eo.ContainsKey(key)) return $"{path}.{key}: not expected, got {ao[key]?.ToJsonString() ?? "null"}";
                    if (!ao.ContainsKey(key)) return $"{path}.{key}: missing, expected {eo[key]?.ToJsonString() ?? "null"}";
                    if (FirstDifference(eo[key], ao[key], $"{path}.{key}") is { } d) return d;
                }
                return null;
            case JsonArray ea when actual is JsonArray aa:
                for (int i = 0; i < Math.Min(ea.Count, aa.Count); i++)
                    if (FirstDifference(ea[i], aa[i], $"{path}[{i}]") is { } d) return d;
                return ea.Count == aa.Count ? null : $"{path}: expected {ea.Count} items, got {aa.Count}";
            case JsonValue ev when actual is JsonValue av:
                var x = ev.GetValue<JsonElement>();
                var y = av.GetValue<JsonElement>();
                bool same = x.ValueKind == y.ValueKind && x.ValueKind switch
                {
                    JsonValueKind.Number => x.GetDecimal() == y.GetDecimal(),
                    JsonValueKind.String => x.GetString() == y.GetString(),
                    _ => true,
                };
                return same ? null : $"{path}: expected {x.GetRawText()}, got {y.GetRawText()}";
            default:
                return $"{path}: expected {expected.ToJsonString()}, got {actual.ToJsonString()}";
        }
    }
}
