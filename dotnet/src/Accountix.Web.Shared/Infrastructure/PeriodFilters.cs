using System.Globalization;
using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.Core.Shared;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Web.Infrastructure;

/// <summary>Financial periods for report and register filters (finance reports + shared/registers.py).</summary>
public static class PeriodFilters
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>_parse_date: Y-m-d, d/m/y or d/m/Y; null when none fits.</summary>
    public static DateOnly? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        foreach (string fmt in new[] { "yyyy-MM-dd", "dd/MM/yy", "dd/MM/yyyy", "d/M/yy", "d/M/yyyy" })
            if (DateOnly.TryParseExact(s.Trim(), fmt, Inv, DateTimeStyles.None, out var d))
                return d;
        return null;
    }

    /// <summary>Periods, newest first (available_periods).</summary>
    public static Task<List<AccountingPeriod>> AllAsync(AccountixDbContext db, CancellationToken ct = default) =>
        db.Set<AccountingPeriod>().AsNoTracking().OrderByDescending(p => p.StartDate).ToListAsync(ct);

    /// <summary>
    /// finance _default_period: the period containing today (an active one
    /// first), else the most recent active period.
    /// </summary>
    public static async Task<AccountingPeriod?> DefaultAsync(AccountixDbContext db, DateOnly today, CancellationToken ct = default)
    {
        var p = await db.Set<AccountingPeriod>().AsNoTracking()
            .Where(x => x.StartDate <= today && x.EndDate >= today)
            .OrderByDescending(x => x.IsActive == true).ThenByDescending(x => x.StartDate)
            .FirstOrDefaultAsync(ct);
        return p ?? await db.Set<AccountingPeriod>().AsNoTracking().Where(x => x.IsActive == true)
            .OrderByDescending(x => x.StartDate).FirstOrDefaultAsync(ct);
    }

    /// <summary>Whole days as date-times, for filtering DateTime columns (_day_bounds).</summary>
    public static (DateTime? Start, DateTime? End) DayBounds(DateOnly? from, DateOnly? to) =>
        (from?.ToDateTime(TimeOnly.MinValue), to?.ToDateTime(TimeOnly.MaxValue));
}

/// <summary>shared/registers.resolve_register_filter: month / financial year / custom range.</summary>
public sealed class RegisterFilter
{
    public string Mode { get; init; } = "month";
    public string Month { get; init; } = "";
    public int? PeriodId { get; init; }
    public string FromStr { get; init; } = "";
    public string ToStr { get; init; } = "";
    public string Label { get; init; } = "All dates";
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public IReadOnlyList<AccountingPeriod> Periods { get; init; } = [];

    public static async Task<RegisterFilter> ResolveAsync(IQueryCollection args, AccountixDbContext db, DateOnly today,
                                                          string defaultMode = "month", CancellationToken ct = default)
    {
        var inv = CultureInfo.InvariantCulture;
        string mode = (args["mode"].FirstOrDefault() ?? defaultMode).Trim().ToLowerInvariant();
        if (mode is not ("month" or "year" or "custom"))
            mode = defaultMode;
        var periods = await PeriodFilters.AllAsync(db, ct);
        string monthStr = (args["month"].FirstOrDefault() ?? "").Trim();
        string fromStr = (args["from"].FirstOrDefault() ?? "").Trim();
        string toStr = (args["to"].FirstOrDefault() ?? "").Trim();
        DateOnly? from = null, to = null;
        int? periodId = null;
        string label = "All dates";

        if (mode == "month")
        {
            int year = today.Year, month = today.Month;
            string[] parts = monthStr.Split('-');
            if (parts.Length == 2 && int.TryParse(parts[0], out int y) && int.TryParse(parts[1], out int m)
                && m is >= 1 and <= 12 && y is >= 1900 and <= 2100)
                (year, month) = (y, m);
            monthStr = $"{year:D4}-{month:D2}";
            from = new DateOnly(year, month, 1);
            to = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
            label = $"{inv.DateTimeFormat.GetMonthName(month)} {year}";
        }
        else if (mode == "year")
        {
            periodId = int.TryParse(args["period_id"].FirstOrDefault(), out int pid) && pid != 0 ? pid : null;
            var period = periods.FirstOrDefault(p => p.Id == periodId)
                         ?? periods.FirstOrDefault(p => p.StartDate <= today && today <= p.EndDate)
                         ?? periods.FirstOrDefault();
            if (period is not null)
            {
                periodId = period.Id;
                (from, to) = (period.StartDate, period.EndDate);
                label = !string.IsNullOrEmpty(period.PeriodName)
                    ? period.PeriodName
                    : $"{period.StartDate.ToString("MMM yyyy", inv)} – {period.EndDate.ToString("MMM yyyy", inv)}";
            }
        }
        else
        {
            from = DateOnly.TryParseExact(fromStr, "yyyy-MM-dd", inv, DateTimeStyles.None, out var f) ? f : null;
            to = DateOnly.TryParseExact(toStr, "yyyy-MM-dd", inv, DateTimeStyles.None, out var t) ? t : null;
            fromStr = from?.ToString("yyyy-MM-dd", inv) ?? "";
            toStr = to?.ToString("yyyy-MM-dd", inv) ?? "";
            label = (from, to) switch
            {
                ({ } a, { } b) => $"{a.ToString("dd MMM yyyy", inv)} – {b.ToString("dd MMM yyyy", inv)}",
                ({ } a, null) => $"From {a.ToString("dd MMM yyyy", inv)}",
                (null, { } b) => $"Up to {b.ToString("dd MMM yyyy", inv)}",
                _ => "All dates",
            };
        }
        return new RegisterFilter
        {
            Mode = mode, Month = monthStr, PeriodId = periodId, FromStr = fromStr, ToStr = toStr,
            Label = label, From = from, To = to, Periods = periods,
        };
    }

    /// <summary>The query string that reproduces this filter (export / print links).</summary>
    public string Query =>
        Mode switch
        {
            "month" => $"mode=month&month={Month}",
            "year" => $"mode=year&period_id={PeriodId}",
            _ => $"mode=custom&from={FromStr}&to={ToStr}",
        };
}

/// <summary>Model of the shared _PeriodFilter partial.</summary>
public sealed class PeriodFilterModel
{
    public string FilterMode { get; init; } = "period";
    /// <summary>null: the page cannot act on a comparative filter, so the option is not offered.</summary>
    public string? CompMode { get; init; }
    public string CompPeriodIdsStr { get; init; } = "";
    public IReadOnlyList<AccountingPeriod> Periods { get; init; } = [];
    public int? SelectedPeriodId { get; init; }
    public string FromStr { get; init; } = "";
    public string ToStr { get; init; } = "";
    public bool ShowLabels { get; init; } = true;
    public IReadOnlyList<ProjectLabel> Labels { get; init; } = [];
    public IReadOnlyList<int> LabelIds { get; init; } = [];
}
