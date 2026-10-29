using Accountix.Parties.Services;

namespace Accountix.Parties.Controllers;

public sealed class RegisterPageModel
{
    public string Doc { get; init; } = "sales";
    public string Title { get; init; } = "";
    public IReadOnlyList<string> Columns { get; init; } = [];
    public IReadOnlyList<RegisterInvoiceRow> Rows { get; init; } = [];
    public decimal[] Totals { get; init; } = new decimal[InvoiceRegister.NVals];
    public string Status { get; init; } = "";
    public required RegisterFilter Filter { get; init; }
    public bool Loaded { get; init; }
    public DateTime Now { get; init; }
    public string ViewPath => Doc == "purchase" ? "/inventory/purchase-invoice/" : "/inventory/invoices/";
}

public sealed class DocumentsPageModel
{
    public IReadOnlyList<string> Docs { get; init; } = [];
    public int Total { get; init; }
    public int LoadedCount => Docs.Count;
    public bool FullRender { get; init; }
    public string Label { get; init; } = "";
    public string Title { get; init; } = "";
}

public sealed record RegisterBodyModel(IReadOnlyList<RegisterInvoiceRow> Rows, int ColumnCount, string ViewPath);

/// <summary>The invoice register (invoicing_app/routes/registers.py, blueprint inv_registers).</summary>
[Area("Parties")]
[Module(Modules.Invoicing)]
[Route("invoicing/registers")]
public sealed class RegistersController(InvoiceRegister register, AccountixDbContext db, CompanyClock clock) : Controller
{
    private (string Doc, string Status) Args()
    {
        string doc = (Request.Query["doc"].FirstOrDefault() ?? "sales").Trim().ToLowerInvariant();
        string status = (Request.Query["status"].FirstOrDefault() ?? "").Trim().ToLowerInvariant();
        if (status is not ("approved" or "unapproved")) status = "";
        return (doc == "purchase" ? "purchase" : "sales", status);
    }

    /// <summary>rights_guard._register_doc: doc=purchase needs purchase invoices, anything else sales invoices.</summary>
    private IActionResult? DocRefusal(bool json = false)
    {
        string doc = (Request.Query["doc"].FirstOrDefault() ?? "").Trim().ToLowerInvariant();
        // The page with no register chosen yet (the sidebar's link) opens for either.
        if (doc.Length == 0 && Request.Query["view"] != "1" && HttpMethods.IsGet(Request.Method))
            return null;
        return this.Deny(doc == "purchase" ? "purchase_invoices" : "sales_invoices", Rights.View, json);
    }

    [HttpGet("")]
    [AnyRight("sales_invoices:view", "purchase_invoices:view")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        if (DocRefusal() is { } refused) return refused;
        var (doc, status) = Args();
        var f = await RegisterFilter.ResolveAsync(Request.Query, db, clock.Today, ct: ct);
        bool loaded = Request.Query["view"] == "1";
        RegisterData? data = null;
        if (loaded)
        {
            await using var snap = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
            data = await register.DataAsync(doc, status, f.From, f.To, ct: ct);
            await snap.CommitAsync(ct);
        }
        return View(new RegisterPageModel
        {
            Doc = doc, Title = InvoiceRegister.TitleOf(doc), Columns = InvoiceRegister.ColumnsOf(doc),
            Rows = data?.Rows ?? [], Totals = data?.Totals ?? new decimal[InvoiceRegister.NVals],
            Status = status, Filter = f, Loaded = loaded, Now = clock.Now,
        });
    }

    [HttpGet("data")]
    [AnyRight("sales_invoices:view", "purchase_invoices:view")]
    public async Task<IActionResult> Data([FromServices] IServiceProvider services, CancellationToken ct)
    {
        if (DocRefusal(json: true) is { } refused) return refused;
        var (doc, status) = Args();
        bool full = Request.Query["full"] == "1";
        int limit = int.TryParse(Request.Query["limit"], out int lm) ? Math.Clamp(lm, 1, full ? 100 : 500) : full ? 100 : 200;
        int offset = int.TryParse(Request.Query["offset"], out int of) ? Math.Max(0, of) : 0;
        int? known = int.TryParse(Request.Query["total"], out int kt) ? Math.Max(0, kt) : null;
        if (full)
        {
            var ff = await RegisterFilter.ResolveAsync(Request.Query, db, clock.Today, ct: ct);
            await using var fsnap = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
            // Asked for here, not in the constructor: the print code is Invoicing's (InvoicePrint).
            var bulk = services.GetRequiredService<BulkInvoices>();
            var (ftotal, invoices) = await bulk.InvoicesAsync(doc, status, ff.From, ff.To, limit, offset, known, ct);
            var docs = await bulk.RenderAsync(doc, invoices, includeSharedAssets: offset == 0, ct);
            await fsnap.CommitAsync(ct);
            return Json(new { total = ftotal, rows = invoices.Count, offset, limit, docs });
        }
        var f = await RegisterFilter.ResolveAsync(Request.Query, db, clock.Today, ct: ct);
        await using var snap = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var data = await register.DataAsync(doc, status, f.From, f.To, limit, offset, known, ct);
        await snap.CommitAsync(ct);
        string viewPath = doc == "purchase" ? "/inventory/purchase-invoice/" : "/inventory/invoices/";
        string html = await this.RenderViewAsync("_RegisterBody", new RegisterBodyModel(data.Rows, data.Columns.Count, viewPath));
        string tfoot = await this.RenderViewAsync("_RegisterTotals", data.Totals);
        var t = data.Totals;
        return Json(new
        {
            total = data.TotalCount, rows = data.Rows.Count, totals = t.Select(v => (double)v).ToArray(),
            labels = new { grand = this.Amount(t[5], 0), paid = this.Amount(t[6], 0), returned = this.Amount(t[7], 0), balance = this.Amount(t[8], 0) },
            offset, limit, html, tfoot,
        });
    }

    [HttpGet("export")]
    [AnyRight("sales_invoices:view", "purchase_invoices:view")]
    public async Task<IActionResult> Export([FromServices] IReportExport export, CancellationToken ct)
    {
        if (DocRefusal() is { } refused) return refused;
        string fmt = (Request.Query["fmt"].FirstOrDefault() ?? "pdf").Trim().ToLowerInvariant();
        if (fmt is not ("excel" or "pdf" or "csv")) return NotFound();
        var (doc, status) = Args();
        var f = await RegisterFilter.ResolveAsync(Request.Query, db, clock.Today, ct: ct);
        RegisterData data;
        await using (var snap = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct))
        {
            data = await register.DataAsync(doc, status, f.From, f.To, ct: ct);
            await snap.CommitAsync(ct);
        }
        string subtitle = f.Label + (status.Length > 0 ? $" · {ViewHelpers.Title(status)}" : "");
        var rows = new List<ReportRow>();
        foreach (var r in data.Rows)
            rows.Add(new ReportRow([r.Date, r.InvoiceNumber, r.VoucherNumber, r.Party, .. r.Vals.Select(v => (object?)Money.Round2(v)), r.Status], "account"));
        rows.Add(new ReportRow(["", "", "", "TOTAL", .. data.Totals.Select(v => (object?)Money.Round2(v)), ""], "grand"));
        var columns = new List<ReportColumn> { new(data.Columns[0], ReportColumnKind.Date), new(data.Columns[1]), new(data.Columns[2]), new(data.Columns[3]) };
        columns.AddRange(data.Columns.Skip(4).Take(InvoiceRegister.NVals).Select(c => new ReportColumn(c, ReportColumnKind.Money)));
        columns.Add(new ReportColumn(data.Columns[^1]));
        var file = await export.ExportTableAsync(fmt, new ReportTable
        {
            Title = data.Title,
            Period = subtitle,
            Filters = [("", $"{data.Rows.Count} documents")],
            Columns = columns,
            Rows = rows,
            MonoColumn = 1,
            FilePeriod = f.Label,
        }, ct);
        return File(file.Content, file.ContentType, file.FileName);
    }

    [HttpGet("documents")]
    [AnyRight("sales_invoices:view", "purchase_invoices:view")]
    public async Task<IActionResult> Documents([FromServices] BulkInvoices bulk, CancellationToken ct)
    {
        if (DocRefusal() is { } refused) return refused;
        var (doc, status) = Args();
        var f = await RegisterFilter.ResolveAsync(Request.Query, db, clock.Today, ct: ct);
        bool fullRender = Request.Query["view"] == "1";
        int total;
        List<string> docs;
        await using (var snap = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct))
        {
            // view=1 is the no-JS fallback (every document server-side); otherwise the first twenty paint with
            // the page and the browser fetches the rest in batches.
            var (t, invoices) = fullRender
                ? await bulk.InvoicesAsync(doc, status, f.From, f.To, ct: ct)
                : await bulk.InvoicesAsync(doc, status, f.From, f.To, limit: 20, offset: 0, ct: ct);
            total = t;
            docs = await bulk.RenderAsync(doc, invoices, includeSharedAssets: true, ct);
            await snap.CommitAsync(ct);
        }
        string label = f.Label + (status.Length > 0 ? $" · {ViewHelpers.Title(status)}" : "");
        return View(new DocumentsPageModel
        {
            Docs = docs, Total = total, FullRender = fullRender, Label = label, Title = InvoiceRegister.TitleOf(doc),
        });
    }

    [HttpGet("documents/export")]
    [AnyRight("sales_invoices:view", "purchase_invoices:view")]
    public async Task<IActionResult> DocumentsExport([FromServices] IReportExport export, [FromServices] BulkInvoices bulk, CancellationToken ct)
    {
        if (DocRefusal() is { } refused) return refused;
        string fmt = (Request.Query["fmt"].FirstOrDefault() ?? "excel").Trim().ToLowerInvariant();
        if (fmt is not ("excel" or "xlsx")) return NotFound();
        var (doc, status) = Args();
        var f = await RegisterFilter.ResolveAsync(Request.Query, db, clock.Today, ct: ct);
        string subtitle = f.Label + (status.Length > 0 ? $" · {ViewHelpers.Title(status)}" : "");
        ExportFile file;
        await using (var snap = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct))
        {
            var (_, invoices) = await bulk.InvoicesAsync(doc, status, f.From, f.To, ct: ct);
            file = await bulk.WorkbookAsync(doc, invoices, subtitle, export, ct);
            await snap.CommitAsync(ct);
        }
        return File(file.Content, file.ContentType, file.FileName);
    }
}
