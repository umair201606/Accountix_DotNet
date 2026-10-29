using System.Globalization;
using Accountix.Inventory.Services;

namespace Accountix.Inventory.Controllers;

/// <summary>Header fields every stock voucher form shows (vouchers/_voucher_meta.html).</summary>
public sealed record VoucherMeta(DateTime? Date, int? LabelId, string? Status, IReadOnlyList<ProjectLabel> Labels, string TodayIso);

/// <summary>One line of a voucher form.</summary>
public sealed record VoucherItemRow(int ProductId, string? ProductName, decimal Quantity, decimal? SystemQty, decimal? PhysicalQty,
                                    decimal? Difference, decimal? UnitCost, decimal? TotalCost);

/// <summary>The consumption / scrap / adjustment / stock-take form.</summary>
public sealed class VoucherFormModel
{
    public required string Kind { get; init; }
    public int? Id { get; init; }
    public string? Number { get; init; }
    public string? Status { get; init; }
    public DateTime? ApprovedAt { get; init; }
    public string? Department { get; init; }
    public string? Reason { get; init; }
    public string? Location { get; init; }
    public int? ChargeAccountId { get; init; }
    public IReadOnlyList<VoucherItemRow> Items { get; init; } = [];
    public required IReadOnlyList<ChartOfAccount> Accounts { get; init; }
    public required VoucherMeta Meta { get; init; }
    public bool Exists => Id is not null;
    public bool Approved => Status == "approved";
}

public sealed record VoucherListRow(int Id, string Number, DateTime? Date, string? Text, int Items, string? Status, int? AdjustmentVoucherId);

public sealed record VoucherListModel(string Kind, IReadOnlyList<VoucherListRow> Rows);

/// <summary>
/// Stock vouchers (inventory_app/routes/vouchers.py): consumption, scrap, stock
/// adjustment and stock take — save, approve (stock through the costing engine,
/// journal through the ledger), unapprove (journal and stock reversed, later
/// issues re-costed), delete; all state changes POST-only and locked.
/// </summary>
[Area("Inventory")]
[Module(Modules.Inventory)]
[Route("inventory/vouchers")]
public sealed class VouchersController(AccountixDbContext db, StockCosting costing, ILedger ledger, IVoucherNumbers numbers,
                                       CompanyClock clock) : Controller
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private const string Cons = "consumption_vouchers", Scrap = "scrap_vouchers", Adj = "adjustment_vouchers", Take = "stock_take_vouchers";

    private int UserId => HttpContext.Access()!.User.Id;

    // ─────────────────────────────────────────────
    // Shared
    // ─────────────────────────────────────────────

    /// <summary>_status_form: GET opens ("create" blank, "view" saved); POST saves ("create"/"edit") and, approving, needs "approve".</summary>
    private IActionResult? StatusFormRights(string section, int? id)
    {
        if (!HttpMethods.IsPost(Request.Method))
            return this.Deny(section, id is null ? Rights.Create : Rights.View);
        if (this.Deny(section, id is null ? Rights.Create : Rights.Edit) is { } refused)
            return refused;
        if (Request.Form["status"].FirstOrDefault() == "approved")
            return this.Deny(section, Rights.Approve);
        return null;
    }

    private async Task<VoucherMeta> MetaAsync(DateTime? date, int? labelId, string? status, CancellationToken ct) =>
        new(date, labelId, status,
            await db.Set<ProjectLabel>().AsNoTracking().Where(l => l.IsActive == true).OrderBy(l => l.Name).ToListAsync(ct),
            clock.Today.ToString("yyyy-MM-dd", Inv));

    /// <summary>_charge_accounts: postable (level-5) active accounts.</summary>
    private Task<List<ChartOfAccount>> ChargeAccountsAsync(CancellationToken ct) =>
        db.Set<ChartOfAccount>().AsNoTracking().Where(a => a.Level >= 5 && a.IsActive == true).OrderBy(a => a.Code).ToListAsync(ct);

    /// <summary>request.form.get(name, type=int): an int, or null when blank or junk.</summary>
    private int? FormInt(string name) =>
        int.TryParse(Request.Form[name].FirstOrDefault(), NumberStyles.Integer, Inv, out int v) ? v : null;

    /// <summary>Decimal(str(float(x))) of a submitted quantity; null when it is not a number.</summary>
    private static decimal? FormQty(IReadOnlyList<string?> values, int i)
    {
        if (i >= values.Count)
            return 0.0m;
        if (!double.TryParse((values[i] ?? "").Trim(), NumberStyles.Float, Inv, out double d) || double.IsNaN(d) || double.IsInfinity(d)
            || Math.Abs(d) > 1e12)
            return null;
        return Py.FloatDecimal((decimal)d);
    }

    private static List<string?> List(IFormCollection form, string key) => form[key].Select(v => (string?)v).ToList();

    private async Task<Dictionary<int, string>> ProductNamesAsync(IEnumerable<int> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await db.Set<InvProduct>().AsNoTracking().Where(p => list.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
    }

    /// <summary>_post_voucher_journal: the voucher's label on every line; description "{kind} {number}: {reason}".</summary>
    private async Task PostVoucherJournalAsync(string vtype, int id, string number, string? reason, int? labelId,
                                               IEnumerable<JournalLineInput> lines, DateTime? entryDate, CancellationToken ct)
    {
        string name = vtype switch { "CONS" => "Consumption", "SCRAP" => "Scrap", "ADJ" => "Stock Adjustment", "ST" => "Stock Take", _ => vtype };
        var withLabel = lines.Select(l => l with { LabelId = labelId }).ToList();
        await ledger.PostAsync(vtype, id, number, $"{name} {number}: {reason ?? ""}", withLabel, entryDate, UserId, ct);
    }

    /// <summary>
    /// handle_costing_refusal: the request is rolled back and the refusal recorded (record_now survives
    /// the rollback); the error middleware then flashes it and goes back, as Python does.
    /// </summary>
    private async Task RecordRefusalAsync(BusinessRuleException e)
    {
        string? name = e switch
        {
            NegativeStockException => "NegativeStockError",
            ConsumedLayerException => "ConsumedLayerError",
            ClosedPeriodException => "ClosedPeriodError",
            DocumentDateException => "DocumentDateError",
            FormInputException => "FormInputError",
            _ => null,
        };
        if (name is null)
            return;
        try
        {
            await HttpContext.RequestServices.GetRequiredService<IAuditTrail>()
                .RecordNowAsync("refused", $"{name}: {e.Message}", module: "accounting");
        }
        catch (NotYetPortedException)
        {
            // Until Platform registers the audit trail.
        }
    }

    private async Task<IActionResult> GuardedAsync(Func<Task<IActionResult>> body)
    {
        try
        {
            return await body();
        }
        catch (BusinessRuleException e)
        {
            db.ChangeTracker.Clear();
            await RecordRefusalAsync(e);
            throw;
        }
    }

    // ─────────────────────────────────────────────
    // Consumption and scrap (one shape)
    // ─────────────────────────────────────────────

    private sealed record IssueKind(string Key, string Section, string VType, string Prefix, string Label, string ListUrl,
                                    string DefaultCode, string DefaultName, string NotePrefix, string CreditText);

    private static readonly IssueKind ConsKind = new("consumption", Cons, "CONS", "CONS", "Consumption", "/inventory/vouchers/consumption/list",
                                                     "5700", "Consumption Expense", "Consumption", "Stock reduction at historic cost");
    private static readonly IssueKind ScrapKind = new("scrap", Scrap, "SCRAP", "SCRAP", "Scrap", "/inventory/vouchers/scrap/list",
                                                      "5800", "Scrap/Write-off", "Scrap", "Stock scrapped at historic cost");

    [HttpGet("consumption")]
    [HttpGet("consumption/{id:int}")]
    [HttpPost("consumption")]
    [HttpPost("consumption/{id:int}")]
    [AnyMember]   // _status_form("consumption_vouchers"): checked in the action, it depends on the request
    public Task<IActionResult> ConsumptionForm(int? id, CancellationToken ct) => IssueFormAsync(ConsKind, id, ct);

    [HttpGet("scrap")]
    [HttpGet("scrap/{id:int}")]
    [HttpPost("scrap")]
    [HttpPost("scrap/{id:int}")]
    [AnyMember]   // _status_form("scrap_vouchers")
    public Task<IActionResult> ScrapForm(int? id, CancellationToken ct) => IssueFormAsync(ScrapKind, id, ct);

    private async Task<object?> LoadIssueAsync(IssueKind k, int id, bool forUpdate, CancellationToken ct) => k.VType == "CONS"
        ? forUpdate ? await db.LockAsync<ConsumptionVoucher>(id, ct) : await db.Set<ConsumptionVoucher>().FirstOrDefaultAsync(v => v.Id == id, ct)
        : forUpdate ? await db.LockAsync<ScrapVoucher>(id, ct) : await db.Set<ScrapVoucher>().FirstOrDefaultAsync(v => v.Id == id, ct);

    private async Task<VoucherFormModel> IssueModelAsync(IssueKind k, object? voucher, CancellationToken ct)
    {
        var accounts = await ChargeAccountsAsync(ct);
        switch (voucher)
        {
            case ConsumptionVoucher c:
            {
                var items = await db.Set<ConsumptionItem>().AsNoTracking().Where(i => i.VoucherId == c.Id).OrderBy(i => i.Id).ToListAsync(ct);
                return new VoucherFormModel
                {
                    Kind = k.Key, Id = c.Id, Number = c.VoucherNumber, Status = c.Status, ApprovedAt = c.ApprovedAt, Department = c.Department,
                    Reason = c.Reason, ChargeAccountId = c.ChargeAccountId, Accounts = accounts,
                    Items = items.Select(i => new VoucherItemRow(i.ProductId, i.ProductName, i.Quantity, null, null, null, i.UnitCost, i.TotalCost)).ToList(),
                    Meta = await MetaAsync(c.Date, c.LabelId, c.Status, ct),
                };
            }
            case ScrapVoucher s:
            {
                var items = await db.Set<ScrapItem>().AsNoTracking().Where(i => i.VoucherId == s.Id).OrderBy(i => i.Id).ToListAsync(ct);
                return new VoucherFormModel
                {
                    Kind = k.Key, Id = s.Id, Number = s.VoucherNumber, Status = s.Status, ApprovedAt = s.ApprovedAt, Reason = s.Reason,
                    ChargeAccountId = s.ChargeAccountId, Accounts = accounts,
                    Items = items.Select(i => new VoucherItemRow(i.ProductId, i.ProductName, i.Quantity, null, null, null, i.UnitCost, i.TotalCost)).ToList(),
                    Meta = await MetaAsync(s.Date, s.LabelId, s.Status, ct),
                };
            }
            default:
                return new VoucherFormModel { Kind = k.Key, Accounts = accounts, Meta = await MetaAsync(null, null, null, ct) };
        }
    }

    private async Task<IActionResult> IssueFormAsync(IssueKind k, int? id, CancellationToken ct)
    {
        if (StatusFormRights(k.Section, id) is { } refused)
            return refused;
        string view = k.VType == "CONS" ? "ConsumptionForm" : "ScrapForm";
        if (!HttpMethods.IsPost(Request.Method))
        {
            var found = id is int gid ? await LoadIssueAsync(k, gid, false, ct) : null;
            if (id is not null && found is null)
            {
                this.Flash("Voucher not found.", "error");
                return Redirect(k.ListUrl);
            }
            return View(view, await IssueModelAsync(k, found, ct));
        }
        return await GuardedAsync(() => SaveIssueAsync(k, id, view, ct));
    }

    private async Task<IActionResult> SaveIssueAsync(IssueKind k, int? id, string view, CancellationToken ct)
    {
        var form = Request.Form;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // A save or approval locks the voucher until commit: two users approving it at once post it once.
        var voucher = id is int lid ? await LoadIssueAsync(k, lid, true, ct) : null;
        if (id is not null && voucher is null)
        {
            this.Flash("Voucher not found.", "error");
            return Redirect(k.ListUrl);
        }
        string? currentStatus = voucher switch { ConsumptionVoucher c => c.Status, ScrapVoucher s => s.Status, _ => null };
        string? currentNumber = voucher switch { ConsumptionVoucher c => c.VoucherNumber, ScrapVoucher s => s.VoucherNumber, _ => null };
        if (currentStatus == "approved")
        {
            this.Flash($"{currentNumber} is approved and posted. Unapprove it to make changes.", "error");
            return Redirect(k.ListUrl);
        }
        string status = form["status"].FirstOrDefault() ?? "unapproved";
        bool isNew = voucher is null;
        if (isNew)
        {
            string number = await numbers.NextAsync(k.Prefix, ct);
            voucher = k.VType == "CONS"
                ? new ConsumptionVoucher { VoucherNumber = number, CreatedBy = UserId, Date = clock.UtcNow, Status = "unapproved", CreatedAt = clock.UtcNow }
                : new ScrapVoucher { VoucherNumber = number, CreatedBy = UserId, Date = clock.UtcNow, Status = "unapproved", CreatedAt = clock.UtcNow };
            db.Add(voucher);
        }
        else if (voucher is ConsumptionVoucher ce)
            await db.Set<ConsumptionItem>().Where(i => i.VoucherId == ce.Id).ExecuteDeleteAsync(ct);
        else if (voucher is ScrapVoucher se)
            await db.Set<ScrapItem>().Where(i => i.VoucherId == se.Id).ExecuteDeleteAsync(ct);

        string reason = form["reason"].FirstOrDefault() ?? "";
        int? charge = FormInt("charge_account_id"), label = FormInt("label_id");
        DateTime date;
        switch (voucher)
        {
            case ConsumptionVoucher c:
                c.Date = date = DocumentDates.Parse(form["date"].FirstOrDefault(), c.Date, clock);
                c.Department = form["department"].FirstOrDefault() ?? "";
                c.Reason = reason;
                c.ChargeAccountId = charge;
                c.LabelId = label;
                break;
            case ScrapVoucher s:
                s.Date = date = DocumentDates.Parse(form["date"].FirstOrDefault(), s.Date, clock);
                s.Reason = reason;
                s.ChargeAccountId = charge;
                s.LabelId = label;
                break;
            default:
                throw new InvalidOperationException();
        }
        await db.SaveChangesAsync(ct);
        int vid = voucher is ConsumptionVoucher cv ? cv.Id : ((ScrapVoucher)voucher).Id;
        string vnum = voucher is ConsumptionVoucher cn ? cn.VoucherNumber : ((ScrapVoucher)voucher).VoucherNumber;

        var products = List(form, "product_id[]");
        var qtys = List(form, "qty[]");
        var lines = new List<(int Product, decimal Qty)>();
        for (int i = 0; i < products.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(products[i]) || !int.TryParse(products[i]!.Trim(), out int pid))
                continue;
            if (FormQty(qtys, i) is not decimal qty || qty <= 0)
                continue;
            lines.Add((pid, qty));
        }
        var names = await ProductNamesAsync(lines.Select(l => l.Product), ct);
        var cItems = new List<ConsumptionItem>();
        var sItems = new List<ScrapItem>();
        foreach (var (pid, qty) in lines)
        {
            var (cost, total) = await costing.CostOfIssueAsync(pid, qty, ct);
            string pname = names.TryGetValue(pid, out var n) ? n : $"Product #{pid}";
            if (k.VType == "CONS")
                cItems.Add(new ConsumptionItem { VoucherId = vid, ProductId = pid, ProductName = pname, Quantity = qty, UnitCost = cost, TotalCost = total });
            else
                sItems.Add(new ScrapItem { VoucherId = vid, ProductId = pid, ProductName = pname, Quantity = qty, UnitCost = cost, TotalCost = total });
        }
        if (lines.Count == 0)
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            this.Flash("Add at least one item with quantity > 0.", "error");
            var shown = isNew ? null : await LoadIssueAsync(k, id!.Value, false, ct);
            return View(view, await IssueModelAsync(k, shown, ct));
        }
        db.AddRange(cItems);
        db.AddRange(sItems);

        if (status == "approved")
        {
            await db.SaveChangesAsync(ct);
            await costing.LockProductsAsync(lines.Select(l => l.Product), ct);
            // Issued through the costing engine: the true historic cost (weighted average / FIFO) at this
            // moment is what gets charged to the target account.
            decimal totalValue = 0m;
            foreach (var (item, idx) in cItems.Cast<object>().Concat(sItems).Select((x, i) => (x, i)))
            {
                var (pid, qty) = lines[idx];
                var (unit, total) = await costing.IssueAsync(pid, k.VType, vid, vnum, Py.FloatDecimal(qty), $"{k.NotePrefix}: {reason}",
                                                             UserId, null, date, null, ct);
                if (item is ConsumptionItem ci) { ci.UnitCost = unit; ci.TotalCost = total; }
                else if (item is ScrapItem si) { si.UnitCost = unit; si.TotalCost = total; }
                totalValue += total;
            }
            int chargeId = charge is int ca and not 0
                ? ca
                : (await ledger.GetOrCreateAccountAsync(k.DefaultCode, k.DefaultName, "expense", null, ct)).Id;
            int inventory = (await ledger.GetOrCreateAccountAsync("1200", "Inventory", "asset", null, ct)).Id;
            await PostVoucherJournalAsync(k.VType, vid, vnum, reason, label,
            [
                new(chargeId, totalValue, 0, $"{k.NotePrefix}: {reason}"),
                new(inventory, 0, totalValue, k.CreditText),
            ], date, ct);
            switch (voucher)
            {
                case ConsumptionVoucher c: c.ApprovedBy = UserId; c.ApprovedAt = clock.UtcNow; break;
                case ScrapVoucher s: s.ApprovedBy = UserId; s.ApprovedAt = clock.UtcNow; break;
            }
        }
        switch (voucher)
        {
            case ConsumptionVoucher c: c.Status = status; break;
            case ScrapVoucher s: s.Status = status; break;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"{k.Label} voucher {vnum} saved.", "success");
        return Redirect(k.ListUrl);
    }

    [HttpGet("consumption/list")]
    [Right(Cons, Rights.View)]
    public async Task<IActionResult> ConsumptionList(CancellationToken ct)
    {
        var rows = await db.Set<ConsumptionVoucher>().AsNoTracking().OrderByDescending(v => v.Id)
            .Select(v => new VoucherListRow(v.Id, v.VoucherNumber, v.Date, v.Department, v.ConsumptionItems.Count, v.Status, null))
            .ToListAsync(ct);
        return View("ConsumptionList", new VoucherListModel("consumption", rows));
    }

    [HttpGet("scrap/list")]
    [Right(Scrap, Rights.View)]
    public async Task<IActionResult> ScrapList(CancellationToken ct)
    {
        var rows = await db.Set<ScrapVoucher>().AsNoTracking().OrderByDescending(v => v.Id)
            .Select(v => new VoucherListRow(v.Id, v.VoucherNumber, v.Date, v.Reason, v.ScrapItems.Count, v.Status, null))
            .ToListAsync(ct);
        return View("ScrapList", new VoucherListModel("scrap", rows));
    }

    [HttpPost("consumption/{id:int}/delete")]
    [Right(Cons, Rights.Delete)]
    public Task<IActionResult> ConsumptionDelete(int id, CancellationToken ct) =>
        DeleteAsync<ConsumptionVoucher>(id, v => v.Status, "Consumption voucher deleted.", ConsKind.ListUrl,
            vid => db.Set<ConsumptionItem>().Where(i => i.VoucherId == vid).ExecuteDeleteAsync(ct), ct);

    [HttpPost("scrap/{id:int}/delete")]
    [Right(Scrap, Rights.Delete)]
    public Task<IActionResult> ScrapDelete(int id, CancellationToken ct) =>
        DeleteAsync<ScrapVoucher>(id, v => v.Status, "Scrap voucher deleted.", ScrapKind.ListUrl,
            vid => db.Set<ScrapItem>().Where(i => i.VoucherId == vid).ExecuteDeleteAsync(ct), ct);

    [HttpPost("adjustment/{id:int}/delete")]
    [Right(Adj, Rights.Delete)]
    public Task<IActionResult> AdjustmentDelete(int id, CancellationToken ct) =>
        DeleteAsync<StockAdjustmentVoucher>(id, v => v.Status, "Adjustment voucher deleted.", AdjListUrl,
            vid => db.Set<StockAdjustmentItem>().Where(i => i.VoucherId == vid).ExecuteDeleteAsync(ct), ct);

    private async Task<IActionResult> DeleteAsync<T>(int id, Func<T, string?> status, string done, string listUrl,
                                                     Func<int, Task<int>> deleteItems, CancellationToken ct) where T : class
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var v = await db.LockAsync<T>(id, ct);
        if (v is null)
            return NotFound();
        if (status(v) == "approved")
        {
            this.Flash("Cannot delete an approved voucher. Unapprove it first.", "error");
            return Redirect(listUrl);
        }
        await deleteItems(id);
        db.Remove(v);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash(done, "success");
        return Redirect(listUrl);
    }

    [HttpPost("consumption/{id:int}/unapprove")]
    [Right(Cons, Rights.Approve)]
    public Task<IActionResult> ConsumptionUnapprove(int id, CancellationToken ct) =>
        GuardedAsync(() => UnapproveAsync<ConsumptionVoucher>("CONS", id, ConsKind.ListUrl, v => v.Status, v => v.VoucherNumber,
            v => { v.Status = "unapproved"; v.ApprovedBy = null; v.ApprovedAt = null; }, ct));

    [HttpPost("scrap/{id:int}/unapprove")]
    [Right(Scrap, Rights.Approve)]
    public Task<IActionResult> ScrapUnapprove(int id, CancellationToken ct) =>
        GuardedAsync(() => UnapproveAsync<ScrapVoucher>("SCRAP", id, ScrapKind.ListUrl, v => v.Status, v => v.VoucherNumber,
            v => { v.Status = "unapproved"; v.ApprovedBy = null; v.ApprovedAt = null; }, ct));

    [HttpPost("adjustment/{id:int}/unapprove")]
    [Right(Adj, Rights.Approve)]
    public Task<IActionResult> AdjustmentUnapprove(int id, CancellationToken ct) =>
        GuardedAsync(() => UnapproveAsync<StockAdjustmentVoucher>("ADJ", id, AdjListUrl, v => v.Status, v => v.VoucherNumber,
            v => { v.Status = "unapproved"; v.ApprovedBy = null; v.ApprovedAt = null; }, ct));

    private async Task<IActionResult> UnapproveAsync<T>(string vtype, int id, string listUrl, Func<T, string?> status, Func<T, string> number,
                                                        Action<T> reset, CancellationToken ct) where T : class
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var v = await db.LockAsync<T>(id, ct);
        if (v is null)
            return NotFound();
        if (status(v) != "approved")
        {
            this.Flash("Voucher is not approved.", "error");
            return Redirect(listUrl);
        }
        await ledger.ReverseAsync(vtype, id, UserId, ct);
        if (vtype == "ADJ")
        {
            // A stock take's adjustment posted its journal under the take (older takes).
            var take = await db.Set<StockTake>().FirstOrDefaultAsync(t => t.AdjustmentVoucherId == id, ct);
            if (take is not null)
            {
                await ledger.ReverseAsync("ST", take.Id, UserId, ct);
                take.Status = "in_progress";
                take.AdjustmentVoucherId = null;
            }
        }
        // Later issues of the same products are re-costed in date order and any change is journalled.
        await costing.ReverseVoucherStockAsync(vtype, id, UserId, ct);
        reset(v);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"{number(v)} unapproved and stock reversed.", "success");
        return Redirect(listUrl);
    }

    // ─────────────────────────────────────────────
    // Stock adjustment
    // ─────────────────────────────────────────────

    private const string AdjListUrl = "/inventory/vouchers/adjustment/list";

    private async Task<VoucherFormModel> AdjustmentModelAsync(StockAdjustmentVoucher? v, CancellationToken ct)
    {
        var accounts = await ChargeAccountsAsync(ct);
        if (v is null)
            return new VoucherFormModel { Kind = "adjustment", Accounts = accounts, Meta = await MetaAsync(null, null, null, ct) };
        var items = await db.Set<StockAdjustmentItem>().AsNoTracking().Where(i => i.VoucherId == v.Id).OrderBy(i => i.Id).ToListAsync(ct);
        return new VoucherFormModel
        {
            Kind = "adjustment", Id = v.Id, Number = v.VoucherNumber, Status = v.Status, ApprovedAt = v.ApprovedAt, Reason = v.Reason,
            Accounts = accounts, Meta = await MetaAsync(v.Date, v.LabelId, v.Status, ct),
            Items = items.Select(i => new VoucherItemRow(i.ProductId, i.ProductName, i.Difference ?? 0, i.SystemQty, i.PhysicalQty, i.Difference,
                                                          i.UnitCost, i.TotalCost)).ToList(),
        };
    }

    [HttpGet("adjustment")]
    [HttpGet("adjustment/{id:int}")]
    [HttpPost("adjustment")]
    [HttpPost("adjustment/{id:int}")]
    [AnyMember]   // _status_form("adjustment_vouchers")
    public async Task<IActionResult> AdjustmentForm(int? id, CancellationToken ct)
    {
        if (StatusFormRights(Adj, id) is { } refused)
            return refused;
        if (!HttpMethods.IsPost(Request.Method))
        {
            var found = id is int gid ? await db.Set<StockAdjustmentVoucher>().FirstOrDefaultAsync(v => v.Id == gid, ct) : null;
            if (id is not null && found is null)
            {
                this.Flash("Voucher not found.", "error");
                return Redirect(AdjListUrl);
            }
            return View("AdjustmentForm", await AdjustmentModelAsync(found, ct));
        }
        return await GuardedAsync(() => SaveAdjustmentAsync(id, ct));
    }

    private async Task<IActionResult> SaveAdjustmentAsync(int? id, CancellationToken ct)
    {
        var form = Request.Form;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var voucher = id is int lid ? await db.LockAsync<StockAdjustmentVoucher>(lid, ct) : null;
        if (id is not null && voucher is null)
        {
            this.Flash("Voucher not found.", "error");
            return Redirect(AdjListUrl);
        }
        if (voucher?.Status == "approved")
        {
            this.Flash($"{voucher.VoucherNumber} is approved and posted. Unapprove it to make changes.", "error");
            return Redirect(AdjListUrl);
        }
        string status = form["status"].FirstOrDefault() ?? "unapproved";
        bool isNew = voucher is null;
        if (voucher is null)
        {
            voucher = new StockAdjustmentVoucher
            {
                VoucherNumber = await numbers.NextAsync("ADJ", ct), CreatedBy = UserId, Date = clock.UtcNow, Status = "unapproved",
                CreatedAt = clock.UtcNow,
            };
            db.Add(voucher);
        }
        else
            await db.Set<StockAdjustmentItem>().Where(i => i.VoucherId == voucher.Id).ExecuteDeleteAsync(ct);
        voucher.Date = DocumentDates.Parse(form["date"].FirstOrDefault(), voucher.Date, clock);
        voucher.Reason = form["reason"].FirstOrDefault() ?? "";
        voucher.LabelId = FormInt("label_id");
        await db.SaveChangesAsync(ct);

        var products = List(form, "product_id[]");
        var phys = List(form, "physical_qty[]");
        var pids = products.Select(p => int.TryParse((p ?? "").Trim(), out int x) ? x : 0).ToList();
        if (status == "approved")
            // Locked before the system quantity is read: a sale posted at the same moment cannot slip
            // between the count and the posting.
            await costing.LockProductsAsync(pids.Where(p => p > 0), ct);
        var names = await ProductNamesAsync(pids.Where(p => p > 0), ct);
        var items = new List<StockAdjustmentItem>();
        for (int i = 0; i < products.Count; i++)
        {
            if (pids[i] <= 0)
                continue;
            int pid = pids[i];
            // The ledger's quantity at the voucher's date, not the figure the form showed.
            decimal sq = await costing.OnHandAsync(pid, voucher.Date, ct);
            if (FormQty(phys, i) is not decimal pq)
                continue;
            decimal diff = pq - sq;
            if (diff == 0)
                continue;
            var (cost, total) = await costing.CostOfIssueAsync(pid, Math.Abs(diff), ct);
            items.Add(new StockAdjustmentItem
            {
                VoucherId = voucher.Id, ProductId = pid, ProductName = names.TryGetValue(pid, out var n) ? n : $"Product #{pid}",
                SystemQty = sq, PhysicalQty = pq, Difference = diff, UnitCost = cost, TotalCost = total,
            });
        }
        if (items.Count == 0)
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            this.Flash("No items with quantity differences to adjust.", "error");
            var shown = isNew ? null : await db.Set<StockAdjustmentVoucher>().FirstOrDefaultAsync(v => v.Id == id, ct);
            return View("AdjustmentForm", await AdjustmentModelAsync(shown, ct));
        }
        db.AddRange(items);

        if (status == "approved")
        {
            await db.SaveChangesAsync(ct);
            int inv = (await ledger.GetOrCreateAccountAsync("1200", "Inventory", "asset", null, ct)).Id;
            int adj = (await ledger.GetOrCreateAccountAsync("5900", "Inventory Adjustment", "expense", null, ct)).Id;
            var jlines = new List<JournalLineInput>();
            foreach (var item in items)
            {
                decimal d = item.Difference ?? 0;
                if (d > 0)
                {
                    // Excess found: booked in at the current valuation cost; the journal takes the row's own 2dp total.
                    decimal unit = await costing.CurrentUnitCostAsync(item.ProductId, ct);
                    var row = await costing.ReceiveAsync(item.ProductId, "ADJ", voucher.Id, voucher.VoucherNumber, Py.FloatDecimal(d), unit,
                                                         $"Adjustment: {voucher.Reason}", UserId, voucher.Date, null, ct);
                    item.UnitCost = row.UnitCost;
                    item.TotalCost = row.TotalCost;
                }
                else
                {
                    var (unit, total) = await costing.IssueAsync(item.ProductId, "ADJ", voucher.Id, voucher.VoucherNumber,
                                                                 Py.FloatDecimal(Math.Abs(d)), $"Adjustment: {voucher.Reason}", UserId, null,
                                                                 voucher.Date, null, ct);
                    item.UnitCost = unit;
                    item.TotalCost = total;
                }
                decimal val = item.TotalCost ?? 0;
                if (d > 0)
                {
                    jlines.Add(new(inv, val, 0));
                    jlines.Add(new(adj, 0, val));
                }
                else
                {
                    jlines.Add(new(adj, val, 0));
                    jlines.Add(new(inv, 0, val));
                }
            }
            await PostVoucherJournalAsync("ADJ", voucher.Id, voucher.VoucherNumber, voucher.Reason, voucher.LabelId, jlines, voucher.Date, ct);
            voucher.ApprovedBy = UserId;
            voucher.ApprovedAt = clock.UtcNow;
        }
        voucher.Status = status;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"Adjustment voucher {voucher.VoucherNumber} saved.", "success");
        return Redirect(AdjListUrl);
    }

    [HttpGet("adjustment/list")]
    [Right(Adj, Rights.View)]
    public async Task<IActionResult> AdjustmentList(CancellationToken ct)
    {
        var rows = await db.Set<StockAdjustmentVoucher>().AsNoTracking().OrderByDescending(v => v.Id)
            .Select(v => new VoucherListRow(v.Id, v.VoucherNumber, v.Date, v.Reason, v.StockAdjustmentItems.Count, v.Status, null))
            .ToListAsync(ct);
        return View("AdjustmentList", new VoucherListModel("adjustment", rows));
    }

    // ─────────────────────────────────────────────
    // Stock take
    // ─────────────────────────────────────────────

    private const string TakeListUrl = "/inventory/vouchers/stock-take/list";

    /// <summary>_next_take_reference: the next free ST-##### (skips any the old count()+1 issued).</summary>
    private async Task<string> NextTakeReferenceAsync(CancellationToken ct)
    {
        while (true)
        {
            string reference = await numbers.NextAsync("ST", ct);
            if (!await db.Set<StockTake>().AnyAsync(t => t.Reference == reference, ct))
                return reference;
        }
    }

    private async Task<VoucherFormModel> TakeModelAsync(StockTake? st, CancellationToken ct)
    {
        var accounts = await ChargeAccountsAsync(ct);
        if (st is null)
            return new VoucherFormModel { Kind = "stock_take", Accounts = accounts, Meta = await MetaAsync(null, null, null, ct) };
        var items = await db.Set<StockTakeItem>().AsNoTracking().Where(i => i.StockTakeId == st.Id).OrderBy(i => i.Id).ToListAsync(ct);
        return new VoucherFormModel
        {
            Kind = "stock_take", Id = st.Id, Number = st.Reference, Status = st.Status, ApprovedAt = st.ApprovedAt, Location = st.Location,
            Accounts = accounts, Meta = await MetaAsync(st.Date, null, st.Status, ct),
            Items = items.Select(i => new VoucherItemRow(i.ProductId, i.ProductName, i.Difference ?? 0, i.SystemQty, i.PhysicalQty, i.Difference,
                                                          i.UnitCost, null)).ToList(),
        };
    }

    [HttpGet("stock-take")]
    [HttpGet("stock-take/{id:int}")]
    [HttpPost("stock-take")]
    [HttpPost("stock-take/{id:int}")]
    [AnyMember]   // _status_form("stock_take_vouchers")
    public async Task<IActionResult> StockTakeForm(int? id, CancellationToken ct)
    {
        if (StatusFormRights(Take, id) is { } refused)
            return refused;
        if (!HttpMethods.IsPost(Request.Method))
        {
            var found = id is int gid ? await db.Set<StockTake>().FirstOrDefaultAsync(t => t.Id == gid, ct) : null;
            if (id is not null && found is null)
            {
                this.Flash("Stock take not found.", "error");
                return Redirect(TakeListUrl);
            }
            return View("StockTakeForm", await TakeModelAsync(found, ct));
        }
        return await GuardedAsync(() => SaveStockTakeAsync(id, ct));
    }

    private async Task<IActionResult> SaveStockTakeAsync(int? id, CancellationToken ct)
    {
        var form = Request.Form;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var st = id is int lid ? await db.LockAsync<StockTake>(lid, ct) : null;
        if (id is not null && st is null)
        {
            this.Flash("Stock take not found.", "error");
            return Redirect(TakeListUrl);
        }
        if (st?.Status == "approved")
        {
            this.Flash($"{st.Reference} is approved and posted. Unapprove its adjustment voucher to make changes.", "error");
            return Redirect(TakeListUrl);
        }
        bool isNew = st is null;
        if (st is null)
        {
            // A sequence, not count()+1: deleting a take made count()+1 re-issue an existing reference.
            st = new StockTake
            {
                Reference = await NextTakeReferenceAsync(ct), CreatedBy = UserId, Date = clock.UtcNow, Status = "in_progress", CreatedAt = clock.UtcNow,
            };
            db.Add(st);
        }
        else
            await db.Set<StockTakeItem>().Where(i => i.StockTakeId == st.Id).ExecuteDeleteAsync(ct);
        st.Date = DocumentDates.Parse(form["date"].FirstOrDefault(), st.Date, clock);
        st.Location = form["location"].FirstOrDefault() ?? "";
        string status = form["status"].FirstOrDefault() ?? "in_progress";
        await db.SaveChangesAsync(ct);

        var products = List(form, "product_id[]");
        var phys = List(form, "physical_qty[]");
        var pids = products.Select(p => int.TryParse((p ?? "").Trim(), out int x) ? x : 0).ToList();
        string? adjNumber = null;
        if (status == "approved")
        {
            // The adjustment's number first, then the products — the order every stock document takes them in.
            adjNumber = await numbers.NextAsync("ADJ", ct);
            // Locked before the system quantity is read (see the adjustment form).
            await costing.LockProductsAsync(pids.Where(p => p > 0), ct);
        }
        var names = await ProductNamesAsync(pids.Where(p => p > 0), ct);
        var items = new List<StockTakeItem>();
        for (int i = 0; i < products.Count; i++)
        {
            if (pids[i] <= 0)
                continue;
            int pid = pids[i];
            decimal sq = await costing.OnHandAsync(pid, st.Date, ct);
            if (FormQty(phys, i) is not decimal pq)
                continue;
            items.Add(new StockTakeItem
            {
                StockTakeId = st.Id, ProductId = pid, ProductName = names.TryGetValue(pid, out var n) ? n : $"Product #{pid}",
                SystemQty = sq, PhysicalQty = pq, Difference = pq - sq, UnitCost = await costing.CurrentUnitCostAsync(pid, ct),
            });
        }
        if (items.Count == 0)
        {
            this.Flash("Add at least one product to the stock take.", "error");
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            var shown = isNew ? null : await db.Set<StockTake>().FirstOrDefaultAsync(t => t.Id == id, ct);
            return View("StockTakeForm", await TakeModelAsync(shown, ct));
        }
        db.AddRange(items);

        if (status == "approved")
        {
            await db.SaveChangesAsync(ct);
            var adj = new StockAdjustmentVoucher
            {
                VoucherNumber = adjNumber!, Date = st.Date, Reason = $"Stock Take: {st.Reference} ({st.Location})", Status = "approved",
                CreatedBy = UserId, ApprovedBy = UserId, ApprovedAt = clock.UtcNow, CreatedAt = clock.UtcNow,
            };
            db.Add(adj);
            await db.SaveChangesAsync(ct);
            st.AdjustmentVoucherId = adj.Id;
            st.ApprovedBy = UserId;
            st.ApprovedAt = clock.UtcNow;
            int inv = (await ledger.GetOrCreateAccountAsync("1200", "Inventory", "asset", null, ct)).Id;
            int adjAcct = (await ledger.GetOrCreateAccountAsync("5900", "Inventory Adjustment", "expense", null, ct)).Id;
            var jlines = new List<JournalLineInput>();
            string note = $"Stock Take {st.Reference} adjustment ({st.Location})";
            foreach (var item in items)
            {
                decimal diff = item.Difference ?? 0;
                if (diff == 0)
                    continue;
                decimal cost = item.UnitCost ?? 0;
                var adjItem = new StockAdjustmentItem
                {
                    VoucherId = adj.Id, ProductId = item.ProductId, ProductName = item.ProductName, SystemQty = item.SystemQty,
                    PhysicalQty = item.PhysicalQty, Difference = diff, UnitCost = item.UnitCost,
                    TotalCost = (decimal)((double)Math.Abs(diff) * (double)cost),
                };
                db.Add(adjItem);
                decimal val;
                if (diff > 0)
                {
                    decimal unit = await costing.CurrentUnitCostAsync(item.ProductId, ct);
                    var row = await costing.ReceiveAsync(item.ProductId, "ADJ", adj.Id, adj.VoucherNumber, Py.FloatDecimal(diff), unit, note,
                                                         UserId, st.Date, null, ct);
                    val = row.TotalCost;
                    adjItem.UnitCost = row.UnitCost;
                    adjItem.TotalCost = row.TotalCost;
                }
                else
                {
                    var (unit, total) = await costing.IssueAsync(item.ProductId, "ADJ", adj.Id, adj.VoucherNumber, Py.FloatDecimal(Math.Abs(diff)),
                                                                 note, UserId, null, st.Date, null, ct);
                    val = total;
                    adjItem.UnitCost = unit;
                    adjItem.TotalCost = total;
                }
                if (diff > 0)
                {
                    jlines.Add(new(inv, val, 0));
                    jlines.Add(new(adjAcct, 0, val));
                }
                else
                {
                    jlines.Add(new(adjAcct, val, 0));
                    jlines.Add(new(inv, 0, val));
                }
            }
            // Posted under the ADJUSTMENT voucher that carries the stock rows, so unapproving it reverses
            // stock and journal together.
            if (jlines.Count > 0)
                await PostVoucherJournalAsync("ADJ", adj.Id, adj.VoucherNumber, adj.Reason, adj.LabelId, jlines, st.Date, ct);
        }
        st.Status = status;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"Stock take {st.Reference} saved.", "success");
        return Redirect(TakeListUrl);
    }

    [HttpGet("stock-take/list")]
    [Right(Take, Rights.View)]
    public async Task<IActionResult> StockTakeList(CancellationToken ct)
    {
        var rows = await db.Set<StockTake>().AsNoTracking().OrderByDescending(t => t.Id)
            .Select(t => new VoucherListRow(t.Id, t.Reference, t.Date, t.Location, t.StockTakeItems.Count, t.Status, t.AdjustmentVoucherId))
            .ToListAsync(ct);
        return View("StockTakeList", new VoucherListModel("stock_take", rows));
    }

    [HttpPost("stock-take/{id:int}/delete")]
    [Right(Take, Rights.Delete)]
    public async Task<IActionResult> StockTakeDelete(int id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var st = await db.LockAsync<StockTake>(id, ct);
        if (st is null)
            return NotFound();
        if (st.Status == "approved")
        {
            this.Flash("Cannot delete an approved stock take.", "error");
            return Redirect(TakeListUrl);
        }
        await db.Set<StockTakeItem>().Where(i => i.StockTakeId == id).ExecuteDeleteAsync(ct);
        db.Remove(st);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash("Stock take deleted.", "success");
        return Redirect(TakeListUrl);
    }

    // ─────────────────────────────────────────────
    // Lookups used by the forms
    // ─────────────────────────────────────────────

    [HttpGet("api/products")]
    [AnyRight("consumption_vouchers:view", "consumption_vouchers:create", "consumption_vouchers:edit",
              "scrap_vouchers:view", "scrap_vouchers:create", "scrap_vouchers:edit",
              "adjustment_vouchers:view", "adjustment_vouchers:create", "adjustment_vouchers:edit",
              "stock_take_vouchers:view", "stock_take_vouchers:create", "stock_take_vouchers:edit")]
    public async Task<IActionResult> ApiProducts([FromQuery] string? q, CancellationToken ct)
    {
        q = (q ?? "").Trim();
        var query = db.Set<InvProduct>().AsNoTracking().Where(p => p.IsActive == true);
        if (q.Length == 0)
            query = query.OrderBy(p => p.Name);
        else
        {
            string like = $"%{q}%";     // as Python's ilike(f"%{q}%"): no escaping
            query = query.Where(p => EF.Functions.ILike(p.Name, like) || EF.Functions.ILike(p.Sku, like));
        }
        var rows = await query.Take(20).Select(p => new { id = p.Id, name = p.Name, sku = p.Sku, stock = (double)(p.CurrentStock ?? 0) })
            .ToListAsync(ct);
        return Json(rows);
    }

    [HttpGet("api/product-stock/{pid:int}")]
    [AnyRight("consumption_vouchers:view", "consumption_vouchers:create", "consumption_vouchers:edit",
              "scrap_vouchers:view", "scrap_vouchers:create", "scrap_vouchers:edit",
              "adjustment_vouchers:view", "adjustment_vouchers:create", "adjustment_vouchers:edit",
              "stock_take_vouchers:view", "stock_take_vouchers:create", "stock_take_vouchers:edit")]
    public async Task<IActionResult> ApiProductStock(int pid, CancellationToken ct) =>
        Json(new
        {
            product_id = pid,
            qty = (double)await costing.OnHandAsync(pid, null, ct),
            cost = (double)await costing.CurrentUnitCostAsync(pid, ct),
        });
}
