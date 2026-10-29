using Accountix.Executive.Services;

namespace Accountix.Executive.Tests;

/// <summary>The rules that need no database: FIFO layers, buckets, Python's number formats.</summary>
public class UnitTests
{
    private static readonly ChartNode Acct = new(1, "1-01-02-01-0101", "Party", "asset", null, 5, null);

    [Fact]
    public void Module_is_registered_under_its_key() =>
        Assert.Equal(Accountix.Core.Entities.Modules.Executive, new ExecutiveModule().Key);

    [Fact]
    public void A_receipt_consumes_the_oldest_layer_first()
    {
        var asOf = new DateOnly(2026, 6, 30);
        var rows = new List<(DateOnly, decimal, decimal)>
        {
            (asOf.AddDays(-45), 1000m, 0m), (asOf.AddDays(-10), 400m, 0m), (asOf.AddDays(-5), 0m, 700m),
        };
        var p = ExecutiveReports.AgeParty(1, Acct, rows, 700m, asOf, ExecutiveReports.Receivable)!;
        // 700 received takes the 45-day layer down to 300; the 10-day layer is untouched.
        Assert.Equal([(45, 300m), (10, 400m)], p.Layers);
        Assert.Equal(45, p.OldestDays);
        Assert.Equal((45 * 300.0 + 10 * 400.0) / 700.0, p.AvgDays);
    }

    [Fact]
    public void An_advance_waits_for_the_invoice_it_settles()
    {
        var asOf = new DateOnly(2026, 6, 30);
        var rows = new List<(DateOnly, decimal, decimal)> { (asOf.AddDays(-30), 0m, 400m), (asOf.AddDays(-20), 1000m, 0m) };
        var p = ExecutiveReports.AgeParty(1, Acct, rows, 600m, asOf, ExecutiveReports.Receivable)!;
        Assert.Equal([(20, 600m)], p.Layers);
    }

    [Fact]
    public void Payables_age_from_credits_and_future_postings_are_zero_days_old()
    {
        var asOf = new DateOnly(2026, 6, 30);
        var rows = new List<(DateOnly, decimal, decimal)> { (asOf.AddDays(-70), 0m, 500m), (asOf.AddDays(3), 0m, 200m), (asOf.AddDays(-1), 100m, 0m) };
        var p = ExecutiveReports.AgeParty(1, Acct, rows, 600m, asOf, ExecutiveReports.Payable)!;
        Assert.Equal([(70, 400m), (0, 200m)], p.Layers);
    }

    [Fact]
    public void Buckets_share_and_width_are_scaled_across_both_sides()
    {
        var recv = ExecutiveReports.AgingSideOf([
            new AgedParty { AccountId = 1, Name = "A", Amount = 750m, AvgDays = 10, OldestDays = 40, Layers = [(40, 250m), (5, 500m)] },
        ]);
        var pay = ExecutiveReports.AgingSideOf([
            new AgedParty { AccountId = 2, Name = "B", Amount = 200m, AvgDays = 95, OldestDays = 95, Layers = [(95, 200m)] },
        ]);
        ExecutiveReports.ScaleBuckets(recv, pay);
        Assert.Equal([500m, 250m, 0m, 0m], recv.Buckets.Select(b => b.Amount));
        Assert.Equal([66.67m, 33.33m, 0m, 0m], recv.Buckets.Select(b => b.Share));
        Assert.Equal([100m, 50m, 0m, 0m], recv.Buckets.Select(b => b.Width));
        Assert.Equal([0m, 0m, 0m, 40m], pay.Buckets.Select(b => b.Width));
        Assert.Equal("A", recv.OldestName);
        Assert.Equal(100m, pay.Top[0].Share);
    }

    [Fact]
    public void Empty_sides_have_zero_widths_not_a_division_by_zero()
    {
        var recv = ExecutiveReports.AgingSideOf([]);
        var pay = ExecutiveReports.AgingSideOf([]);
        ExecutiveReports.ScaleBuckets(recv, pay);
        Assert.All(recv.Buckets.Concat(pay.Buckets), b => Assert.Equal(0m, b.Width));
        Assert.Equal(0.0, recv.AvgDays);
        Assert.Null(recv.OldestAccountId);
    }

    [Theory]
    [InlineData(14.0, "14.0")]
    [InlineData(171.33, "171.33")]
    [InlineData(-0.0, "-0.0")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1e15, "1000000000000000.0")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.00001, "1e-05")]
    [InlineData(57.995000000000005, "57.995000000000005")]
    [InlineData(123456789.125, "123456789.125")]
    public void Repr_prints_floats_as_python_does(double v, string expected) => Assert.Equal(expected, Py.Repr(v));

    [Theory]
    [InlineData(0.265, 2, 0.27)]     // exact binary value is just above the tie
    [InlineData(0.285, 2, 0.28)]     // just below
    [InlineData(2.675, 2, 2.67)]
    [InlineData(0.125, 2, 0.12)]     // a true tie goes to even
    [InlineData(0.375, 2, 0.38)]
    [InlineData(-1.005, 2, -1.0)]
    [InlineData(171.333333, 2, 171.33)]
    public void Round_is_pythons_round(double x, int digits, double expected) => Assert.Equal(expected, Py.Round(x, digits));

    [Theory]
    [InlineData("1500.0000", "1.5E+3")]
    [InlineData("10", "1E+1")]
    [InlineData("2.5000", "2.5")]
    [InlineData("7", "7")]
    [InlineData("-120.50", "-120.5")]
    public void Normalize_prints_as_python_decimal(string value, string expected) =>
        Assert.Equal(expected, Py.Normalized(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Comma_format_rounds_half_even_like_python_decimal()
    {
        Assert.Equal("1,234.56", Py.Comma2(1234.565m));
        Assert.Equal("1,234.58", Py.Comma2(1234.575m));
        Assert.Equal("-16,497,500.00", Py.Comma2(-16497500m));
    }

    [Fact]
    public void Delta_is_undefined_from_zero_or_a_loss()
    {
        Assert.Null(ExecutiveReports.Delta(100m, 0m));
        Assert.Null(ExecutiveReports.Delta(100m, -5m));
        Assert.Null(ExecutiveReports.Delta(100m, null));
        Assert.Equal(25m, ExecutiveReports.Delta(125m, 100m));
    }

    [Fact]
    public void Nice_steps_are_round()
    {
        Assert.Equal(200000.0, ProfitChart.NiceStep(725000));
        Assert.Equal(2.5, ProfitChart.NiceStep(9));
        Assert.Equal(1.0, ProfitChart.NiceStep(0));
    }

    [Fact]
    public void Dates_parse_as_the_python_route_reads_them()
    {
        Assert.Equal(new DateOnly(2026, 6, 30), Controllers.ExecutiveController.ParseDate("2026-06-30"));
        Assert.Equal(new DateOnly(2026, 6, 30), Controllers.ExecutiveController.ParseDate("30/06/2026"));
        Assert.Equal(new DateOnly(2026, 1, 5), Controllers.ExecutiveController.ParseDate("2026-1-5"));
        Assert.Null(Controllers.ExecutiveController.ParseDate("yesterday"));
        Assert.Null(Controllers.ExecutiveController.ParseDate(""));
    }

    /// <summary>
    /// 300 chart-like values and Python's repr(v), repr(round(v, 2)) and round(v) for each, captured from the
    /// interpreter (Golden/capture/unit_repr.py): the values travel as repr text, so they parse back exactly.
    /// </summary>
    [Fact]
    public void Repr_and_round_agree_with_python_on_chart_like_values()
    {
        var golden = Golden.Load("UnitRepr");
        var values = golden["values"]!.AsArray().Select(v => double.Parse(v!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(300, values.Count);
        Golden.Equal(golden["out"], values.Select(v => new object[] { Py.Repr(v), Py.Repr(Py.Round(v, 2)), Py.RoundInt(v) }), "repr / round");
    }
}
