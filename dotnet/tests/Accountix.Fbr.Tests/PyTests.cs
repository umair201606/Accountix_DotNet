using System.Globalization;
using Accountix.Fbr.Services;

namespace Accountix.Fbr.Tests;

/// <summary>Python's numbers, JSON and repr, as the payload and FBR's answers need them.</summary>
public class PyTests
{
    [Theory]
    [InlineData(2.675, 2, 2.67)]
    [InlineData(0.125, 2, 0.12)]
    [InlineData(0.375, 2, 0.38)]
    [InlineData(1.005, 2, 1.0)]
    [InlineData(-0.004, 2, -0.0)]
    [InlineData(1234.5, 0, 1234.0)]
    [InlineData(1235.5, 0, 1236.0)]
    public void Round_is_pythons(double x, int n, double expected)
    {
        double r = Py.Round(x, n);
        Assert.Equal(expected, r);
        Assert.Equal(double.IsNegative(expected), double.IsNegative(r));
    }

    [Theory]
    [InlineData(100.0, "100.0")]
    [InlineData(0.1, "0.1")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1e15, "1000000000000000.0")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.00001, "1e-05")]
    [InlineData(-0.0, "-0.0")]
    [InlineData(1.5e-7, "1.5e-07")]
    [InlineData(12345678901234567890.0, "1.2345678901234567e+19")]
    [InlineData(5e-324, "5e-324")]
    public void Float_repr_is_pythons(double x, string expected) => Assert.Equal(expected, Py.FloatRepr(x));

    [Fact]
    public void Dumps_writes_ints_and_floats_apart_and_escapes_as_python()
    {
        var v = new PyDict { ["a"] = 1L, ["b"] = 1.0, ["c"] = new List<object?>(), ["d"] = new PyDict(), ["e"] = "é\"\\\n\u007f😀", ["f"] = null, ["g"] = true };
        Assert.Equal("{\"a\": 1, \"b\": 1.0, \"c\": [], \"d\": {}, \"e\": \"\\u00e9\\\"\\\\\\n\\u007f\\ud83d\\ude00\", \"f\": null, \"g\": true}", Py.Dumps(v));
        Assert.Equal("{\n  \"a\": 1,\n  \"c\": [\n    1,\n    2.5\n  ]\n}", Py.Dumps(new PyDict { ["a"] = 1L, ["c"] = new List<object?> { 1L, 2.5 } }, indent: 2));
        Assert.Equal("{\"a\":1,\"b\":{\"x\":1,\"y\":2}}\n", Py.Jsonify(new PyDict { ["b"] = new PyDict { ["y"] = 2L, ["x"] = 1L }, ["a"] = 1L }));
    }

    /// <summary>
    /// 20,014 doubles across every magnitude money takes (and the awkward ones): repr(x),
    /// repr(round(x, 2)) and repr(round(x, 0)) as CPython printed them (Golden/CPythonRoundRepr.json,
    /// one "bits repr round2 round0" line per value; the inputs are the IEEE bits in the fixture).
    /// </summary>
    [Fact]
    public void Round_and_repr_match_cpython_on_many_values()
    {
        var actual = Golden.Load("CPythonRoundRepr").AsArray().Select(n =>
        {
            long bits = long.Parse(n!.GetValue<string>().Split(' ')[0], CultureInfo.InvariantCulture);
            double x = BitConverter.Int64BitsToDouble(bits);
            return $"{bits} {Py.FloatRepr(x)} {Py.FloatRepr(Py.Round(x, 2))} {Py.FloatRepr(Py.Round(x, 0))}";
        }).ToList();
        Assert.True(actual.Count > 20000);
        Golden.Match("CPythonRoundRepr", actual);
    }

    /// <summary>The values of <see cref="Round_and_repr_match_cpython_on_many_values"/> (how the fixture's inputs were made).</summary>
    internal static List<double> ManyValues()
    {
        var rng = new Random(20261029);
        var values = new List<double> { 2.675, 0.125, 1.005, 0.285, 1.115, 8.345, 0.5, 1.5, 2.5, -2.5, 1e16, 9007199254740993, 1e-7, 123456.785 };
        for (int i = 0; i < 20000; i++)
        {
            double scale = Math.Pow(10, rng.Next(-6, 14));
            double v = Math.Round(rng.NextDouble() * scale, rng.Next(0, 7)) * (rng.Next(4) == 0 ? -1 : 1);
            if (i % 3 == 0) v = rng.Next(0, 10_000_000) / 1000.0 + 0.005;   // ties of the decimal kind
            values.Add(v);
        }
        return values;
    }

    internal static readonly string[] Answers =
    [
        """{"validationResponse": {"statusCode": "01", "status": "Invalid", "errorCode": "0052", "error": "Provide proper HS Code", "invoiceStatuses": [{"itemSNo": "1", "statusCode": "01", "status": "Invalid", "errorCode": "0052", "error": "HS Code “x” isn't valid"}]}, "dated": "2026-10-29 10:00:00"}""",
        """{"fbrInvoiceNumber": "7000007DI1747119701593", "submissionId": 12345678901234567890, "amount": 1.50, "rate": 1e2, "neg": -0.0, "ok": true, "none": null, "s": "a'b", "t": "a'b\"c", "u": "tab\tnl\n€\u0007"}""",
        """[1, 2.5, "x", {"k": []}]""",
        """ "just a string" """,
        "{\"a\": 1, \"a\": 2, \"b\": 3}",
    ];

    /// <summary>
    /// FBR's answers parsed and written back (json.loads → json.dumps indent 2 / repr / sorted compact dumps)
    /// as CPython wrote them (Golden/CPythonAnswerRoundTrips.json).
    /// </summary>
    [Fact]
    public void Answers_read_and_written_back_match_cpython()
    {
        var actual = Answers.Select(a =>
        {
            var v = Py.Loads(a);
            return new { answer = a, indent2 = Py.Dumps(v, indent: 2), repr = Py.Repr(v), sorted = Py.Dumps(v, null, true, ",", ":") };
        }).ToList();
        Golden.Match("CPythonAnswerRoundTrips", actual);
    }
}
