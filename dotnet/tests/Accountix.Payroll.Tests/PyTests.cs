using Accountix.Payroll.Services;

namespace Accountix.Payroll.Tests;

/// <summary>Python's float semantics, which the payroll figures depend on to the paisa.</summary>
public class PyTests
{
    [Theory]
    [InlineData(2.675, 2, 2.67)]        // 2.675 is 2.67499999... in binary
    [InlineData(0.125, 2, 0.12)]        // exact tie: to even
    [InlineData(0.375, 2, 0.38)]
    [InlineData(1234.5, 0, 1234.0)]
    [InlineData(1235.5, 0, 1236.0)]
    [InlineData(-2.5, 0, -2.0)]
    [InlineData(4838.709677419355, 2, 4838.71)]
    [InlineData(1e-7, 2, 0.0)]
    public void Round_is_pythons_round(double x, int n, double expected) => Assert.Equal(expected, Py.Round(x, n));

    [Theory]
    [InlineData(12500.0, "12500.0")]
    [InlineData(0.1 + 0.2, "0.30000000000000004")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1e15, "1000000000000000.0")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(1e-5, "1e-05")]
    [InlineData(123.456, "123.456")]
    [InlineData(-4166.67, "-4166.67")]
    [InlineData(1.2345678901234568e17, "1.2345678901234568e+17")]
    [InlineData(0.0, "0.0")]
    public void Repr_is_pythons_repr(double x, string expected) => Assert.Equal(expected, Py.Repr(x));

    [Fact]
    public void Dec_is_Decimal_of_str()
    {
        Assert.Equal(0.30000000000000004m, Py.Dec(0.1 + 0.2));
        Assert.Equal(4838.71m, Py.Dec(Py.Round(150000.0 * 1 / 31, 2)));
    }

    [Fact]
    public void Sum_is_neumaier_compensated_as_in_python_312()
    {
        Assert.Equal(1.0, Py.Sum(Enumerable.Repeat(0.1, 10)));
        double naive = 0;
        for (int i = 0; i < 10; i++) naive += 0.1;
        Assert.NotEqual(1.0, naive);
        Assert.Equal(0.0, Py.Sum([]));
    }

    [Theory]
    [InlineData(1234567.5, 0, "1,234,568")]
    [InlineData(2.5, 0, "2")]
    [InlineData(55000.0, 0, "55,000")]
    [InlineData(1234.565, 2, "1,234.57")]
    [InlineData(-5.0, 0, "-5")]
    [InlineData(999.995, 2, "1,000.00")]
    public void Fixed_is_format_with_commas(double x, int decimals, string expected) => Assert.Equal(expected, Py.Fixed(x, decimals));

    [Fact]
    public void Amount_follows_format_amount()
    {
        Assert.Equal("1,23,45,678.50", Py.Amount(12345678.5, 2, "hi"));
        Assert.Equal("(1,234.50)", Py.Amount(-1234.5, 2, "en"));
        Assert.Equal("55,000", Py.Amount(55000, 0, "en"));
    }

    [Fact]
    public void Json_is_json_dumps()
    {
        var comps = new List<KeyValuePair<string, object?>>
        {
            new("allowances", new List<KeyValuePair<string, object?>> { new("House Rent", 12500.0), new("Café", 0.5) }),
            new("info", new List<KeyValuePair<string, object?>> { new("Days in month", Py.Num.Int(31)), new("Paid days", (Py.Num)28.0) }),
        };
        Assert.Equal("{\"allowances\": {\"House Rent\": 12500.0, \"Caf\\u00e9\": 0.5}, \"info\": {\"Days in month\": 31, \"Paid days\": 28.0}}",
                     Py.Dumps(comps));
    }

    [Theory]
    [InlineData("loan", "Loan")]
    [InlineData("salary_advance", "Salary_Advance")]
    [InlineData("ADVANCE", "Advance")]
    public void Title_is_str_title(string s, string expected) => Assert.Equal(expected, Py.Title(s));

    [Theory]
    [InlineData(" 5 ", 5.0)]
    [InlineData("1e3", 1000.0)]
    [InlineData("1,000", null)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    public void ParseFloat_is_float_or_none(string raw, double? expected) => Assert.Equal(expected, Py.ParseFloat(raw));
}
