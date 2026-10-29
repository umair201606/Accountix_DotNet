namespace Accountix.FixedAssets.Services;

/// <summary>An asset's depreciation still backed by a posted journal (live_depreciation_query, aggregated in SQL).</summary>
public sealed record LiveCharges(decimal Sum, DateOnly? Last, DateOnly? First, int Count)
{
    public static readonly LiveCharges None = new(0m, null, null, 0);
}

/// <summary>One planned year of the schedule (depreciation_schedule row).</summary>
public sealed record ScheduleRow(DateOnly PeriodEnd, int Months, double Opening, double Charge, double Closing);

/// <summary>
/// The depreciation engine (fixed_assets_app/models/asset.py FixedAsset and
/// routes/depreciation.py), on an asset and its live charges. Floats on purpose:
/// the Python app computes in floats and the posted figures must agree with it.
/// Nothing here is stored as a running total: accumulated depreciation is the
/// sum of the live charges, floored at cost less salvage.
/// </summary>
public sealed class AssetCalc(FixedAsset asset, LiveCharges live)
{
    public const string StraightLine = "straight_line";
    public const string DecliningBalance = "declining_balance";
    public static readonly string[] Methods = [StraightLine, DecliningBalance];

    public FixedAsset Asset { get; } = asset;
    public LiveCharges Live { get; } = live;

    public double Cost => Py.F(Asset.PurchaseCost);
    public double Salvage => Py.F(Asset.SalvageValue);
    public double DepreciableAmount => Cost - Salvage;

    /// <summary>posted_depreciation: live charges, floored at cost less salvage.</summary>
    public double PostedDepreciation => Py.Min(Py.F(Live.Sum), DepreciableAmount);

    public double NetBookValue => Cost - PostedDepreciation;

    /// <summary>What may still be charged before the salvage floor.</summary>
    public double RemainingDepreciable => Py.Max(0.0, DepreciableAmount - PostedDepreciation);

    /// <summary>last_depreciation_date: the latest live charge, or the purchase date.</summary>
    public DateOnly LastDepreciationDate => Live.Last ?? Asset.PurchaseDate;

    /// <summary>charged_months: months of the life already covered by live charges.</summary>
    public int ChargedMonths => MonthsBetween(Asset.PurchaseDate, LastDepreciationDate);

    public int DueMonths(DateOnly asOf) => MonthsBetween(LastDepreciationDate, asOf);

    /// <summary>
    /// monthly_charges: the next <paramref name="months"/> charges after
    /// <paramref name="chargedMonths"/> of the life. Prospective (IAS 8): what is
    /// left is spread over the life that is left; the life's last month takes the
    /// remainder; reducing balance is double-declining switching to straight line.
    /// </summary>
    public List<double> MonthlyCharges(int months, int chargedMonths, double? accumulated = null)
    {
        double cost = Cost, salvage = Salvage;
        int life = Asset.UsefulLife;
        int lifeM = life * 12;
        double acc = accumulated ?? PostedDepreciation;
        double rate = life != 0 ? 2.0 / life / 12 : 0;
        var output = new List<double>();
        for (int i = 0; i < Math.Max(0, months); i++)
        {
            double remaining = cost - salvage - acc;
            if (remaining <= 0.005 || lifeM <= 0)
                break;
            int left = lifeM - chargedMonths - i;
            double charge;
            if (left <= 1)
                charge = remaining;            // last month of the life, or overdue
            else
            {
                charge = remaining / left;
                if (Asset.DepreciationMethod == DecliningBalance)
                    charge = Py.Max(charge, (cost - acc) * rate);
                charge = Py.Min(charge, remaining);
            }
            acc += charge;
            output.Add(charge);
        }
        return output;
    }

    /// <summary>due_depreciation: chargeable from the last live posting up to <paramref name="asOf"/>.</summary>
    public double DueDepreciation(DateOnly asOf)
    {
        if (Asset.Status != "active" || Asset.UsefulLife <= 0 || Asset.PurchaseCost <= 0)
            return 0;
        int months = DueMonths(asOf);
        if (months <= 0)
            return 0;
        double amount = Py.Round(Py.Sum(MonthlyCharges(months, ChargedMonths)), 2);
        return Py.Round(Py.Min(amount, RemainingDepreciable), 2);
    }

    /// <summary>annual_depreciation: the charge planned for the next twelve months.</summary>
    public double AnnualDepreciation => Py.Round(Py.Sum(MonthlyCharges(12, ChargedMonths)), 2);

    /// <summary>depreciation_schedule: planned charges to the end of the life, by year.</summary>
    public List<ScheduleRow> Schedule()
    {
        if (Asset.Status != "active" || Asset.PurchaseCost == 0 || Asset.UsefulLife == 0)
            return [];
        var start = LastDepreciationDate;
        int done = ChargedMonths;
        var charges = MonthlyCharges(Asset.UsefulLife * 12 - done + 1, done);
        double nbv = NetBookValue;
        var rows = new List<ScheduleRow>();
        for (int i = 0; i < charges.Count; i += 12)
        {
            var chunk = charges.Skip(i).Take(12).ToList();
            var end = AddMonths(start, i + chunk.Count);
            double charge = Py.Round(Py.Sum(chunk), 2);
            rows.Add(new ScheduleRow(end, chunk.Count, Py.Round(nbv, 2), charge, Py.Round(nbv - charge, 2)));
            nbv -= charge;
        }
        return rows;
    }

    /// <summary>_months_between: whole calendar months from start to end.</summary>
    public static int MonthsBetween(DateOnly start, DateOnly end)
    {
        if (end <= start)
            return 0;
        int months = (end.Year - start.Year) * 12 + (end.Month - start.Month);
        if (end.Day < start.Day)
            months -= 1;
        return Math.Max(0, months);
    }

    /// <summary>_add_months: n calendar months on, the day clamped to the month's length.</summary>
    public static DateOnly AddMonths(DateOnly d, int n)
    {
        int m = d.Month - 1 + n;
        int y = d.Year + (int)Math.Floor(m / 12.0);
        m = ((m % 12) + 12) % 12 + 1;
        return new DateOnly(y, m, Math.Min(d.Day, DateTime.DaysInMonth(y, m)));
    }
}
