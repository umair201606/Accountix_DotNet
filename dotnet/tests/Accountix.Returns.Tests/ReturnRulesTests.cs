using System.Text.Json;
using Accountix.Invoicing.Services;
using Accountix.Returns.Services;

namespace Accountix.Returns.Tests;

/// <summary>validate_return and _over_returned, in Python's words.</summary>
public class ReturnRulesTests
{
    private static PyData Data(string json) => new(JsonDocument.Parse(json).RootElement.Clone());

    [Fact]
    public void Validation_lists_every_problem_in_order()
    {
        var errors = ReturnRules.Validate(Data("""
            {"items": [{"current_return_qty": -1, "max_returnable_qty": 3},
                       {"current_return_qty": "2.5", "max_returnable_qty": 2}]}
            """));
        Assert.Equal(
        [
            "Original invoice is required",
            "Row 1: Return quantity cannot be negative",
            "Row 2: Return quantity (2.5) exceeds max returnable (2.0)",
        ], errors);
    }

    [Fact]
    public void Nothing_to_return_is_refused_and_a_hair_over_the_max_is_allowed()
    {
        Assert.Equal(["At least one item must have a return quantity greater than 0"],
                     ReturnRules.Validate(Data("""{"original_invoice_id": 4, "items": [{"current_return_qty": 0}]}""")));
        Assert.Empty(ReturnRules.Validate(Data("""{"original_invoice_id": 4, "items": [{"current_return_qty": 2.0005, "max_returnable_qty": 2}]}""")));
    }

    [Fact]
    public void A_missing_quantity_is_pythons_type_error()
    {
        Assert.Throws<PyValueException>(() => ReturnRules.Validate(Data("""{"items": [{"current_return_qty": null}]}""")));
    }

    [Fact]
    public void The_cap_is_what_was_sold_less_other_approved_returns_per_product()
    {
        // Product 7 sold on two lines (3 + 2), 1 already credited; product 9 sold 4.
        var sold = new (int?, double)[] { (7, 3), (9, 4), (7, 2), (null, 10) };
        var already = new (int?, double)[] { (7, 1) };
        var asked = Data("""
            {"items": [{"product_id": 7, "current_return_qty": 3}, {"product_id": "7", "current_return_qty": 1.5},
                       {"product_id": 9, "current_return_qty": 4}, {"product_id": 0, "current_return_qty": 99}]}
            """);
        Assert.Equal(["Product #7: returning 4.5 but only 4 remain returnable on the invoice"],
                     ReturnRules.OverReturned(sold, already, asked));
        Assert.Empty(ReturnRules.OverReturned(sold, [], asked));
    }

    [Fact]
    public void Products_to_lock_skip_junk_ids_and_empty_lines()
    {
        Assert.Equal([5, 6],
            ReturnRules.ProductIdsToLock(Data("""
                {"items": [{"product_id": 5, "current_return_qty": 1}, {"product_id": "x", "current_return_qty": 1},
                           {"product_id": 8, "current_return_qty": 0}, {"product_id": null, "current_return_qty": 2},
                           {"product_id": "6", "current_return_qty": "1"}]}
                """)));
    }
}
