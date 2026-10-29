using System.Globalization;

namespace Accountix.Finance.Services;

/// <summary>
/// _resolve_period + _resolve_labels (finance_app/routes/reports.py): Single
/// Period / Custom Range / Comparative, and the project-label filter.
/// </summary>
public sealed class ReportPeriod
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public List<AccountingPeriod> Periods { get; init; } = [];
    public int? SelectedPeriodId { get; set; }
    public string FilterMode { get; init; } = "period";
    public string FromStr { get; init; } = "";
    public string ToStr { get; init; } = "";
    public string CompMode { get; init; } = "";
    public List<AccountingPeriod> CompPeriods { get; init; } = [];
    public string CompPeriodIdsStr { get; init; } = "";
    public List<ProjectLabel> Labels { get; init; } = [];
    public string LabelIdsStr { get; init; } = "";
    public List<int> LabelIds { get; init; } = [];

    /// <summary>Comparative periods actually in force (comp_mode set).</summary>
    public IReadOnlyList<AccountingPeriod> ActiveComp => CompMode.Length > 0 ? CompPeriods : [];

    /// <summary>_parse_date: %Y-%m-%d, %d/%m/%y, %d/%m/%Y.</summary>
    public static DateOnly? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        foreach (string fmt in new[] { "yyyy-M-d", "d/M/yy", "d/M/yyyy" })
            if (DateOnly.TryParseExact(s, fmt, Inv, DateTimeStyles.None, out var d))
                return d;
        return null;
    }

    public static async Task<ReportPeriod> ResolveAsync(HttpRequest request, AccountixDbContext db, DateOnly today,
                                                        CancellationToken ct = default)
    {
        var q = request.Query;
        string filterMode = q["filter_mode"].FirstOrDefault() ?? "period";
        int? periodId = int.TryParse(q["period_id"].FirstOrDefault(), NumberStyles.Integer, Inv, out int pid) ? pid : null;
        string fromStr = (q["from"].FirstOrDefault() ?? "").Trim();
        string toStr = (q["to"].FirstOrDefault() ?? "").Trim();
        DateOnly? from = fromStr.Length > 0 ? ParseDate(fromStr) : null;
        DateOnly? to = toStr.Length > 0 ? ParseDate(toStr) : null;

        var periods = await PeriodFilters.AllAsync(db, ct);
        int? selected = periodId;

        string compMode = q["comp_mode"].FirstOrDefault() ?? "";
        string compIdsStr = q["comp_period_ids"].FirstOrDefault() ?? "";
        var compIds = compIdsStr.Split(',').Select(p => p.Trim())
            .Where(p => p.Length > 0 && p.All(char.IsAsciiDigit) && int.TryParse(p, out _)).Select(int.Parse).ToList();
        var compPeriods = compIds.Count == 0 ? [] : periods.Where(p => compIds.Contains(p.Id)).OrderBy(p => p.StartDate).ToList();

        if (filterMode is "period" or "comparative" && periodId is int idv && idv != 0
            && periods.FirstOrDefault(p => p.Id == idv) is { } period)
        {
            from = period.StartDate;
            to = period.EndDate;
        }

        var (labels, labelIdsStr, labelIds) = await LabelsAsync(request, db, ct);
        var result = new ReportPeriod
        {
            Periods = periods, FilterMode = filterMode, FromStr = fromStr, ToStr = toStr, CompMode = compMode,
            CompPeriods = compPeriods, CompPeriodIdsStr = compIdsStr, Labels = labels, LabelIdsStr = labelIdsStr, LabelIds = labelIds,
        };

        // A bare open leaves the dates empty (the report waits for View) but still pre-selects the current period.
        bool noPeriod = periodId is null or 0;
        if (noPeriod && fromStr.Length == 0 && toStr.Length == 0)
        {
            if (selected is null or 0 && await PeriodFilters.DefaultAsync(db, today, ct) is { } active)
                selected = active.Id;
            result.From = from;
            result.To = to;
            result.SelectedPeriodId = selected;
            return result;
        }

        if (from is null && to is null)
        {
            if (await PeriodFilters.DefaultAsync(db, today, ct) is { } active)
            {
                from = active.StartDate;
                to = active.EndDate;
                if (selected is null or 0) selected = active.Id;
            }
        }
        else if (filterMode == "period" && selected is null or 0 && await PeriodFilters.DefaultAsync(db, today, ct) is { } active)
            selected = active.Id;

        result.From = from;
        result.To = to;
        result.SelectedPeriodId = selected;
        return result;
    }

    /// <summary>_resolve_labels: every label (archived too), the raw label_ids string and its ids.</summary>
    public static async Task<(List<ProjectLabel> Labels, string Raw, List<int> Ids)> LabelsAsync(HttpRequest request, AccountixDbContext db,
                                                                                                 CancellationToken ct = default)
    {
        var labels = await db.Set<ProjectLabel>().AsNoTracking().OrderBy(l => l.Name).ToListAsync(ct);
        string raw = request.Query["label_ids"].FirstOrDefault() ?? "";
        var ids = raw.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0 && x.All(char.IsAsciiDigit) && int.TryParse(x, out _))
            .Select(int.Parse).ToList();
        return (labels, raw, ids);
    }

    /// <summary>The shared _PeriodFilter partial's model; empty state: no mode, nothing chosen.</summary>
    public PeriodFilterModel FilterModel(bool loaded, bool comparative = true) => new()
    {
        FilterMode = loaded ? FilterMode : "",
        CompMode = comparative ? (loaded ? CompMode : "") : null,
        CompPeriodIdsStr = loaded ? CompPeriodIdsStr : "",
        Periods = Periods,
        SelectedPeriodId = SelectedPeriodId,
        FromStr = loaded ? FromStr : "",
        ToStr = loaded ? ToStr : "",
        ShowLabels = true,
        Labels = Labels,
        LabelIds = LabelIds,
    };

    /// <summary>The export links the statements print: ?filter_mode=…&amp;period_id=…&amp;format=.</summary>
    public string ExportQuery(bool loaded, string format, bool includeFrom = true)
    {
        string E(string? s) => Uri.EscapeDataString(s ?? "");
        var parts = new List<string>
        {
            $"filter_mode={E(loaded ? FilterMode : "")}",
            $"period_id={(SelectedPeriodId is int id ? id.ToString(Inv) : "None")}",
        };
        if (includeFrom) parts.Add($"from={E(loaded ? FromStr : "")}");
        parts.Add($"to={E(loaded ? ToStr : "")}");
        parts.Add($"comp_mode={E(loaded ? CompMode : "")}");
        parts.Add($"comp_period_ids={E(loaded ? CompPeriodIdsStr : "")}");
        parts.Add($"label_ids={E(LabelIdsStr)}");
        parts.Add($"format={format}");
        return "?" + string.Join("&", parts);
    }

    // ── wording shared by the screens and the exports ──────────────────

    /// <summary>_col_label: "31 December, 2026", or "Amount" with no date.</summary>
    public static string ColLabel(DateOnly? d) => d is DateOnly x ? x.ToString("dd MMMM, yyyy", Inv) : "Amount";

    /// <summary>_period_line: "For the period … to …" / "As at …".</summary>
    public static string PeriodLine(DateOnly? from = null, DateOnly? to = null, DateOnly? asOf = null)
    {
        if (from is not null && to is not null) return $"For the period {ColLabel(from)} to {ColLabel(to)}";
        if (asOf is not null) return $"As at {ColLabel(asOf)}";
        return "";
    }

    /// <summary>_file_period: the date part of an export's file name.</summary>
    public static string? FilePeriod(DateOnly? from = null, DateOnly? to = null, DateOnly? asOf = null)
    {
        if (from is DateOnly f && to is DateOnly t) return $"{f:yyyy-MM-dd}_to_{t:yyyy-MM-dd}";
        if (asOf is DateOnly a) return $"as_at_{a:yyyy-MM-dd}";
        return null;
    }

    /// <summary>_export_dates: an export asked for without a period gets the current one.</summary>
    public static async Task<(DateOnly From, DateOnly To)> ExportDatesAsync(DateOnly? from, DateOnly? to, AccountixDbContext db,
                                                                            DateOnly today, CancellationToken ct = default)
    {
        if (from is DateOnly f && to is DateOnly t) return (f, t);
        if (await PeriodFilters.DefaultAsync(db, today, ct) is { } p) return (p.StartDate, p.EndDate);
        return (new DateOnly(today.Year, 1, 1), today);
    }
}
