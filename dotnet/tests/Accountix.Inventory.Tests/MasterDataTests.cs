using Accountix.Inventory.Services;

namespace Accountix.Inventory.Tests;

/// <summary>shared/master_data.py's parsing rules, without a database, and against what Python answered (golden).</summary>
public class MasterDataTests
{
    private static readonly object?[] Inputs =
    [
        "", " 12 ", "1,250.50", "-3", "abc", "2.5", "1e3", "1_000", ".5", "5.", "inf", "NaN", "1e400", "10000000000000",
        "0.00001", "  ", null, true, 3001234567.0, 20.5, 7.0, 0.1, "12345678901234567890123", "+4", "1.2.3", "e5",
    ];

    private static string Outcome(Func<decimal> f)
    {
        try { return "ok:" + f().ToString(System.Globalization.CultureInfo.InvariantCulture); }
        catch (InvalidValueException e) { return "err:" + e.Message; }
    }

    [Fact]
    public void Numbers_are_read_as_python_reads_them()
    {
        Assert.Equal("ok:1250.50", Outcome(() => MasterData.Number("1,250.50", "Price")));
        Assert.Equal("ok:0", Outcome(() => MasterData.Number("  ", "Price")));
        Assert.Equal("err:Price cannot be negative.", Outcome(() => MasterData.Number("-3", "Price")));
        Assert.Equal("ok:-3", Outcome(() => MasterData.Number("-3", "Qty", minimum: null)));
        Assert.Equal("err:Price must be a number (got 'abc').", Outcome(() => MasterData.Number("abc", "Price")));
        Assert.Equal("err:Price must be a number.", Outcome(() => MasterData.Number("inf", "Price")));
        Assert.Equal("err:Price is too large.", Outcome(() => MasterData.Number("1e400", "Price")));
        Assert.Equal("err:Level must be a whole number.", Outcome(() => MasterData.Number("2.5", "Level", whole: true)));
        Assert.Equal("err:Price must be at least 1.", Outcome(() => MasterData.Number("0.5", "Price", minimum: 1m)));
    }

    [Fact]
    public void Text_and_floats_print_as_python_prints_them()
    {
        Assert.Equal("3001234567", MasterData.Text(3001234567.0));
        Assert.Equal("", MasterData.Text(true));
        Assert.Equal("", MasterData.Text(null));
        Assert.Equal("20.5", MasterData.Text(20.5));
        Assert.Equal("a b c", MasterData.SingleLine("  a \t b\n c "));
        Assert.Equal("100.0", MasterData.Float(100));
        Assert.Equal("0.1", MasterData.Float(0.1));
        Assert.Equal("1e-05", MasterData.Float(0.00001));
        Assert.Equal("10", MasterData.Qty(10.0000m));
        Assert.Equal("2.5", MasterData.Qty(2.5000m));
        Assert.Equal("None", MasterData.Str(null));
    }

    [Fact]
    public void Submitted_rows_drop_deleted_blank_and_non_object_entries()
    {
        var rows = MasterData.SubmittedRows("""[{"sku":"A"}, {"sku":"", "_x":"y"}, 5, {"sku":"B","_delete":true}, {"n": 3}]""");
        Assert.Equal(2, rows.Count);
        Assert.Equal("A", rows[0].Get("sku"));
        Assert.Equal(3L, rows[1].Get("n"));
        Assert.Empty(MasterData.SubmittedRows(""));
        Assert.Throws<InvalidValueException>(() => MasterData.SubmittedRows("{}"));
        var e = Assert.Throws<InvalidValueException>(() => MasterData.SubmittedRows("[" + string.Join(",", Enumerable.Repeat("{}", 2001)) + "]"));
        Assert.Equal("At most 2,000 rows can be saved at a time.", e.Message);
    }

    /// <summary>
    /// Every awkward input read as the Python app's shared/master_data.py read it (number, text,
    /// single_line), captured into Golden/master_data_parsing.json by Golden/capture/master_data.py.
    /// A parsed number is compared by value: Python printed a parsed Decimal as typed ("1E+3", "5.").
    /// </summary>
    [Fact]
    public void Number_and_text_read_odd_inputs_as_python_did()
    {
        Golden.SameInputs("master_data_parsing", Inputs);
        var lines = Inputs.Select((v, i) =>
        {
            string number = Outcome(() => MasterData.Number(v, "Price"));
            if (number.StartsWith("ok:"))
                number = "ok:" + decimal.Parse(number[3..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture)
                    .ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture);
            return $"{i}|{number}|{MasterData.Text(v)}|{MasterData.SingleLine(v)}";
        }).ToList();
        Golden.Match("master_data_parsing", lines);
    }
}
