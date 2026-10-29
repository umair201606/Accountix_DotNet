using System.Globalization;
using Accountix.Accounting.Services;

namespace Accountix.Accounting.Controllers;

/// <summary>Voucher lists, print, register and the JSON the voucher pages call.</summary>
public sealed partial class VouchersController
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ── list ────────────────────────────────────────────────────────────

    [HttpGet("vouchers/list")]
    [AnyMember]   // _voucher_listing: one type needs its view right, all types any
    public async Task<IActionResult> List([FromQuery] string? vtype, [FromQuery] string? status, CancellationToken ct)
    {
        vtype ??= "";
        status ??= "";
        if (ListingRefusal(vtype) is { } refused) return refused;
        var (from, to, periods, selected, mode, fromStr, toStr) = await ResolveVoucherPeriodAsync(ct);
        var types = ViewableTypes();
        var q = db.Set<AccountingVoucher>()
            .Where(v => !v.VoucherNumber.EndsWith("-REV") && types.Contains(v.VoucherType));
        if (vtype.Length > 0) q = q.Where(v => v.VoucherType == vtype);
        if (status.Length > 0) q = q.Where(v => v.Status == status);
        if (from is DateTime f) q = q.Where(v => v.VoucherDate >= f);
        if (to is DateTime t) q = q.Where(v => v.VoucherDate <= t);
        var vouchers = await q.Include(v => v.CashBankAccount).Include(v => v.CreatedByNavigation)
            .Include(v => v.ApprovedByNavigation).OrderByDescending(v => v.Id).AsNoTracking().ToListAsync(ct);
        return View(new VoucherListModel
        {
            Vouchers = vouchers, VType = vtype, Status = status,
            Filter = new PeriodFilterModel
            {
                FilterMode = mode, Periods = periods, SelectedPeriodId = selected,
                FromStr = fromStr, ToStr = toStr, ShowLabels = false,
            },
        });
    }

    private IActionResult? ListingRefusal(string? vtype)
    {
        vtype = (vtype ?? "").Trim().ToUpperInvariant();
        if (Modules.VoucherSection.TryGetValue(vtype, out var section))
            return this.Deny(section, Rights.View);
        return ViewableTypes().Count > 0 ? null : Refusal.Right(HttpContext, Rights.View);
    }

    /// <summary>_resolve_voucher_period: single period (default: the current one) or a custom range.</summary>
    private async Task<(DateTime? From, DateTime? To, List<AccountingPeriod> Periods, int? Selected, string Mode, string FromStr, string ToStr)>
        ResolveVoucherPeriodAsync(CancellationToken ct)
    {
        var q = Request.Query;
        string mode = q["filter_mode"].FirstOrDefault() ?? "period";
        int? periodId = int.TryParse(q["period_id"].FirstOrDefault(), out int pid) ? pid : null;
        string fromStr = (q["from"].FirstOrDefault() ?? "").Trim();
        string toStr = (q["to"].FirstOrDefault() ?? "").Trim();
        DateTime? from = PeriodFilters.ParseDate(fromStr)?.ToDateTime(TimeOnly.MinValue);
        DateTime? to = PeriodFilters.ParseDate(toStr)?.ToDateTime(TimeOnly.MaxValue);
        var periods = await PeriodFilters.AllAsync(db, ct);
        int? selected = periodId;
        if (mode == "period" && periodId is int p && periods.FirstOrDefault(x => x.Id == p) is { } period)
        {
            from = period.StartDate.ToDateTime(TimeOnly.MinValue);
            to = period.EndDate.ToDateTime(TimeOnly.MaxValue);
        }
        if (from is null && to is null)
        {
            if (await PeriodFilters.DefaultAsync(db, clock.Today, ct) is { } active)
            {
                from = active.StartDate.ToDateTime(TimeOnly.MinValue);
                to = active.EndDate.ToDateTime(TimeOnly.MaxValue);
                selected ??= active.Id;
            }
        }
        else if (mode == "period" && selected is null && await PeriodFilters.DefaultAsync(db, clock.Today, ct) is { } active)
            selected = active.Id;
        return (from, to, periods, selected, mode, fromStr, toStr);
    }

    // ── print / export ──────────────────────────────────────────────────

    [HttpGet("vouchers/{id:int}/preview")]
    [AnyMember]   // the section follows the voucher's type
    public async Task<IActionResult> Preview(int id, CancellationToken ct)
    {
        var v = await DocAsync(id, ct);
        if (v is null) return NotFound();
        if (this.Deny(Section(v.VoucherType), Rights.View) is { } refused) return refused;
        return View(VoucherDoc.For(v));
    }

    [HttpGet("vouchers/{id:int}/export")]
    [AnyMember]   // the section follows the voucher's type
    public async Task<IActionResult> Export(int id, [FromQuery] string? fmt, [FromServices] IReportExport export, CancellationToken ct)
    {
        var v = await DocAsync(id, ct);
        if (v is null) return NotFound();
        if (this.Deny(Section(v.VoucherType), Rights.View) is { } refused) return refused;
        var doc = VoucherDoc.For(v);
        var rows = doc.Lines.Select((l, i) => new ReportRow(
        [
            i + 1, l.Account?.Code ?? "", l.Account?.Name ?? "", l.Description ?? "", l.Debit ?? 0m, l.Credit ?? 0m,
        ])).ToList();
        rows.Add(new ReportRow(["", "", "", "Total", doc.TotalDebit, doc.TotalCredit], "grand"));
        string status = v.Status == "approved" ? "Approved" : ViewHelpers.Title(v.Status);
        string period = string.Join(" · ", new[] { v.VoucherDate.ToString("dd MMM yyyy", Inv), status }.Where(x => x.Length > 0));
        string narration = (v.Notes ?? "").Trim();
        var file = await export.ExportTableAsync(fmt ?? "pdf", new ReportTable
        {
            Title = $"{Labels[v.VoucherType]} {v.VoucherNumber}",
            Period = period,
            Filters = narration.Length > 0 ? [("", narration)] : [],
            Columns =
            [
                new("#", ReportColumnKind.Integer), new("A/c Code"), new("Account Name"), new("Description"),
                new("Debit", ReportColumnKind.Money), new("Credit", ReportColumnKind.Money),
            ],
            Rows = rows,
            MonoColumn = 1,
        }, ct);
        return File(file.Content, file.ContentType, file.FileName);
    }

    private IQueryable<AccountingVoucher> DocQuery() =>
        db.Set<AccountingVoucher>().AsNoTracking()
            .Include(v => v.AccountingVoucherLines).ThenInclude(l => l.Account)
            .Include(v => v.CashBankAccount).Include(v => v.CreatedByNavigation).Include(v => v.ApprovedByNavigation)
            .AsSplitQuery();

    private Task<AccountingVoucher?> DocAsync(int id, CancellationToken ct) =>
        DocQuery().FirstOrDefaultAsync(v => v.Id == id, ct);

    /// <summary>Many vouchers with their lines in a few queries (500 at a time), in the order of <paramref name="ids"/>.</summary>
    private async Task<List<AccountingVoucher>> DocsAsync(IReadOnlyList<int> ids, CancellationToken ct)
    {
        var found = new Dictionary<int, AccountingVoucher>(ids.Count);
        foreach (var chunk in ids.Chunk(500))
            foreach (var v in await DocQuery().Where(v => chunk.Contains(v.Id)).ToListAsync(ct))
                found[v.Id] = v;
        return ids.Where(found.ContainsKey).Select(id => found[id]).ToList();
    }

    // ── register ────────────────────────────────────────────────────────

    private (string VType, string Status) RegisterArgs()
    {
        string vtype = (Request.Query["vtype"].FirstOrDefault() ?? "").Trim().ToUpperInvariant();
        if (!Labels.ContainsKey(vtype)) vtype = "";
        string status = (Request.Query["status"].FirstOrDefault() ?? "").Trim().ToLowerInvariant();
        if (status is not ("approved" or "unapproved")) status = "";
        return (vtype, status);
    }

    private IQueryable<AccountingVoucher> RegisterQuery(string vtype, string status, DateOnly? from, DateOnly? to)
    {
        var types = ViewableTypes();
        var q = db.Set<AccountingVoucher>().Where(v => !v.VoucherNumber.EndsWith("-REV") && types.Contains(v.VoucherType));
        if (vtype.Length > 0) q = q.Where(v => v.VoucherType == vtype);
        if (status.Length > 0) q = q.Where(v => v.Status == status);
        var (start, end) = PeriodFilters.DayBounds(from, to);
        if (start is DateTime s) q = q.Where(v => v.VoucherDate >= s);
        if (end is DateTime e) q = q.Where(v => v.VoucherDate <= e);
        return q;
    }

    /// <summary>_voucher_register_rows: (rows, debit, credit, count); totals cover the whole filtered set.</summary>
    private async Task<(List<RegisterRow> Rows, decimal Debit, decimal Credit, int Total)> RegisterRowsAsync(
        string vtype, string status, DateOnly? from, DateOnly? to, int? limit = null, int offset = 0, int? knownTotal = null,
        CancellationToken ct = default)
    {
        var baseQ = RegisterQuery(vtype, status, from, to);
        int total = knownTotal ?? await baseQ.CountAsync(ct);
        var ids = baseQ.Select(v => v.Id);
        var sums = await db.Set<AccountingVoucherLine>().Where(l => ids.Contains(l.VoucherId))
            .GroupBy(_ => 1).Select(g => new { Dr = g.Sum(l => l.Debit ?? 0), Cr = g.Sum(l => l.Credit ?? 0) })
            .FirstOrDefaultAsync(ct);
        var ordered = baseQ.OrderBy(v => v.VoucherDate).ThenBy(v => v.Id).AsNoTracking();
        if (limit is int l)
            ordered = ordered.Skip(offset).Take(l);
        var vouchers = await ordered.ToListAsync(ct);
        var vids = vouchers.Select(v => v.Id).ToList();
        var lines = (await db.Set<AccountingVoucherLine>().Include(x => x.Account).AsNoTracking()
                .Where(x => vids.Contains(x.VoucherId)).ToListAsync(ct))
            .GroupBy(x => x.VoucherId).ToDictionary(g => g.Key, g => g.ToList());
        var rows = vouchers.Select(v =>
        {
            var vl = lines.GetValueOrDefault(v.Id) ?? [];
            return new RegisterRow(v.Id, v.VoucherDate, v.VoucherNumber, Narration(v, vl),
                                   vl.Sum(x => x.Debit ?? 0), vl.Sum(x => x.Credit ?? 0));
        }).ToList();
        return (rows, sums?.Dr ?? 0, sums?.Cr ?? 0, total);
    }

    /// <summary>The register's Description: the voucher's notes, else its first user line, never the cash/bank line.</summary>
    public static string Narration(AccountingVoucher v, IEnumerable<AccountingVoucherLine> lines)
    {
        if (!string.IsNullOrWhiteSpace(v.Notes)) return v.Notes.Trim();
        var ordered = lines.OrderBy(l => l.LineNo).ToList();
        var line = ordered.FirstOrDefault(l => l.LineNo != 0 && !string.IsNullOrWhiteSpace(l.Description))
                   ?? ordered.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.Description));
        if (line is not null) return line.Description!.Trim();
        var withAccount = ordered.FirstOrDefault(l => l.Account is not null);
        return withAccount is not null ? $"{withAccount.Account.Code} {withAccount.Account.Name}" : "-";
    }

    [HttpGet("registers/vouchers")]
    [AnyMember]   // _voucher_listing
    public async Task<IActionResult> Register(CancellationToken ct)
    {
        var (vtype, status) = RegisterArgs();
        if (ListingRefusal(vtype) is { } refused) return refused;
        var f = await RegisterFilter.ResolveAsync(Request.Query, db, clock.Today, ct: ct);
        bool loaded = Request.Query["view"] == "1";
        var model = new RegisterModel { VType = vtype, Status = status, Filter = f, Loaded = loaded, Now = clock.Now };
        if (loaded)
            (model.Rows, model.TotalDebit, model.TotalCredit, _) = await RegisterRowsAsync(vtype, status, f.From, f.To, ct: ct);
        return View(model);
    }

    [HttpGet("registers/vouchers/data")]
    [AnyMember]   // _voucher_listing
    public async Task<IActionResult> RegisterData(CancellationToken ct)
    {
        var (vtype, status) = RegisterArgs();
        if (ListingRefusal(vtype) is { } refused) return refused;
        bool full = Request.Query["full"] == "1";
        int limit = int.TryParse(Request.Query["limit"], out int lm) ? Math.Clamp(lm, 1, full ? 100 : 500) : full ? 100 : 200;
        int offset = int.TryParse(Request.Query["offset"], out int of) ? Math.Max(0, of) : 0;
        int? known = int.TryParse(Request.Query["total"], out int kt) ? Math.Max(0, kt) : null;
        var f = await RegisterFilter.ResolveAsync(Request.Query, db, clock.Today, ct: ct);
        var (rows, dr, cr, total) = await RegisterRowsAsync(vtype, status, f.From, f.To, limit, offset, known, ct);
        if (full)
        {
            var docs = new List<string>();
            foreach (var v in await DocsAsync(rows.Select(r => r.Id).ToList(), ct))
                docs.Add(await this.RenderViewAsync("_VoucherDoc", VoucherDoc.For(v)));
            return Json(new { total, rows = rows.Count, offset, limit, docs });
        }
        string html = await this.RenderViewAsync("_VoucherTableBody", rows);
        string tfoot = await this.RenderViewAsync("_VoucherTableTotals", (dr, cr));
        return Json(new
        {
            total, rows = rows.Count, debit = (double)dr, credit = (double)cr,
            debit_label = this.Amount(dr, 0), credit_label = this.Amount(cr, 0),
            offset, limit, html, tfoot,
        });
    }

    [HttpGet("registers/vouchers/documents")]
    [AnyMember]   // _voucher_listing
    public async Task<IActionResult> RegisterDocuments(CancellationToken ct)
    {
        var (vtype, status) = RegisterArgs();
        if (ListingRefusal(vtype) is { } refused) return refused;
        var f = await RegisterFilter.ResolveAsync(Request.Query, db, clock.Today, ct: ct);
        List<VoucherDoc>? docs = null;
        if (Request.Query["view"] == "1")
        {
            var ids = await RegisterQuery(vtype, status, f.From, f.To).OrderBy(v => v.VoucherDate).ThenBy(v => v.Id)
                .Select(v => v.Id).ToListAsync(ct);
            docs = (await DocsAsync(ids, ct)).Select(VoucherDoc.For).ToList();
        }
        string label = f.Label + (vtype.Length > 0 ? $" · {Labels[vtype]}" : "") + (status.Length > 0 ? $" · {ViewHelpers.Title(status)}" : "");
        return View(new DocumentsModel { Docs = docs, Label = label });
    }

    [HttpGet("registers/vouchers/export")]
    [AnyMember]   // _voucher_listing
    public async Task<IActionResult> RegisterExport([FromServices] IReportExport export, CancellationToken ct)
    {
        string fmt = (Request.Query["fmt"].FirstOrDefault() ?? "pdf").Trim().ToLowerInvariant();
        if (fmt is not ("excel" or "pdf" or "csv")) return NotFound();
        var (vtype, status) = RegisterArgs();
        if (ListingRefusal(vtype) is { } refused) return refused;
        var f = await RegisterFilter.ResolveAsync(Request.Query, db, clock.Today, ct: ct);
        var vouchers = await RegisterQuery(vtype, status, f.From, f.To)
            .Include(v => v.AccountingVoucherLines).ThenInclude(l => l.Account)
            .OrderBy(v => v.VoucherDate).ThenBy(v => v.Id).AsNoTracking().AsSplitQuery().ToListAsync(ct);
        var rows = new List<ReportRow>();
        decimal tdr = 0, tcr = 0;
        foreach (var v in vouchers)
        {
            decimal dr = Money.Round2(v.AccountingVoucherLines.Sum(l => l.Debit ?? 0));
            decimal cr = Money.Round2(v.AccountingVoucherLines.Sum(l => l.Credit ?? 0));
            tdr += dr;
            tcr += cr;
            rows.Add(new ReportRow([v.VoucherDate, v.VoucherNumber, Narration(v, v.AccountingVoucherLines), dr, cr], "account"));
        }
        rows.Add(new ReportRow(["", "", "TOTAL", Money.Round2(tdr), Money.Round2(tcr)], "grand"));
        string subtitle = f.Label + (vtype.Length > 0 ? $" · {Labels[vtype]}" : "") + (status.Length > 0 ? $" · {ViewHelpers.Title(status)}" : "");
        var file = await export.ExportTableAsync(fmt, new ReportTable
        {
            Title = "Voucher Register",
            Period = subtitle,
            Filters = [("", $"{vouchers.Count} vouchers")],
            Columns = [new("Date", ReportColumnKind.Date), new("Voucher #"), new("Description"), new("Debit", ReportColumnKind.Money), new("Credit", ReportColumnKind.Money)],
            Rows = rows,
            MonoColumn = 1,
            FilePeriod = f.Label,
        }, ct);
        return File(file.Content, file.ContentType, file.FileName);
    }

    // ── JSON the forms call ─────────────────────────────────────────────

    [HttpGet("api/accounts")]
    [AnyMember]   // OPEN in the rights map: any account picker
    public async Task<IActionResult> ApiAccounts([FromQuery] string? q, [FromQuery] int? exclude, CancellationToken ct)
    {
        q = (q ?? "").Trim();
        // Only level-5 operational accounts are postable.
        var query = db.Set<ChartOfAccount>().Where(a => a.IsActive == true && a.Level >= Coa.PostingLevel);
        if (q.Length > 0)
            query = query.Where(a => EF.Functions.ILike(a.Name, $"%{q}%") || EF.Functions.ILike(a.Code, $"%{q}%"));
        if (exclude is int ex && ex != 0)
            query = query.Where(a => a.Id != ex);
        var list = await query.OrderBy(a => a.Code).Take(30).Select(a => new { id = a.Id, code = a.Code, name = a.Name, type = a.Type }).ToListAsync(ct);
        return Json(list);
    }

    [HttpGet("api/cash-bank-accounts")]
    [AnyMember]   // OPEN in the rights map
    public async Task<IActionResult> ApiCashBankAccounts([FromQuery] string? q, CancellationToken ct)
    {
        q = (q ?? "").Trim();
        var query = db.Set<ChartOfAccount>().Where(a => a.IsActive == true && a.Type == "asset" && a.Level >= Coa.PostingLevel
            && (EF.Functions.ILike(a.Name, "%cash%") || EF.Functions.ILike(a.Name, "%bank%") || a.Code.StartsWith("1-01-01-")));
        if (q.Length > 0)
            query = query.Where(a => EF.Functions.ILike(a.Name, $"%{q}%"));
        var list = await query.OrderBy(a => a.Code).Select(a => new { id = a.Id, code = a.Code, name = a.Name, type = a.Type }).ToListAsync(ct);
        return Json(list);
    }

    [HttpGet("api/account-balance")]
    [AnyRight("cash_payment_vouchers:view", "cash_payment_vouchers:create", "cash_payment_vouchers:edit",
              "cash_receipt_vouchers:view", "cash_receipt_vouchers:create", "cash_receipt_vouchers:edit",
              "bank_payment_vouchers:view", "bank_payment_vouchers:create", "bank_payment_vouchers:edit",
              "bank_receipt_vouchers:view", "bank_receipt_vouchers:create", "bank_receipt_vouchers:edit",
              "journal_vouchers:view", "journal_vouchers:create", "journal_vouchers:edit", "bank_reconciliation:view")]
    public async Task<IActionResult> ApiAccountBalance([FromQuery] int? id, CancellationToken ct)
    {
        var acc = id is int aid ? await db.Set<ChartOfAccount>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == aid, ct) : null;
        if (acc is null) return NotFound(new { ok = false });
        var sums = await db.Set<GlDailyBalance>().Where(g => g.AccountId == acc.Id)
            .GroupBy(_ => 1).Select(g => new { Dr = g.Sum(x => x.Debit), Cr = g.Sum(x => x.Credit) }).FirstOrDefaultAsync(ct);
        return Json(new { ok = true, id = acc.Id, code = acc.Code, name = acc.Name, balance = (double)((sums?.Dr ?? 0) - (sums?.Cr ?? 0)) });
    }

    /// <summary>Journal type → the rights section of the document that posted it (rights_guard._JOURNAL_SECTION).</summary>
    private static readonly IReadOnlyDictionary<string, string> JournalSection = new Dictionary<string, string>
    {
        ["SI"] = "sales_invoices", ["PI"] = "purchase_invoices", ["PMT"] = "purchase_invoices",
        ["SR"] = "sales_returns", ["PR"] = "purchase_returns",
        ["CONS"] = "consumption_vouchers", ["SCRAP"] = "scrap_vouchers", ["ADJ"] = "adjustment_vouchers", ["OPENING"] = "products",
        ["FA-ACQ"] = "fixed_assets_assets", ["FA-DEP"] = "fixed_assets_assets", ["FA-DISP"] = "fixed_assets_assets",
        ["FA-CAP"] = "fixed_assets_assets", ["FA-TRF"] = "fixed_assets_assets",
        ["PRL"] = "payroll", ["PRL-PAY"] = "payroll", ["LOAN-DSB"] = "loans",
        ["CPV"] = "cash_payment_vouchers", ["CRV"] = "cash_receipt_vouchers", ["BPV"] = "bank_payment_vouchers",
        ["BRV"] = "bank_receipt_vouchers", ["JV"] = "journal_vouchers",
    };

    private bool MayReadJournal(string? vtype)
    {
        vtype = (vtype ?? "").Trim().ToUpperInvariant();
        if (vtype.StartsWith("CADJ-")) vtype = vtype[5..];
        string? section = JournalSection.GetValueOrDefault(vtype) ?? (vtype.StartsWith("PF-") ? "pf" : null);
        return Access.Can("financial_reports", Rights.View) || (section is not null && Access.Can(section, Rights.View));
    }

    [HttpGet("gl-entry")]
    [AnyMember]   // _gl_entry: the document's own section, or financial reports
    public async Task<IActionResult> GlEntry([FromQuery] string? type, [FromQuery] int? id, [FromQuery] string? also,
                                             [FromServices] GlEntries entries, CancellationToken ct)
    {
        string vtype = (type ?? "").Trim();
        var alsoPairs = new List<(string Type, int Id)>();
        foreach (string part in (also ?? "").Split(',').Take(200))
        {
            string p = part.Trim();
            int colon = p.LastIndexOf(':');
            if (colon > 0 && int.TryParse(p[(colon + 1)..], out int aid) && p[(colon + 1)..].All(char.IsAsciiDigit) && colon <= 20)
                alsoPairs.Add((p[..colon].ToUpperInvariant(), aid));
        }
        if (!MayReadJournal(vtype) || alsoPairs.Any(a => !MayReadJournal(a.Type)))
            return Refusal.Right(HttpContext, Rights.View, json: true);
        if (vtype.Length == 0 || vtype.Length > 20)
            return BadRequest(new { ok = false, error = "Unknown document type" });
        if (id is not int vid || vid == 0)
            return Json(new { ok = true, posted = Array.Empty<object>(), history = Array.Empty<object>(), saved = false });
        var data = await entries.ForAsync(vtype, vid, alsoPairs, ct);
        return Json(new { ok = true, saved = true, ledger_url = "/finance/ledger", posted = data.Posted, history = data.History });
    }
}

public sealed record RegisterRow(int Id, DateTime Date, string Number, string Description, decimal Debit, decimal Credit);

public sealed class VoucherListModel
{
    public IReadOnlyList<AccountingVoucher> Vouchers { get; init; } = [];
    public string VType { get; init; } = "";
    public string Status { get; init; } = "";
    public required PeriodFilterModel Filter { get; init; }
}

public sealed class RegisterModel
{
    public string VType { get; init; } = "";
    public string Status { get; init; } = "";
    public required RegisterFilter Filter { get; init; }
    public bool Loaded { get; init; }
    public List<RegisterRow> Rows { get; set; } = [];
    public decimal TotalDebit { get; set; }
    public decimal TotalCredit { get; set; }
    public DateTime Now { get; init; }
}

public sealed class DocumentsModel
{
    public List<VoucherDoc>? Docs { get; init; }
    public string Label { get; init; } = "";
}

/// <summary>One voucher as a printed document (_voucher_doc.html).</summary>
public sealed class VoucherDoc
{
    public required AccountingVoucher Voucher { get; init; }
    public required IReadOnlyList<AccountingVoucherLine> Lines { get; init; }
    public decimal TotalDebit => Lines.Sum(l => l.Debit ?? 0);
    public decimal TotalCredit => Lines.Sum(l => l.Credit ?? 0);

    public static VoucherDoc For(AccountingVoucher v) =>
        new() { Voucher = v, Lines = v.AccountingVoucherLines.OrderBy(l => l.LineNo).ToList() };
}
