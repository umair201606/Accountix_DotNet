using System.Text.Json;
using Accountix.Core.Shared;

namespace Accountix.Tests;

/// <summary>
/// Core's PyCompat against Python 3.12 itself: Fixtures/pycompat.json holds repr(x) and round(x, nd) for
/// 2,833 values (edge cases, random doubles, random bit patterns), recorded with the interpreter on 29 Oct 2026.
/// </summary>
public class PyCompatTests
{
    private sealed record Case(string X, string Repr, int Nd, string Round);

    private static IEnumerable<Case> Cases()
    {
        string file = Path.Combine(AppContext.BaseDirectory, "Fixtures", "pycompat.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
            yield return new Case(c.GetProperty("x").GetString()!, c.GetProperty("repr").GetString()!,
                                  c.GetProperty("nd").GetInt32(), c.GetProperty("round").GetString()!);
    }

    private static double D(string bits) => BitConverter.Int64BitsToDouble(long.Parse(bits));

    [Fact]
    public void Repr_and_round_match_python_on_every_recorded_value()
    {
        var wrong = new List<string>();
        foreach (var c in Cases())
        {
            double x = D(c.X);
            if (PyCompat.FloatRepr(x) != c.Repr)
                wrong.Add($"repr {c.Repr}: got {PyCompat.FloatRepr(x)}");
            double r = PyCompat.Round(x, c.Nd);
            if (BitConverter.DoubleToInt64Bits(r) != long.Parse(c.Round))
                wrong.Add($"round({c.Repr}, {c.Nd}) = {PyCompat.FloatRepr(D(c.Round))}: got {PyCompat.FloatRepr(r)}");
        }
        Assert.True(wrong.Count == 0, $"{wrong.Count} differences:\n" + string.Join("\n", wrong.Take(20)));
    }

    [Fact]
    public void Json_writes_non_finite_values_as_json_dumps_does()
    {
        Assert.Equal("NaN", PyCompat.FloatRepr(double.NaN, json: true));
        Assert.Equal("Infinity", PyCompat.FloatRepr(double.PositiveInfinity, json: true));
        Assert.Equal("-Infinity", PyCompat.FloatRepr(double.NegativeInfinity, json: true));
        Assert.Equal("nan", PyCompat.FloatRepr(double.NaN));
    }
}
