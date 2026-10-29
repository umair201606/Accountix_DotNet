using System.Globalization;
using Accountix.Finance.Services;

namespace Accountix.Finance.Controllers;

// ── page models ─────────────────────────────────────────────────────────

public abstract class StatementPage
{
    public required ReportPeriod Period { get; init; }
    /// <summary>False on a bare open: the report waits for View.</summary>
    public bool Loaded { get; init; }
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public IReadOnlyList<AccountingPeriod> Comp => Loaded ? Period.ActiveComp : [];
    public bool HasComp => Comp.Count > 0;
}

public sealed class TrialBalancePage : StatementPage
{
    public TrialBalanceResult? Result { get; init; }
    public DateOnly AsOf { get; init; }
}

public sealed class ProfitLossPage : StatementPage
{
    public List<PlRow> Rows { get; init; } = [];
    public decimal Net { get; init; }
}

public sealed class BalanceSheetPage : StatementPage
{
    public BalanceSheetResult? Result { get; init; }
    public DateOnly AsOf { get; init; }
}

public sealed class SociePage : StatementPage
{
    public List<SocieColumn> Columns { get; init; } = [];
    public List<SocieRow> Rows { get; init; } = [];
    public List<(DateOnly Start, DateOnly End)> Specs { get; init; } = [];
}

public sealed class CashFlowPage : StatementPage
{
    public required CashFlowResult Result { get; init; }
}

public sealed class LedgerPage : StatementPage
{
    public List<LedgerSection> Sections { get; init; } = [];
    public List<AccountNode> Heads { get; init; } = [];
    public List<AccountNode> Leaves { get; init; } = [];
    public string Mode { get; init; } = "";
    public string SelectionMode { get; init; } = "custom";
    public string SelectedAccountIds { get; init; } = "";
    public string SelectedHeadIds { get; init; } = "";
}

public sealed class LabelPlPage
{
    public bool Loaded { get; init; }
    public List<PlRow> Rows { get; init; } = [];
    public decimal Net { get; init; }
    public List<ProjectLabel> Labels { get; init; } = [];
    public List<ProjectLabel> LabelsAll { get; init; } = [];
    public List<int> Picked { get; init; } = [];
    public List<decimal> LabelNets { get; init; } = [];
    public decimal? UnlabelledNet { get; init; }
    public string FromStr { get; init; } = "";
    public string ToStr { get; init; } = "";
}

/// <summary>
/// The financial statements (finance_app/routes/reports.py, prefix /finance):
/// dashboard, general ledger, trial balance, P&amp;L, P&amp;L by label, balance
/// sheet, SOCIE and cash flow. Every statement reads one REPEATABLE READ
/// snapshot and waits for View before calculating; an explicit export always runs.
/// </summary>
[Area("Finance")]
[Module(Modules.Finance)]
[Route("finance")]
public sealed partial class ReportsController(AccountixDbContext db, FinanceData data, FinanceStatements statements,
                                              ReportExport export, CompanyClock clock) : Controller
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const string Section = "financial_reports";

    private string? Format => Request.Query["format"].FirstOrDefault();
    private bool ExportAsked => Format is "excel" or "pdf";

    private IActionResult Send(ExportFile file) => File(file.Content, file.ContentType, file.FileName);

    [HttpGet("")]
    [Right(Section, Rights.View)]
    public IActionResult Dashboard() => View();

    // ── trial balance ───────────────────────────────────────────────────

    [HttpGet("trial-balance")]
    [Right(Section, Rights.View)]
    [ConsistentSnapshot]
    public async Task<IActionResult> TrialBalance(CancellationToken ct)
    {
        var p = await ReportPeriod.ResolveAsync(Request, db, clock.Today, ct);
        if (p.From is null && !ExportAsked)
            return View(new TrialBalancePage { Period = p, AsOf = clock.Today });
        var asOf = p.To ?? clock.Today;
        var result = await statements.TrialBalanceAsync(p.From, asOf, p.ActiveComp, p.LabelIds, ct);
        if (Format == "excel") return Send(TrialBalanceExport("excel", p, result));
        if (Format == "pdf") return Send(TrialBalanceExport("pdf", p, result));
        return View(new TrialBalancePage { Period = p, Loaded = true, From = p.From, To = p.To, AsOf = asOf, Result = result });
    }

    // ── profit & loss ───────────────────────────────────────────────────

    [HttpGet("profit-loss")]
    [Right(Section, Rights.View)]
    [ConsistentSnapshot]
    public async Task<IActionResult> ProfitLoss(CancellationToken ct)
    {
        var p = await ReportPeriod.ResolveAsync(Request, db, clock.Today, ct);
        if (p.From is null && !ExportAsked)
            return View(new ProfitLossPage { Period = p });
        var (rows, net) = await statements.ProfitLossAsync(p.From, p.To, p.ActiveComp, p.LabelIds, ct);
        if (Format == "excel") return Send(ProfitLossExcel(p, rows));
        if (Format == "pdf") return Send(ProfitLossPdf(p, rows));
        return View(new ProfitLossPage { Period = p, Loaded = true, From = p.From, To = p.To, Rows = rows, Net = net });
    }

    // ── P&L by label ────────────────────────────────────────────────────

    [HttpGet("label-pl")]
    [Right(Section, Rights.View)]
    [ConsistentSnapshot]
    public async Task<IActionResult> LabelPl(CancellationToken ct)
    {
        var labelsAll = await db.Set<ProjectLabel>().AsNoTracking().OrderBy(l => l.Name).ToListAsync(ct);
        var today = clock.Today;
        var q = Request.Query;
        if (!new[] { "from", "to", "label_id", "format", "run" }.Any(k => q.ContainsKey(k)))
            return View(new LabelPlPage
            {
                LabelsAll = labelsAll, FromStr = new DateOnly(today.Year, 1, 1).ToString("yyyy-MM-dd", Inv),
                ToStr = today.ToString("yyyy-MM-dd", Inv),
            });
        var from = ReportPeriod.ParseDate(q["from"].FirstOrDefault()) ?? new DateOnly(today.Year, 1, 1);
        var to = ReportPeriod.ParseDate(q["to"].FirstOrDefault()) ?? today;
        var picked = q["label_id"].Where(x => !string.IsNullOrEmpty(x) && x.All(char.IsAsciiDigit) && int.TryParse(x, out _))
            .Select(x => int.Parse(x!, Inv)).ToList();
        var labels = labelsAll.Where(l => picked.Count == 0 || picked.Contains(l.Id)).ToList();

        var lp = await statements.LabelPlAsync(from, to, labels, picked.Count == 0, ct);
        var (rows, net, nets, unlabelledNet) = (lp.Rows, lp.Net, lp.Nets, lp.UnlabelledNet);

        string? fmt = Format;
        if (fmt is "excel" or "pdf" or "csv")
            return Send(await LabelPlExportAsync(fmt, from, to, rows, net, labels, picked, nets, unlabelledNet, ct));
        return View(new LabelPlPage
        {
            Loaded = true, Rows = rows, Net = net, Labels = labels, LabelsAll = labelsAll, Picked = picked, LabelNets = nets,
            UnlabelledNet = unlabelledNet, FromStr = from.ToString("yyyy-MM-dd", Inv), ToStr = to.ToString("yyyy-MM-dd", Inv),
        });
    }

    // ── balance sheet ───────────────────────────────────────────────────

    [HttpGet("balance-sheet")]
    [Right(Section, Rights.View)]
    [ConsistentSnapshot]
    public async Task<IActionResult> BalanceSheet(CancellationToken ct)
    {
        var p = await ReportPeriod.ResolveAsync(Request, db, clock.Today, ct);
        if (p.From is null && !ExportAsked)
            return View(new BalanceSheetPage { Period = p, AsOf = clock.Today });
        var asOf = p.To ?? clock.Today;
        var result = await statements.BalanceSheetAsync(asOf, p.ActiveComp, p.LabelIds, ct);
        if (Format == "excel") return Send(BalanceSheetExcel(p, result));
        if (Format == "pdf") return Send(BalanceSheetPdf(p, result));
        return View(new BalanceSheetPage { Period = p, Loaded = true, From = p.From, To = p.To, AsOf = asOf, Result = result });
    }

    // ── SOCIE ───────────────────────────────────────────────────────────

    [HttpGet("socie")]
    [Right(Section, Rights.View)]
    [ConsistentSnapshot]
    public async Task<IActionResult> Socie(CancellationToken ct)
    {
        var p = await ReportPeriod.ResolveAsync(Request, db, clock.Today, ct);
        if (p.From is null && !ExportAsked)
            return View(new SociePage { Period = p });
        var (from, to) = await ReportPeriod.ExportDatesAsync(p.From, p.To, db, clock.Today, ct);
        var specs = FinanceStatements.SocieSpecs(from, to, p.ActiveComp);
        var (columns, rows) = await statements.SocieAsync(specs, p.LabelIds, ct);
        if (ExportAsked && specs.Count > 0)
            return Send(SocieExport(Format!, from, to, specs, columns, rows));
        return View(new SociePage { Period = p, Loaded = true, From = from, To = to, Columns = columns, Rows = rows, Specs = specs });
    }

    // ── cash flow ───────────────────────────────────────────────────────

    [HttpGet("cash-flow")]
    [Right(Section, Rights.View)]
    [ConsistentSnapshot]
    public async Task<IActionResult> CashFlow(CancellationToken ct)
    {
        var p = await ReportPeriod.ResolveAsync(Request, db, clock.Today, ct);
        if (p.From is null && !ExportAsked)
            return View(new CashFlowPage { Period = p, Result = new CashFlowResult() });
        string method = (await data.SettingsAsync(ct))?.CashFlowMethod is { Length: > 0 } m ? m : "indirect";
        var (from, to) = await ReportPeriod.ExportDatesAsync(p.From, p.To, db, clock.Today, ct);
        var result = await statements.CashFlowAsync(from, to, method, p.ActiveComp, p.LabelIds, ct);
        if (Format == "excel") return Send(CashFlowExcel(p, from, to, result));
        if (Format == "pdf") return Send(CashFlowPdf(p, from, to, result));
        return View(new CashFlowPage { Period = p, Loaded = true, From = from, To = to, Result = result });
    }

    // ── general ledger ──────────────────────────────────────────────────

    [HttpGet("ledger")]
    [Right(Section, Rights.View)]
    [ConsistentSnapshot]
    public async Task<IActionResult> Ledger(CancellationToken ct)
    {
        var chart = await data.ChartAsync(ct);
        var active = chart.ById.Values.Where(a => a.Active).OrderBy(a => a.Code, StringComparer.Ordinal).ToList();
        var parentIds = chart.ById.Values.Where(a => a.ParentId is not null).Select(a => a.ParentId!.Value).ToHashSet();
        var heads = active.Where(a => a.ParentId is not null && parentIds.Contains(a.Id)).ToList();
        var leaves = active.Where(a => !parentIds.Contains(a.Id)).ToList();

        var p = await ReportPeriod.ResolveAsync(Request, db, clock.Today, ct);
        if (p.From is null && !ExportAsked)
            return View(new LedgerPage { Period = p, Heads = heads, Leaves = leaves });

        var q = Request.Query;
        string mode = q["mode"].FirstOrDefault() ?? "all";
        string selectionMode = q["selection_mode"].FirstOrDefault() ?? "custom";
        string accountIdsStr = q["account_ids"].FirstOrDefault() ?? "";
        string headIdsStr = q["head_ids"].FirstOrDefault() ?? "";
        static List<int> Ints(string s) => s.Split(',').Select(x => x.Trim())
            .Where(x => x.Length > 0 && x.All(char.IsAsciiDigit) && int.TryParse(x, out _)).Select(x => int.Parse(x, Inv)).ToList();
        var ids = new List<int>();
        if (mode == "all")
            ids = active.Select(a => a.Id).ToList();
        else if (mode == "select")
        {
            if (selectionMode == "custom" && accountIdsStr.Length > 0)
                ids = Ints(accountIdsStr);
            else if (selectionMode == "head" && headIdsStr.Length > 0)
                ids = Ints(headIdsStr).SelectMany(h => FinanceStatements.LeafDescendants(chart, h)).Distinct().Order().ToList();
        }
        var sections = ids.Count > 0 ? await statements.LedgerSectionsAsync(ids, p.From, p.To, p.LabelIds, ct) : [];
        sections = sections.Where(s => !s.Empty).ToList();

        if (Format == "excel") return Send(LedgerExcel(p, sections));
        if (Format == "pdf") return Send(LedgerPdf(p, sections));
        return View(new LedgerPage
        {
            Period = p, Loaded = true, From = p.From, To = p.To, Sections = sections, Heads = heads, Leaves = leaves, Mode = mode,
            SelectionMode = selectionMode, SelectedAccountIds = accountIdsStr, SelectedHeadIds = headIdsStr,
        });
    }
}
