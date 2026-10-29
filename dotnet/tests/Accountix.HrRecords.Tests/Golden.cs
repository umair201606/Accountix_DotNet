using System.Text.Json;
using System.Text.RegularExpressions;

namespace Accountix.HrRecords.Tests;

/// <summary>
/// Python's answers, captured once (29 Oct 2026) into Golden/*.json by the scripts
/// in Golden/capture/, for data the tests now build through .NET alone. A test
/// normalises .NET's answer the way the capture normalised Python's (ids → the
/// world's keys, dates → days from the test's own "today") and compares.
/// </summary>
public static class Golden
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "Golden");

    public static JsonElement Load(string name)
    {
        string file = Path.Combine(Dir, name + ".json");
        Assert.True(File.Exists(file), $"golden fixture {file} missing (Golden/*.json is copied to the output)");
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        return doc.RootElement.Clone();
    }

    /// <summary>"id":123 → "id":"key" for the ids in <paramref name="keys"/> (unknown ids: "?123").</summary>
    public static string IdsToKeys(string json, IReadOnlyDictionary<int, string> keys, string field = "id") =>
        Regex.Replace(json, $"\"{Regex.Escape(field)}\":(\\d+)",
            m => $"\"{field}\":\"{(keys.TryGetValue(int.Parse(m.Groups[1].Value), out string? k) ? k : "?" + m.Groups[1].Value)}\"");

    /// <summary>The world's keys by id (the reverse of World.Ids).</summary>
    public static Dictionary<int, string> Keys(World w) =>
        w.Ids.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);

    /// <summary>Two texts equal, line by line, with the first difference spelled out.</summary>
    public static void SameText(string expected, string actual, string what)
    {
        if (expected == actual) return;
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        for (int i = 0; i < Math.Max(e.Length, a.Length); i++)
        {
            string el = i < e.Length ? e[i] : "<none>", al = i < a.Length ? a[i] : "<none>";
            if (el != al)
            {
                int c = 0;
                while (c < el.Length && c < al.Length && el[c] == al[c]) c++;
                int from = Math.Max(0, c - 60);
                throw new Xunit.Sdk.XunitException(
                    $"{what}: differs from Python's (golden) at line {i + 1}, column {c + 1}\n" +
                    $"  python: …{Cut(el, from)}\n  .net:   …{Cut(al, from)}");
            }
        }
        throw new Xunit.Sdk.XunitException($"{what}: differs from Python's (golden)");
    }

    /// <summary>Two row lists equal, with every differing row listed.</summary>
    public static void SameRows(IReadOnlyList<string> expected, IReadOnlyList<string> actual, string what)
    {
        var problems = new List<string>();
        for (int i = 0; i < Math.Max(expected.Count, actual.Count); i++)
        {
            string e = i < expected.Count ? expected[i] : "<none>", a = i < actual.Count ? actual[i] : "<none>";
            if (e != a) problems.Add($"  row {i}:\n    python: {e}\n    .net:   {a}");
        }
        Assert.True(problems.Count == 0,
            $"{what}: {problems.Count} row(s) differ from Python's (golden; {expected.Count} expected, {actual.Count} got)\n" +
            string.Join("\n", problems.Take(20)));
    }

    private static string Cut(string s, int from) => s.Length <= from ? "" : s[from..Math.Min(s.Length, from + 160)];
}
