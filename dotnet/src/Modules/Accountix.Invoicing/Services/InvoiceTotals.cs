using System.Text.Json;

namespace Accountix.Invoicing.Services;

/// <summary>One invoice line as the totals chain reads it (floats, as Python holds them).</summary>
public sealed class TLine
{
    public int? ProductId { get; init; }
    public double Quantity { get; init; }
    public double UnitPrice { get; init; }
    public double DiscountPct { get; init; }
    public double DiscountAmount { get; init; }
    public double SalesTaxPct { get; init; }
    public double TotalBeforeDiscount { get; init; }
    public double TotalAfterDiscount { get; init; }
    /// <summary>The product's weight (by-weight charge split), 0 when none.</summary>
    public double ProductWeight { get; init; }
    public int? CategoryId { get; init; }

    public static TLine From(InvInvoiceItem it, InvProduct? p) => new()
    {
        ProductId = it.ProductId, Quantity = Py.F(it.Quantity), UnitPrice = Py.F(it.UnitPrice),
        DiscountPct = Py.F(it.DiscountPct), DiscountAmount = Py.F(it.DiscountAmount), SalesTaxPct = Py.F(it.SalesTaxPct),
        TotalBeforeDiscount = Py.F(it.TotalBeforeDiscount), TotalAfterDiscount = Py.F(it.TotalAfterDiscount),
        ProductWeight = Py.F(p?.Weight), CategoryId = p?.CategoryId,
    };

    public static TLine From(InvPurchaseInvoiceItem it, InvProduct? p) => new()
    {
        ProductId = it.ProductId, Quantity = Py.F(it.Quantity), UnitPrice = Py.F(it.UnitPrice),
        DiscountPct = Py.F(it.DiscountPct), DiscountAmount = Py.F(it.DiscountAmount), SalesTaxPct = Py.F(it.SalesTaxPct),
        TotalBeforeDiscount = Py.F(it.TotalBeforeDiscount), TotalAfterDiscount = Py.F(it.TotalAfterDiscount),
        ProductWeight = Py.F(p?.Weight), CategoryId = p?.CategoryId,
    };
}

/// <summary>One additional charge row (AdditionalCharge) as Python reads it.</summary>
public sealed class TCharge
{
    public int ChargeAccountId { get; init; }
    public string? Description { get; init; }
    public double Amount { get; init; }
    public string? Scope { get; init; }
    public string? Distribution { get; init; }
    public string? ManualAllocations { get; init; }
    public string? Treatment { get; init; }
    public bool StTaxable { get; init; }
    public bool WhtTaxable { get; init; }

    public static TCharge From(AdditionalCharge c) => new()
    {
        ChargeAccountId = c.ChargeAccountId, Description = c.Description, Amount = Py.F(c.Amount),
        Scope = c.Scope, Distribution = c.Distribution, ManualAllocations = c.ManualAllocations,
        Treatment = c.Treatment, StTaxable = c.StTaxable == true, WhtTaxable = c.WhtTaxable == true,
    };
}

/// <summary>The header figures the chain reads.</summary>
public sealed class TDoc
{
    public double Subtotal { get; init; }
    public double TotalDiscount { get; init; }
    public double TotalTax { get; init; }
    public double GlobalSalesTaxPct { get; init; }
    public double FurtherTaxPct { get; init; }
    public bool ApplyFurtherTax { get; init; }
    public double WithholdingTaxPct { get; init; }
    public bool ApplyWithholdingTax { get; init; }
    public string? TaxMode { get; init; }

    public static TDoc From(InvInvoice inv) => new()
    {
        Subtotal = Py.F(inv.Subtotal), TotalDiscount = Py.F(inv.TotalDiscount), TotalTax = Py.F(inv.TotalTax),
        GlobalSalesTaxPct = Py.F(inv.GlobalSalesTaxPct), FurtherTaxPct = Py.F(inv.FurtherTaxPct),
        ApplyFurtherTax = inv.ApplyFurtherTax == true, WithholdingTaxPct = Py.F(inv.WithholdingTaxPct),
        ApplyWithholdingTax = inv.ApplyWithholdingTax == true, TaxMode = inv.TaxMode,
    };
}

public sealed class ChargePools
{
    public double Billed { get; set; }
    public double Absorb { get; set; }
    public double Expense { get; set; }
    public double StBase { get; set; }
    public double WhtBase { get; set; }
    public List<TCharge> BilledRows { get; } = [];
    public List<TCharge> ExpenseRows { get; } = [];
}

public sealed record SalesTotals(
    ChargePools Pools, double Subtotal, double Discount, double EffectiveSubtotal, double SalesTaxBase,
    double SalesTax, double FurtherTax, double WhtBase, double Wht, double Billed, double NetReceivable);

/// <summary>
/// The v3 §8 money for a document (shared/invoice_totals.py): the posted
/// journal, the print and the FBR payload all ask here, in Python's floats.
/// </summary>
public static class InvoiceTotals
{
    public static ChargePools ChargePoolsFromRows(IEnumerable<TCharge> rows)
    {
        var p = new ChargePools();
        foreach (var r in rows)
        {
            double amt = r.Amount;
            if (amt <= 0) continue;
            string t = r.Treatment is "bill" or "absorb" or "expense" ? r.Treatment : "bill";
            if (t == "absorb") p.Absorb += amt;
            else if (t == "expense") { p.Expense += amt; p.ExpenseRows.Add(r); }
            else
            {
                p.Billed += amt;
                p.BilledRows.Add(r);
                if (r.StTaxable) p.StBase += amt;
                if (r.WhtTaxable) p.WhtBase += amt;
            }
        }
        p.Billed = Py.Round(p.Billed, 2);
        p.Absorb = Py.Round(p.Absorb, 2);
        p.Expense = Py.Round(p.Expense, 2);
        p.StBase = Py.Round(p.StBase, 2);
        p.WhtBase = Py.Round(p.WhtBase, 2);
        return p;
    }

    /// <summary>A line's pre-discount goods value.</summary>
    public static double LineValue(TLine it)
    {
        double v = it.TotalBeforeDiscount;
        if (v != 0) return v;
        return it.Quantity * it.UnitPrice;
    }

    /// <summary>A line's post-discount goods value.</summary>
    public static double LineNet(TLine it)
    {
        double value = LineValue(it);
        double net = it.TotalAfterDiscount;
        if (net != 0 || value == 0) return Py.Round(net, 2);
        double discount = it.DiscountAmount;
        if (discount == 0)
            discount = value * it.DiscountPct / 100;
        return Py.Round(Math.Max(0.0, value - discount), 2);
    }

    /// <summary>_allocate: total split pro-rata over (key, weight), the residual on the last bucket.</summary>
    public static List<(TKey Key, double Share)> Allocate<TKey>(double total, IReadOnlyList<(TKey Key, double Weight)> buckets)
    {
        total = Py.Round(total, 2);
        var outList = new List<(TKey, double)>();
        if (buckets.Count == 0 || total == 0) return outList;
        double basis = Py.Sum(buckets.Select(b => b.Weight));
        double allocated = 0.0;
        int n = buckets.Count;
        for (int i = 0; i < n; i++)
        {
            var (key, weight) = buckets[i];
            double share;
            if (i == n - 1)
                share = Py.Round(total - allocated, 2);
            else
            {
                share = basis > 0 ? Py.Round(total * weight / basis, 2) : Py.Round(total / n, 2);
                allocated = Py.Round(allocated + share, 2);
            }
            if (share != 0) outList.Add((key, share));
        }
        return outList;
    }

    /// <summary>Python's sort key (account_id is not None, account_id or 0): the None bucket first.</summary>
    private static List<(int? Key, double Weight)> Ordered(List<(int? Key, double Weight)> weights) =>
        weights.Select((w, i) => (w, i)).OrderBy(x => x.w.Key is not null).ThenBy(x => x.w.Key ?? 0).ThenBy(x => x.i)
            .Select(x => x.w).ToList();

    private static void AddWeight(List<(int? Key, double Weight)> weights, int? key, double add)
    {
        int idx = weights.FindIndex(w => w.Key == key);
        if (idx < 0) weights.Add((key, 0.0 + add));
        else weights[idx] = (key, weights[idx].Weight + add);
    }

    /// <summary>revenue_splits: amount over per-category revenue accounts (None: the global revenue account).</summary>
    public static List<(int? AccountId, double Amount)> RevenueSplits(IReadOnlyList<TLine> items, IReadOnlyDictionary<int, int> categoryAccounts, double amount)
    {
        var weights = new List<(int? Key, double Weight)>();
        foreach (var it in items)
        {
            int? account = it.CategoryId is int cid && categoryAccounts.TryGetValue(cid, out int a) ? a : null;
            AddWeight(weights, account, LineValue(it));
        }
        return Allocate(amount, Ordered(weights));
    }

    private static List<double> ManualChargeShares(TCharge chg, int count)
    {
        List<double> values;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(chg.ManualAllocations) ? "[]" : chg.ManualAllocations);
            values = doc.RootElement.EnumerateArray()
                .Select(v => Py.Round(PyData.Truthy(v) ? PyData.ToFloat(v, "manual") : 0.0, 2)).ToList();
        }
        catch (Exception e) when (e is JsonException or PyValueException or InvalidOperationException)
        {
            values = [];
        }
        return values.Concat(Enumerable.Repeat(0.0, count)).Take(count).ToList();
    }

    private static List<double> ChargeLineShares(TCharge chg, IReadOnlyList<TLine> items, IReadOnlyList<double> nets)
    {
        int count = items.Count;
        double amount = Py.Round(chg.Amount, 2);
        if (count == 0 || amount <= 0) return Enumerable.Repeat(0.0, count).ToList();
        string method = string.IsNullOrEmpty(chg.Distribution) ? "pro_rata_value" : chg.Distribution;
        if (method == "manual") return ManualChargeShares(chg, count);
        IReadOnlyList<double> weights;
        if (method == "pro_rata_qty")
            weights = items.Select(it => it.Quantity).ToList();
        else if (method == "by_weight")
        {
            var w = items.Select(it => it.Quantity * it.ProductWeight).ToList();
            weights = Py.Sum(w) <= 0 ? nets : w;
        }
        else
            weights = nets;
        var shares = Allocate(amount, weights.Select((w, i) => (i, w)).ToList()).ToDictionary(x => x.Key, x => x.Share);
        return Enumerable.Range(0, count).Select(i => shares.GetValueOrDefault(i, 0.0)).ToList();
    }

    /// <summary>line_tax_bases: each line's sales-tax base, in line order.</summary>
    public static List<double> LineTaxBases(IReadOnlyList<TLine> items, IEnumerable<TCharge> chargeRows)
    {
        if (items.Count == 0) return [];
        var nets = items.Select(LineNet).ToList();
        var bases = nets.ToList();
        foreach (var chg in chargeRows)
        {
            double amount = Py.Round(chg.Amount, 2);
            if (amount <= 0) continue;
            string treatment = chg.Treatment is "bill" or "absorb" or "expense" ? chg.Treatment : "bill";
            if (treatment == "expense") continue;
            List<double> shares;
            if ((string.IsNullOrEmpty(chg.Scope) ? "general" : chg.Scope) == "individual")
                shares = ChargeLineShares(chg, items, nets);
            else
            {
                var split = Allocate(amount, nets.Select((w, i) => (i, w)).ToList()).ToDictionary(x => x.Key, x => x.Share);
                shares = Enumerable.Range(0, items.Count).Select(i => split.GetValueOrDefault(i, 0.0)).ToList();
            }
            bool entersBase = treatment == "absorb" || chg.StTaxable;
            if (!entersBase) continue;
            bases = bases.Zip(shares, (b, s) => Py.Round(b + s, 2)).ToList();
        }
        return bases;
    }

    /// <summary>line_tax_amounts: the tax each line generated; [] in Combined mode.</summary>
    public static List<double> LineTaxAmounts(string? taxMode, IReadOnlyList<TLine> items, IEnumerable<TCharge> chargeRows)
    {
        if ((string.IsNullOrEmpty(taxMode) ? "general" : taxMode) == "general") return [];
        var bases = LineTaxBases(items, chargeRows);
        var amounts = new List<double>();
        foreach (var (it, b) in items.Zip(bases))
        {
            double rate = it.SalesTaxPct;
            amounts.Add(rate > 0 ? Py.Round(b * rate / 100, 2) : 0.0);
        }
        return amounts;
    }

    /// <summary>line_taxes: the stored document tax over its lines, footing exactly.</summary>
    public static List<double> LineTaxes(TDoc inv, IReadOnlyList<TLine> items, IEnumerable<TCharge> chargeRows)
    {
        if (items.Count == 0) return [];
        double total = Py.Round(inv.TotalTax, 2);
        if (total == 0) return Enumerable.Repeat(0.0, items.Count).ToList();
        var bases = LineTaxBases(items, chargeRows);
        List<double> weights = (string.IsNullOrEmpty(inv.TaxMode) ? "general" : inv.TaxMode) == "general"
            ? bases.Select(b => Math.Max(b, 0.0)).ToList()
            : items.Zip(bases, (it, b) => Math.Max(b * it.SalesTaxPct / 100, 0.0)).ToList();
        if (Py.Sum(weights) <= 0) weights = Enumerable.Repeat(1.0, items.Count).ToList();
        var split = Allocate(total, weights.Select((w, i) => (i, w)).ToList()).ToDictionary(x => x.Key, x => x.Share);
        return Enumerable.Range(0, items.Count).Select(i => split.GetValueOrDefault(i, 0.0)).ToList();
    }

    /// <summary>output_tax_splits: output tax by rate sub-account (None: the global Output Sales Tax account).</summary>
    public static List<(int? AccountId, double Amount)> OutputTaxSplits(TDoc inv, IReadOnlyList<TLine> items, IEnumerable<TCharge> chargeRows,
                                                                       IReadOnlyDictionary<double, int> rateAccounts, double amount)
    {
        int? Map(double rate) => rateAccounts.TryGetValue(rate, out int a) ? a : null;
        bool perLine = (string.IsNullOrEmpty(inv.TaxMode) ? "general" : inv.TaxMode) != "general";
        var weights = new List<(int? Key, double Weight)>();
        if (perLine)
        {
            var taxes = LineTaxAmounts(inv.TaxMode, items, chargeRows);
            foreach (var (it, tax) in items.Zip(taxes))
            {
                double rate = Py.Round(it.SalesTaxPct, 4);
                if (rate <= 0 || tax <= 0) continue;
                AddWeight(weights, Map(rate), tax);
            }
            if (weights.Count == 0 || Py.Sum(weights.Select(w => w.Weight)) <= 0)
            {
                foreach (var it in items)
                {
                    double rate = Py.Round(it.SalesTaxPct, 4);
                    if (rate <= 0) continue;
                    AddWeight(weights, Map(rate), LineValue(it) * rate);
                }
            }
        }
        else
        {
            double rate = Py.Round(inv.GlobalSalesTaxPct, 4);
            weights = [(Map(rate), 1.0)];
        }
        if (weights.Count == 0)
        {
            double rate = Py.Round(inv.GlobalSalesTaxPct, 4);
            weights = [(Map(rate), 1.0)];
        }
        return Allocate(amount, Ordered(weights));
    }

    /// <summary>sales_totals: the full §8 chain for a sales invoice.</summary>
    public static SalesTotals Sales(TDoc inv, IEnumerable<TCharge> chargeRows)
    {
        var pools = ChargePoolsFromRows(chargeRows);
        double subtotal = Py.Round(inv.Subtotal, 2);
        double discount = Py.Round(inv.TotalDiscount, 2);
        double effective = Py.Round(subtotal + pools.Absorb, 2);
        double stBase = Py.Round(effective - discount + pools.StBase, 2);
        double whtBase = Py.Round(effective - discount + pools.WhtBase, 2);
        double salesTax = (string.IsNullOrEmpty(inv.TaxMode) ? "general" : inv.TaxMode) == "general"
            ? Py.Round(stBase * inv.GlobalSalesTaxPct / 100, 2)
            : Py.Round(inv.TotalTax, 2);
        double further = inv.ApplyFurtherTax ? Py.Round(stBase * inv.FurtherTaxPct / 100, 2) : 0.0;
        double wht = inv.ApplyWithholdingTax ? Py.Round(whtBase * inv.WithholdingTaxPct / 100, 2) : 0.0;
        double net = Py.Round(effective - discount + pools.Billed + salesTax + further - wht, 2);
        return new SalesTotals(pools, subtotal, discount, effective, stBase, salesTax, further, whtBase, wht, pools.Billed, net);
    }
}

/// <summary>shared/posting_helpers.label_weights / split_by_label (Decimal, ROUND_HALF_UP to the cent).</summary>
public static class LabelSplit
{
    public static List<(int? Label, decimal Weight)> Weights(IEnumerable<(int? Label, double Weight)> pairs)
    {
        var outList = new List<(int? Label, decimal Weight)>();
        foreach (var (label, weight) in pairs)
        {
            decimal w = weight == 0 || double.IsNaN(weight) ? 0m : Py.Dec(weight);
            int idx = outList.FindIndex(x => x.Label == label);
            if (idx < 0) outList.Add((label, w));
            else outList[idx] = (label, outList[idx].Weight + w);
        }
        return outList;
    }

    private static decimal Cent(decimal d) => Math.Round(d, 2, MidpointRounding.AwayFromZero);

    public static List<(int? Label, decimal Amount)> Split(decimal amount, IReadOnlyList<(int? Label, decimal Weight)> weights)
    {
        amount = Cent(amount);
        if (weights.Count == 0) return [(null, amount)];
        decimal total = weights.Where(w => w.Weight > 0).Sum(w => w.Weight);
        if (weights.Count == 1 || total <= 0) return [(weights[0].Label, amount)];
        var outList = new List<(int? Label, decimal Amount)>();
        decimal left = amount;
        var positive = weights.Where(w => w.Weight > 0).ToList();
        for (int i = 0; i < positive.Count; i++)
        {
            var (k, v) = positive[i];
            decimal share;
            if (i == positive.Count - 1) share = left;
            else
            {
                share = Cent(amount * v / total);
                left -= share;
            }
            if (share != 0) outList.Add((k, share));
        }
        return outList.Count > 0 ? outList : [(weights[0].Label, amount)];
    }
}
