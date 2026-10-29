using Accountix.Parties.Controllers;
using Accountix.Parties.Services;
using static Accountix.Parties.Services.TrackingRules;

namespace Accountix.Parties.Tests;

/// <summary>The pure rules of shared/payment_tracking.py, tracking.py and shared/master_data.py.</summary>
public class RulesTests
{
    [Theory]
    [InlineData("Net 30", 30)]
    [InlineData("30 days", 30)]
    [InlineData("Due on Receipt", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("Net 0", 0)]
    [InlineData("Net 3651", null)]
    [InlineData("Net 99999999999999999999", null)]
    public void Payment_terms_give_credit_days(string? terms, int? days) => Assert.Equal(days, TermsDays(terms));

    [Fact]
    public void Due_dates_age_into_the_standard_buckets()
    {
        var issued = new DateOnly(2026, 1, 10);
        Assert.Equal(new DateOnly(2026, 2, 9), EffectiveDueDate(null, issued, "Net 30"));
        Assert.Equal(issued, EffectiveDueDate(null, issued, "Net 0"));      // no grace invented
        Assert.Equal(new DateOnly(2026, 1, 1), EffectiveDueDate(new DateOnly(2026, 1, 1), issued, "Net 30"));
        Assert.Equal(0, OverdueDays(new DateOnly(2026, 3, 1), new DateOnly(2026, 2, 1)));
        Assert.Equal(28, OverdueDays(new DateOnly(2026, 2, 1), new DateOnly(2026, 3, 1)));
        Assert.Equal(["not_due", "d1_30", "d1_30", "d31_60", "d61_90", "d90_plus"],
                     new[] { 0, 1, 30, 31, 90, 91 }.Select(AgeBucket));
        Assert.Equal("1 – 30 days", BucketLabel("d1_30"));
    }

    [Fact]
    public void Assignment_and_payment_states()
    {
        Assert.Equal("unassigned", AssignState(5000, 0));
        Assert.Equal("unassigned", AssignState(5000, 0.01m));
        Assert.Equal("partial", AssignState(5000, 1000));
        Assert.Equal("partial", AssignState(5000, 1000, 1000));       // partly forced, money still loose
        Assert.Equal("assigned", AssignState(5000, 4999.99m));
        Assert.Equal("forced", AssignState(5000, 5000, 2000));
        Assert.Equal("review", AssignState(1000, 3000));
        Assert.Equal(["unpaid", "partial", "paid", "paid"], new[] { 0m, 100m, 2999.99m, 3000m }.Select(s => PayState(3000, s)));
    }

    [Fact]
    public void Direction_decides_whether_a_party_line_is_money()
    {
        Assert.Equal("receipt", FlowOf("customer", 0, 5000, "CRV"));
        Assert.Equal("receipt", FlowOf("customer", null, 5000, "JV"));
        Assert.Null(FlowOf("customer", 5000, 0, "CRV"));              // the invoice's own posting
        Assert.Null(FlowOf("customer", 0, 5000, "CPV"));
        Assert.Equal("payment", FlowOf("supplier", 900, 0, "BPV"));
        Assert.Null(FlowOf("supplier", 0, 900, "BPV"));
        Assert.Null(FlowOf("supplier", 0.004m, 0, "BPV"));
        Assert.Equal("1-01-02-01-0105", EntityCode("customer", 5));
        Assert.Equal("2-01-01-01-10000", EntityCode("supplier", 9900));
        Assert.Equal(-42, LedgerPartyId(42));
        Assert.True(IsLedgerParty(-42));
        Assert.False(IsLedgerParty(42));
        Assert.Equal(2.35m, Q(2.345m));                              // half up, as Decimal quantize
        Assert.Equal("5000.0", PyFloat(5000m));
        Assert.Equal("1,234.50", Comma2(1234.5m));
        Assert.Equal("05 Oct 2026", FmtDate(new DateOnly(2026, 10, 5)));
    }

    [Fact]
    public void Query_string_helpers_read_as_the_routes_do()
    {
        Assert.Equal(("customer", 12), TrackingController.SplitParty("customer:12"));
        Assert.Equal(("supplier", -77), TrackingController.SplitParty("supplier:-77"));
        Assert.Equal(("", null), TrackingController.SplitParty("employee:3"));
        Assert.Equal(("", null), TrackingController.SplitParty("customer:"));
        Assert.Equal(("", null), TrackingController.SplitParty("customer:1a"));
        Assert.Equal(new DateTime(2026, 3, 4), TrackingController.ParseDate("2026-03-04"));
        Assert.Equal(new DateTime(2026, 3, 4), TrackingController.ParseDate("04/03/2026"));
        Assert.Null(TrackingController.ParseDate("nope"));
        Assert.Equal("customer:12+x", QueryText.Encode("customer:12 x"));
    }

    [Fact]
    public void Master_data_reads_cells_and_numbers_as_python_does()
    {
        Assert.Equal("3001234567", MasterData.Text(3001234567.0));
        Assert.Equal("2.5", MasterData.Text(2.5));
        Assert.Equal("", MasterData.Text(true));
        Assert.Equal("Ali Traders", MasterData.SingleLine("  Ali \t Traders "));
        Assert.Equal("ali traders", MasterData.Key(" ALI  Traders"));
        Assert.Equal(1250.5m, MasterData.Number("1,250.50", "Credit limit"));
        Assert.Equal(0m, MasterData.Number("", "Credit limit"));
        Assert.Equal("Credit limit cannot be negative.", Assert.Throws<MasterDataInvalid>(() => MasterData.Number("-1", "Credit limit")).Message);
        Assert.Equal("Credit limit must be a number (got 'abc').", Assert.Throws<MasterDataInvalid>(() => MasterData.Number("abc", "Credit limit")).Message);
        Assert.Equal("Credit limit must be a number.", Assert.Throws<MasterDataInvalid>(() => MasterData.Number("Infinity", "Credit limit")).Message);
        Assert.Equal("Credit limit is too large.", Assert.Throws<MasterDataInvalid>(() => MasterData.Number("1e13", "Credit limit")).Message);
        Assert.Equal("a; b; and 1 more", MasterData.Summarise(["a", "b", "c"], 2));
        var rows = MasterData.SubmittedRows("""[{"name":"A"},{"name":"B","_delete":true},{"name":"","notes":""},3,{"_x":"y"}]""");
        Assert.Single(rows);
        Assert.Throws<MasterDataInvalid>(() => MasterData.SubmittedRows("{}"));
        Assert.Throws<MasterDataInvalid>(() => MasterData.SubmittedRows("[oops"));
    }
}
