using System.Text.Json;

namespace Accountix.Hr.Tests;

/// <summary>
/// Python's answers, captured once (29 Oct 2026) into Golden/*.json by the scripts
/// in Golden/capture/, for steps the tests now take through .NET alone. A test
/// normalises .NET's answer the way the capture normalised Python's and compares.
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

    /// <summary>A JSON answer with its keys sorted, as the capture printed Python's (json.dumps(sort_keys=True)).</summary>
    public static string SortedJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Write(doc.RootElement);

        static string Write(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Object => "{" + string.Join(", ", e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => JsonSerializer.Serialize(p.Name) + ": " + Write(p.Value))) + "}",
            JsonValueKind.Array => "[" + string.Join(", ", e.EnumerateArray().Select(Write)) + "]",
            JsonValueKind.String => JsonSerializer.Serialize(e.GetString(), new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }),
            _ => e.GetRawText(),
        };
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
}
