using System.Globalization;
using Accountix.Accounting.Services;
using Microsoft.Extensions.Logging;

namespace Accountix.Accounting.Controllers;

/// <summary>Accounting vouchers (finance_app/routes/accounting.py): CPV / CRV / BPV / BRV / JV.</summary>
[Area("Accounting")]
[Module(Modules.Accounting)]
[Route("accounting")]
public sealed partial class VouchersController(
    AccountixDbContext db, ILedger ledger, IVoucherNumbers numbers, CompanyClock clock,
    IPaymentTracking tracking, ILogger<VouchersController> log) : Controller
{
    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        ["CPV"] = "Cash Payment Voucher",
        ["CRV"] = "Cash Receipt Voucher",
        ["BPV"] = "Bank Payment Voucher",
        ["BRV"] = "Bank Receipt Voucher",
        ["JV"] = "Journal Voucher",
    };

    public static readonly string[] CashBankTypes = ["CPV", "CRV", "BPV", "BRV"];

    private EffectiveAccess Access => HttpContext.Access()!;
    private static string Section(string? vtype) => Modules.VoucherSection.GetValueOrDefault(vtype ?? "", "journal_vouchers");

    /// <summary>Voucher types the person may view (rights_guard.viewable_voucher_types).</summary>
    private List<string> ViewableTypes() =>
        Modules.VoucherSection.Where(kv => Access.Can(kv.Value, Rights.View)).Select(kv => kv.Key).ToList();

    // ── Dashboard ───────────────────────────────────────────────────────

    [HttpGet("")]
    [HttpGet("dashboard")]
    [AnyRight("cash_payment_vouchers:view", "cash_receipt_vouchers:view", "bank_payment_vouchers:view",
              "bank_receipt_vouchers:view", "journal_vouchers:view", "chart_of_accounts:view", "bank_reconciliation:view")]
    public async Task<IActionResult> Dashboard(CancellationToken ct)
    {
        var types = ViewableTypes();
        var vouchers = db.Set<AccountingVoucher>().Where(v => types.Contains(v.VoucherType));
        var byType = await db.Set<AccountingVoucher>().GroupBy(v => v.VoucherType)
            .Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var model = new DashboardModel
        {
            Total = await vouchers.CountAsync(ct),
            Unapproved = await vouchers.CountAsync(v => v.Status == "unapproved", ct),
            Approved = await vouchers.CountAsync(v => v.Status == "approved", ct),
            ByType = Labels.Keys.Where(types.Contains).ToDictionary(t => t, t => byType.GetValueOrDefault(t)),
            Recent = await vouchers.OrderByDescending(v => v.Id).Take(10).AsNoTracking().ToListAsync(ct),
            Viewable = types,
            Creatable = Labels.Keys.Where(t => Access.Can(Section(t), Rights.Create)).ToList(),
        };
        return View(model);
    }

    // ── The voucher form ────────────────────────────────────────────────

    [HttpGet("vouchers")]
    [HttpGet("vouchers/{id:int}")]
    [AnyMember]   // rights checked below: they follow the voucher's type
    public async Task<IActionResult> Form(int? id, [FromQuery] string? type, [FromQuery] string? edit, CancellationToken ct)
    {
        AccountingVoucher? v = null;
        if (id is int vid)
        {
            v = await LoadAsync(vid, ct);
            if (v is null) return NotFound();
            if (this.Deny(Section(v.VoucherType), Rights.View) is { } refused) return refused;
        }
        else if (type is not null && Labels.ContainsKey(type))
        {
            if (this.Deny(Section(type), Rights.Create) is { } refused) return refused;
        }
        else if (!Labels.Keys.Any(t => Access.Can(Section(t), Rights.Create)))
            return Refusal.Right(HttpContext, Rights.Create);
        return View("Form", await FormModelAsync(v, type, null, ct));
    }

    [HttpPost("vouchers")]
    [HttpPost("vouchers/{id:int}")]
    [AnyMember]   // rights checked below: they follow the voucher's type and the action
    public async Task<IActionResult> Save(int? id, CancellationToken ct)
    {
        var form = Request.Form;
        string action = form["action"].FirstOrDefault() ?? "save";
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // A save or approval locks the voucher until commit, so two users
        // approving it at once post it once (the second sees it approved).
        AccountingVoucher? voucher = null;
        if (id is int vid)
        {
            voucher = await db.LockAsync<AccountingVoucher>(vid, ct);
            if (voucher is null) return NotFound();
        }
        string section = Section(voucher?.VoucherType ?? form["voucher_type"].FirstOrDefault() ?? "CPV");
        if (this.Deny(section, voucher is null ? Rights.Create : Rights.Edit) is { } refused) return refused;
        if (action == "approve" && this.Deny(section, Rights.Approve) is { } refusedApprove) return refusedApprove;

        if (voucher?.Status == "approved")
        {
            this.Flash("That voucher is approved and cannot be edited. Unapprove it first to make changes.", "error");
            return Redirect($"/accounting/vouchers/{voucher.Id}");
        }

        bool isNew = voucher is null;
        int userId = Access.User.Id;
        if (voucher is null)
        {
            string vtype = form["voucher_type"].FirstOrDefault() ?? "CPV";
            if (!Labels.ContainsKey(vtype))
                return BadRequest();
            voucher = new AccountingVoucher
            {
                VoucherType = vtype,
                VoucherNumber = await numbers.NextAsync(vtype, ct),
                CreatedBy = userId,
                CreatedAt = clock.UtcNow,
                Status = "unapproved",
                VoucherDate = clock.UtcNow,
            };
            db.Add(voucher);
        }
        else
        {
            await db.Set<AccountingVoucherLine>().Where(l => l.VoucherId == voucher.Id).ExecuteDeleteAsync(ct);
            foreach (var e in db.ChangeTracker.Entries<AccountingVoucherLine>().ToList())
                e.State = EntityState.Detached;
            voucher.AccountingVoucherLines.Clear();
        }

        voucher.VoucherDate = ParseVoucherDate(form["voucher_date"].FirstOrDefault()) ?? clock.UtcNow;
        voucher.CashBankAccountId = int.TryParse((form["cash_bank_account_id"].FirstOrDefault() ?? "").Trim(), out int cb) ? cb : null;
        voucher.Notes = form["notes"].FirstOrDefault() ?? "";
        await db.SaveChangesAsync(ct);

        var accounts = form["account_id[]"].ToArray();
        var descs = form["description[]"].ToArray();
        var debits = form["debit[]"].ToArray();
        var credits = form["credit[]"].ToArray();
        var labelIds = form["label_id[]"].ToArray();

        // A row names a real account: the test the saver applies, so the
        // totals that follow never count a line it skips.
        bool ValidAid(int i) => i < accounts.Length && int.TryParse((accounts[i] ?? "").Trim(), out _)
                                && !string.IsNullOrWhiteSpace(accounts[i]);

        var defaultLabel = await DefaultVoucherLabelAsync(ct);
        int? defaultLid = defaultLabel?.Id;
        string? headerRaw = form.ContainsKey("label_id") ? form["label_id"].FirstOrDefault() ?? "" : null;
        int? headerLid;
        if (!string.IsNullOrWhiteSpace(headerRaw))
            headerLid = int.TryParse(headerRaw, out int h) && await LabelExistsAsync(h, ct) ? h : defaultLid;
        else if (headerRaw is not null)
            headerLid = defaultLid;
        else
            headerLid = null;
        voucher.LabelId = headerLid;

        async Task<int?> ValidLabel(string? raw)
        {
            if (!string.IsNullOrWhiteSpace(raw))
                return int.TryParse(raw.Trim(), out int lid) && await LabelExistsAsync(lid, ct) ? lid : headerLid ?? defaultLid;
            return headerLid ?? defaultLid;
        }

        bool hasLines = false;
        bool isCb = CashBankTypes.Contains(voucher.VoucherType);
        for (int i = 0; i < accounts.Length; i++)
        {
            if (!ValidAid(i)) continue;
            decimal d = i < debits.Length && !string.IsNullOrWhiteSpace(debits[i]) ? PyDecimal(debits[i]) : 0m;
            decimal c = i < credits.Length && !string.IsNullOrWhiteSpace(credits[i]) ? PyDecimal(credits[i]) : 0m;
            if (d == 0 && c == 0) continue;
            // A negative amount is the other side written with a minus.
            if (d < 0 || c < 0)
            {
                this.Flash($"Line {i + 1}: amounts cannot be negative — put the amount in the other column instead.", "error");
                return Redirect(Request.Path + Request.QueryString);
            }
            if (voucher.VoucherType == "JV" && d > 0 && c > 0)
            {
                this.Flash($"Line {i + 1}: a line is either a debit or a credit, not both.", "error");
                return Redirect(Request.Path + Request.QueryString);
            }
            // Cash/bank vouchers: counter accounts sit OPPOSITE the cash/bank line.
            if (isCb)
            {
                decimal amt = d + c;
                (d, c) = voucher.VoucherType is "CRV" or "BRV" ? (0m, amt) : (amt, 0m);
            }
            voucher.AccountingVoucherLines.Add(new AccountingVoucherLine
            {
                LineNo = i + 1,
                AccountId = int.Parse(accounts[i]!.Trim()),
                Description = i < descs.Length ? descs[i] ?? "" : "",
                Debit = d,
                Credit = c,
                LabelId = i < labelIds.Length ? await ValidLabel(labelIds[i]) : null,
            });
            hasLines = true;
        }

        if (!hasLines)
            return await RenderError("Add at least one line with amount.");

        decimal SumSide(string?[] values) => values.Select((x, i) => (x, i))
            .Where(p => !string.IsNullOrWhiteSpace(p.x) && ValidAid(p.i)).Sum(p => PyDecimal(p.x!));

        if (isCb)
        {
            if (voucher.CashBankAccountId is null or 0)
                return await RenderError("Select a Cash/Bank account.");
            decimal total = SumSide(debits) + SumSide(credits);
            if (total == 0)
                return await RenderError("Total amount must be greater than 0.");
            bool payment = voucher.VoucherType is "CPV" or "BPV";
            voucher.AccountingVoucherLines.Add(new AccountingVoucherLine
            {
                LineNo = 0,
                AccountId = voucher.CashBankAccountId.Value,
                Description = $"{Labels[voucher.VoucherType]} - {(payment ? "Payment" : "Receipt")}",
                Debit = payment ? 0m : total,
                Credit = payment ? total : 0m,
                // The header label is the project of the money channel.
                LabelId = voucher.LabelId,
            });
        }
        if (voucher.VoucherType == "JV")
        {
            decimal totalD = SumSide(debits), totalC = SumSide(credits);
            if (totalD != totalC)
                return await RenderError($"Debit total ({Py(totalD)}) does not match Credit total ({Py(totalC)}).");
        }
        await db.SaveChangesAsync(ct);

        if (action == "approve")
        {
            // Posting can refuse (closed period, a line on an aggregating
            // account): save as an unapproved draft instead and say why.
            string? errors;
            await tx.CreateSavepointAsync("approve", ct);
            try
            {
                errors = await ApproveAsync(voucher, userId, ct);
            }
            catch (Exception e) when (e is BusinessRuleException or DbUpdateException or InvalidOperationException)
            {
                await tx.RollbackToSavepointAsync("approve", ct);
                DetachPostings();
                errors = string.IsNullOrEmpty(e.Message) ? "Could not approve the voucher." : e.Message;
            }
            if (errors is not null)
            {
                voucher.Status = "unapproved";
                voucher.ApprovedBy = null;
                voucher.ApprovedAt = null;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                this.Flash($"{Labels[voucher.VoucherType]} {voucher.VoucherNumber} saved as unapproved — could not approve: {errors}", "error");
                return Redirect($"/accounting/vouchers/{voucher.Id}");
            }
        }
        // Only a literal "approve" approves; anything else saves unapproved.
        voucher.Status = action == "approve" ? "approved" : "unapproved";
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await ReconcileTrackingAsync(voucher.Id, ct);
        this.Flash($"{Labels[voucher.VoucherType]} {voucher.VoucherNumber} {(action == "approve" ? "approved" : "saved")}.", "success");
        return Redirect($"/accounting/vouchers/{voucher.Id}");

        async Task<IActionResult> RenderError(string message)
        {
            // Rendered without committing: the transaction rolls back, so a
            // new voucher's number goes back to the series.
            this.Flash(message, "error");
            ViewData["FlashNow"] = true;
            return View("Form", await FormModelAsync(voucher, null, isNew ? voucher : null, ct));
        }
    }

    /// <summary>_approve_voucher: post the lines, stamp the approver. Returns an error text or null.</summary>
    private async Task<string?> ApproveAsync(AccountingVoucher v, int userId, CancellationToken ct)
    {
        var lines = v.AccountingVoucherLines.OrderBy(l => l.LineNo).ToList();
        if (lines.Count == 0)
            lines = await db.Set<AccountingVoucherLine>().Where(l => l.VoucherId == v.Id).OrderBy(l => l.LineNo).ToListAsync(ct);
        if (lines.Count == 0)
            return "No lines to post.";
        await ledger.PostAsync(v.VoucherType, v.Id, v.VoucherNumber, $"{Labels[v.VoucherType]} {v.VoucherNumber}",
            lines.Select(l => new JournalLineInput(l.AccountId, l.Debit ?? 0, l.Credit ?? 0, l.Description, l.LabelId)).ToList(),
            v.VoucherDate, userId, ct);
        v.ApprovedBy = userId;
        v.ApprovedAt = clock.UtcNow;
        return null;
    }

    [HttpPost("vouchers/{id:int}/approve")]
    [AnyMember]   // the section follows the voucher's type
    public async Task<IActionResult> Approve(int id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var v = await db.LockAsync<AccountingVoucher>(id, ct);
        if (v is null) return NotFound();
        if (this.Deny(Section(v.VoucherType), Rights.Approve) is { } refused) return refused;
        if (v.Status == "approved")
        {
            this.Flash("Already approved.", "error");
            return Redirect($"/accounting/vouchers/{v.Id}");
        }
        string? err;
        try
        {
            err = await ApproveAsync(v, Access.User.Id, ct);
        }
        catch (Exception e) when (e is BusinessRuleException or DbUpdateException or InvalidOperationException)
        {
            await tx.RollbackAsync(ct);
            this.Flash($"Could not approve: {e.Message}", "error");
            return Redirect($"/accounting/vouchers/{v.Id}");
        }
        if (err is not null)
        {
            this.Flash(err, "error");
            return Redirect($"/accounting/vouchers/{v.Id}");
        }
        v.Status = "approved";
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await ReconcileTrackingAsync(v.Id, ct);
        this.Flash($"{Labels[v.VoucherType]} {v.VoucherNumber} approved.", "success");
        return Redirect($"/accounting/vouchers/{v.Id}");
    }

    [HttpPost("vouchers/{id:int}/unapprove")]
    [AnyMember]   // the section follows the voucher's type
    public async Task<IActionResult> Unapprove(int id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var v = await db.LockAsync<AccountingVoucher>(id, ct);
        if (v is null) return NotFound();
        if (this.Deny(Section(v.VoucherType), Rights.Approve) is { } refused) return refused;
        if (v.Status != "approved")
        {
            this.Flash("Voucher is not approved.", "error");
            return Redirect("/accounting/vouchers/list");
        }
        try
        {
            await ledger.ReverseAsync(v.VoucherType, v.Id, Access.User.Id, ct);
        }
        catch (Exception e) when (e is BusinessRuleException or DbUpdateException or InvalidOperationException)
        {
            await tx.RollbackAsync(ct);
            this.Flash($"Could not unapprove: {e.Message}", "error");
            return Redirect($"/accounting/vouchers/{v.Id}");
        }
        v.Status = "unapproved";
        v.ApprovedBy = null;
        v.ApprovedAt = null;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        // Unapproved money settles nothing: assigned invoices drop back to unpaid.
        await ReconcileTrackingAsync(v.Id, ct);
        this.Flash($"{Labels[v.VoucherType]} {v.VoucherNumber} unapproved.", "success");
        return Redirect($"/accounting/vouchers/{v.Id}");
    }

    [HttpPost("vouchers/{id:int}/delete")]
    [AnyMember]   // the section follows the voucher's type
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var v = await db.LockAsync<AccountingVoucher>(id, ct);
        if (v is null) return NotFound();
        if (this.Deny(Section(v.VoucherType), Rights.Delete) is { } refused) return refused;
        if (v.Status == "approved")
        {
            this.Flash("Cannot delete an approved voucher. Unapprove it first.", "error");
            return Redirect("/accounting/vouchers/list");
        }
        // Assignments against a voucher that no longer exists are meaningless.
        try
        {
            await tracking.DropForVoucherAsync(v.Id, ct);
        }
        catch (Exception e) when (e is NotYetPortedException or InvalidOperationException or DbUpdateException)
        {
            log.LogWarning(e, "Invoice tracking not refreshed for voucher {Id}", v.Id);
        }
        await db.Set<AccountingVoucherLine>().Where(l => l.VoucherId == v.Id).ExecuteDeleteAsync(ct);
        db.Remove(v);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"{Labels[v.VoucherType]} {v.VoucherNumber} deleted.", "success");
        return Redirect("/accounting/vouchers/list");
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private Task<AccountingVoucher?> LoadAsync(int id, CancellationToken ct) =>
        db.Set<AccountingVoucher>()
            .Include(v => v.AccountingVoucherLines).ThenInclude(l => l.Account)
            .Include(v => v.CashBankAccount)
            .Include(v => v.Label)
            .Include(v => v.ApprovedByNavigation)
            .AsSplitQuery()
            .FirstOrDefaultAsync(v => v.Id == id, ct);

    private async Task<VoucherFormModel> FormModelAsync(AccountingVoucher? v, string? initialType, AccountingVoucher? renderedNew,
                                                        CancellationToken ct)
    {
        var settings = await db.Set<InventorySetting>().AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        var labels = await db.Set<ProjectLabel>().AsNoTracking().OrderBy(l => l.Name).ToListAsync(ct);
        if (v is not null)
        {
            // Lines added in this request still need their accounts for the page.
            var ids = v.AccountingVoucherLines.Select(l => l.AccountId).Distinct().ToList();
            var accts = await db.Set<ChartOfAccount>().AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
            foreach (var l in v.AccountingVoucherLines)
                l.Account ??= accts.GetValueOrDefault(l.AccountId)!;
            if (v.CashBankAccount is null && v.CashBankAccountId is int cbid)
                v.CashBankAccount = await db.Set<ChartOfAccount>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == cbid, ct);
            if (v.Label is null && v.LabelId is int lid)
                v.Label = labels.FirstOrDefault(l => l.Id == lid);
        }
        return new VoucherFormModel
        {
            Voucher = v,
            InitialType = initialType is not null && Labels.ContainsKey(initialType) ? initialType : "",
            ProjectLabels = labels,
            AnyActiveLabel = labels.Any(l => l.IsActive == true),
            DefaultLabel = await DefaultVoucherLabelAsync(ct),
            PerLineLabeling = settings?.PerLineLabelingVoucher == true,
            Now = clock.Now,
        };
    }

    private async Task<ProjectLabel?> DefaultVoucherLabelAsync(CancellationToken ct)
    {
        var settings = await db.Set<InventorySetting>().AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        if (settings?.DefaultVoucherLabelId is not int lid)
            return null;
        return await db.Set<ProjectLabel>().AsNoTracking().FirstOrDefaultAsync(l => l.Id == lid && l.IsActive == true, ct);
    }

    private Task<bool> LabelExistsAsync(int id, CancellationToken ct) =>
        db.Set<ProjectLabel>().AnyAsync(l => l.Id == id, ct);

    private async Task ReconcileTrackingAsync(int voucherId, CancellationToken ct)
    {
        // Non-fatal, as in Python: a voucher save must not fail over a cache.
        try
        {
            await tracking.ReconcileVoucherAsync(voucherId, ct);
        }
        catch (Exception e) when (e is NotYetPortedException or InvalidOperationException or DbUpdateException)
        {
            log.LogWarning(e, "Invoice tracking not refreshed for voucher {Id}", voucherId);
        }
    }

    private void DetachPostings()
    {
        foreach (var e in db.ChangeTracker.Entries().Where(e => e.Entity is JournalEntry or JournalLine && e.State == EntityState.Added).ToList())
            e.State = EntityState.Detached;
    }

    /// <summary>The voucher form's date formats, as Python tries them; null when none fits.</summary>
    public static DateTime? ParseVoucherDate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        foreach (string fmt in new[] { "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd", "dd/MM/yyyy", "MM/dd/yyyy", "d/M/yyyy", "M/d/yyyy" })
            if (DateTime.TryParseExact(s, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return d;
        return null;
    }

    /// <summary>Decimal(str(float(x))): what Python stores for a typed amount (0 when it is not a number).</summary>
    public static decimal PyDecimal(string? raw)
    {
        if (!double.TryParse((raw ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double f)
            || double.IsNaN(f) || double.IsInfinity(f))
            return 0m;
        return decimal.Parse(PyFloat(f), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>Python's str(float): shortest round-trip, always with a decimal point.</summary>
    public static string PyFloat(double f)
    {
        string r = f.ToString("R", CultureInfo.InvariantCulture);
        return r.Contains('.') || r.Contains('E') ? r : r + ".0";
    }

    private static string Py(decimal d) => d.ToString(CultureInfo.InvariantCulture);
}

public sealed class DashboardModel
{
    public int Total { get; init; }
    public int Unapproved { get; init; }
    public int Approved { get; init; }
    public IReadOnlyDictionary<string, int> ByType { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<AccountingVoucher> Recent { get; init; } = [];
    public IReadOnlyList<string> Viewable { get; init; } = [];
    public IReadOnlyList<string> Creatable { get; init; } = [];
}

public sealed class VoucherFormModel
{
    public AccountingVoucher? Voucher { get; init; }
    public string InitialType { get; init; } = "";
    public IReadOnlyList<ProjectLabel> ProjectLabels { get; init; } = [];
    public bool AnyActiveLabel { get; init; }
    public ProjectLabel? DefaultLabel { get; init; }
    public bool PerLineLabeling { get; init; }
    public DateTime Now { get; init; }

    public bool IsNew => Voucher is null;
    public string VType => Voucher?.VoucherType ?? (InitialType.Length > 0 ? InitialType : "CPV");
    public bool IsApproved => Voucher?.Status == "approved";
    public bool Editable => !IsApproved;
    public bool FormActive => Editable;
    public bool IsCb => VouchersController.CashBankTypes.Contains(VType);
    public bool IsReceipt => VType is "CRV" or "BRV";
    public string Label => VouchersController.Labels[VType];
    public string CbLabel => VType switch { "CPV" or "CRV" => "Cash Account", "BPV" or "BRV" => "Bank Account", _ => "Cash/Bank Account" };
}
