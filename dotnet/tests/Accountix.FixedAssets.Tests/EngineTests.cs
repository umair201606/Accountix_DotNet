using Accountix.Core.Entities;
using Accountix.FixedAssets.Services;

namespace Accountix.FixedAssets.Tests;

/// <summary>
/// The depreciation engine without a database (tests/unit/test_fixed_assets.py's
/// arithmetic): charges are simulated the way the posting stores them — rounded
/// to the paisa, summed as live rows.
/// </summary>
public class EngineTests
{
    private static FixedAsset Asset(double cost = 1_000_000, double salvage = 100_000, int life = 5, string method = AssetCalc.StraightLine,
                                    DateOnly? purchase = null) => new()
    {
        AssetCode = "FA-T1", Name = "T1", PurchaseDate = purchase ?? new DateOnly(2024, 1, 1),
        PurchaseCost = Py.Dec(cost), SalvageValue = Py.Dec(salvage), UsefulLife = life, DepreciationMethod = method,
        Status = "active", IsActive = true,
    };

    /// <summary>Books what post_asset_depreciation would on <paramref name="on"/> (computed or manual); returns the charge.</summary>
    private sealed class Book(FixedAsset asset)
    {
        public List<(DateOnly Date, decimal Amount)> Rows { get; } = [];
        public AssetCalc Calc => new(asset, Rows.Count == 0 ? LiveCharges.None
            : new LiveCharges(Rows.Sum(r => r.Amount), Rows.Max(r => r.Date), Rows.Min(r => r.Date), Rows.Count));

        public double Post(DateOnly on, double? amount = null)
        {
            var calc = Calc;
            double charge = amount is double m ? Py.Round(Py.Min(m, calc.RemainingDepreciable), 2) : calc.DueDepreciation(on);
            if (charge <= 0 || asset.Status != "active") return 0;
            Rows.Add((on, Py.Dec(charge)));
            return charge;
        }

        public void Reverse(int index) => Rows.RemoveAt(index);

        public void Monthly(DateOnly start, int months)
        {
            for (int i = 1; i <= months; i++)
                Post(AssetCalc.AddMonths(start, i));
        }
    }

    [Fact]
    public void Straight_line_charges_evenly_without_drift()
    {
        var book = new Book(Asset());
        foreach (int year in new[] { 2025, 2026, 2027, 2028, 2029 })
            book.Post(new DateOnly(year, 1, 1));
        Assert.Equal([180_000m, 180_000m, 180_000m, 180_000m, 180_000m], book.Rows.Select(r => r.Amount));
        Assert.Equal(900_000, book.Calc.PostedDepreciation);
    }

    [Fact]
    public void Last_depreciation_date_is_the_latest_not_the_earliest()
    {
        var book = new Book(Asset());
        foreach (int year in new[] { 2025, 2026, 2027 })
            book.Post(new DateOnly(year, 1, 1));
        Assert.Equal(new DateOnly(2027, 1, 1), book.Calc.LastDepreciationDate);
    }

    [Theory]
    [InlineData(AssetCalc.StraightLine)]
    [InlineData(AssetCalc.DecliningBalance)]
    public void Depreciation_never_breaches_the_salvage_floor(string method)
    {
        var book = new Book(Asset(method: method));
        for (int year = 2025; year < 2040; year++)
            book.Post(new DateOnly(year, 1, 1));
        Assert.Equal(100_000, book.Calc.NetBookValue, 0.01);
        Assert.True(book.Calc.PostedDepreciation <= 900_000 + 0.01);
    }

    [Fact]
    public void A_manual_charge_is_capped_at_the_salvage_floor()
    {
        var book = new Book(Asset());
        Assert.Equal(900_000, book.Post(new DateOnly(2025, 1, 1), 5_000_000));
        Assert.Equal(100_000, book.Calc.NetBookValue, 0.01);
    }

    [Fact]
    public void A_reversed_period_becomes_chargeable_again()
    {
        var book = new Book(Asset());
        book.Post(new DateOnly(2025, 1, 1));
        book.Post(new DateOnly(2026, 1, 1));
        book.Reverse(1);
        Assert.Equal(180_000, book.Calc.PostedDepreciation);
        Assert.Equal(180_000, book.Calc.DueDepreciation(new DateOnly(2026, 1, 1)));
    }

    [Fact]
    public void Reducing_balance_reaches_salvage_exactly_at_end_of_life()
    {
        var book = new Book(Asset(method: AssetCalc.DecliningBalance));
        book.Monthly(new DateOnly(2024, 1, 1), 60);
        Assert.Equal(100_000, book.Calc.NetBookValue, 0.005);
        Assert.Equal(0, book.Calc.DueDepreciation(new DateOnly(2031, 1, 1)));
    }

    [Fact]
    public void Reducing_balance_front_loads_the_charge()
    {
        double ddb = new Book(Asset(method: AssetCalc.DecliningBalance)).Calc.DueDepreciation(new DateOnly(2025, 1, 1));
        double sl = new Book(Asset()).Calc.DueDepreciation(new DateOnly(2025, 1, 1));
        Assert.Equal(180_000, sl);
        Assert.True(ddb > sl);
        Assert.InRange(ddb, 330_000, 400_000);
    }

    [Fact]
    public void Straight_line_monthly_lands_on_salvage_to_the_paisa()
    {
        var book = new Book(Asset(cost: 1_000_000, salvage: 0, life: 3));
        book.Monthly(new DateOnly(2024, 1, 1), 36);
        Assert.Equal(0, book.Calc.NetBookValue, 0.005);
        Assert.Equal(1_000_000, book.Calc.PostedDepreciation, 0.005);
    }

    [Fact]
    public void Changing_the_life_replans_the_rest_prospectively()
    {
        var asset = Asset();                              // 900k over 5y, 15k a month
        var book = new Book(asset);
        book.Post(new DateOnly(2026, 1, 1));              // two years
        Assert.Equal(360_000, book.Calc.PostedDepreciation);
        asset.UsefulLife = 8;                             // 72 months left
        Assert.Equal(540_000.0 / 72 * 12, book.Calc.DueDepreciation(new DateOnly(2027, 1, 1)), 0.01);
        book.Monthly(new DateOnly(2026, 1, 1), 72);
        Assert.Equal(100_000, book.Calc.NetBookValue, 0.005);
    }

    [Fact]
    public void An_overdue_residual_is_charged_by_the_next_run()
    {
        var book = new Book(Asset(purchase: new DateOnly(2015, 1, 1)));
        Assert.Equal(900_000, book.Calc.DueDepreciation(new DateOnly(2026, 1, 1)));
    }

    [Fact]
    public void The_schedule_projects_to_salvage()
    {
        foreach (string method in new[] { AssetCalc.StraightLine, AssetCalc.DecliningBalance })
        {
            var book = new Book(Asset(method: method));
            book.Post(new DateOnly(2025, 1, 1));
            var rows = book.Calc.Schedule();
            Assert.Equal(48, rows.Sum(r => r.Months));
            Assert.Equal(book.Calc.NetBookValue, rows[0].Opening, 0.01);
            Assert.Equal(100_000, rows[^1].Closing, 0.01);
        }
    }

    [Fact]
    public void Inactive_assets_take_no_charge()
    {
        var asset = Asset();
        asset.Status = "disposed";
        Assert.Equal(0, new Book(asset).Post(new DateOnly(2025, 1, 1), 1000));
        Assert.Empty(new Book(asset).Calc.Schedule());
        Assert.Equal(0, new Book(asset).Calc.DueDepreciation(new DateOnly(2025, 1, 1)));
    }

    [Theory]
    [InlineData("2024-01-31", "2024-02-29", 0)]
    [InlineData("2024-01-31", "2024-03-31", 2)]
    [InlineData("2024-01-15", "2025-01-14", 11)]
    [InlineData("2024-01-15", "2025-01-15", 12)]
    [InlineData("2025-01-01", "2024-01-01", 0)]
    public void Months_are_calendar_months(string from, string to, int months) =>
        Assert.Equal(months, AssetCalc.MonthsBetween(DateOnly.Parse(from), DateOnly.Parse(to)));

    [Theory]
    [InlineData("2024-01-31", 1, "2024-02-29")]
    [InlineData("2024-11-30", 3, "2025-02-28")]
    [InlineData("2024-03-15", 12, "2025-03-15")]
    public void Add_months_clamps_the_day(string from, int n, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), AssetCalc.AddMonths(DateOnly.Parse(from), n));
}

/// <summary>Python's float semantics, checked on the cases that differ from .NET's defaults.</summary>
public class PyTests
{
    [Theory]
    [InlineData(2.675, 2, 2.67)]        // 2.675 is 2.67499999… in binary
    [InlineData(0.125, 2, 0.12)]        // exact tie: to even
    [InlineData(0.375, 2, 0.38)]
    [InlineData(-1.005, 2, -1.0)]
    [InlineData(1234567.891, 2, 1234567.89)]
    public void Round_is_pythons(double x, int n, double expected) => Assert.Equal(expected, Py.Round(x, n));

    [Theory]
    [InlineData(2.5, "2")]
    [InlineData(3.5, "4")]
    [InlineData(1234567.5, "1,234,568")]
    [InlineData(-0.4, "-0")]
    [InlineData(0.0, "0")]
    public void Comma_zero_is_pythons(double x, string expected) => Assert.Equal(expected, Py.Comma(x));

    [Fact]
    public void Comma_two_places()
    {
        Assert.Equal("1,234,567.89", Py.Comma(1234567.885, 2));      // binary value below the tie
        Assert.Equal("0.10", Py.Comma(0.1, 2));
        Assert.Equal("-12.35", Py.Comma(-12.345000001, 2));
    }

    [Theory]
    [InlineData(1.0, "1.0")]
    [InlineData(0.1, "0.1")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1234567890123456.0, "1234567890123456.0")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.00001, "1e-05")]
    [InlineData(250000.0, "250000.0")]
    public void Repr_is_pythons(double x, string expected) => Assert.Equal(expected, Py.Repr(x));

    [Fact]
    public void Sum_is_compensated_as_in_python_312()
    {
        // sum([0.1] * 10) == 1.0 on Python 3.12 (it was 0.9999999999999999 before).
        Assert.Equal(1.0, Py.Sum(Enumerable.Repeat(0.1, 10)));
        Assert.Equal(0.0, Py.Sum([]));
    }
}
