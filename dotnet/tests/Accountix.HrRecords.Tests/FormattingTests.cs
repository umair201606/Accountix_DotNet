using System.Globalization;
using Accountix.HrRecords.Services;

namespace Accountix.HrRecords.Tests;

/// <summary>Python's printing of numbers and JSON, which the pages and the JSON answers copy byte for byte.</summary>
public class FormattingTests
{
    [Theory]
    [InlineData(0.1, "0.1")]
    [InlineData(7.0, "7.0")]
    [InlineData(-2.5, "-2.5")]
    [InlineData(1e15, "1000000000000000.0")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1.5e16, "1.5e+16")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.00001, "1e-05")]
    [InlineData(123.456, "123.456")]
    [InlineData(0.0, "0.0")]
    public void Float_prints_as_python_repr(double value, string expected) => Assert.Equal(expected, Py.Float(value));

    [Theory]
    [InlineData(2.675, 2, 2.67)]      // 2.67499999... in binary
    [InlineData(1.005, 2, 1.0)]
    [InlineData(0.125, 2, 0.12)]      // a true tie: to even
    [InlineData(0.375, 2, 0.38)]
    [InlineData(2.5, 0, 2.0)]
    [InlineData(-1.255, 2, -1.25)]
    [InlineData(1234.5678, 1, 1234.6)]
    public void Round_is_pythons_round(double value, int digits, double expected) => Assert.Equal(expected, Py.Round(value, digits));

    [Fact]
    public void Json_is_flask_jsonify()
    {
        var v = new List<object?>
        {
            new Dictionary<string, object?> { ["title"] = "é <b>&'\"\n", ["id"] = 1, ["z"] = 1.5, ["a"] = null, ["ok"] = true, ["f"] = 2.0 },
        };
        Assert.Equal("[{\"a\":null,\"f\":2.0,\"id\":1,\"ok\":true,\"title\":\"\\u00e9 <b>&'\\\"\\n\",\"z\":1.5}]\n", PyJson.Serialize(v));
    }

    /// <summary>
    /// Golden/formatting.json (capture/formatting.py): Python's repr, round(v, 2), round(v * 24, 2),
    /// round(v, 1) and %g of 400 floats of four kinds (hand-typed figures, any magnitude, hours from
    /// seconds, signed thousandths), and Flask's jsonify bytes for text and floats.
    /// </summary>
    [Fact]
    public void Floats_rounding_and_json_match_python_on_many_values()
    {
        var golden = Golden.Load("formatting");
        var rows = golden.GetProperty("rows").EnumerateArray().Select(r => r.EnumerateArray().Select(x => x.GetString()!).ToArray()).ToList();
        Assert.Equal(400, rows.Count);
        var values = rows.Select(r => double.Parse(r[0], NumberStyles.Float, CultureInfo.InvariantCulture)).ToList();
        var expected = rows.Select(r => string.Join(" | ", r)).ToList();
        var actual = values.Select(v => string.Join(" | ", Py.Float(v), Py.Float(Py.Round(v, 2)), Py.Float(Py.Round(v * 24, 2)),
                                                    Py.Float(Py.Round(v, 1)), Py.G(v))).ToList();
        Golden.SameRows(expected, actual, "repr | round 2 | round x24 | round 1 | %g");
        string mine = PyJson.Serialize(new Dictionary<string, object?> { ["s"] = "Ünïcødé ✓ \u0007 \t", ["v"] = values.Take(5).Cast<object?>().ToList() });
        Golden.SameText(golden.GetProperty("jsonify").GetString()!, mine, "jsonify");
    }
}
