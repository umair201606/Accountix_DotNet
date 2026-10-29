using System.Globalization;
using Accountix.Core.Entities;
using Accountix.FixedAssets.Services;

namespace Accountix.FixedAssets.Tests;

/// <summary>
/// The engine against Python's own FixedAsset.monthly_charges, bit for bit (fixed expectations
/// captured from the Python app: Golden/EngineMonthlyCharges.json): awkward costs, both methods,
/// part-charged lives, overdue assets. A charge that differs in the last binary digit would round
/// to a different paisa sooner or later.
/// </summary>
public class EngineParityTests
{
    internal static List<(decimal Cost, decimal Salvage, int Life, string Method, int Months, int Charged, decimal Acc)> Cases()
    {
        var rnd = new Random(20261029);
        var cases = new List<(decimal, decimal, int, string, int, int, decimal)>();
        for (int i = 0; i < 120; i++)
        {
            decimal cost = Math.Round((decimal)(rnd.NextDouble() * 5_000_000 + 1000), 2);
            decimal salvage = i % 3 == 0 ? 0m : Math.Round(cost * (decimal)rnd.NextDouble() * 0.3m, 2);
            int life = rnd.Next(1, 16);
            int charged = rnd.Next(0, life * 12 + 6);
            decimal acc = Math.Round((cost - salvage) * (decimal)Math.Min(1.0, charged / (life * 12.0)) * (decimal)(0.9 + rnd.NextDouble() * 0.1), 2);
            cases.Add((cost, salvage, life, i % 2 == 0 ? AssetCalc.StraightLine : AssetCalc.DecliningBalance,
                       rnd.Next(1, 40), charged, acc));
        }
        return cases;
    }

    internal static object[] Input((decimal Cost, decimal Salvage, int Life, string Method, int Months, int Charged, decimal Acc) c) =>
    [
        c.Cost.ToString(CultureInfo.InvariantCulture), c.Salvage.ToString(CultureInfo.InvariantCulture), c.Life, c.Method,
        c.Months, c.Charged, c.Acc.ToString(CultureInfo.InvariantCulture),
    ];

    /// <summary>Each case with its charges (repr) and their rounded sum: "sum=" + repr(round(sum, 2)).</summary>
    internal static List<object> Answer(List<(decimal, decimal, int, string, int, int, decimal)> cases, IReadOnlyList<List<string>> lines) =>
        cases.Select((c, i) => (object)new { input = Input(c), charges = lines[i].Take(lines[i].Count - 1).ToList(), sum = lines[i][^1] }).ToList();

    [Fact]
    public void Monthly_charges_and_their_rounded_sums_equal_pythons()
    {
        var cases = Cases();
        var mine = new List<List<string>>();
        foreach (var c in cases)
        {
            var calc = new AssetCalc(new FixedAsset
            {
                PurchaseCost = c.Cost, SalvageValue = c.Salvage, UsefulLife = c.Life, DepreciationMethod = c.Method, Status = "active",
                AssetCode = "X", Name = "X",
            }, LiveCharges.None);
            var ch = calc.MonthlyCharges(c.Months, c.Charged, Py.F(c.Acc));
            mine.Add(ch.Select(Py.Repr).Append("sum=" + Py.Repr(Py.Round(Py.Sum(ch), 2))).ToList());
        }
        Golden.Match("EngineMonthlyCharges", Answer(cases, mine));
    }
}
