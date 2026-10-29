using System.Data;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Accountix.Finance.Services;

/// <summary>One account of the chart, held in memory for a request (parents walked without queries).</summary>
public sealed record AccountNode(int Id, string Code, string Name, string Type, int? ParentId, int Level, bool Active,
                                 bool IsFixed, string? CashFlowActivity, string? PlSection);

/// <summary>The whole chart in one query; effective_* walk the parents in memory (ChartOfAccount._inherited).</summary>
public sealed class Chart
{
    public IReadOnlyDictionary<int, AccountNode> ById { get; }
    public IReadOnlyDictionary<string, AccountNode> ByCode { get; }
    public IReadOnlyDictionary<int, List<AccountNode>> Children { get; }

    public Chart(IEnumerable<AccountNode> accounts)
    {
        var list = accounts.ToList();
        ById = list.ToDictionary(a => a.Id);
        var byCode = new Dictionary<string, AccountNode>();
        foreach (var a in list.OrderBy(a => a.Id))
            byCode.TryAdd(a.Code, a);   // first by id, as Query.filter_by(code=).first() mostly answers
        ByCode = byCode;
        var kids = new Dictionary<int, List<AccountNode>>();
        foreach (var a in list)
            if (a.ParentId is int p)
                (kids.TryGetValue(p, out var l) ? l : kids[p] = []).Add(a);
        Children = kids;
    }

    public AccountNode? Parent(AccountNode a) => a.ParentId is int p && ById.TryGetValue(p, out var n) ? n : null;

    private string? Inherited(AccountNode? a, Func<AccountNode, string?> attr)
    {
        int guard = 0;
        while (a is not null && guard++ < 64)
        {
            string? v = attr(a);
            if (!string.IsNullOrEmpty(v)) return v;
            a = Parent(a);
        }
        return null;
    }

    public string? CashFlowActivity(AccountNode a) => Inherited(a, x => x.CashFlowActivity);
    public string? PlSection(AccountNode a) => Inherited(a, x => x.PlSection);

    /// <summary>Postable cash and equivalents: effective activity "cash", level 5 or deeper.</summary>
    public List<int> CashIds() => ById.Values.Where(a => CashFlowActivity(a) == "cash" && a.Level >= 5).Select(a => a.Id).ToList();

    /// <summary>l3_head: the account's level-3 ancestor's name (its own when none).</summary>
    public string L3Head(AccountNode acct)
    {
        AccountNode? a = acct;
        int guard = 0;
        while (a is not null && a.Level > 3 && guard++ < 64)
            a = Parent(a);
        return a?.Name ?? acct.Name;
    }

    /// <summary>The node and every descendant.</summary>
    public List<int> Subtree(int id)
    {
        var output = new List<int>();
        var stack = new Stack<int>();
        stack.Push(id);
        while (stack.Count > 0)
        {
            int n = stack.Pop();
            output.Add(n);
            if (Children.TryGetValue(n, out var kids))
                foreach (var k in kids) stack.Push(k.Id);
        }
        return output;
    }
}

public sealed record AccountBalance(int AccountId, string Code, string Name, string Type, decimal Dr, decimal Cr);

/// <summary>One P&amp;L structure entry: a section ("section" key) or a running subtotal ("subtotal" key).</summary>
public sealed record PlEntry(string? Section, string? Subtotal, string Label, bool Negate);

public sealed record PlItem(string Code, string Name, decimal Contrib);

/// <summary>A rendered P&amp;L row (_pl_rows): header / account / total / subtotal.</summary>
public sealed class PlRow
{
    public required string Kind { get; init; }
    public string Label { get; init; } = "";
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Section { get; init; }
    public string? Subtotal { get; init; }
    public decimal Amount { get; init; }
    public IReadOnlyList<string> ShownCodes { get; init; } = [];
    /// <summary>Comparative figures: the row's own amount first, then one per comparative period.</summary>
    public List<decimal>? CompAmounts { get; set; }
    public List<decimal>? ByLabel { get; set; }
    public decimal? Unlabelled { get; set; }
}

/// <summary>_pl_period_lookup: display-signed figures keyed by code / section / subtotal.</summary>
public sealed record PlLookup(Dictionary<string, decimal> Accounts, Dictionary<string, decimal> Totals, Dictionary<string, decimal> Subtotals);

/// <summary>
/// The ledger reads every financial report shares (finance_app/routes/reports.py helpers):
/// balances from the reporting table gl_daily_balances (posted activity per account,
/// label and day), never from every journal line; the chart once per request.
/// </summary>
public sealed class FinanceData(AccountixDbContext db)
{
    public static readonly string[] PlTypes = ["revenue", "expense", "contra-expense"];
    public static readonly string[] BsTypes = ["asset", "liability", "equity"];

    public static readonly IReadOnlyList<PlEntry> DefaultPlStructure =
    [
        new("sales", null, "Sales", false),
        new("sales_returns", null, "Less: Sales Returns & Discounts", true),
        new(null, "net_sales", "Net Sales", false),
        new("cost_of_sales", null, "Cost of Sales", true),
        new(null, "gross_profit", "Gross Profit", false),
        new("admin", null, "Administrative Expenses", true),
        new("selling_distribution", null, "Selling & Distribution Expenses", true),
        new("other_operating", null, "Other Operating Expenses", true),
        new(null, "operating_profit", "Operating Profit", false),
        new("other_income", null, "Other Income", false),
        new("finance_cost", null, "Finance Costs", true),
        new(null, "profit_before_tax", "Profit Before Tax", false),
        new("income_tax", null, "Taxation", true),
        new(null, "net_profit", "Net Profit", false),
    ];

    public AccountixDbContext Db => db;

    private Chart? _chart;
    private ReportSetting? _settings;
    private bool _settingsLoaded;

    public async Task<Chart> ChartAsync(CancellationToken ct = default) =>
        _chart ??= new Chart(await db.Set<ChartOfAccount>().AsNoTracking()
            .Select(a => new AccountNode(a.Id, a.Code, a.Name, a.Type, a.ParentId, a.Level, a.IsActive == true,
                                         a.IsFixed == true, a.CashFlowActivity, a.PlSection))
            .ToListAsync(ct));

    /// <summary>ReportSettings.get(): the company's row (lowest id), or the defaults when none exists yet.</summary>
    public async Task<ReportSetting?> SettingsAsync(CancellationToken ct = default)
    {
        if (!_settingsLoaded)
        {
            _settings = await db.Set<ReportSetting>().AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
            _settingsLoaded = true;
        }
        return _settings;
    }

    // ── balances (gl_daily_balances) ────────────────────────────────────

    private IQueryable<GlDailyBalance> Glb(IReadOnlyCollection<int>? labelIds)
    {
        var q = db.Set<GlDailyBalance>().AsNoTracking();
        if (labelIds is { Count: > 0 })
            q = q.Where(g => labelIds.Contains(g.LabelId));
        return q;
    }

    /// <summary>_all_account_balances: (dr, cr) per account up to a day, ordered by code.</summary>
    public async Task<List<AccountBalance>> AllAccountBalancesAsync(DateOnly? asOf, IReadOnlyCollection<string>? types = null,
                                                                   IReadOnlyCollection<int>? labelIds = null, CancellationToken ct = default)
    {
        var q = Glb(labelIds);
        if (asOf is DateOnly d) q = q.Where(g => g.Day <= d);
        var sums = await q.GroupBy(g => g.AccountId)
            .Select(g => new { g.Key, Dr = g.Sum(x => x.Debit), Cr = g.Sum(x => x.Credit) })
            .ToListAsync(ct);
        var chart = await ChartAsync(ct);
        var output = new List<AccountBalance>(sums.Count);
        foreach (var s in sums)
        {
            if (!chart.ById.TryGetValue(s.Key, out var a)) continue;
            if (types is { Count: > 0 } && !types.Contains(a.Type)) continue;
            output.Add(new AccountBalance(a.Id, a.Code, a.Name, a.Type, s.Dr, s.Cr));
        }
        output.Sort((x, y) => string.CompareOrdinal(x.Code, y.Code));
        return output;
    }

    /// <summary>_period_movements: {account_id: (dr, cr)} of posted activity within the window.</summary>
    public async Task<Dictionary<int, (decimal Dr, decimal Cr)>> PeriodMovementsAsync(DateOnly? from, DateOnly? to,
        IReadOnlyCollection<string>? types = null, IReadOnlyCollection<int>? labelIds = null, CancellationToken ct = default)
    {
        var q = Glb(labelIds);
        if (from is DateOnly f) q = q.Where(g => g.Day >= f);
        if (to is DateOnly t) q = q.Where(g => g.Day <= t);
        var sums = await q.GroupBy(g => g.AccountId)
            .Select(g => new { g.Key, Dr = g.Sum(x => x.Debit), Cr = g.Sum(x => x.Credit) })
            .ToListAsync(ct);
        var chart = await ChartAsync(ct);
        var output = new Dictionary<int, (decimal, decimal)>();
        foreach (var s in sums)
        {
            if (types is { Count: > 0 } && (!chart.ById.TryGetValue(s.Key, out var a) || !types.Contains(a.Type))) continue;
            output[s.Key] = (s.Dr, s.Cr);
        }
        return output;
    }

    /// <summary>_sum_balances: combined (dr, cr) of several accounts at a day, one query.</summary>
    public async Task<(decimal Dr, decimal Cr)> SumBalancesAsync(IReadOnlyCollection<int> accountIds, DateOnly? asOf,
                                                                 IReadOnlyCollection<int>? labelIds = null, CancellationToken ct = default)
    {
        if (accountIds.Count == 0) return (0m, 0m);
        var q = Glb(labelIds).Where(g => accountIds.Contains(g.AccountId));
        if (asOf is DateOnly d) q = q.Where(g => g.Day <= d);
        var r = await q.GroupBy(_ => 1).Select(g => new { Dr = g.Sum(x => x.Debit), Cr = g.Sum(x => x.Credit) }).FirstOrDefaultAsync(ct);
        return r is null ? (0m, 0m) : (r.Dr, r.Cr);
    }

    /// <summary>_net_income: revenue (cr − dr) less expense (dr − cr) up to a day.</summary>
    public async Task<decimal> NetIncomeAsync(DateOnly? asOf, IReadOnlyCollection<int>? labelIds = null, CancellationToken ct = default)
    {
        var all = await AllAccountBalancesAsync(asOf, ["revenue", "expense"], labelIds, ct);
        return NetIncome(all);
    }

    public static decimal NetIncome(IEnumerable<AccountBalance> balances)
    {
        decimal rev = 0m, exp = 0m;
        foreach (var b in balances)
        {
            if (b.Type == "revenue") rev += b.Cr - b.Dr;
            else if (b.Type == "expense") exp += b.Dr - b.Cr;
        }
        return rev - exp;
    }

    // ── profit & loss ───────────────────────────────────────────────────

    /// <summary>ReportSettings.pl_structure(): the saved JSON list, else DEFAULT_PL_STRUCTURE.</summary>
    public static IReadOnlyList<PlEntry> ParseStructure(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return DefaultPlStructure;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return DefaultPlStructure;
            var output = new List<PlEntry>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                string? Str(string key) => e.TryGetProperty(key, out var v) ? v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Null => null,
                    _ => v.ToString(),
                } : null;
                bool hasSection = e.TryGetProperty("section", out _);
                bool hasSubtotal = e.TryGetProperty("subtotal", out _);
                bool negate = e.TryGetProperty("negate", out var n) && n.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.Number => n.GetDouble() != 0,
                    JsonValueKind.String => n.GetString()!.Length > 0,
                    JsonValueKind.Array => n.GetArrayLength() > 0,
                    JsonValueKind.Object => n.EnumerateObject().Any(),
                    _ => false,
                };
                if (hasSection)
                    output.Add(new PlEntry(Str("section") ?? "", null, Str("label") ?? "", negate));
                else if (hasSubtotal)
                    output.Add(new PlEntry(null, Str("subtotal") ?? "", Str("label") ?? "", negate));
            }
            return output;
        }
        catch (JsonException)
        {
            return DefaultPlStructure;
        }
    }

    public async Task<IReadOnlyList<PlEntry>> StructureAsync(CancellationToken ct = default) =>
        ParseStructure((await SettingsAsync(ct))?.PlStructureJson);

    /// <summary>_pl_by_section: one window's P&amp;L accounts grouped by structure section (contribution to profit).</summary>
    public async Task<Dictionary<string, List<PlItem>>> PlBySectionAsync(DateOnly? from, DateOnly? to,
        IReadOnlyCollection<int>? labelIds = null, CancellationToken ct = default) =>
        PlBySection(await PeriodMovementsAsync(from, to, PlTypes, labelIds, ct), await ChartAsync(ct));

    /// <summary>_pl_by_section over movements already read ({account_id: (dr, cr)}).</summary>
    public static Dictionary<string, List<PlItem>> PlBySection(IReadOnlyDictionary<int, (decimal Dr, decimal Cr)> movements, Chart chart)
    {
        var bySection = new Dictionary<string, List<PlItem>>();
        foreach (var (aid, (dr, cr)) in movements.OrderBy(m => m.Key))
        {
            if (!chart.ById.TryGetValue(aid, out var a) || !PlTypes.Contains(a.Type)) continue;
            decimal contrib = a.Type == "expense" ? (cr > dr ? dr - cr : cr - dr) : cr - dr;
            if (dr == 0 && cr == 0) continue;
            string section = chart.PlSection(a) ?? (a.Type == "revenue" ? "other_income" : "other_operating");
            (bySection.TryGetValue(section, out var l) ? l : bySection[section] = []).Add(new PlItem(a.Code, a.Name, contrib));
        }
        return bySection;
    }

    /// <summary>P&amp;L movements of a window per label in ONE query: {label_id: {account_id: (dr, cr)}} (0 = no label).</summary>
    public async Task<Dictionary<int, Dictionary<int, (decimal Dr, decimal Cr)>>> PlMovementsByLabelAsync(DateOnly? from, DateOnly? to,
        CancellationToken ct = default)
    {
        var chart = await ChartAsync(ct);
        var q = db.Set<GlDailyBalance>().AsNoTracking();
        if (from is DateOnly f) q = q.Where(g => g.Day >= f);
        if (to is DateOnly t) q = q.Where(g => g.Day <= t);
        var sums = await q.GroupBy(g => new { g.LabelId, g.AccountId })
            .Select(g => new { g.Key.LabelId, g.Key.AccountId, Dr = g.Sum(x => x.Debit), Cr = g.Sum(x => x.Credit) })
            .ToListAsync(ct);
        var output = new Dictionary<int, Dictionary<int, (decimal, decimal)>>();
        foreach (var s in sums)
        {
            if (!chart.ById.TryGetValue(s.AccountId, out var a) || !PlTypes.Contains(a.Type)) continue;
            (output.TryGetValue(s.LabelId, out var d) ? d : output[s.LabelId] = [])[s.AccountId] = (s.Dr, s.Cr);
        }
        return output;
    }

    /// <summary>_pl_rows: the sectioned P&amp;L per the structure; returns (rows, net profit).</summary>
    public async Task<(List<PlRow> Rows, decimal Net)> PlRowsAsync(DateOnly? from, DateOnly? to,
        IReadOnlyCollection<int>? labelIds = null, CancellationToken ct = default) =>
        PlRows(await PlBySectionAsync(from, to, labelIds, ct), await SettingsAsync(ct));

    public static (List<PlRow> Rows, decimal Net) PlRows(Dictionary<string, List<PlItem>> bySection, ReportSetting? settings)
    {
        int detail = settings?.PlDetailRows is int n && n != 0 ? n : 10;
        var rows = new List<PlRow>();
        decimal running = 0m;
        foreach (var entry in ParseStructure(settings?.PlStructureJson))
        {
            if (entry.Section is string sec)
            {
                if (!bySection.TryGetValue(sec, out var items) || items.Count == 0) continue;
                decimal Disp(decimal c) => entry.Negate ? -c : c;
                decimal total = items.Sum(i => i.Contrib);
                running += total;
                var sorted = items.OrderBy(i => -Math.Abs(i.Contrib)).ThenBy(i => i.Code, StringComparer.Ordinal).ToList();
                var shown = sorted.Take(detail).ToList();
                var hidden = sorted.Skip(detail).ToList();
                rows.Add(new PlRow { Kind = "header", Label = entry.Label, Section = sec });
                foreach (var i in shown)
                    rows.Add(new PlRow { Kind = "account", Code = i.Code, Name = i.Name, Section = sec, Amount = Disp(i.Contrib) });
                if (hidden.Count > 0)
                    rows.Add(new PlRow
                    {
                        Kind = "account", Code = "", Name = $"Others ({hidden.Count} accounts)", Section = sec,
                        ShownCodes = shown.Select(i => i.Code).ToList(), Amount = Disp(hidden.Sum(i => i.Contrib)),
                    });
                rows.Add(new PlRow { Kind = "total", Label = $"Total {entry.Label}", Section = sec, Amount = Disp(total) });
            }
            else if (entry.Subtotal is string sub)
                rows.Add(new PlRow { Kind = "subtotal", Label = entry.Label, Subtotal = sub, Amount = running });
        }
        return (rows, running);
    }

    /// <summary>_pl_period_lookup: one window's figures keyed for comparative columns.</summary>
    public async Task<PlLookup> PlLookupAsync(DateOnly? from, DateOnly? to, IReadOnlyCollection<int>? labelIds = null,
                                              CancellationToken ct = default) =>
        PlLookupOf(await PlBySectionAsync(from, to, labelIds, ct), await StructureAsync(ct));

    public static PlLookup PlLookupOf(Dictionary<string, List<PlItem>> bySection, IReadOnlyList<PlEntry> structure)
    {
        var accounts = new Dictionary<string, decimal>();
        var totals = new Dictionary<string, decimal>();
        var subtotals = new Dictionary<string, decimal>();
        decimal running = 0m;
        foreach (var entry in structure)
        {
            if (entry.Section is string sec)
            {
                var items = bySection.TryGetValue(sec, out var l) ? l : [];
                decimal Disp(decimal c) => entry.Negate ? -c : c;
                decimal total = items.Sum(i => i.Contrib);
                running += total;
                foreach (var i in items) accounts[i.Code] = Disp(i.Contrib);
                totals[sec] = Disp(total);
            }
            else if (entry.Subtotal is string sub)
                subtotals[sub] = running;
        }
        return new PlLookup(accounts, totals, subtotals);
    }

    /// <summary>_pl_comp_amount: one comparative window's figure for an already-rendered row.</summary>
    public static decimal PlCompAmount(PlRow row, PlLookup lookup)
    {
        if (row.Kind == "total")
            return row.Section is string s && lookup.Totals.TryGetValue(s, out var t) ? t : 0m;
        if (row.Kind == "subtotal")
            return row.Subtotal is string st && lookup.Subtotals.TryGetValue(st, out var v) ? v : 0m;
        if (row.Code.Length > 0)
            return lookup.Accounts.TryGetValue(row.Code, out var a) ? a : 0m;
        // "Others (N accounts)": whatever the section total does not attribute to the accounts above it.
        decimal shown = row.ShownCodes.Sum(c => lookup.Accounts.TryGetValue(c, out var x) ? x : 0m);
        decimal secTotal = row.Section is string sec && lookup.Totals.TryGetValue(sec, out var tt) ? tt : 0m;
        return Math.Round(secTotal - shown, 2, MidpointRounding.ToEven);
    }
}

/// <summary>
/// consistent_snapshot: the report runs in one REPEATABLE READ transaction, so
/// every query sees the books as of the same instant (a posting landing between
/// two queries can no longer make Books Integrity flag a mismatch the ledger
/// does not have).
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class ConsistentSnapshotAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var db = context.HttpContext.RequestServices.GetRequiredService<AccountixDbContext>();
        if (!db.Database.IsNpgsql() || db.Database.CurrentTransaction is not null)
        {
            await next();
            return;
        }
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, context.HttpContext.RequestAborted);
        await next();
    }
}
