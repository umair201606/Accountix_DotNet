using System.Globalization;
using Accountix.Executive.Services;

namespace Accountix.Executive.Controllers;

public sealed class DashboardModel
{
    public required SideTotals Receivable { get; init; }
    public required SideTotals Payable { get; init; }
    public required AgingSide RecvAge { get; init; }
    public required AgingSide PayAge { get; init; }
    public required Liquidity Liq { get; init; }
    public required Profitability Profit { get; init; }
    public ProfitChart? ProfitGeo { get; init; }
    public bool Configured { get; init; }
    public string AsOf { get; init; } = "";
    public bool CanEdit { get; init; }
}

public sealed class RegisterModel
{
    public required string Side { get; init; }
    public required ExecutiveReports.SideMeta Meta { get; init; }
    public required List<PartyRow> Rows { get; init; }
    public required SideTotals Totals { get; init; }
    public required List<GroupOption> Groups { get; init; }
    public bool Configured { get; init; }
    public string Q { get; init; } = "";
    public int? Group { get; init; }
    public string AsOf { get; init; } = "";
    public bool CanEdit { get; init; }
}

public sealed record PartyModel(PartyRow Row, ExecutiveReports.SideMeta Meta, List<LedgerLine> Lines);

public sealed record SettingsModel(List<SelectableAccount> Accounts, bool CanEdit);

public sealed class IntegrityModel
{
    public bool Loaded { get; init; }
    public List<IntegrityCheck> Checks { get; init; } = [];
    public bool? AllOk { get; init; }
    public List<StockCostAdjustment> Adjustments { get; init; } = [];
}

/// <summary>Executive Reports (executive_app/routes/reports.py): receivables, payables and the accounts they watch, Books Integrity.</summary>
[Area("Executive")]
[Module(Modules.Executive)]
[Route("executive")]
public sealed class ExecutiveController(ExecutiveReports reports, BooksIntegrity books, CompanyClock clock) : Controller
{
    private const string Resource = "executive_reports";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] DateFormats = ["yyyy-M-d", "d/M/yyyy", "M/d/yyyy"];

    /// <summary>_parse_date: YYYY-MM-DD, DD/MM/YYYY or MM/DD/YYYY; anything else is no date.</summary>
    public static DateOnly? ParseDate(string? value)
    {
        value = (value ?? "").Trim();
        if (value.Length == 0) return null;
        foreach (string f in DateFormats)
            if (DateOnly.TryParseExact(value, f, Inv, DateTimeStyles.None, out var d))
                return d;
        return null;
    }

    private bool CanEdit => HttpContext.Access()?.Can(Resource, Rights.Edit) == true;

    [HttpGet("")]
    [Right(Resource, Rights.View)]
    [ConsistentSnapshot]
    public async Task<IActionResult> Dashboard([FromQuery(Name = "as_of")] string? asOfText, CancellationToken ct)
    {
        var asOf = ParseDate(asOfText);
        var (receivable, payable) = await reports.BothSidesAsync(asOf, ct);
        var (recvAge, payAge) = await reports.AgingAsync(asOf, ct);
        // Profitability is ledger-derived and independent of the account selection.
        var profit = await reports.ProfitabilityAsync(asOf: asOf, ct: ct);
        return View(new DashboardModel
        {
            Receivable = receivable, Payable = payable, RecvAge = recvAge, PayAge = payAge,
            Liq = await reports.LiquidityAsync(asOf, ct), Profit = profit, ProfitGeo = ProfitChart.From(profit),
            Configured = (await reports.SelectionsAsync(ct)).Count > 0, AsOf = asOfText ?? "", CanEdit = CanEdit,
        });
    }

    [HttpGet("receivables")]
    [Right(Resource, Rights.View)]
    [ConsistentSnapshot]
    public Task<IActionResult> Receivables([FromServices] IReportExport export, CancellationToken ct) =>
        RegisterAsync(ExecutiveReports.Receivable, export, ct);

    [HttpGet("payables")]
    [Right(Resource, Rights.View)]
    [ConsistentSnapshot]
    public Task<IActionResult> Payables([FromServices] IReportExport export, CancellationToken ct) =>
        RegisterAsync(ExecutiveReports.Payable, export, ct);

    private async Task<IActionResult> RegisterAsync(string side, IReportExport export, CancellationToken ct)
    {
        string? asOfText = Request.Query["as_of"].FirstOrDefault();
        var asOf = ParseDate(asOfText);
        string search = (Request.Query["q"].FirstOrDefault() ?? "").Trim();
        int? groupId = int.TryParse(Request.Query["group"].FirstOrDefault(), NumberStyles.Integer, Inv, out int g) ? g : null;
        var rows = await reports.PartyRowsAsync(side, asOf, search.Length > 0 ? search : null, groupId, ct);
        string? fmt = Request.Query["format"].FirstOrDefault();
        if (!string.IsNullOrEmpty(fmt))
            return await ExportRegisterAsync(fmt, side, rows, asOf, search, groupId, export, ct);
        return View("Register", new RegisterModel
        {
            Side = side, Meta = ExecutiveReports.Meta[side], Rows = rows, Totals = ExecutiveReports.Totals(rows),
            Groups = await reports.GroupsAsync(ct), Configured = (await reports.SelectionsAsync(ct)).Count > 0,
            Q = search, Group = groupId, AsOf = asOfText ?? "", CanEdit = CanEdit,
        });
    }

    private string AsOfText(DateOnly? asOf) => asOf is DateOnly d
        ? $"Balances as at {d.ToString("dd MMM yyyy", Inv)}"
        : $"Balances as at {clock.Now.ToString("dd MMM yyyy", Inv)} (all postings)";

    /// <summary>Aged receivables / payables: each party's balance split into the dashboard's FIFO buckets.</summary>
    private async Task<IActionResult> ExportRegisterAsync(string fmt, string side, List<PartyRow> rows, DateOnly? asOf, string search,
                                                          int? groupId, IReportExport export, CancellationToken ct)
    {
        var meta = ExecutiveReports.Meta[side];
        var ages = await reports.PartyAgingAsync(asOf, ct);
        var defs = ExecutiveReports.BucketDefs;
        var columns = new List<ReportColumn>
        {
            new("Code"), new("Party / account"), new("Group"),
            new("Debit", ReportColumnKind.Money), new("Credit", ReportColumnKind.Money), new(meta.Title, ReportColumnKind.Money),
        };
        columns.AddRange(defs.Select(b => new ReportColumn(b.Label, ReportColumnKind.Money)));
        columns.Add(new("Avg days", ReportColumnKind.Integer));
        columns.Add(new("Oldest days", ReportColumnKind.Integer));
        var sums = defs.ToDictionary(b => b.Key, _ => 0.0);
        var data = new List<ReportRow>();
        foreach (var r in rows)
        {
            ages.TryGetValue(r.AccountId, out var a);
            var cells = new List<object?> { r.Code, r.Name, r.Group, r.Debit, r.Credit, r.Amount };
            foreach (var (key, _, _, _) in defs)
            {
                double v = a?.Buckets.GetValueOrDefault(key) ?? 0.0;
                sums[key] += v;
                cells.Add(Py.FromDouble(v));
            }
            cells.Add(a is null ? "" : Py.RoundInt(a.AvgDays));
            cells.Add(a is null ? "" : a.OldestDays);
            data.Add(new ReportRow(cells));
        }
        var t = ExecutiveReports.Totals(rows);
        var total = new List<object?> { "", $"Total ({t.Parties} parties)", "", rows.Sum(r => r.Debit), rows.Sum(r => r.Credit), t.Amount };
        total.AddRange(defs.Select(b => (object?)Py.FromDouble(sums[b.Key])));
        total.Add("");
        total.Add("");
        data.Add(new ReportRow(total, "grand"));
        var filters = new List<(string, string)>();
        if (search.Length > 0) filters.Add(("", $"Search: {search}"));
        if (groupId is int gid && gid != 0)
        {
            var group = (await reports.GroupsAsync(ct)).FirstOrDefault(x => x.Id == gid);
            filters.Add(("", $"Group: {group?.Label ?? gid.ToString(Inv)}"));
        }
        var file = await export.ExportTableAsync(fmt, new ReportTable
        {
            Title = $"Aged {meta.Title}", Period = AsOfText(asOf) + " · FIFO aging", Filters = filters,
            Columns = columns, Rows = data, FilePeriod = (asOf ?? clock.Today).ToString("yyyy-MM-dd", Inv),
        }, ct);
        return File(file.Content, file.ContentType, file.FileName);
    }

    [HttpGet("party/{accountId:int}")]
    [Right(Resource, Rights.View)]
    [ConsistentSnapshot]
    public async Task<IActionResult> Party(int accountId, [FromServices] IReportExport export, CancellationToken ct)
    {
        var row = (await reports.PartyRowsAsync(ct: ct)).FirstOrDefault(r => r.AccountId == accountId);
        if (row is null)
        {
            this.Flash("That account is not in the executive report scope, or carries no balance.", "error");
            return Redirect("/executive/receivables");
        }
        var asOf = ParseDate(Request.Query["as_of"].FirstOrDefault());
        string? fmt = Request.Query["format"].FirstOrDefault();
        if (!string.IsNullOrEmpty(fmt))
        {
            // Every posting, oldest first, so the running balance is right; the screen's list is newest-first and capped.
            var chrono = (await reports.AccountLedgerAsync(accountId, asOf, null, ct)).AsEnumerable().Reverse().ToList();
            var data = new List<ReportRow>();
            decimal bal = 0m;
            foreach (var l in chrono)
            {
                bal += l.Debit - l.Credit;
                string voucher = string.Join(" ", new[] { l.VoucherType, l.VoucherNumber }.Where(x => !string.IsNullOrEmpty(x)));
                data.Add(new ReportRow([l.Date, voucher, l.Description, l.Debit, l.Credit, bal]));
            }
            data.Add(new ReportRow(["", "", "Balance", row.Debit, row.Credit, row.Amount], "grand"));
            var file = await export.ExportTableAsync(fmt, new ReportTable
            {
                Title = $"Party Ledger — {row.Name}", Period = $"{row.Code} · {row.Group} · " + AsOfText(asOf),
                Columns =
                [
                    new("Date", ReportColumnKind.Date), new("Voucher"), new("Narration"), new("Debit", ReportColumnKind.Money),
                    new("Credit", ReportColumnKind.Money), new("Running balance (Dr+)", ReportColumnKind.Money),
                ],
                Rows = data, FilePeriod = row.Code,
            }, ct);
            return File(file.Content, file.ContentType, file.FileName);
        }
        var lines = await reports.AccountLedgerAsync(accountId, asOf, 200, ct);
        return View(new PartyModel(row, ExecutiveReports.Meta[row.Side], lines));
    }

    [HttpGet("settings")]
    [Right(Resource, Rights.View)]
    public async Task<IActionResult> Settings(CancellationToken ct) =>
        View(new SettingsModel(await reports.SelectableAccountsAsync(ct), CanEdit));

    /// <summary>Pick the accounts the two registers read (replace, not merge).</summary>
    [HttpPost("settings")]
    [Right(Resource, Rights.Edit)]
    public async Task<IActionResult> SaveSettings(CancellationToken ct)
    {
        if (this.DenyPage(Resource, Rights.View))
            return Redirect("/hub");
        var form = await Request.ReadFormAsync(ct);
        int count = await reports.SetSelectionAsync(form["account"].ToArray(), form["deep"].ToArray(),
                                                    HttpContext.Access()?.User.Id, clock.UtcNow, ct);
        this.Flash($"{count} account{(count == 1 ? "" : "s")} now feed the executive reports.", "success");
        return Redirect("/executive/settings");
    }

    /// <summary>Books integrity: does every sub-ledger still agree with the GL? Runs when asked (Run checks / export).</summary>
    [HttpGet("integrity")]
    [Right(Resource, Rights.View)]
    [ConsistentSnapshot]
    public async Task<IActionResult> Integrity([FromServices] IReportExport export, CancellationToken ct)
    {
        string? fmt = Request.Query["format"].FirstOrDefault();
        if (string.IsNullOrEmpty(Request.Query["run"].FirstOrDefault()) && string.IsNullOrEmpty(fmt))
            return View(new IntegrityModel { Loaded = false });
        var checks = await books.RunAllAsync(ct);
        var adjustments = await books.RecentAdjustmentsAsync(ct);
        if (!string.IsNullOrEmpty(fmt))
        {
            var rows = checks.Select(c => new ReportRow([c.Label, c.Ok ? "Pass" : "Fail", c.Expected, c.Actual, c.Detail])).ToList();
            int passed = checks.Count(c => c.Ok);
            string verdict = passed == checks.Count ? "All checks pass" : $"{checks.Count - passed} of {checks.Count} checks FAIL";
            var now = clock.Now;     // the company's clock, not the server's UTC
            bool excel = fmt is "excel" or "xlsx";
            var extra = new List<(string Name, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<object?>> Rows)>();
            if (excel)
                extra.Add(("Recent re-costings", ["Document", "Posted on", "Was", "Now", "Change", "Why"],
                           adjustments.Select(a => (IReadOnlyList<object?>)new object?[] { a.VoucherNumber, a.EntryDate, a.OldCost, a.NewCost, a.Delta, a.Trigger ?? "" }).ToList()));
            var file = await export.ExportTableAsync(excel ? "excel" : fmt, new ReportTable
            {
                Title = "Books Integrity",
                Period = $"{verdict} · run {now.ToString("dd MMM yyyy HH:mm", Inv)}",
                Columns = [new("Check"), new("Status"), new("Expected", ReportColumnKind.Money), new("Actual", ReportColumnKind.Money), new("Detail")],
                Rows = rows,
                FilePeriod = now.ToString("yyyy-MM-dd", Inv),
                ExtraSheets = extra,
            }, ct);
            return File(file.Content, file.ContentType, file.FileName);
        }
        return View(new IntegrityModel { Loaded = true, Checks = checks, AllOk = checks.All(c => c.Ok), Adjustments = adjustments });
    }
}
