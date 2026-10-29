using System.Globalization;
using Accountix.Accounting.Services;

namespace Accountix.Accounting.Controllers;

/// <summary>Bank reconciliation (finance_app/routes/bank_rec.py).</summary>
[Area("Accounting")]
[Module(Modules.Accounting)]
[Route("accounting/bank-reconciliation")]
public sealed class BankRecController(AccountixDbContext db, Ledger ledger, CompanyClock clock) : Controller
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public sealed record Row(JournalLine Line, JournalEntry Entry)
    {
        public decimal Net => Math.Round((Line.Debit ?? 0) - (Line.Credit ?? 0), 2, MidpointRounding.ToEven);
    }

    /// <summary>Everything the page and the statement show for one reconciliation (reconcile()).</summary>
    public sealed class RecData
    {
        public List<Row> Rows { get; init; } = [];
        public HashSet<int> Mine { get; init; } = [];
        public decimal Book { get; init; }
        public decimal ClearedBefore { get; init; }
        public decimal Cleared { get; init; }
        public decimal Statement { get; init; }
        public decimal Difference { get; init; }
        public decimal Unpresented { get; init; }
        public decimal InTransit { get; init; }
        public List<Row> Unticked { get; init; } = [];
        public decimal BankPerBooks { get; init; }
    }

    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.ToEven);

    private async Task<RecData> ReconcileAsync(BankReconciliation rec, CancellationToken ct)
    {
        var end = rec.StatementDate.ToDateTime(TimeOnly.MaxValue);
        var rows = (await db.Set<JournalLine>().AsNoTracking().Include(l => l.JournalEntry)
                .Where(l => l.AccountId == rec.AccountId && l.JournalEntry.IsPosted == true && l.JournalEntry.EntryDate <= end)
                .OrderBy(l => l.JournalEntry.EntryDate).ThenBy(l => l.JournalEntry.Id).ThenBy(l => l.Id)
                .ToListAsync(ct))
            .Select(l => new Row(l, l.JournalEntry)).ToList();
        // Line ids cleared by this account's other COMPLETED reconciliations.
        var before = (await db.Set<BankRecItem>()
                .Where(i => i.Reconciliation.AccountId == rec.AccountId && i.Reconciliation.Status == "completed"
                            && i.Reconciliation.Id != rec.Id)
                .Select(i => i.JournalLineId).ToListAsync(ct)).ToHashSet();
        var mine = (await db.Set<BankRecItem>().Where(i => i.ReconciliationId == rec.Id).Select(i => i.JournalLineId)
            .ToListAsync(ct)).ToHashSet();
        decimal book = R2(rows.Sum(r => r.Net));
        decimal clearedBefore = R2(rows.Where(r => before.Contains(r.Line.Id)).Sum(r => r.Net));
        var open = rows.Where(r => !before.Contains(r.Line.Id)).ToList();
        decimal cleared = R2(clearedBefore + open.Where(r => mine.Contains(r.Line.Id)).Sum(r => r.Net));
        var unticked = open.Where(r => !mine.Contains(r.Line.Id)).ToList();
        decimal unpresented = R2(unticked.Where(r => r.Net < 0).Sum(r => -r.Net));
        decimal inTransit = R2(unticked.Where(r => r.Net > 0).Sum(r => r.Net));
        decimal statement = R2(rec.StatementBalance);
        return new RecData
        {
            Rows = open, Mine = mine, Book = book, ClearedBefore = clearedBefore, Cleared = cleared,
            Statement = statement, Difference = R2(statement - cleared), Unpresented = unpresented,
            InTransit = inTransit, Unticked = unticked,
            // Book balance + payments not yet presented - deposits in transit = what the bank should show.
            BankPerBooks = R2(book + unpresented - inTransit),
        };
    }

    [HttpGet("")]
    [Right("bank_reconciliation", Rights.View)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var recs = await db.Set<BankReconciliation>().AsNoTracking().Include(r => r.Account)
            .OrderByDescending(r => r.StatementDate).ThenByDescending(r => r.Id).ToListAsync(ct);
        return View(new BankRecListModel { Recs = recs, Accounts = await ledger.CashBankAccountsAsync(ct), Today = clock.Today });
    }

    [HttpPost("new")]
    [Right("bank_reconciliation", Rights.Create)]
    public async Task<IActionResult> New(CancellationToken ct)
    {
        var f = Request.Form;
        var account = await ledger.CashBankAccountAsync(int.TryParse(f["account_id"], out int aid) ? aid : null, ct)
                      ?? throw new FormInputException("Choose the bank or cash account to reconcile.");
        var when = FormInput.Date(f, "statement_date", label: "Statement date");
        double balance = FormInput.Float(f, "statement_balance", null, "Statement closing balance");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // One draft per account: two users starting one at once queue here.
        await db.SerializeAsync("bank-rec", account.Id);
        if (await db.Set<BankReconciliation>().AnyAsync(r => r.AccountId == account.Id && r.Status == "draft", ct))
            throw new FormInputException($"{account.Name} already has a reconciliation in progress — finish or delete it first.");
        var last = await db.Set<BankReconciliation>().Where(r => r.AccountId == account.Id && r.Status == "completed")
            .OrderByDescending(r => r.StatementDate).FirstOrDefaultAsync(ct);
        if (last is not null && when <= last.StatementDate)
            throw new FormInputException($"{account.Name} is reconciled to {last.StatementDate.ToString("dd MMM yyyy", Inv)}; the next statement must be later.");
        var rec = new BankReconciliation
        {
            AccountId = account.Id, StatementDate = when, StatementBalance = Math.Round((decimal)balance, 2, MidpointRounding.ToEven),
            Status = "draft", Notes = "", CreatedBy = HttpContext.Access()!.User.Id, CreatedAt = clock.UtcNow,
        };
        db.Add(rec);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Redirect($"/accounting/bank-reconciliation/{rec.Id}");
    }

    [HttpGet("{id:int}")]
    [Right("bank_reconciliation", Rights.View)]
    public async Task<IActionResult> View(int id, [FromQuery] string? format, [FromServices] IReportExport export, CancellationToken ct)
    {
        var rec = await db.Set<BankReconciliation>().AsNoTracking().Include(r => r.Account).FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rec is null) return NotFound();
        var d = await ReconcileAsync(rec, ct);
        if (!string.IsNullOrEmpty(format))
            return await ExportAsync(rec, d, format, export, ct);
        return View("View", new BankRecViewModel { Rec = rec, D = d });
    }

    [HttpPost("{id:int}/save")]
    [Right("bank_reconciliation", Rights.Edit)]
    public async Task<IActionResult> Save(int id, CancellationToken ct)
    {
        var f = Request.Form;
        if (f["action"] == "complete" && this.Deny("bank_reconciliation", Rights.Approve) is { } refused) return refused;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rec = await db.LockAsync<BankReconciliation>(id, ct);
        if (rec is null) return NotFound();
        await db.Entry(rec).Reference(r => r.Account).LoadAsync(ct);
        if (rec.Status != "draft")
            throw new FormInputException("Reopen the reconciliation to change it.");
        if (!string.IsNullOrEmpty(f["statement_balance"]))
            rec.StatementBalance = Math.Round((decimal)FormInput.Float(f, "statement_balance", null, "Statement closing balance"), 2,
                                              MidpointRounding.ToEven);
        var allowed = (await ReconcileAsync(rec, ct)).Rows.Select(r => r.Line.Id).ToHashSet();
        var wanted = f["line_ids[]"].Where(s => s is not null && s.All(char.IsAsciiDigit) && s.Length > 0)
            .Select(s => int.Parse(s!)).Where(allowed.Contains).Distinct().OrderBy(x => x).ToList();
        await db.Set<BankRecItem>().Where(i => i.ReconciliationId == rec.Id).ExecuteDeleteAsync(ct);
        foreach (int lid in wanted)
            db.Add(new BankRecItem { ReconciliationId = rec.Id, JournalLineId = lid });
        await db.SaveChangesAsync(ct);
        if (f["action"] == "complete")
        {
            decimal diff = (await ReconcileAsync(rec, ct)).Difference;
            if (Math.Abs(diff) >= 0.005m)
            {
                await tx.CommitAsync(ct);
                this.Flash($"Saved. Not complete: the cleared balance differs from the statement by {diff.ToString("#,##0.00", Inv)}.", "warning");
                return Redirect($"/accounting/bank-reconciliation/{rec.Id}");
            }
            rec.Status = "completed";
            rec.CompletedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            this.Flash($"{rec.Account.Name} reconciled to {rec.StatementDate.ToString("dd MMM yyyy", Inv)}.", "success");
            return Redirect($"/accounting/bank-reconciliation/{rec.Id}");
        }
        await tx.CommitAsync(ct);
        this.Flash("Reconciliation saved.", "success");
        return Redirect($"/accounting/bank-reconciliation/{rec.Id}");
    }

    [HttpPost("{id:int}/reopen")]
    [Right("bank_reconciliation", Rights.Approve)]
    public async Task<IActionResult> Reopen(int id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rec = await db.LockAsync<BankReconciliation>(id, ct);
        if (rec is null) return NotFound();
        await db.SerializeAsync("bank-rec", rec.AccountId);
        bool later = await db.Set<BankReconciliation>().AnyAsync(r => r.AccountId == rec.AccountId && r.StatementDate > rec.StatementDate, ct);
        if (rec.Status != "completed" || later)
            throw new FormInputException("Only the latest completed reconciliation can be reopened.");
        if (await db.Set<BankReconciliation>().AnyAsync(r => r.AccountId == rec.AccountId && r.Status == "draft", ct))
            throw new FormInputException("Finish or delete the reconciliation in progress first.");
        rec.Status = "draft";
        rec.CompletedAt = null;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash("Reconciliation reopened.", "success");
        return Redirect($"/accounting/bank-reconciliation/{rec.Id}");
    }

    [HttpPost("{id:int}/delete")]
    [Right("bank_reconciliation", Rights.Delete)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rec = await db.LockAsync<BankReconciliation>(id, ct);
        if (rec is null) return NotFound();
        if (rec.Status != "draft")
            throw new FormInputException("Reopen a completed reconciliation before deleting it.");
        await db.Set<BankRecItem>().Where(i => i.ReconciliationId == rec.Id).ExecuteDeleteAsync(ct);
        db.Remove(rec);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash("Reconciliation deleted.", "success");
        return Redirect("/accounting/bank-reconciliation");
    }

    /// <summary>The bank reconciliation statement as Excel / PDF / CSV (_export_statement).</summary>
    private async Task<IActionResult> ExportAsync(BankReconciliation rec, RecData d, string fmt, IReportExport export, CancellationToken ct)
    {
        var rows = new List<ReportRow>
        {
            new(["Balance as per books", "", d.Book]),
            new(["Add: payments not yet presented", "", d.Unpresented]),
        };
        rows.AddRange(d.Unticked.Where(r => r.Net < 0).Select(r => new ReportRow(
            [$"   {r.Entry.VoucherNumber} {r.Entry.EntryDate.ToString("dd MMM yyyy", Inv)}", r.Line.Description is { Length: > 0 } ld ? ld : r.Entry.Description ?? "", -r.Net])));
        rows.Add(new ReportRow(["Less: deposits in transit", "", -d.InTransit]));
        rows.AddRange(d.Unticked.Where(r => r.Net > 0).Select(r => new ReportRow(
            [$"   {r.Entry.VoucherNumber} {r.Entry.EntryDate.ToString("dd MMM yyyy", Inv)}", r.Line.Description is { Length: > 0 } ld ? ld : r.Entry.Description ?? "", -r.Net])));
        rows.Add(new ReportRow(["Balance as per bank (books)", "", d.BankPerBooks], "total"));
        rows.Add(new ReportRow(["Balance as per bank statement", "", d.Statement], "total"));
        rows.Add(new ReportRow(["Difference", "", R2(d.Statement - d.BankPerBooks)], "grand"));
        var file = await export.ExportTableAsync(fmt, new ReportTable
        {
            Title = "Bank Reconciliation Statement",
            Period = $"{rec.Account.Code} {rec.Account.Name} — as at {rec.StatementDate.ToString("dd MMM yyyy", Inv)}",
            Columns = [new(""), new("Details"), new("Amount", ReportColumnKind.Money)],
            Rows = rows,
            FilePeriod = rec.StatementDate.ToString("yyyy-MM-dd", Inv),
        }, ct);
        return File(file.Content, file.ContentType, file.FileName);
    }
}

public sealed class BankRecListModel
{
    public IReadOnlyList<BankReconciliation> Recs { get; init; } = [];
    public IReadOnlyList<ChartOfAccount> Accounts { get; init; } = [];
    public DateOnly Today { get; init; }
}

public sealed class BankRecViewModel
{
    public required BankReconciliation Rec { get; init; }
    public required BankRecController.RecData D { get; init; }
}
