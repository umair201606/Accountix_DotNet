using System.Globalization;

namespace Accountix.Accounting.Services;

/// <summary>
/// General-ledger posting (shared/ledger_utils.py). Every write happens in the
/// caller's transaction; gl_daily_balances follows through the
/// <see cref="GlSummaryInterceptor"/>.
/// </summary>
public sealed class Ledger(AccountixDbContext db, IPeriods periods) : ILedger
{
    public async Task<JournalEntry> PostAsync(string voucherType, int voucherId, string voucherNumber, string description,
                                              IReadOnlyList<JournalLineInput> lines, DateTime? entryDate = null,
                                              int createdBy = 1, CancellationToken ct = default)
    {
        // A double-entry system must never persist an unbalanced journal.
        decimal totalDebit = lines.Sum(l => l.Debit);
        decimal totalCredit = lines.Sum(l => l.Credit);
        if (Math.Abs(totalDebit - totalCredit) > 0.01m)
            throw new LedgerException(
                $"Unbalanced journal for {voucherNumber}: debits {Py(totalDebit)} != credits {Py(totalCredit)}");

        // Aggregating accounts (levels 1-4) only roll up child balances.
        var ids = lines.Select(l => l.AccountId).Distinct().ToList();
        var accounts = await db.Set<ChartOfAccount>().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        foreach (var l in lines)
        {
            if (!accounts.TryGetValue(l.AccountId, out var acct))
                throw new LedgerException($"Journal for {voucherNumber} references unknown account id {l.AccountId}");
            if (!acct.IsPostable())
                throw new LedgerException(
                    $"Cannot post to aggregating account {acct.Code} {acct.Name} (level {acct.Level}); " +
                    "post to a level-5 operational account.");
        }

        // A closed period's figures are final (checked after the more specific problems).
        DateTime when = entryDate is DateTime d
            ? (d.TimeOfDay == TimeSpan.Zero ? d.Date : d)
            : DateTime.UtcNow;
        await periods.RequireOpenAsync(when, $"post {voucherNumber} into", ct);

        var entry = new JournalEntry
        {
            VoucherType = voucherType,
            VoucherId = voucherId,
            VoucherNumber = voucherNumber,
            Description = description,
            EntryDate = when,
            CreatedBy = createdBy,
            CreatedAt = DateTime.UtcNow,
            IsPosted = true,
        };
        foreach (var l in lines)
            entry.JournalLines.Add(new JournalLine
            {
                AccountId = l.AccountId,
                Debit = l.Debit,
                Credit = l.Credit,
                Description = l.Description ?? "",
                LabelId = l.LabelId,
            });
        db.Add(entry);
        await db.SaveChangesAsync(ct);
        return entry;
    }

    public async Task ReverseAsync(string voucherType, int voucherId, int createdBy = 1, CancellationToken ct = default)
    {
        var entries = await db.Set<JournalEntry>()
            .Where(e => e.VoucherType == voucherType && e.VoucherId == voucherId && e.IsPosted == true)
            .ToListAsync(ct);
        // Un-posting rewrites the period the entry SITS IN, so that period must be open.
        foreach (var e in entries)
            await periods.RequireOpenAsync(e.EntryDate, $"un-post {e.VoucherNumber} from", ct);
        foreach (var e in entries)
            e.IsPosted = false;
        await db.SaveChangesAsync(ct);
    }

    public async Task<ChartOfAccount> PostingAccountAsync(string role, CancellationToken ct = default)
    {
        var (name, type, alternates) = Coa.PostingAccounts[role];
        string code = Coa.RoleCodes[role];
        var acct = await ByCodeAsync(code, ct);
        if (acct is not null)
            return acct;
        foreach (string alt in alternates)
        {
            acct = await ByCodeAsync(alt, ct);
            if (acct is not null)
                return acct;
        }
        return await GetOrCreateAccountAsync(code, name, type, null, ct);
    }

    public async Task<ChartOfAccount> EntityAccountAsync(string kind, int entityId, string name, CancellationToken ct = default)
    {
        string parentCode = Coa.EntityParentCodes[kind];
        var parent = await ByCodeAsync(parentCode, ct);
        if (parent is null)
        {
            await Coa.SeedFixedTreeAsync(db, new HashSet<int> { 1, 2, 3, 4 }, ct);
            parent = await ByCodeAsync(parentCode, ct)
                     ?? throw new InvalidOperationException($"Chart has no {parentCode} for {kind} accounts.");
        }
        string code = EntityCode(parent.Code, entityId);
        var acct = await ByCodeAsync(code, ct);
        if (acct is null)
        {
            acct = new ChartOfAccount
            {
                Code = code, Name = name, Type = parent.Type, ParentId = parent.Id, Level = 5,
                IsFixed = false, IsActive = true, CreatedAt = DateTime.UtcNow,
            };
            db.Add(acct);
            await db.SaveChangesAsync(ct);
        }
        else if (acct.Name != name)
            acct.Name = name;
        return acct;
    }

    public async Task<ChartOfAccount> PartyAccountAsync(string kind, int entityId, string name, int? overrideAccountId = null,
                                                        CancellationToken ct = default)
    {
        if (overrideAccountId is int oid && oid != 0)
        {
            var acct = await db.Set<ChartOfAccount>().FirstOrDefaultAsync(a => a.Id == oid, ct);
            if (acct is not null && acct.IsPostable())
                return acct;
        }
        if (entityId != 0)
            return await EntityAccountAsync(kind, entityId, string.IsNullOrEmpty(name) ? $"{kind} #{entityId}" : name, ct);
        return await PostingAccountAsync(kind == "customer" ? "ar" : "ap", ct);
    }

    public async Task<ChartOfAccount> GetOrCreateAccountAsync(string code, string name, string type, string? parentCode = null,
                                                              CancellationToken ct = default)
    {
        var acct = await ByCodeAsync(code, ct);
        if (acct is not null)
            return acct;
        if (Coa.LegacyCodeMap.TryGetValue(code, out string? mapped))
        {
            acct = await ByCodeAsync(mapped, ct);
            if (acct is not null)
                return acct;
            code = mapped;
        }
        ChartOfAccount? parent = null;
        if (Coa.IsSegmented(code) && Coa.ParentCodeOf(code) is string pc)
            parent = await ByCodeAsync(pc, ct);
        if (parent is null && parentCode is not null)
        {
            var legacyParent = await ByCodeAsync(parentCode, ct);
            if (legacyParent is { Level: 4 })
                parent = legacyParent;
        }
        if (parent is null)
        {
            // The type's catch-all level-4 head in the fixed tree.
            await Coa.SeedFixedTreeAsync(db, new HashSet<int> { 1, 2, 3, 4 }, ct);
            string head = Coa.CatchallParentCodes.GetValueOrDefault(type, "5-04-01-01");
            parent = await ByCodeAsync(head, ct) ?? throw new InvalidOperationException($"Chart has no {head}.");
            // A legacy code with no fixed slot (5710, 2125, 5123–5126) is parked under the next free
            // code, so it is never found by its code again: find it by name (as Python does), or
            // every impairment / payroll run opened another account. Same lock key as Python.
            await db.SerializeAsync("legacy-account", parent.Id, name);
            int parentId = parent.Id;
            var existing = await db.Set<ChartOfAccount>()
                .Where(a => a.ParentId == parentId && a.Name == name && a.Type == type)
                .OrderBy(a => a.Id).FirstOrDefaultAsync(ct);
            if (existing is not null)
                return existing;
            code = await Coa.NextChildCodeAsync(db, parent, ct);
        }
        acct = new ChartOfAccount
        {
            Code = code, Name = name, Type = type, ParentId = parent.Id, Level = 5,
            IsFixed = false, IsActive = true, CreatedAt = DateTime.UtcNow,
        };
        db.Add(acct);
        await db.SaveChangesAsync(ct);
        return acct;
    }

    public async Task<IReadOnlyList<ChartOfAccount>> CashBankAccountsAsync(CancellationToken ct = default) =>
        await db.Set<ChartOfAccount>()
            .Where(a => a.IsActive == true && a.Type == "asset" && a.Level >= Coa.PostingLevel
                        && (a.Code.StartsWith("1-01-01-") || EF.Functions.ILike(a.Name, "%cash%")
                            || EF.Functions.ILike(a.Name, "%bank%")))
            .OrderBy(a => a.Code)
            .ToListAsync(ct);

    public async Task<bool> EntityHasPostingsAsync(string kind, int entityId, CancellationToken ct = default)
    {
        string code = EntityCode(Coa.EntityParentCodes[kind], entityId);
        var acct = await ByCodeAsync(code, ct);
        return acct is not null && await db.Set<JournalLine>().AnyAsync(l => l.AccountId == acct.Id, ct);
    }

    public async Task<ChartOfAccount?> CashBankAccountAsync(int? accountId, CancellationToken ct = default)
    {
        if (accountId is not int id || id == 0)
            return null;
        return (await CashBankAccountsAsync(ct)).FirstOrDefault(a => a.Id == id);
    }

    public async Task<IReadOnlyList<ChartOfAccount>> AccountsUnderAsync(string prefix, IReadOnlyCollection<string>? types = null,
                                                                        CancellationToken ct = default)
    {
        var q = db.Set<ChartOfAccount>()
            .Where(a => a.IsActive == true && a.Level >= Coa.PostingLevel && a.Code.StartsWith(prefix));
        if (types is { Count: > 0 })
            q = q.Where(a => types.Contains(a.Type));
        return await q.OrderBy(a => a.Code).ToListAsync(ct);
    }

    public Task<ChartOfAccount?> ByCodeAsync(string code, CancellationToken ct = default)
    {
        // Pending accounts first (Python's autoflush would find them).
        var pending = db.ChangeTracker.Entries<ChartOfAccount>()
            .FirstOrDefault(e => e.State == EntityState.Added && e.Entity.Code == code)?.Entity;
        return pending is not null
            ? Task.FromResult<ChartOfAccount?>(pending)
            : db.Set<ChartOfAccount>().FirstOrDefaultAsync(a => a.Code == code, ct);
    }

    private static string EntityCode(string parentCode, int entityId) =>
        $"{parentCode}-{(entityId + Coa.EntityIdOffset).ToString(CultureInfo.InvariantCulture).PadLeft(4, '0')}";

    // Python prints Decimal sums as written (e.g. "55000" or "55000.00").
    private static string Py(decimal d) => d.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A journal the ledger refuses to post (unbalanced, aggregating or unknown account).</summary>
public sealed class LedgerException(string message) : BusinessRuleException(message);

/// <summary>Voucher number series (VoucherNumber.next): atomic, gapless, numbered in commit order.</summary>
public sealed class VoucherNumbers(AccountixDbContext db) : IVoucherNumbers
{
    public async Task<string> NextAsync(string prefix, CancellationToken ct = default)
    {
        int cid = db.Company.CompanyId
                  ?? throw new NoActiveCompanyException($"VoucherNumber.next('{prefix}') with no active company");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO voucher_numbers (company_id, prefix, next_number) VALUES ({cid}, {prefix}, 1) ON CONFLICT (company_id, prefix) DO NOTHING",
            ct);
        int taken = await db.Database.SqlQuery<int>(
                $"UPDATE voucher_numbers SET next_number = next_number + 1 WHERE company_id = {cid} AND prefix = {prefix} RETURNING next_number AS \"Value\"")
            .ToListAsync(ct).ContinueWith(t => t.Result.Single(), ct);
        // A row already loaded (settings page, an earlier allocation) must not write its stale value back.
        foreach (var e in db.ChangeTracker.Entries<VoucherNumber>().Where(e => e.Entity.Prefix == prefix).ToList())
            e.State = EntityState.Detached;
        return $"{prefix}-{(taken - 1).ToString(CultureInfo.InvariantCulture).PadLeft(5, '0')}";
    }
}

/// <summary>Accounting period locking (shared/periods.py).</summary>
public sealed class Periods(AccountixDbContext db) : IPeriods
{
    public async Task<AccountingPeriod?> PeriodForAsync(DateTime when, CancellationToken ct = default)
    {
        var d = DateOnly.FromDateTime(when);
        return await db.Set<AccountingPeriod>()
            .Where(p => p.StartDate <= d && p.EndDate >= d)
            .OrderByDescending(p => p.StartDate)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<AccountingPeriod?> RequireOpenAsync(DateTime when, string action = "post to", CancellationToken ct = default)
    {
        var p = await PeriodForAsync(when, ct);
        if (p is null)
            return null;
        // A shared lock on the period, held to commit: postings never block each
        // other, but closing it (exclusive) waits for postings in flight.
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM accounting_periods WHERE id = {p.Id} FOR SHARE", ct);
        await db.Entry(p).ReloadAsync(ct);
        if (p.IsClosed != true)
            return p;
        var inv = CultureInfo.InvariantCulture;
        string closedOn = p.ClosedAt is DateTime c ? c.ToString("dd MMM yyyy", inv) : "earlier";
        throw new ClosedPeriodException(
            $"Cannot {action} {when.ToString("dd MMM yyyy", inv)}: the period '{p.PeriodName}' " +
            $"({p.StartDate.ToString("dd MMM yyyy", inv)} – {p.EndDate.ToString("dd MMM yyyy", inv)}) " +
            $"was closed on {closedOn} and its figures are final. Post an adjusting entry in the current " +
            $"open period instead, or reopen '{p.PeriodName}' in Settings → Periods if closing it was a mistake.");
    }
}
