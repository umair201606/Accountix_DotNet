using Accountix.Fbr.Services;

namespace Accountix.Fbr.Controllers;

/// <summary>
/// FBR Digital Invoicing (fbr_app/routes/dashboard.py, blueprint
/// fbr_dashboard, /fbr): the submissions list, Send / Validate from the
/// sales invoice page, Retry, a record's JSON and an invoice's status.
/// </summary>
[Area("Fbr")]
[Module(Modules.Fbr)]
[Route("fbr")]
public sealed class FbrDashboardController(AccountixDbContext db, FbrConfigs configs, FbrMapper mapper,
                                           FbrClient client, CompanyClock clock) : Controller
{
    public const string Section = "fbr_dashboard";
    public static readonly string[] Statuses = ["pending", "success", "failed", "processing"];

    public sealed record Row(int Id, int InvInvoiceId, string? InvoiceNumber, string? FbrInvoiceNumber, string? InvoiceType,
                             string? Status, string? ErrorMessage, int? AttemptCount, DateTime? SubmittedAt, string? SubmittedBy);

    public sealed record DashboardModel(FbrConfig Config, IReadOnlyList<Row> Records, string? Status);

    private int UserId => HttpContext.Access()!.User.Id;

    /// <summary>Flask's jsonify: sorted keys, compact, a closing newline.</summary>
    internal static ContentResult Answer(PyDict body, int status = 200) =>
        new() { Content = Py.Jsonify(body), ContentType = "application/json", StatusCode = status };

    private static ContentResult Refused(string error, int status = 400) => Answer(new PyDict { ["ok"] = false, ["error"] = error }, status);

    // ── GET /fbr/ ──────────────────────────────────────────────────────────

    [HttpGet("")]
    [Right(Section, Rights.View)]
    public async Task<IActionResult> Dashboard([FromQuery] string? status, CancellationToken ct)
    {
        var config = await configs.GetAsync(ct);
        if (!FbrConfigs.Configured(config))
        {
            this.Flash("FBR module is not configured. Ask an administrator to configure FBR settings first.", "warning");
            return View("Dashboard", new DashboardModel(config, [], status));
        }

        // One query for the whole list: the invoice number and the submitter
        // come in the same row (the template's r.invoice / r.submitter were
        // a query per row).
        var records = db.Set<FbrInvoiceRecord>().AsNoTracking();
        if (!string.IsNullOrEmpty(status))
            records = records.Where(r => r.Status == status);
        var rows = await (
            from r in records
            join i in db.Set<InvInvoice>() on r.InvInvoiceId equals i.Id into invoices
            from i in invoices.DefaultIfEmpty()
            join u in db.Users on r.SubmittedBy equals u.Id into users
            from u in users.DefaultIfEmpty()
            orderby r.Id descending
            select new Row(r.Id, r.InvInvoiceId, i == null ? null : i.InvoiceNumber, r.FbrInvoiceNumber, r.InvoiceType,
                           r.Status, r.ErrorMessage, r.AttemptCount, r.SubmittedAt, u == null ? null : u.FullName)
        ).ToListAsync(ct);
        return View("Dashboard", new DashboardModel(config, rows, status));
    }

    // ── POST /fbr/submit/<invoice_id> ──────────────────────────────────────

    /// <summary>
    /// Send to FBR. Reporting cannot be taken back, so the invoice stays
    /// locked (FOR NO KEY UPDATE, as Python's scoped_get_404(for_update=True))
    /// through the call to FBR: a second Send — from either app — waits,
    /// then finds it reported.
    /// </summary>
    [HttpPost("submit/{invoiceId:int}")]
    [Right(Section, Rights.Approve)]
    public async Task<IActionResult> Submit(int invoiceId)
    {
        // From here nothing follows the browser: an answer FBR gave must be recorded.
        var ct = CancellationToken.None;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var config = await configs.GetAsync(ct);
        if (!FbrConfigs.Configured(config))
            return Refused("FBR not configured");

        var invoice = await db.LockAsync<InvInvoice>(invoiceId, ct);
        if (invoice is null)
            return NotFound();
        if (invoice.VoucherStatus != "approved")
            return Refused("Only approved invoices can be submitted to FBR");
        if (await ReportedAsync(invoiceId, ct))
            return Refused("Invoice already submitted successfully to FBR");

        PyDict payload;
        try
        {
            payload = (await mapper.BuildAsync(invoiceId, config, ct))!;
        }
        catch (FbrMapper.BuildException e)
        {
            return Refused($"Failed to build payload: {e.Message}", 500);
        }

        var answer = await client.SubmitAsync(config, payload);
        var record = FbrSubmissions.Record(db, invoiceId, payload, answer, UserId, FbrMapper.InvoiceType(payload), clock.UtcNow);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        if (answer.Ok)
            return Answer(new PyDict
            {
                ["ok"] = true,
                ["message"] = "Invoice submitted to FBR successfully.",
                ["record_id"] = (long)record.Id,
                ["fbr_invoice_number"] = record.FbrInvoiceNumber,
            });
        return Answer(new PyDict
        {
            ["ok"] = false,
            ["error"] = string.IsNullOrEmpty(record.ErrorMessage) ? "FBR submission failed" : record.ErrorMessage,
            ["record_id"] = (long)record.Id,
        }, 400);
    }

    // ── POST /fbr/validate/<invoice_id> ────────────────────────────────────

    [HttpPost("validate/{invoiceId:int}")]
    [Right(Section, Rights.Edit)]
    public async Task<IActionResult> Validate(int invoiceId)
    {
        var ct = CancellationToken.None;
        var config = await configs.GetAsync(ct);
        if (!FbrConfigs.Configured(config))
            return Refused("FBR not configured");
        PyDict? payload;
        try
        {
            payload = await mapper.BuildAsync(invoiceId, config, ct);
        }
        catch (FbrMapper.BuildException e)
        {
            return Refused($"Failed to build payload: {e.Message}", 500);
        }
        if (payload is null)
            return NotFound();

        var answer = await client.ValidateAsync(config, payload);
        if (answer.Ok)
            return Answer(new PyDict { ["ok"] = true, ["message"] = "Invoice validated successfully.", ["data"] = answer.Data });
        object? error = answer.Data is PyDict d
            ? d.TryGetValue("error", out var err) ? err : d.TryGetValue("message", out var msg) ? msg : "Validation failed"
            : Py.Str(answer.Data);
        return Answer(new PyDict { ["ok"] = false, ["error"] = error, ["data"] = answer.Data }, 400);
    }

    // ── POST /fbr/retry/<record_id> ────────────────────────────────────────

    /// <summary>
    /// Retry a failed submission with the invoice as it is NOW, under the
    /// same lock as Send: two retries, or a retry and a Send, report once.
    /// </summary>
    [HttpPost("retry/{recordId:int}")]
    [Right(Section, Rights.Approve)]
    public async Task<IActionResult> Retry(int recordId)
    {
        var ct = CancellationToken.None;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var config = await configs.GetAsync(ct);
        if (!FbrConfigs.Configured(config))
            return Refused("FBR not configured");

        var found = await db.Set<FbrInvoiceRecord>().AsNoTracking().Where(r => r.Id == recordId)
            .Select(r => (int?)r.InvInvoiceId).FirstOrDefaultAsync(ct);
        if (found is not int invoiceId)
            return NotFound();
        var invoice = await db.LockAsync<InvInvoice>(invoiceId, ct);
        if (invoice is null)
            return NotFound();
        // Read after the lock (Python: db.session.refresh(record)).
        var record = await db.Set<FbrInvoiceRecord>().FirstOrDefaultAsync(r => r.Id == recordId, ct);
        if (record is null)
            return NotFound();
        if (record.Status == "success" || await ReportedAsync(invoice.Id, ct))
            return Refused("Invoice already submitted successfully to FBR");
        if (invoice.VoucherStatus != "approved")
            return Refused("Only approved invoices can be submitted to FBR");

        PyDict payload;
        try
        {
            payload = (await mapper.BuildAsync(invoice.Id, config, ct))!;
        }
        catch (FbrMapper.BuildException e)
        {
            return Refused($"Failed to build payload: {e.Message}", 500);
        }

        var answer = await client.SubmitAsync(config, payload);
        FbrSubmissions.Retry(record, payload, answer, UserId, clock.UtcNow);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        if (answer.Ok)
            return Answer(new PyDict
            {
                ["ok"] = true,
                ["message"] = "Retry successful.",
                ["record_id"] = (long)record.Id,
                ["fbr_invoice_number"] = record.FbrInvoiceNumber,
            });
        return Answer(new PyDict
        {
            ["ok"] = false,
            ["error"] = string.IsNullOrEmpty(record.ErrorMessage) ? "Retry failed" : record.ErrorMessage,
            ["record_id"] = (long)record.Id,
        }, 400);
    }

    // ── GET /fbr/record/<record_id> ────────────────────────────────────────

    [HttpGet("record/{recordId:int}")]
    [Right(Section, Rights.View)]
    public async Task<IActionResult> Record(int recordId, CancellationToken ct)
    {
        var r = await db.Set<FbrInvoiceRecord>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == recordId, ct);
        if (r is null)
            return NotFound();
        string? submitter = r.SubmittedBy is int uid
            ? await db.Users.AsNoTracking().Where(u => u.Id == uid).Select(u => u.FullName).FirstOrDefaultAsync(ct)
            : null;
        return Answer(new PyDict
        {
            ["id"] = (long)r.Id,
            ["inv_invoice_id"] = (long)r.InvInvoiceId,
            ["fbr_invoice_number"] = r.FbrInvoiceNumber,
            ["fbr_submission_id"] = r.FbrSubmissionId,
            ["status"] = r.Status,
            ["error_message"] = r.ErrorMessage,
            ["attempt_count"] = r.AttemptCount is int n ? (long)n : null,
            ["invoice_type"] = r.InvoiceType,
            ["submitted_at"] = r.SubmittedAt?.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) ?? "",
            ["submitted_by"] = submitter ?? "",
            ["request_json"] = r.RequestJson,
            ["response_json"] = r.ResponseJson,
        });
    }

    // ── GET /fbr/status/<invoice_id> ───────────────────────────────────────

    [HttpGet("status/{invoiceId:int}")]
    [Right(Section, Rights.View)]
    public async Task<IActionResult> Status(int invoiceId, CancellationToken ct)
    {
        var record = await db.Set<FbrInvoiceRecord>().AsNoTracking().Where(r => r.InvInvoiceId == invoiceId)
            .OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct);
        if (record is null)
            return Refused("No FBR record found", 404);
        if (record.Status == "success" && !string.IsNullOrEmpty(record.FbrInvoiceNumber))
        {
            var answer = await client.StatusAsync(await configs.GetAsync(ct), record.FbrInvoiceNumber);
            if (answer.Ok)
                return Answer(new PyDict { ["ok"] = true, ["data"] = answer.Data });
        }
        return Answer(new PyDict
        {
            ["ok"] = true,
            ["status"] = record.Status,
            ["message"] = string.IsNullOrEmpty(record.ErrorMessage) ? "Pending" : record.ErrorMessage,
        });
    }

    private Task<bool> ReportedAsync(int invoiceId, CancellationToken ct) =>
        db.Set<FbrInvoiceRecord>().AnyAsync(r => r.InvInvoiceId == invoiceId && r.Status == "success", ct);
}
