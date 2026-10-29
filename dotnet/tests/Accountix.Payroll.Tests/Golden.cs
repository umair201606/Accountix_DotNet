using System.Text.Json;

namespace Accountix.Payroll.Tests;

/// <summary>
/// Fixed expectations captured from the Python app before it was removed
/// (Golden/*.json, written by the scripts in Golden/capture/, which say how).
/// </summary>
public static class Golden
{
    public static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "Golden", name);

    public static JsonElement Load(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(PathOf(name)));
        return doc.RootElement.Clone();
    }

    /// <summary>Row lists equal, row by row; a failure names the first row that differs and shows both lists.</summary>
    public static void Same(IReadOnlyList<string> expected, IReadOnlyList<string> actual, string what)
    {
        string Both() => $"\nPYTHON (golden):\n  {string.Join("\n  ", expected)}\n.NET:\n  {string.Join("\n  ", actual)}";
        for (int i = 0; i < Math.Min(expected.Count, actual.Count); i++)
            Assert.True(expected[i] == actual[i], $"{what}, row {i}:\n PYTHON {expected[i]}\n .NET   {actual[i]}{Both()}");
        Assert.True(expected.Count == actual.Count, $"{what}: Python {expected.Count} rows, .NET {actual.Count}{Both()}");
    }
}
