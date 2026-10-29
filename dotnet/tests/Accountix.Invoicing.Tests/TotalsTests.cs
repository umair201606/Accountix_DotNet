using System.Text.Json;
using Accountix.Invoicing.Services;

namespace Accountix.Invoicing.Tests;

/// <summary>Python's float semantics, the figures every posting is built from.</summary>
public class PyFloatTests
{
    [Fact]
    public void Round_is_half_even_on_the_exact_binary_value()
    {
        double[] xs = [2.675, 1.005, 0.125, 0.375, 1.115, 2.5, -2.675, 1234.565, 0.285, 8.345];
        double[] py = [2.67, 1.0, 0.12, 0.38, 1.11, 2.5, -2.67, 1234.57, 0.28, 8.35];
        Assert.Equal(py, xs.Select(x => Py.Round(x, 2)).ToArray());
    }

    [Fact]
    public void Fixed_repr_and_g_print_as_python_does()
    {
        Assert.Equal(["0.12", "2.67", "0.00", "-0.00"], new[] { 0.125, 2.675, 1e-7, -0.001 }.Select(x => Py.Fixed(x, 2)));
        Assert.Equal(["1e-05", "1e+16", "1.2345678901234568e+17", "0.30000000000000004", "100.0", "0.00015", "12345678.9"],
            new[] { 1e-05, 1e16, 123456789012345680.0, 0.1 + 0.2, 100.0, 1.5e-4, 12345678.9 }.Select(Py.Repr));
        Assert.Equal(["2.5", "10", "1.23457e+06", "1e+06", "0.0001", "1e-05", "3", "1e-07"],
            new[] { 2.5, 10.0, 1234567.0, 999999.5, 0.0001, 0.00001, 3.0, 1e-7 }.Select(x => Py.G(x)));
    }

    [Fact]
    public void Sum_is_compensated_like_cpython_312_and_plus_equals_is_not()
    {
        var tenths = Enumerable.Repeat(0.1, 10).ToList();
        Assert.Equal(1.0, Py.Sum(tenths));
        Assert.Equal(1.0, Py.Sum([1e16, 1.0, -1e16]));
        double a = 0;
        foreach (double x in tenths) a += x;
        Assert.Equal("0.9999999999999999", Py.Repr(a));
    }

    [Fact]
    public void Amounts_format_with_brackets_and_both_groupings()
    {
        Assert.Equal("1,234,567.89", Py.Amount(1234567.891));
        Assert.Equal("(2.67)", Py.Amount(-2.675));
        Assert.Equal("12,34,567.00", Py.Amount(1234567.0, 2, "hi"));
        Assert.Equal("1,234", Py.Amount(1234.5, 0));   // 1234.5 is exact: half-even → 1234
    }

    [Fact]
    public void Json_is_written_as_flask_writes_it()
    {
        var obj = new Dictionary<string, object?> { ["b"] = 1.0, ["a"] = "é<", ["c"] = new List<object?> { null, true, 2 } };
        Assert.Equal("{\"a\":\"\\u00e9<\",\"b\":1.0,\"c\":[null,true,2]}", PyJson.Dumps(obj));
        Assert.Equal("{\"a\": \"\\u00e9\\u003c\", \"b\": 1.0, \"c\": [null, true, 2]}", PyJson.Dumps(obj, htmlSafe: true));
    }
}

/// <summary>shared/invoice_totals.py (tests/unit/test_invoice_splits.py).</summary>
public class InvoiceSplitTests
{
    [Fact]
    public void Allocate_splits_pro_rata_with_the_residual_on_the_last_bucket()
    {
        Assert.Equal([("a", 75.0), ("b", 25.0)], InvoiceTotals.Allocate(100, [("a", 3.0), ("b", 1.0)]));
        var three = InvoiceTotals.Allocate(100, [("a", 1.0), ("b", 1.0), ("c", 1.0)]);
        Assert.Equal([("a", 33.33), ("b", 33.33), ("c", 33.34)], three);
        Assert.Equal([("a", 50.0), ("b", 50.0)], InvoiceTotals.Allocate(100, [("a", 0.0), ("b", 0.0)]));
        Assert.Equal([("a", 1234.56)], InvoiceTotals.Allocate(1234.56, [("a", 7.0)]));
        Assert.Empty(InvoiceTotals.Allocate(0, [("a", 1.0)]));
        Assert.Empty(InvoiceTotals.Allocate<string>(100, []));
        foreach (double total in new[] { 100, 0.03, 999999.99, 1234.57 })
            Assert.Equal(Py.Round(total, 2), Py.Round(InvoiceTotals.Allocate(total, [("a", 2.0), ("b", 3.0), ("c", 5.0)]).Sum(x => x.Share), 2));
    }

    private static TLine Line(int? category, double value, double rate, double qty = 1) =>
        new() { CategoryId = category, Quantity = qty, UnitPrice = value / qty, TotalBeforeDiscount = value, SalesTaxPct = rate };

    [Fact]
    public void Revenue_splits_follow_the_category_map()
    {
        var none = new Dictionary<int, int>();
        Assert.Equal([((int?)null, 1000.0)], InvoiceTotals.RevenueSplits([Line(null, 600, 0), Line(null, 400, 0)], none, 1000));
        var map = new Dictionary<int, int> { [1] = 11, [2] = 22 };
        Assert.Equal(new Dictionary<int, double> { [11] = 600, [22] = 400 },
            InvoiceTotals.RevenueSplits([Line(1, 600, 0), Line(2, 400, 0)], map, 1000).ToDictionary(x => x.AccountId!.Value, x => x.Amount));
        var partial = new Dictionary<int, int> { [1] = 11 };
        Assert.Equal(new Dictionary<int, double> { [11] = 700, [-1] = 300 },
            InvoiceTotals.RevenueSplits([Line(1, 700, 0), Line(3, 300, 0)], partial, 1000).ToDictionary(x => x.AccountId ?? -1, x => x.Amount));
        // Absorbed charges ride along pro rata: 1,100 over 1,000 of goods.
        Assert.Equal(new Dictionary<int, double> { [11] = 550, [-1] = 550 },
            InvoiceTotals.RevenueSplits([Line(1, 500, 0), Line(null, 500, 0)], partial, 1100).ToDictionary(x => x.AccountId ?? -1, x => x.Amount));
        var three = new Dictionary<int, int> { [1] = 11, [2] = 22, [3] = 33 };
        Assert.Equal(1000.0, InvoiceTotals.RevenueSplits([Line(1, 333.33, 0), Line(2, 333.33, 0), Line(3, 333.33, 0)], three, 1000).Sum(x => x.Amount));
    }

    private static TDoc Doc(string taxMode = "individual", double globalRate = 0, double totalTax = 0) =>
        new() { TaxMode = taxMode, GlobalSalesTaxPct = globalRate, TotalTax = totalTax };

    [Fact]
    public void Output_tax_splits_follow_each_rate()
    {
        var none = new Dictionary<double, int>();
        Assert.Equal([((int?)null, 180.0)], InvoiceTotals.OutputTaxSplits(Doc(), [Line(null, 600, 18), Line(null, 400, 18)], [], none, 180));
        var rates = new Dictionary<double, int> { [18] = 18, [5] = 5 };
        Assert.Equal(new Dictionary<int, double> { [18] = 180, [5] = 50 },
            InvoiceTotals.OutputTaxSplits(Doc(), [Line(null, 1000, 18), Line(null, 1000, 5)], [], rates, 230).ToDictionary(x => x.AccountId!.Value, x => x.Amount));
        Assert.Equal([((int?)18, 180.0)], InvoiceTotals.OutputTaxSplits(Doc(), [Line(null, 1000, 18), Line(null, 5000, 0)], [], rates, 180));
        Assert.Equal([((int?)18, 180.0)], InvoiceTotals.OutputTaxSplits(Doc("general", 18), [Line(null, 600, 0), Line(null, 400, 0)], [], rates, 180));
        Assert.Equal(76.67, InvoiceTotals.OutputTaxSplits(Doc(), [Line(null, 333.33, 18), Line(null, 333.33, 5)], [], rates, 76.67).Sum(x => x.Amount));
    }

    [Fact]
    public void Example_b_splits_by_each_lines_own_tax()
    {
        // §12.2 Example B: two discounted lines at 18% / 17%, a Combined billed charge and a Per-item billed
        // charge in the ST base, one expense-only charge. The bases are 1,861.94 and 2,948.06.
        var items = new List<TLine>
        {
            new() { Quantity = 2, UnitPrice = 1000, DiscountAmount = 200, TotalBeforeDiscount = 2000, TotalAfterDiscount = 1800, SalesTaxPct = 18 },
            new() { Quantity = 1, UnitPrice = 3000, DiscountAmount = 150, TotalBeforeDiscount = 3000, TotalAfterDiscount = 2850, SalesTaxPct = 17 },
        };
        var charges = new List<TCharge>
        {
            new() { ChargeAccountId = 1, Amount = 100, Scope = "general", Treatment = "bill", StTaxable = true },
            new() { ChargeAccountId = 2, Amount = 60, Scope = "individual", Distribution = "pro_rata_value", Treatment = "bill", StTaxable = true },
            new() { ChargeAccountId = 3, Amount = 40, Scope = "general", Treatment = "expense", StTaxable = false },
        };
        Assert.Equal([1861.94, 2948.06], InvoiceTotals.LineTaxBases(items, charges));
        Assert.Equal([335.15, 501.17], InvoiceTotals.LineTaxAmounts("individual", items, charges));
        var rates = new Dictionary<double, int> { [18] = 18, [17] = 17 };
        var outSplit = InvoiceTotals.OutputTaxSplits(Doc(totalTax: 836.32), items, charges, rates, 836.32).ToDictionary(x => x.AccountId!.Value, x => x.Amount);
        Assert.Equal(new Dictionary<int, double> { [18] = 335.15, [17] = 501.17 }, outSplit);
    }

    [Fact]
    public void Absorbed_and_per_item_charges_enter_the_line_bases()
    {
        var items = new List<TLine> { Line(null, 600, 10), Line(null, 400, 10) };
        var absorb = new List<TCharge> { new() { Amount = 100, Scope = "general", Treatment = "absorb" } };
        Assert.Equal([660.0, 440.0], InvoiceTotals.LineTaxBases(items, absorb));
        Assert.Equal([66.0, 44.0], InvoiceTotals.LineTaxAmounts("individual", items, absorb));

        var byWeight = new List<TLine>
        {
            new() { Quantity = 2, UnitPrice = 1000, TotalBeforeDiscount = 2000, TotalAfterDiscount = 2000, SalesTaxPct = 10, ProductWeight = 5 },
            new() { Quantity = 2, UnitPrice = 500, TotalBeforeDiscount = 1000, TotalAfterDiscount = 1000, SalesTaxPct = 10, ProductWeight = 15 },
        };
        var freight = new List<TCharge> { new() { Amount = 100, Scope = "individual", Distribution = "by_weight", Treatment = "bill", StTaxable = true } };
        Assert.Equal([2025.0, 1075.0], InvoiceTotals.LineTaxBases(byWeight, freight));
        Assert.Equal([202.5, 107.5], InvoiceTotals.LineTaxAmounts("individual", byWeight, freight));
    }

    [Fact]
    public void Sales_chain_derives_tax_further_tax_and_withholding_from_the_bases()
    {
        var charges = new List<TCharge>
        {
            new() { Amount = 100, Treatment = "bill", StTaxable = true, WhtTaxable = true },
            new() { Amount = 50, Treatment = "absorb" },
            new() { Amount = 30, Treatment = "expense" },
        };
        var t = InvoiceTotals.Sales(new TDoc
        {
            Subtotal = 1000, TotalDiscount = 100, TaxMode = "general", GlobalSalesTaxPct = 18,
            ApplyFurtherTax = true, FurtherTaxPct = 3, ApplyWithholdingTax = true, WithholdingTaxPct = 4.5,
        }, charges);
        Assert.Equal(1050, t.EffectiveSubtotal);
        Assert.Equal(1050, t.SalesTaxBase);       // 1,050 - 100 + 100 billed in the ST base
        Assert.Equal(189, t.SalesTax);
        Assert.Equal(31.5, t.FurtherTax);
        Assert.Equal(47.25, t.Wht);
        Assert.Equal(Py.Round(1050 - 100 + 100 + 189 + 31.5 - 47.25, 2), t.NetReceivable);
        Assert.Equal(100, t.Billed);
        Assert.Single(t.Pools.ExpenseRows);
    }

    [Fact]
    public void Label_split_is_cent_exact_with_the_remainder_last()
    {
        var w = LabelSplit.Weights([(1, 1.0), (2, 1.0), (3, 1.0), (1, 0.0)]);
        var s = LabelSplit.Split(100m, w);
        Assert.Equal([(1, 33.33m), (2, 33.33m), (3, 33.34m)], s.Select(x => (x.Label!.Value, x.Amount)));
        Assert.Equal([((int?)7, 10.01m)], LabelSplit.Split(10.005m, [(7, 1m)]));   // ROUND_HALF_UP
    }
}

/// <summary>shared/doc_checks.py (tests/unit/test_doc_checks.py).</summary>
public class DocChecksTests
{
    private static PyData Data(object o) => new(JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement.Clone());

    private static Dictionary<string, object?> Row(params (string K, object? V)[] extra)
    {
        var d = new Dictionary<string, object?> { ["product_id"] = 1, ["quantity"] = 2, ["unit_price"] = 100 };
        foreach (var (k, v) in extra) d[k] = v;
        return d;
    }

    private static List<string> Errors(Dictionary<string, object?> row, params (string K, object? V)[] doc)
    {
        var d = new Dictionary<string, object?> { ["items"] = new[] { row } };
        foreach (var (k, v) in doc) d[k] = v;
        return DocChecks.AmountErrors(Data(d));
    }

    [Fact]
    public void A_normal_invoice_passes() =>
        Assert.Empty(Errors(Row(("discount_pct", 10), ("sales_tax_pct", 18)), ("global_discount_pct", 5), ("withholding_tax_pct", 4.5)));

    [Fact]
    public void Out_of_range_figures_are_refused()
    {
        Assert.Contains(Errors(Row(("unit_price", -5))), e => e.Contains("unit price"));
        Assert.Contains(Errors(Row(("discount_pct", 150))), e => e.Contains("discount"));
        Assert.Contains(Errors(Row(), ("global_discount_pct", 101)), e => e.Contains("Discount"));
        Assert.Contains(Errors(Row(("discount_amount", 500))), e => e.Contains("more than the line"));
        Assert.Contains(Errors(Row(("sales_tax_pct", -1))), e => e.Contains("sales tax"));
        Assert.Contains(Errors(Row(), ("charges", new[] { new { amount = -10 } })), e => e.Contains("Charge 1"));
    }

    [Fact]
    public void Text_in_a_number_field_is_reported_not_a_crash()
    {
        Assert.Contains(Errors(Row(("unit_price", "abc"))), e => e.Contains("must be a number"));
        Assert.Empty(Errors(Row(), ("global_sales_tax_pct", ""), ("further_tax_pct", null)));
    }
}

/// <summary>shared/order_linkage.py (tests/unit/test_order_linkage.py).</summary>
public class OrderLinkageTests
{
    private static List<(int?, double)> Released(IEnumerable<(int? ProductId, int? Oid, double Qty)> lines, params (int Pid, double Qty)[] returned) =>
        OrderLinkage.AllocateReturn(lines, returned.ToList()).Select(r => (r.SourceOrderItemId, r.Quantity)).ToList();

    [Fact]
    public void Tally_sums_repeated_products_and_skips_rows_without_one()
    {
        Assert.Equal([(7, 5.0), (8, 5.0)], OrderLinkage.Tally([(7, 2), (8, 5), (7, 3)]));
        Assert.Equal([(7, 2.0)], OrderLinkage.Tally([(null, 4), (7, 2)]));
        Assert.Equal([(7, 0.0)], OrderLinkage.Tally([(7, 0)]));
    }

    [Fact]
    public void Returned_quantity_goes_back_to_the_order_lines_that_billed_it()
    {
        Assert.Equal([(99, 4.0)], Released([(7, 99, 10)], (7, 4)));
        Assert.Equal([(99, 10.0)], Released([(7, 99, 10)], (7, 10)));
        Assert.Empty(Released([(7, null, 10)], (7, 4)));
        Assert.Equal([(99, 3.0)], Released([(7, 99, 10), (8, 100, 5)], (7, 3)));
        Assert.Equal([(99, 4.0)], Released([(7, 99, 4), (7, 100, 6)], (7, 4)));
        Assert.Equal([(99, 4.0), (100, 3.0)], Released([(7, 99, 4), (7, 100, 6)], (7, 7)));
        Assert.Equal([(99, 4.0), (100, 6.0)], Released([(7, 99, 4), (7, 100, 6)], (7, 25)));
        Assert.Empty(Released([(7, 99, 10)], (7, 0)));
        Assert.Empty(Released([(7, 99, 10)], (7, -3)));
        Assert.Empty(Released([(7, 99, 10)]));
        Assert.Equal([(100, 2.0)], Released([(7, 99, 0), (7, 100, 5)], (7, 2)));
        var frac = Released([(7, 99, 0.1), (7, 100, 0.1), (7, 101, 0.1)], (7, 0.3));
        Assert.Equal([99, 100, 101], frac.Select(x => x.Item1!.Value));
        Assert.Equal(0.3, Py.Round(frac.Sum(x => x.Item2), 6));
    }

    [Fact]
    public void Order_progress_reads_open_partial_and_invoiced()
    {
        static OrderLine L(decimal qty, decimal invoiced, decimal price)
        {
            var item = new Accountix.Core.Entities.InvSalesOrderItem { Quantity = qty, InvoicedQty = invoiced, UnitPrice = price };
            return OrderLine.Of(item);
        }
        Assert.Equal((300.0, 300.0, "open"), OrderLinkage.Progress([L(2, 0, 100), L(1, 0, 100)]));
        Assert.Equal((300.0, 200.0, "partial"), OrderLinkage.Progress([L(2, 1, 100), L(1, 0, 100)]));
        Assert.Equal((300.0, 0.0, "invoiced"), OrderLinkage.Progress([L(2, 2, 100), L(1, 1, 100)]));
    }
}
