// Golden fixtures compared line by line (Expected / Match / Same / SameInputs / Rel / RelDates).
// Shared by: Accountix.Inventory.Tests, Accountix.Invoicing.Tests, Accountix.Orders.Tests, Accountix.Returns.Tests (each aliases it as `Golden`).
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Accountix.TestSupport;

/// <summary>
/// Fixed expectations captured from the Python app on 29 Oct 2026, before it was removed.
/// <c>Golden/&lt;name&gt;.json</c> holds <c>{"captured": ..., "capture": "Golden/capture/&lt;script&gt;.py", "inputs": ..., "expected": ...}</c>;
/// the capture script ran the same steps through the Python app on its own fresh data and
/// normalised the answer exactly as the test normalises .NET's (ids → roles, dates relative
/// to the test's own dates, generated numbers → roles). The test builds its data through
/// .NET only and compares.
/// <para>
/// <c>ACCOUNTIX_GOLDEN_PERTURB=1</c> (or <c>=last</c>) changes one figure of the first (or last) line of
/// every .NET answer before the comparison: every golden test must then fail (the check that the comparison bites).
/// </para>
/// </summary>
public static class GoldenLines
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "Golden");

    public static JsonElement Load(string name)
    {
        string path = Path.Combine(Dir, name + ".json");
        Assert.True(File.Exists(path), $"golden fixture missing: {path}");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.Clone();
    }

    /// <summary>The fixture's <c>expected</c> (or <c>expected.&lt;step&gt;</c>) as lines.</summary>
    public static List<string> Expected(string name, string? step = null)
    {
        var e = Load(name).GetProperty("expected");
        if (step is not null) e = e.GetProperty(step);
        return e.EnumerateArray().Select(x => x.GetString()!).ToList();
    }

    /// <summary>.NET's normalised lines against the fixture, failing at the first difference.</summary>
    public static void Match(string name, IReadOnlyList<string> actual, string? step = null) =>
        Same(Expected(name, step), Perturb(actual), step is null ? name : $"{name} / {step}");

    /// <summary>The inputs the capture used must be the inputs the test uses (else the fixture is for other steps).</summary>
    public static void SameInputs(string name, object inputs)
    {
        string want = Canonical(Load(name).GetProperty("inputs"));
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(inputs));
        string have = Canonical(doc.RootElement);
        Assert.True(want == have, $"{name}: the test's inputs are not the ones the fixture was captured for\nfixture: {want}\ntest:    {have}");
    }

    public static void Same(IReadOnlyList<string> expected, IReadOnlyList<string> actual, string what)
    {
        for (int i = 0; i < Math.Max(expected.Count, actual.Count); i++)
        {
            string a = i < expected.Count ? expected[i] : "<none>", b = i < actual.Count ? actual[i] : "<none>";
            if (a != b)
            {
                string dir = Path.Combine(Path.GetTempPath(), "accountix_golden");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, Regex.Replace(what, @"[^\w.-]+", "_") + ".actual.json");
                File.WriteAllText(file, JsonSerializer.Serialize(actual, new JsonSerializerOptions { WriteIndented = true }));
                Assert.Fail($"{what}: line {i} differs from the Python answer\nPython: {a}\n.NET:   {b}\n(.NET's lines written to {file})");
            }
        }
    }

    /// <summary>A date as days from the test's own anchor date: "D+0", "D-3".</summary>
    public static string Rel(DateOnly d, DateOnly anchor) { int n = d.DayNumber - anchor.DayNumber; return n >= 0 ? $"D+{n}" : $"D{n}"; }

    /// <summary>Every yyyy-MM-dd in <paramref name="s"/> as days from <paramref name="anchor"/>.</summary>
    public static string RelDates(string s, DateOnly anchor) =>
        Regex.Replace(s, @"\b(\d{4})-(\d{2})-(\d{2})\b", m => Rel(new DateOnly(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                                                                             int.Parse(m.Groups[3].Value)), anchor));

    private static IReadOnlyList<string> Perturb(IReadOnlyList<string> actual)
    {
        string? mode = Environment.GetEnvironmentVariable("ACCOUNTIX_GOLDEN_PERTURB");
        if (mode is not ("1" or "last")) return actual;
        var copy = actual.ToList();
        // "1": the first line with a figure; "last": the last one (a journal line, the chart data).
        int i = mode == "last" ? copy.FindLastIndex(l => Regex.IsMatch(l, @"[1-9]")) : copy.FindIndex(l => Regex.IsMatch(l, @"[1-9]"));
        if (i < 0) { copy.Add("perturbed"); return copy; }
        var m = Regex.Matches(copy[i], @"[1-9]").Last();
        copy[i] = copy[i][..m.Index] + (char)('1' + (copy[i][m.Index] - '0') % 9) + copy[i][(m.Index + 1)..];
        return copy;
    }

    private static string Canonical(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
                                     .Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", e.EnumerateArray().Select(Canonical)) + "]",
        // 30006 and 30006.0 are one number (C# and Python print a whole double differently).
        JsonValueKind.Number => e.GetDecimal().ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture),
        JsonValueKind.String => JsonSerializer.Serialize(e.GetString()),   // one escaping for both: "&" and "\u0026" are one text
        _ => e.GetRawText(),
    };
}
