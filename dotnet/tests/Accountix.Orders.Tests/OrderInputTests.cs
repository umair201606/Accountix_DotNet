using System.Text.Json;
using Accountix.Core.Shared;
using Accountix.Invoicing.Services;
using Accountix.Orders.Services;

namespace Accountix.Orders.Tests;

/// <summary>The order JSON read as sales.py / purchases.py read it, and the template values printed as Jinja prints them.</summary>
public class OrderInputTests
{
    private static PyData Data(string json) => new(JsonDocument.Parse(json).RootElement.Clone());
    private static JsonElement? El(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData("\"2026-05-12\"", "2026-05-12")]
    [InlineData("\"2026-1-5\"", "2026-01-05")]      // strptime's %m / %d take one digit
    [InlineData("\"\"", null)]                       // falsy: no date
    [InlineData("0", null)]                          // the form's g() sends 0 for an empty field
    [InlineData("null", null)]
    public void Dates_are_read_as_strptime_reads_them(string json, string? expected) =>
        Assert.Equal(expected, OrderInput.StrpDate(El(json))?.ToString("yyyy-MM-dd"));

    [Theory]
    [InlineData("\"12/05/2026\"")]
    [InlineData("\"2026-02-30\"")]
    [InlineData("\"2026-05-12T10:00\"")]
    [InlineData("20260512")]
    public void A_date_strptime_refuses_is_a_server_error(string json) =>
        Assert.Throws<OrderInputException>(() => OrderInput.StrpDate(El(json)));

    [Fact]
    public void Ids_and_text_go_to_the_columns_as_python_hands_them_over()
    {
        Assert.Equal(12, OrderInput.Int(El("\"12\""), "customer_id"));
        Assert.Equal(12, OrderInput.Int(El(" 12"), "customer_id"));
        Assert.Equal(0, OrderInput.Int(El("0"), "customer_id"));         // then the foreign key refuses it (409)
        Assert.Throws<OrderInputException>(() => OrderInput.Int(El("null"), "customer_id"));
        Assert.Throws<OrderInputException>(() => OrderInput.Int(El("\"abc\""), "customer_id"));
        Assert.Null(OrderInput.OptionalId(Data("{\"party_account_id\": \"\"}"), "party_account_id"));
        Assert.Equal(7, OrderInput.OptionalId(Data("{\"party_account_id\": \"7\"}"), "party_account_id"));

        var d = Data("{\"notes\": 0, \"a\": true, \"b\": null, \"c\": \"x\"}");
        Assert.Equal("0", OrderInput.Text(d, "notes", ""));
        Assert.Equal("true", OrderInput.Text(d, "a", ""));
        Assert.Null(OrderInput.Text(d, "b", ""));
        Assert.Equal("", OrderInput.Text(d, "missing", ""));
        Assert.Throws<OrderInputException>(() => OrderInput.Float(Data("{\"x\": \"1,000\"}"), "x", 0));
        Assert.Equal(17.0, OrderInput.Float(Data("{\"x\": \"17\"}"), "x", 0));
    }

    [Fact]
    public void A_new_order_takes_the_number_it_was_sent_unless_blank_or_auto()
    {
        Assert.True(DocNumbers.IsPlaceholder(OrderInput.NewNumber(Data("{\"so_number\": \"(auto)\"}"), "so_number")));
        Assert.True(DocNumbers.IsPlaceholder(OrderInput.NewNumber(Data("{\"so_number\": \"\"}"), "so_number")));
        Assert.True(DocNumbers.IsPlaceholder(OrderInput.NewNumber(Data("{}"), "so_number")));
        Assert.Equal("SO-X", OrderInput.NewNumber(Data("{\"so_number\": \"SO-X\"}"), "so_number"));
        Assert.Matches("^SO-202605-0042$", DocNumbers.FromId("SO", new DateTime(2026, 5, 31, 23, 0, 0), 42));
    }

    [Fact]
    public void Items_must_be_a_list_of_rows()
    {
        Assert.Empty(OrderInput.Items(Data("{}")));
        Assert.Throws<OrderInputException>(() => OrderInput.Items(Data("{\"items\": null}")));
        Assert.Throws<OrderInputException>(() => OrderInput.Items(Data("{\"items\": {\"a\": 1}}")));
        Assert.Throws<OrderInputException>(() => OrderInput.Items(Data("{\"items\": [1]}")));
        var line = OrderInput.Line(Data("{\"product_id\": 3, \"quantity\": 0.1, \"unit_price\": 0.2}"));
        Assert.Equal((3, 0.1m, 0.2m, "pcs", ""), (line.ProductId, line.Quantity, line.UnitPrice, line.Unit, line.Description));
        Assert.Equal(1m, OrderInput.Line(Data("{\"product_id\": 3}")).Quantity);   // quantity defaults to 1
        Assert.Throws<OrderInputException>(() => OrderInput.Line(Data("{\"quantity\": 1}")));   // product_id NOT NULL
    }

    [Fact]
    public void The_editor_prints_what_jinja_prints()
    {
        var blank = new OrderFormModel();
        Assert.Equal(("unapproved", false, "0", "0", "active", "active", ""), (blank.VStatus, blank.IsApproved, blank.DroppedOrZero,
            blank.SalesTaxPctText, blank.DiscountGeneralActive, blank.TaxGeneralActive, blank.NotesText));
        Assert.True(blank.EditableUnless("delivered"));

        var saved = new OrderFormModel { Id = 5, Status = "approved", GlobalSalesTaxPct = 17m, Notes = null, TaxMode = "individual" };
        Assert.Equal(("approved", true, "", "17.0", "", "", "active", "None"), (saved.VStatus, saved.IsApproved, saved.DroppedOrZero,
            saved.SalesTaxPctText, saved.DiscountGeneralActive, saved.TaxGeneralActive, saved.TaxIndividualActive, saved.NotesText));
        Assert.Equal("", saved.TaxColumnStyle);
        Assert.False(saved.EditableUnless("delivered"));
        Assert.False(new OrderFormModel { Id = 5, Status = "cancelled" }.EditableUnless("received"));
        Assert.Equal("None", new OrderFormModel { Id = 5, Status = null }.VStatus);

        Assert.Equal(("None", "2026-01-05", "Unapproved", "None"),
            (Jinja.Date(null), Jinja.Date(new DateOnly(2026, 1, 5)), Jinja.Title("unapproved"), Jinja.Title(null)));
        Assert.Equal(0, OrderReads.WeightOr0(0m));
        Assert.Equal(2.5, OrderReads.WeightOr0(2.5m));
    }
}
