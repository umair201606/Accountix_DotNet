// Golden fixtures compared node by node or byte for byte (Load / Node / Equal / Bytes).
// Shared by: Accountix.Accounting.Tests, Accountix.Executive.Tests, Accountix.Finance.Tests (each aliases it as `Golden`).
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Accountix.TestSupport;

/// <summary>
/// Fixed expectations. Each <c>Golden/&lt;name&gt;.json</c> is the Python app's answer for one
/// test's own data, captured once on 29 Oct 2026 (before the Python app was retired) by the
/// script in <c>Golden/capture/</c> that bears the test's name. A test builds the same data through
/// .NET only, normalises its answer as the capture did (ids → codes or roles, generated numbers
/// → their order) and compares it here, so the protection the live parity test gave stays.
/// <list type="bullet">
/// <item>Numbers compare as numbers (Python wrote floats, .NET decimals), to 1e-9 relative.</item>
/// <item><c>ACCOUNTIX_GOLDEN_PERTURB=1</c>: each comparison is also run on a perturbed copy of the
/// .NET answer (first and last number changed, or text when there is none) and must then fail —
/// proof that the fixture really guards the figures.</item>
/// <item><c>ACCOUNTIX_GOLDEN_DIFF=&lt;dir&gt;</c>: on a mismatch, .NET's normalised answer is written there
/// (<c>ACCOUNTIX_GOLDEN_DUMP=&lt;dir&gt;</c>: always).</item>
/// </list>
/// </summary>
public static class GoldenBytes
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Dir => Path.Combine(AppContext.BaseDirectory, "Golden");

    public static JsonNode Load(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Dir, name + ".json")))
        ?? throw new InvalidOperationException($"Golden/{name}.json is empty");

    public static byte[] Bytes(string file) => File.ReadAllBytes(Path.Combine(Dir, file));

    public static JsonNode? Node(object? value) =>
        value as JsonNode ?? JsonSerializer.SerializeToNode(value, Options);

    /// <summary>.NET's normalised answer equals the fixture <paramref name="name"/>.</summary>
    public static void Equal(string name, object? actual, double tolerance = 1e-9) =>
        Equal(Load(name), actual, name, tolerance);

    /// <summary>.NET's normalised answer equals <paramref name="expected"/> (a part of a fixture).</summary>
    public static void Equal(JsonNode? expected, object? actual, string what, double tolerance = 1e-9)
    {
        var node = Node(actual);
        var diffs = new List<string>();
        Compare(expected, node, "$", tolerance, diffs);
        if (Environment.GetEnvironmentVariable("ACCOUNTIX_GOLDEN_DUMP") is { Length: > 0 } all)
        {
            Directory.CreateDirectory(all);
            File.WriteAllText(Path.Combine(all, Safe(what) + ".net.json"), node?.ToJsonString(Options) ?? "null");
        }
        if (diffs.Count > 0)
        {
            if (Environment.GetEnvironmentVariable("ACCOUNTIX_GOLDEN_DIFF") is { Length: > 0 } dir)
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, Safe(what) + ".net.json"), node?.ToJsonString(Options) ?? "null");
            }
            Assert.Fail($"{what}: .NET differs from the Python golden in {diffs.Count} place(s):\n  " +
                        string.Join("\n  ", diffs.Take(25)));
        }
        if (Environment.GetEnvironmentVariable("ACCOUNTIX_GOLDEN_PERTURB") is { Length: > 0 })
            foreach (var (path, perturbed) in Perturbations(node))
            {
                var caught = new List<string>();
                Compare(expected, perturbed, "$", tolerance, caught);
                Assert.True(caught.Count > 0, $"{what}: perturbing {path} of the .NET answer went unnoticed");
            }
    }

    private static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    private static void Compare(JsonNode? e, JsonNode? a, string path, double tol, List<string> diffs)
    {
        if (diffs.Count > 50) return;
        var ek = Kind(e);
        var ak = Kind(a);
        if (ek != ak)
        {
            diffs.Add($"{path}: Python {Show(e)}, .NET {Show(a)}");
            return;
        }
        switch (ek)
        {
            case JsonValueKind.Object:
            {
                var eo = e!.AsObject();
                var ao = a!.AsObject();
                foreach (var (k, v) in eo)
                    if (!ao.ContainsKey(k)) diffs.Add($"{path}.{k}: missing in .NET (Python {Show(v)})");
                    else Compare(v, ao[k], $"{path}.{k}", tol, diffs);
                foreach (var (k, v) in ao)
                    if (!eo.ContainsKey(k)) diffs.Add($"{path}.{k}: only in .NET ({Show(v)})");
                break;
            }
            case JsonValueKind.Array:
            {
                var ea = e!.AsArray();
                var aa = a!.AsArray();
                if (ea.Count != aa.Count)
                    diffs.Add($"{path}: {ea.Count} items in Python, {aa.Count} in .NET");
                for (int i = 0; i < Math.Min(ea.Count, aa.Count); i++)
                    Compare(ea[i], aa[i], $"{path}[{i}]", tol, diffs);
                break;
            }
            case JsonValueKind.Number:
            {
                double x = Num(e!), y = Num(a!);
                if (Math.Abs(x - y) > tol * Math.Max(1, Math.Abs(x)))
                    diffs.Add($"{path}: Python {Show(e)}, .NET {Show(a)}");
                break;
            }
            case JsonValueKind.String:
                if (e!.GetValue<string>() != a!.GetValue<string>())
                    diffs.Add($"{path}: Python {Show(e)}, .NET {Show(a)}");
                break;
            case JsonValueKind.True:
                if (e!.GetValue<bool>() != a!.GetValue<bool>())
                    diffs.Add($"{path}: Python {Show(e)}, .NET {Show(a)}");
                break;
        }
    }

    private static JsonValueKind Kind(JsonNode? n) => n switch
    {
        null => JsonValueKind.Null,
        JsonObject => JsonValueKind.Object,
        JsonArray => JsonValueKind.Array,
        // True and False are one kind for the comparison; the text tells them apart.
        _ => n.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? JsonValueKind.True : n.GetValueKind(),
    };

    private static double Num(JsonNode n) => double.Parse(n.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static string Show(JsonNode? n)
    {
        string s = n?.ToJsonString() ?? "null";
        return s.Length > 160 ? s[..160] + "…" : s;
    }

    /// <summary>Copies of the answer with one leaf changed: the first and the last number (else text, else a bool).</summary>
    private static IEnumerable<(string Path, JsonNode? Node)> Perturbations(JsonNode? node)
    {
        var leaves = new List<(string Path, JsonValueKind Kind)>();
        Leaves(node, "$", leaves);
        var all = Enumerable.Range(0, leaves.Count);
        var pick = all.Where(i => leaves[i].Kind == JsonValueKind.Number).ToList();
        if (pick.Count == 0) pick = all.Where(i => leaves[i].Kind == JsonValueKind.String).ToList();
        if (pick.Count == 0) pick = all.Where(i => leaves[i].Kind is JsonValueKind.True or JsonValueKind.False).ToList();
        if (pick.Count == 0)
        {
            if (node is JsonArray { Count: > 0 } arr)
            {
                var copy = arr.DeepClone().AsArray();
                copy.RemoveAt(copy.Count - 1);
                yield return ("$ (last item dropped)", copy);
            }
            yield break;
        }
        foreach (int target in new[] { pick[0], pick[^1] }.Distinct())
            yield return (leaves[target].Path, CloneChanging(node, target, new int[1]));
    }

    /// <summary>Every non-null leaf, in document order (the order <see cref="CloneChanging"/> counts in).</summary>
    private static void Leaves(JsonNode? n, string path, List<(string, JsonValueKind)> into)
    {
        switch (n)
        {
            case JsonObject o:
                foreach (var (k, v) in o) Leaves(v, $"{path}.{k}", into);
                break;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++) Leaves(a[i], $"{path}[{i}]", into);
                break;
            case null:
                break;
            default:
                into.Add((path, n.GetValueKind()));
                break;
        }
    }

    /// <summary>A deep copy with the <paramref name="target"/>-th leaf changed (walks the tree, never parses a path).</summary>
    private static JsonNode? CloneChanging(JsonNode? n, int target, int[] seen)
    {
        switch (n)
        {
            case JsonObject o:
            {
                var copy = new JsonObject();
                foreach (var (k, v) in o) copy[k] = CloneChanging(v, target, seen);
                return copy;
            }
            case JsonArray a:
            {
                var copy = new JsonArray();
                foreach (var v in a) copy.Add(CloneChanging(v, target, seen));
                return copy;
            }
            case null:
                return null;
            default:
                return seen[0]++ == target ? Changed(n) : n.DeepClone();
        }
    }

    private static JsonNode Changed(JsonNode leaf) => leaf.GetValueKind() switch
    {
        JsonValueKind.Number => JsonValue.Create(Num(leaf) + Math.Max(0.01, Math.Abs(Num(leaf)) * 1e-6)),
        JsonValueKind.String => JsonValue.Create(leaf.GetValue<string>() + "~"),
        _ => JsonValue.Create(leaf.GetValueKind() != JsonValueKind.True),
    };
}
