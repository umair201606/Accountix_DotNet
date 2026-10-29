using static Accountix.Parties.Services.TrackingRules;

namespace Accountix.Parties.Services;

/// <summary>
/// Invoice payment tracking (shared/payment_tracking.py). Subledger matching
/// only: nothing here writes, reverses or touches a journal entry or a ledger
/// balance. It reads approved vouchers (the money) and approved invoices (the
/// debt), records which money settled which invoice (payment_allocations) and
/// keeps each invoice's cached paid_amount / payment_status in step.
///
/// Write methods run in the caller's transaction when there is one (the
/// caller commits), else in their own. Locks are Python's, in Python's order:
/// the receipt line, then the invoices (ascending id).
/// </summary>
public sealed class PaymentTracking(AccountixDbContext db, CompanyClock clock, CompanyContext company) : IPaymentTracking
{
    // Per-request caches: the party index and the party lists do not change under a tracking operation.
    private Dictionary<int, PartyRef>? _index;
    private List<(int Id, string Code, string Name, string Kind)>? _subledger;
    private Dictionary<int, PartyInfo>? _customers;
    private Dictionary<int, PartyInfo>? _suppliers;

    /// <summary>Forget the per-request caches (tests that change parties or the chart in between).</summary>
    public void Forget()
    {
        _index = null;
        _subledger = null;
        _customers = null;
        _suppliers = null;
    }

    private DateOnly Today => clock.Today;

    // ── Party identification ────────────────────────────────────────────

    public async Task<Dictionary<int, PartyInfo>> CustomersAsync(CancellationToken ct = default) =>
        _customers ??= await db.Set<InvCustomer>().AsNoTracking()
            .Select(c => new PartyInfo(c.Id, c.Name, c.PaymentTerms)).ToDictionaryAsync(p => p.Id, ct);

    public async Task<Dictionary<int, PartyInfo>> SuppliersAsync(CancellationToken ct = default) =>
        _suppliers ??= await db.Set<InvSupplier>().AsNoTracking()
            .Select(s => new PartyInfo(s.Id, s.Name, s.PaymentTerms)).ToDictionaryAsync(p => p.Id, ct);

    private Task<Dictionary<int, PartyInfo>> PartiesAsync(string kind, CancellationToken ct) =>
        kind == "customer" ? CustomersAsync(ct) : SuppliersAsync(ct);

    /// <summary>
    /// party_account_index: account id → the customer / supplier it settles, from (1) the deterministic
    /// subledger codes, (2) override accounts used by exactly one party on invoices, (3) the subledgers'
    /// own accounts (negative "ledger" parties).
    /// </summary>
    public async Task<Dictionary<int, PartyRef>> PartyAccountIndexAsync(CancellationToken ct = default)
    {
        if (_index is not null) return _index;
        var customers = await CustomersAsync(ct);
        var suppliers = await SuppliersAsync(ct);
        var index = new Dictionary<int, PartyRef>();

        var codeMap = new Dictionary<string, (string Kind, int Id)>();
        foreach (var (kind, names) in new[] { ("customer", customers), ("supplier", suppliers) })
            foreach (int id in names.Keys)
                codeMap[EntityCode(kind, id)] = (kind, id);
        if (codeMap.Count > 0)
        {
            var codes = codeMap.Keys.ToList();
            var rows = await db.Set<ChartOfAccount>().AsNoTracking().Where(a => codes.Contains(a.Code))
                .Select(a => new { a.Id, a.Code, a.Name }).ToListAsync(ct);
            // Python fills customers first, then suppliers.
            foreach (var a in rows.OrderBy(a => codeMap[a.Code].Kind == "customer" ? 0 : 1))
            {
                var (kind, pid) = codeMap[a.Code];
                var names = kind == "customer" ? customers : suppliers;
                string name = names.TryGetValue(pid, out var p) && !string.IsNullOrEmpty(p.Name) ? p.Name : a.Name;
                index[a.Id] = new PartyRef(kind, pid, name);
            }
        }

        // Override accounts, dropped when more than one party used them.
        var seen = new Dictionary<int, HashSet<(string Kind, int Id)>>();
        if (customers.Count > 0)
            foreach (var r in await db.Set<InvInvoice>().AsNoTracking().Where(i => i.PartyAccountId != null)
                         .Select(i => new { A = i.PartyAccountId!.Value, P = i.CustomerId }).Distinct().ToListAsync(ct))
                (seen.TryGetValue(r.A, out var set) ? set : seen[r.A] = []).Add(("customer", r.P));
        if (suppliers.Count > 0)
            foreach (var r in await db.Set<InvPurchaseInvoice>().AsNoTracking().Where(i => i.PartyAccountId != null)
                         .Select(i => new { A = i.PartyAccountId!.Value, P = i.SupplierId }).Distinct().ToListAsync(ct))
                (seen.TryGetValue(r.A, out var set) ? set : seen[r.A] = []).Add(("supplier", r.P));
        foreach (var (accountId, keys) in seen)
        {
            if (index.ContainsKey(accountId) || keys.Count != 1) continue;
            var (kind, pid) = keys.First();
            var names = kind == "customer" ? customers : suppliers;
            index[accountId] = new PartyRef(kind, pid, names.TryGetValue(pid, out var p) ? p.Name ?? "" : "");
        }

        // The subledgers' own accounts: whole-ledger payers with no invoices.
        foreach (var a in await SubledgerAccountsAsync(ct))
            if (!index.ContainsKey(a.Id))
                index[a.Id] = new PartyRef(a.Kind, LedgerPartyId(a.Id), a.Name);
        return _index = index;
    }

    /// <summary>_subledger_accounts for both kinds: the control accounts and their children.</summary>
    private async Task<List<(int Id, string Code, string Name, string Kind)>> SubledgerAccountsAsync(CancellationToken ct)
    {
        if (_subledger is not null) return _subledger;
        string cp = CustomerParent, sp = SupplierParent, cpx = CustomerParent + "-", spx = SupplierParent + "-";
        var rows = await db.Set<ChartOfAccount>().AsNoTracking()
            .Where(a => a.Code == cp || a.Code.StartsWith(cpx) || a.Code == sp || a.Code.StartsWith(spx))
            .Select(a => new { a.Id, a.Code, a.Name }).ToListAsync(ct);
        return _subledger = rows
            .Select(a => (a.Id, a.Code, a.Name, Kind: a.Code == cp || a.Code.StartsWith(cpx) ? "customer" : "supplier"))
            .OrderBy(a => a.Kind == "customer" ? 0 : 1)
            .ToList();
    }

    /// <summary>ledger_parties: the subledger accounts that stand in for a party, for the filter pickers.</summary>
    public async Task<List<(int Id, string Name, string Kind, string Code)>> LedgerPartiesAsync(string? kind = null, CancellationToken ct = default)
    {
        var index = await PartyAccountIndexAsync(ct);
        var output = new List<(int Id, string Name, string Kind, string Code)>();
        foreach (var a in await SubledgerAccountsAsync(ct))
        {
            if (kind is not null && a.Kind != kind) continue;
            if (index.TryGetValue(a.Id, out var entry) && IsLedgerParty(entry.Id))
                output.Add((entry.Id, a.Name, a.Kind, a.Code));
        }
        return output.OrderBy(r => r.Code, StringComparer.Ordinal).ToList();
    }

    // ── Allocation aggregates ──────────────────────────────────────────

    private sealed class LineFacts
    {
        public int Id { get; init; }
        public int AccountId { get; init; }
        public decimal? Debit { get; init; }
        public decimal? Credit { get; init; }
        public string VoucherType { get; init; } = "";
    }

    /// <summary>settlement_lines: of these voucher lines, the ones that are an assignable receipt / payment right now.</summary>
    public async Task<Dictionary<int, (string Kind, int PartyId)>> SettlementLinesAsync(IEnumerable<int> lineIds,
        Dictionary<int, PartyRef>? index = null, CancellationToken ct = default)
    {
        index ??= await PartyAccountIndexAsync(ct);
        var output = new Dictionary<int, (string, int)>();
        var ids = lineIds.Distinct().ToList();
        if (index.Count == 0 || ids.Count == 0) return output;
        var accountIds = index.Keys.ToList();
        var rows = await (from l in db.Set<AccountingVoucherLine>().AsNoTracking()
                          join v in db.Set<AccountingVoucher>().AsNoTracking() on l.VoucherId equals v.Id
                          where v.Status == "approved" && accountIds.Contains(l.AccountId) && ids.Contains(l.Id)
                          select new LineFacts { Id = l.Id, AccountId = l.AccountId, Debit = l.Debit, Credit = l.Credit, VoucherType = v.VoucherType })
            .ToListAsync(ct);
        foreach (var l in rows)
        {
            var party = index[l.AccountId];
            if (FlowOf(party.Kind, l.Debit, l.Credit, l.VoucherType) is null) continue;
            output[l.Id] = (party.Kind, party.Id);
        }
        return output;
    }

    /// <summary>_live_doc_ids: of these invoices, the ones that still exist and are approved.</summary>
    private async Task<HashSet<int>> LiveDocIdsAsync(string docType, IEnumerable<int> docIds, CancellationToken ct)
    {
        var ids = docIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        if (docType == Sales)
            return (await db.Set<InvInvoice>().AsNoTracking().Where(i => i.VoucherStatus == "approved" && ids.Contains(i.Id))
                .Select(i => i.Id).ToListAsync(ct)).ToHashSet();
        return (await db.Set<InvPurchaseInvoice>().AsNoTracking().Where(i => i.Status == "approved" && ids.Contains(i.Id))
            .Select(i => i.Id).ToListAsync(ct)).ToHashSet();
    }

    /// <summary>classify_allocations: (still backed on both sides, stale with the reason).</summary>
    public async Task<(List<PaymentAllocation> Live, List<(PaymentAllocation Allocation, string Reason)> Stale)> ClassifyAsync(
        IReadOnlyList<PaymentAllocation> allocs, Dictionary<int, PartyRef>? index = null, CancellationToken ct = default)
    {
        var live = new List<PaymentAllocation>();
        var stale = new List<(PaymentAllocation, string)>();
        if (allocs.Count == 0) return (live, stale);
        var lines = await SettlementLinesAsync(allocs.Select(a => a.VoucherLineId), index, ct);
        var liveDocs = new Dictionary<string, HashSet<int>>
        {
            [Sales] = await LiveDocIdsAsync(Sales, allocs.Where(a => a.DocType == Sales).Select(a => a.DocId), ct),
            [Purchase] = await LiveDocIdsAsync(Purchase, allocs.Where(a => a.DocType == Purchase).Select(a => a.DocId), ct),
        };
        foreach (var a in allocs)
        {
            if (!lines.TryGetValue(a.VoucherLineId, out var party))
                stale.Add((a, $"The voucher no longer carries this {a.PartyType} as an approved settlement."));
            else if (party != (a.PartyType, a.PartyId))
                stale.Add((a, "The voucher line now belongs to a different customer or supplier."));
            else if (a.DocType == NotApplicable)
                live.Add(a);
            else if (!(liveDocs.TryGetValue(a.DocType, out var docs) && docs.Contains(a.DocId)))
                stale.Add((a, "The invoice is no longer approved, or was deleted."));
            else
                live.Add(a);
        }
        return (live, stale);
    }

    private IQueryable<PaymentAllocation> Allocations => db.Set<PaymentAllocation>().AsNoTracking();

    private static Dictionary<int, decimal> SumBy(IEnumerable<PaymentAllocation> allocs, Func<PaymentAllocation, int> key)
    {
        var output = new Dictionary<int, decimal>();
        foreach (var a in allocs)
            output[key(a)] = Q(output.GetValueOrDefault(key(a)) + Q(a.Amount));
        return output;
    }

    public async Task<Dictionary<int, decimal>> AllocatedByLineAsync(IReadOnlyCollection<int> lineIds, CancellationToken ct = default)
    {
        if (lineIds.Count == 0) return [];
        var ids = lineIds.ToList();
        var allocs = await Allocations.Where(a => ids.Contains(a.VoucherLineId)).ToListAsync(ct);
        return SumBy((await ClassifyAsync(allocs, ct: ct)).Live, a => a.VoucherLineId);
    }

    public async Task<Dictionary<int, decimal>> ForcedByLineAsync(IReadOnlyCollection<int> lineIds, CancellationToken ct = default)
    {
        if (lineIds.Count == 0) return [];
        var ids = lineIds.ToList();
        var allocs = await Allocations.Where(a => a.DocType == NotApplicable && ids.Contains(a.VoucherLineId)).ToListAsync(ct);
        return SumBy((await ClassifyAsync(allocs, ct: ct)).Live, a => a.VoucherLineId);
    }

    public async Task<Dictionary<int, string>> ForcedNoteByLineAsync(IReadOnlyCollection<int> lineIds, CancellationToken ct = default)
    {
        if (lineIds.Count == 0) return [];
        var ids = lineIds.ToList();
        var output = new Dictionary<int, string>();
        foreach (var a in await Allocations.Where(a => a.DocType == NotApplicable && ids.Contains(a.VoucherLineId)).ToListAsync(ct))
            output[a.VoucherLineId] = a.Note ?? "";
        return output;
    }

    /// <summary>allocated_by_doc: live assigned money per invoice (all invoices of the type when ids is null).</summary>
    public async Task<Dictionary<int, decimal>> AllocatedByDocAsync(string docType, IReadOnlyCollection<int>? docIds = null,
        CancellationToken ct = default)
    {
        var q = Allocations.Where(a => a.DocType == docType);
        if (docIds is not null)
        {
            if (docIds.Count == 0) return [];
            var ids = docIds.ToList();
            q = q.Where(a => ids.Contains(a.DocId));
        }
        var allocs = await q.ToListAsync(ct);
        return SumBy((await ClassifyAsync(allocs, ct: ct)).Live, a => a.DocId);
    }

    // ── Documents ──────────────────────────────────────────────────────

    /// <summary>The invoice columns the tracker reads.</summary>
    public sealed class DocInfo
    {
        public int Id { get; init; }
        public string Number { get; init; } = "";
        public string VoucherNumber { get; init; } = "";
        public DateTime? InvoiceDate { get; init; }
        public DateTime? CreatedAt { get; init; }
        public DateTime? DueDate { get; init; }
        public decimal? Total { get; init; }
        public int PartyId { get; init; }
        public string? Status { get; init; }
    }

    private IQueryable<DocInfo> Docs(string docType) => docType == Sales
        ? db.Set<InvInvoice>().AsNoTracking().Select(i => new DocInfo
        {
            Id = i.Id, Number = i.InvoiceNumber, VoucherNumber = i.VoucherNumber, InvoiceDate = i.InvoiceDate,
            CreatedAt = i.CreatedAt, DueDate = i.DueDate, Total = i.TotalAmount, PartyId = i.CustomerId, Status = i.VoucherStatus,
        })
        : db.Set<InvPurchaseInvoice>().AsNoTracking().Select(i => new DocInfo
        {
            Id = i.Id, Number = i.InvoiceNumber, VoucherNumber = i.VoucherNumber, InvoiceDate = i.InvoiceDate,
            CreatedAt = i.CreatedAt, DueDate = null, Total = i.TotalAmount, PartyId = i.SupplierId, Status = i.Status,
        });

    private static void RequireInvoiceType(string docType)
    {
        if (docType is not (Sales or Purchase))
            throw new ArgumentException($"'{docType}' is not an invoice type");
    }

    private IQueryable<DocInfo> ApprovedDocs(string docType)
    {
        RequireInvoiceType(docType);
        return Docs(docType).Where(d => d.Status == "approved");
    }

    /// <summary>credited_by_doc: the value of the approved returns raised against each invoice.</summary>
    public async Task<Dictionary<int, decimal>> CreditedByDocAsync(string docType, IReadOnlyCollection<int>? docIds = null,
        CancellationToken ct = default)
    {
        List<int>? ids = docIds?.ToList();
        if (ids is { Count: 0 }) return [];
        if (docType == Sales)
        {
            var q = db.Set<InvSalesReturn>().AsNoTracking().Where(r => r.Status == "approved");
            if (ids is not null) q = q.Where(r => ids.Contains(r.OriginalInvoiceId));
            return await q.GroupBy(r => r.OriginalInvoiceId).Select(g => new { g.Key, S = g.Sum(r => r.NetReturnAmount ?? 0) })
                .ToDictionaryAsync(x => x.Key, x => Q(x.S), ct);
        }
        var p = db.Set<InvPurchaseReturn>().AsNoTracking().Where(r => r.Status == "approved");
        if (ids is not null) p = p.Where(r => ids.Contains(r.OriginalInvoiceId));
        return await p.GroupBy(r => r.OriginalInvoiceId).Select(g => new { g.Key, S = g.Sum(r => r.NetReturnAmount ?? 0) })
            .ToDictionaryAsync(x => x.Key, x => Q(x.S), ct);
    }

    /// <summary>returns_for_doc: the approved returns against one invoice, oldest first.</summary>
    public async Task<List<(int Id, string Number, DateTime? Date, DateTime? CreatedAt, string? Notes, decimal? Amount)>> ReturnsForDocAsync(
        string docType, int docId, CancellationToken ct = default)
    {
        if (docType == Sales)
            return (await db.Set<InvSalesReturn>().AsNoTracking().Where(r => r.Status == "approved" && r.OriginalInvoiceId == docId)
                    .OrderBy(r => r.ReturnDate).ThenBy(r => r.Id)
                    .Select(r => new { r.Id, r.ReturnNumber, r.ReturnDate, r.CreatedAt, r.Notes, r.NetReturnAmount }).ToListAsync(ct))
                .Select(r => (r.Id, r.ReturnNumber, r.ReturnDate, r.CreatedAt, r.Notes, r.NetReturnAmount)).ToList();
        return (await db.Set<InvPurchaseReturn>().AsNoTracking().Where(r => r.Status == "approved" && r.OriginalInvoiceId == docId)
                .OrderBy(r => r.ReturnDate).ThenBy(r => r.Id)
                .Select(r => new { r.Id, r.ReturnNumber, r.ReturnDate, r.CreatedAt, r.Notes, r.NetReturnAmount }).ToListAsync(ct))
            .Select(r => (r.Id, r.ReturnNumber, r.ReturnDate, r.CreatedAt, r.Notes, r.NetReturnAmount)).ToList();
    }

    /// <summary>_doc_dict.</summary>
    private static InvoiceRow Row(string docType, DocInfo inv, PartyInfo? party, decimal settled, decimal credited, DateOnly asOf)
    {
        decimal total = Q(inv.Total);
        settled = Q(settled);
        credited = Q(credited);
        decimal outstanding = Q(total - settled - credited);
        var issued = AsDate(inv.InvoiceDate) ?? AsDate(inv.CreatedAt);
        var due = EffectiveDueDate(AsDate(inv.DueDate), issued, party?.PaymentTerms);
        return new InvoiceRow
        {
            DocType = docType,
            Id = inv.Id,
            Number = inv.Number,
            VoucherNumber = inv.VoucherNumber,
            Date = issued,
            DueDate = due,
            PartyId = inv.PartyId,
            PartyName = party?.Name ?? "—",
            Total = total,
            Settled = settled,
            Credited = credited,
            OverdueDays = outstanding > Tolerance ? OverdueDays(due, asOf) : 0,
        };
    }

    private async Task<List<InvoiceRow>> RowsForAsync(string docType, List<DocInfo> invoices, DateOnly asOf, CancellationToken ct)
    {
        var ids = invoices.Select(i => i.Id).ToList();
        var settled = await AllocatedByDocAsync(docType, ids, ct);
        var credited = await CreditedByDocAsync(docType, ids, ct);
        var parties = await PartiesAsync(PartyForDoc[docType], ct);
        return invoices.Select(inv => Row(docType, inv, parties.GetValueOrDefault(inv.PartyId),
            settled.GetValueOrDefault(inv.Id), credited.GetValueOrDefault(inv.Id), asOf)).ToList();
    }

    /// <summary>invoice_rows: tracking rows for one invoice family, newest first.</summary>
    public async Task<List<InvoiceRow>> InvoiceRowsAsync(string docType, InvoiceFilter? f = null, DateOnly? asOf = null,
        CancellationToken ct = default)
    {
        f ??= new InvoiceFilter();
        var q = ApprovedDocs(docType);
        if (f.PartyId is int pid && pid != 0) q = q.Where(d => d.PartyId == pid);
        if (f.From is DateTime from) q = q.Where(d => d.InvoiceDate >= from);
        if (f.To is DateTime to) q = q.Where(d => d.InvoiceDate <= to);
        if (!string.IsNullOrEmpty(f.Search))
        {
            string term = $"%{f.Search.Trim()}%";
            q = q.Where(d => EF.Functions.ILike(d.Number, term));
        }
        var invoices = await q.OrderByDescending(d => d.InvoiceDate).ThenByDescending(d => d.Id).ToListAsync(ct);
        var rows = await RowsForAsync(docType, invoices, asOf ?? Today, ct);
        return rows.Where(r => (f.PayStates is not { Count: > 0 } || f.PayStates.Contains(r.PayState))
                               && (!f.OverdueOnly || r.IsOverdue)
                               && (f.AgeBuckets is not { Count: > 0 } || f.AgeBuckets.Contains(r.AgeBucket))).ToList();
    }

    /// <summary>filter_invoices: the narrowing invoice_rows does, over rows already built.</summary>
    public static List<InvoiceRow> FilterInvoices(IEnumerable<InvoiceRow> rows, InvoiceFilter f)
    {
        string term = (f.Search ?? "").Trim().ToLowerInvariant();
        DateOnly? from = AsDate(f.From), to = AsDate(f.To);
        var output = new List<InvoiceRow>();
        foreach (var r in rows)
        {
            if (f.PartyId is int pid && pid != 0 && r.PartyId != pid) continue;
            if (from is not null && (r.Date is null || r.Date < from)) continue;
            if (to is not null && (r.Date is null || r.Date > to)) continue;
            if (term.Length > 0 && !(r.Number ?? "").ToLowerInvariant().Contains(term)) continue;
            if (f.PayStates is { Count: > 0 } && !f.PayStates.Contains(r.PayState)) continue;
            if (f.OverdueOnly && !r.IsOverdue) continue;
            if (f.AgeBuckets is { Count: > 0 } && !f.AgeBuckets.Contains(r.AgeBucket)) continue;
            output.Add(r);
        }
        return output;
    }

    /// <summary>invoice_row: one invoice (any status), or null when it is not this company's.</summary>
    public async Task<InvoiceRow?> InvoiceRowAsync(string docType, int docId, DateOnly? asOf = null, CancellationToken ct = default)
    {
        RequireInvoiceType(docType);
        var inv = await Docs(docType).FirstOrDefaultAsync(d => d.Id == docId, ct);
        if (inv is null) return null;
        return (await RowsForAsync(docType, [inv], asOf ?? Today, ct))[0];
    }

    /// <summary>open_invoices: the party's invoices that still owe something (or that this line already settles), oldest first.</summary>
    public async Task<List<InvoiceRow>> OpenInvoicesAsync(string partyType, int partyId, int? lineId = null, DateOnly? asOf = null,
        CancellationToken ct = default)
    {
        string docType = DocForParty[partyType];
        var invoices = await ApprovedDocs(docType).Where(d => d.PartyId == partyId)
            .OrderBy(d => d.InvoiceDate).ThenBy(d => d.Id).ToListAsync(ct);
        var rows = await RowsForAsync(docType, invoices, asOf ?? Today, ct);
        var mine = new Dictionary<int, decimal>();
        if (lineId is int lid && lid != 0)
            foreach (var a in await Allocations.Where(a => a.VoucherLineId == lid && a.DocType == docType).ToListAsync(ct))
                mine[a.DocId] = Q(a.Amount);
        var output = new List<InvoiceRow>();
        foreach (var row in rows)
        {
            decimal already = mine.GetValueOrDefault(row.Id);
            row.AssignedHere = already;
            row.Assignable = Q(row.Outstanding) + already;
            if (row.Assignable > 0 || already > 0)
                output.Add(row);
        }
        return output;
    }

    // ── Feed (payments and receipts) ───────────────────────────────────

    private sealed class FeedLine
    {
        public int LineId { get; init; }
        public int LineNo { get; init; }
        public int AccountId { get; init; }
        public string? Description { get; init; }
        public decimal? Debit { get; init; }
        public decimal? Credit { get; init; }
        public int VoucherId { get; init; }
        public string VoucherNumber { get; init; } = "";
        public string VoucherType { get; init; } = "";
        public DateTime VoucherDate { get; init; }
        public string? Notes { get; init; }
        public string? CashAccount { get; init; }
    }

    private IQueryable<FeedLine> ApprovedLines() =>
        from l in db.Set<AccountingVoucherLine>().AsNoTracking()
        join v in db.Set<AccountingVoucher>().AsNoTracking() on l.VoucherId equals v.Id
        where v.Status == "approved"
        select new FeedLine
        {
            LineId = l.Id, LineNo = l.LineNo, AccountId = l.AccountId, Description = l.Description, Debit = l.Debit, Credit = l.Credit,
            VoucherId = v.Id, VoucherNumber = v.VoucherNumber, VoucherType = v.VoucherType, VoucherDate = v.VoucherDate,
            Notes = v.Notes, CashAccount = v.CashBankAccount != null ? v.CashBankAccount.Name : null,
        };

    private static FeedRow FeedDict(FeedLine line, PartyRef party, decimal assigned, decimal forced, string reason,
                                    (string Code, string Name)? account)
    {
        string flow = FlowOf(party.Kind, line.Debit, line.Credit, line.VoucherType)!;
        return new FeedRow
        {
            LineId = line.LineId,
            VoucherId = line.VoucherId,
            VoucherNumber = line.VoucherNumber,
            VoucherType = line.VoucherType,
            Date = line.VoucherDate,
            Flow = flow,
            PartyType = party.Kind,
            PartyId = party.Id,
            PartyName = party.Name,
            CashAccount = line.CashAccount ?? "",
            LedgerCode = account?.Code ?? "",
            LedgerName = account?.Name ?? "",
            Description = line.Description ?? "",
            Notes = line.Notes ?? "",
            Amount = Q(flow == "receipt" ? line.Credit : line.Debit),
            Assigned = Q(assigned),
            Forced = Q(forced),
            ForcedReason = reason ?? "",
        };
    }

    /// <summary>
    /// feed_rows: approved receipts from customers and payments to suppliers, newest first. <paramref name="index"/>
    /// and <paramref name="live"/> (every live allocation) may be passed so one page computes them once.
    /// </summary>
    public async Task<List<FeedRow>> FeedRowsAsync(FeedFilter? f = null, int? limit = null, Dictionary<int, PartyRef>? index = null,
        List<PaymentAllocation>? live = null, CancellationToken ct = default)
    {
        f ??= new FeedFilter();
        index ??= await PartyAccountIndexAsync(ct);
        if (index.Count == 0) return [];
        var accountIds = index.Where(kv => f.PartyType is null || kv.Value.Kind == f.PartyType)
            .Where(kv => f.PartyId is not int pid || pid == 0 || kv.Value.Id == pid)
            .Select(kv => kv.Key).ToList();
        if (accountIds.Count == 0) return [];

        var q = ApprovedLines().Where(l => accountIds.Contains(l.AccountId));
        if (f.From is DateTime from) q = q.Where(l => l.VoucherDate >= from);
        if (f.To is DateTime to) q = q.Where(l => l.VoucherDate <= to);
        if (!string.IsNullOrEmpty(f.Search))
        {
            string term = $"%{f.Search.Trim()}%";
            var accounts = db.Set<ChartOfAccount>();
            q = q.Where(l => EF.Functions.ILike(l.VoucherNumber, term) || EF.Functions.ILike(l.Description!, term)
                             || EF.Functions.ILike(l.Notes!, term)
                             || accounts.Any(a => a.Id == l.AccountId && (EF.Functions.ILike(a.Name, term) || EF.Functions.ILike(a.Code, term))));
        }
        var pairs = await q.OrderByDescending(l => l.VoucherDate).ThenByDescending(l => l.VoucherId).ThenBy(l => l.LineNo).ToListAsync(ct);

        if (live is null)
        {
            var lineIds = pairs.Select(p => p.LineId).ToList();
            var allocs = lineIds.Count == 0 ? [] : await Allocations.Where(a => lineIds.Contains(a.VoucherLineId)).ToListAsync(ct);
            live = (await ClassifyAsync(allocs, index, ct)).Live;
        }
        var assignedMap = new Dictionary<int, decimal>();
        var forcedMap = new Dictionary<int, decimal>();
        var reasonMap = new Dictionary<int, string>();
        foreach (var a in live)
        {
            assignedMap[a.VoucherLineId] = Q(assignedMap.GetValueOrDefault(a.VoucherLineId) + Q(a.Amount));
            if (a.DocType == NotApplicable)
            {
                forcedMap[a.VoucherLineId] = Q(forcedMap.GetValueOrDefault(a.VoucherLineId) + Q(a.Amount));
                reasonMap[a.VoucherLineId] = a.Note ?? "";
            }
        }
        var accountRows = new Dictionary<int, (string Code, string Name)>();
        if (pairs.Count > 0)
        {
            var ids = pairs.Select(p => p.AccountId).Distinct().ToList();
            foreach (var a in await db.Set<ChartOfAccount>().AsNoTracking().Where(a => ids.Contains(a.Id))
                         .Select(a => new { a.Id, a.Code, a.Name }).ToListAsync(ct))
                accountRows[a.Id] = (a.Code, a.Name);
        }
        var rows = new List<FeedRow>();
        foreach (var line in pairs)
        {
            if (!index.TryGetValue(line.AccountId, out var party)) continue;
            string? rowFlow = FlowOf(party.Kind, line.Debit, line.Credit, line.VoucherType);
            if (rowFlow is null || (f.Flow is { Length: > 0 } && rowFlow != f.Flow)) continue;
            var row = FeedDict(line, party, assignedMap.GetValueOrDefault(line.LineId), forcedMap.GetValueOrDefault(line.LineId),
                reasonMap.GetValueOrDefault(line.LineId, ""), accountRows.TryGetValue(line.AccountId, out var acc) ? acc : null);
            if (f.States is { Count: > 0 } && !f.States.Contains(row.State)) continue;
            rows.Add(row);
            if (limit is int lim && lim > 0 && rows.Count >= lim) break;
        }
        return rows;
    }

    /// <summary>feed_snapshot: every feed row (newest first) and the broken assignments, from one index and one pass.</summary>
    public async Task<(List<FeedRow> Rows, List<(PaymentAllocation Allocation, string Reason)> Stale)> FeedSnapshotAsync(CancellationToken ct = default)
    {
        var index = await PartyAccountIndexAsync(ct);
        var (live, stale) = await ClassifyAsync(await Allocations.ToListAsync(ct), index, ct);
        return (await FeedRowsAsync(index: index, live: live, ct: ct), stale);
    }

    /// <summary>filter_feed: the narrowing feed_rows does in SQL, over rows already built.</summary>
    public static List<FeedRow> FilterFeed(IEnumerable<FeedRow> rows, FeedFilter f)
    {
        string term = (f.Search ?? "").Trim().ToLowerInvariant();
        DateOnly? from = AsDate(f.From), to = AsDate(f.To);
        var output = new List<FeedRow>();
        foreach (var r in rows)
        {
            if (f.States is { Count: > 0 } && !f.States.Contains(r.State)) continue;
            if (f.Flow is { Length: > 0 } && r.Flow != f.Flow) continue;
            if (f.PartyType is { Length: > 0 } && r.PartyType != f.PartyType) continue;
            if (f.PartyId is int pid && pid != 0 && r.PartyId != pid) continue;
            var day = DateOnly.FromDateTime(r.Date);
            if (from is not null && day < from) continue;
            if (to is not null && day > to) continue;
            if (term.Length > 0 && !string.Join(" ", r.VoucherNumber, r.Description, r.Notes, r.LedgerCode, r.LedgerName)
                    .ToLowerInvariant().Contains(term))
                continue;
            output.Add(r);
        }
        return output;
    }

    /// <summary>feed_row: one feed item, or null when the line is not an assignable settlement.</summary>
    public async Task<FeedRow?> FeedRowAsync(int lineId, CancellationToken ct = default)
    {
        var line = await ApprovedLines().FirstOrDefaultAsync(l => l.LineId == lineId, ct);
        if (line is null) return null;
        var index = await PartyAccountIndexAsync(ct);
        if (!index.TryGetValue(line.AccountId, out var party) || FlowOf(party.Kind, line.Debit, line.Credit, line.VoucherType) is null)
            return null;
        // One read of the line's allocations, classified once: allocated_by_line, forced_by_line and forced_note_by_line.
        var allocs = await Allocations.Where(a => a.VoucherLineId == lineId).ToListAsync(ct);
        var live = (await ClassifyAsync(allocs, index, ct)).Live;
        decimal assigned = live.Aggregate(0m, (s, a) => Q(s + Q(a.Amount)));
        decimal forced = live.Where(a => a.DocType == NotApplicable).Aggregate(0m, (s, a) => Q(s + Q(a.Amount)));
        string reason = allocs.Where(a => a.DocType == NotApplicable).Select(a => a.Note ?? "").LastOrDefault() ?? "";
        var acct = await db.Set<ChartOfAccount>().AsNoTracking().Where(a => a.Id == line.AccountId)
            .Select(a => new { a.Code, a.Name }).FirstOrDefaultAsync(ct);
        return FeedDict(line, party, assigned, forced, reason, acct is null ? null : (acct.Code, acct.Name));
    }

    /// <summary>draft_settlement_count: unapproved vouchers carrying a party line.</summary>
    public async Task<int> DraftSettlementCountAsync(CancellationToken ct = default)
    {
        var index = await PartyAccountIndexAsync(ct);
        if (index.Count == 0) return 0;
        var accountIds = index.Keys.ToList();
        // SQL's status != 'approved': a NULL status is not counted.
        return await (from v in db.Set<AccountingVoucher>().AsNoTracking()
                      join l in db.Set<AccountingVoucherLine>().AsNoTracking() on v.Id equals l.VoucherId
                      where v.Status != null && v.Status != "approved" && accountIds.Contains(l.AccountId)
                      select v.Id).Distinct().CountAsync(ct);
    }

    // ── History ────────────────────────────────────────────────────────

    /// <summary>history_for_doc: what settled one invoice, oldest first, with the running balance after each.</summary>
    public async Task<List<HistoryEvent>> HistoryForDocAsync(string docType, int docId, CancellationToken ct = default)
    {
        RequireInvoiceType(docType);
        var inv = await Docs(docType).FirstOrDefaultAsync(d => d.Id == docId, ct);
        if (inv is null) return [];
        var allocs = await (from a in Allocations
                            join v in db.Set<AccountingVoucher>().AsNoTracking() on a.VoucherId equals v.Id
                            where a.DocType == docType && a.DocId == docId
                            orderby v.VoucherDate, a.Id
                            select a).ToListAsync(ct);
        var liveIds = (await ClassifyAsync(allocs, ct: ct)).Live.Select(a => a.Id).ToHashSet();
        var voucherIds = allocs.Select(a => a.VoucherId).Distinct().ToList();
        var vouchers = await db.Set<AccountingVoucher>().AsNoTracking().Where(v => voucherIds.Contains(v.Id))
            .Select(v => new { v.Id, v.VoucherNumber, v.VoucherType, v.VoucherDate, Cash = v.CashBankAccount != null ? v.CashBankAccount.Name : null })
            .ToDictionaryAsync(v => v.Id, ct);
        var lineIds = allocs.Select(a => a.VoucherLineId).Distinct().ToList();
        var lines = await db.Set<AccountingVoucherLine>().AsNoTracking().Where(l => lineIds.Contains(l.Id))
            .Select(l => new { l.Id, l.Description }).ToDictionaryAsync(l => l.Id, l => l.Description, ct);
        var users = await UserNamesAsync(allocs.Select(a => a.CreatedBy), ct);

        var events = new List<HistoryEvent>();
        foreach (var a in allocs)
        {
            var v = vouchers.GetValueOrDefault(a.VoucherId);
            events.Add(new HistoryEvent
            {
                Kind = "money",
                Id = a.Id,
                VoucherId = a.VoucherId,
                LineId = a.VoucherLineId,
                VoucherNumber = v?.VoucherNumber ?? "",
                VoucherType = v?.VoucherType ?? "",
                Date = v is not null ? v.VoucherDate : a.CreatedAt,
                CashAccount = v?.Cash ?? "",
                Description = lines.GetValueOrDefault(a.VoucherLineId) ?? "",
                Amount = Q(a.Amount),
                Counts = liveIds.Contains(a.Id),
                Note = a.Note ?? "",
                By = a.CreatedBy is int u ? users.GetValueOrDefault(u, "") : "",
                CreatedAt = a.CreatedAt,
            });
        }
        foreach (var r in await ReturnsForDocAsync(docType, docId, ct))
            events.Add(new HistoryEvent
            {
                Kind = "return",
                Id = r.Id,
                VoucherNumber = r.Number,
                VoucherType = docType == Sales ? "Credit note" : "Debit note",
                Date = r.Date ?? r.CreatedAt,
                Description = r.Notes ?? "",
                Amount = Q(r.Amount),
                Counts = true,
            });
        events = events.OrderBy(e => AsDate(e.Date) ?? DateOnly.MinValue).ThenBy(e => e.Kind == "return" ? 0 : 1).ToList();
        decimal running = Q(inv.Total);
        foreach (var e in events)
        {
            if (e.Counts) running = Q(running - e.Amount);
            e.BalanceAfter = running;
        }
        return events;
    }

    private async Task<Dictionary<int, string>> UserNamesAsync(IEnumerable<int?> ids, CancellationToken ct)
    {
        var list = ids.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        if (list.Count == 0) return [];
        return await db.Users.AsNoTracking().Where(u => list.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName }).ToDictionaryAsync(u => u.Id, u => u.FullName ?? "", ct);
    }

    /// <summary>history_for_line: the invoices one receipt / payment was split across.</summary>
    public async Task<List<SplitRow>> HistoryForLineAsync(int lineId, CancellationToken ct = default)
    {
        var allocs = await Allocations.Where(a => a.VoucherLineId == lineId).OrderBy(a => a.Id).ToListAsync(ct);
        var users = await UserNamesAsync(allocs.Select(a => a.CreatedBy), ct);
        var invoiceRows = new Dictionary<(string, int), InvoiceRow?>();
        foreach (var dt in new[] { Sales, Purchase })
        {
            var ids = allocs.Where(a => a.DocType == dt).Select(a => a.DocId).Distinct().ToList();
            if (ids.Count == 0) continue;
            var docs = await Docs(dt).Where(d => ids.Contains(d.Id)).ToListAsync(ct);
            foreach (var row in await RowsForAsync(dt, docs, Today, ct))
                invoiceRows[(dt, row.Id)] = row;
        }
        var output = new List<SplitRow>();
        foreach (var a in allocs)
        {
            bool forced = a.DocType == NotApplicable;
            var row = forced ? null : invoiceRows.GetValueOrDefault((a.DocType, a.DocId));
            output.Add(new SplitRow
            {
                Id = a.Id,
                DocType = a.DocType,
                DocId = a.DocId,
                Forced = forced,
                Number = forced ? "Not applicable to any invoice" : row?.Number ?? $"#{a.DocId}",
                PartyName = row?.PartyName ?? "",
                Total = row?.Total ?? 0m,
                Outstanding = row?.Outstanding ?? 0m,
                Amount = Q(a.Amount),
                Note = a.Note ?? "",
                By = a.CreatedBy is int u ? users.GetValueOrDefault(u, "") : "",
                CreatedAt = a.CreatedAt,
            });
        }
        return output;
    }

    // ── Writes ─────────────────────────────────────────────────────────

    /// <summary>The caller's transaction, or one of our own (committed when the body succeeds).</summary>
    private async Task<T> WriteAsync<T>(Func<Task<T>> body, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            var r = await body();
            await db.SaveChangesAsync(ct);
            return r;
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var result = await body();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }

    private async Task<object?> LoadInvoiceAsync(string docType, int docId, CancellationToken ct) => docType == Sales
        ? await db.Set<InvInvoice>().FirstOrDefaultAsync(i => i.Id == docId, ct)
        : await db.Set<InvPurchaseInvoice>().FirstOrDefaultAsync(i => i.Id == docId, ct);

    private static void SetCache(object inv, decimal settled, decimal credited)
    {
        switch (inv)
        {
            case InvInvoice si:
                si.PaidAmount = Q(settled);
                si.PaymentStatus = PayState(Q(si.TotalAmount), Q(settled) + credited);
                break;
            case InvPurchaseInvoice pi:
                pi.PaidAmount = Q(settled);
                pi.PaymentStatus = PayState(Q(pi.TotalAmount), Q(settled) + credited);
                break;
        }
    }

    /// <summary>recompute_doc: re-derive an invoice's paid_amount / payment_status from its allocations (pending changes saved first).</summary>
    public async Task<object?> RecomputeDocAsync(string docType, int docId, CancellationToken ct = default)
    {
        if (docType is not (Sales or Purchase)) return null;
        await db.SaveChangesAsync(ct);
        var inv = await LoadInvoiceAsync(docType, docId, ct);
        if (inv is null) return null;
        decimal settled = (await AllocatedByDocAsync(docType, [docId], ct)).GetValueOrDefault(docId);
        decimal credited = (await CreditedByDocAsync(docType, [docId], ct)).GetValueOrDefault(docId);
        SetCache(inv, settled, credited);
        return inv;
    }

    /// <summary>_lock_line: the receipt line, locked until commit. Every write on a line goes through here first.</summary>
    private Task LockLineAsync(int lineId, CancellationToken ct) => db.LockAsync<AccountingVoucherLine>(lineId, ct);

    /// <summary>_lock_docs: invoices about to be assigned against, in id order.</summary>
    private async Task LockDocsAsync(string docType, IEnumerable<int> docIds, CancellationToken ct)
    {
        var ids = docIds.Distinct().Order().ToArray();
        if (ids.Length == 0 || !db.Database.IsNpgsql()) return;
        int cid = company.CompanyId ?? 0;
        if (docType == Sales)
            await db.Database.ExecuteSqlAsync(
                $"SELECT 1 FROM inv_invoices WHERE id = ANY({ids}) AND company_id = {cid} ORDER BY id FOR NO KEY UPDATE", ct);
        else
            await db.Database.ExecuteSqlAsync(
                $"SELECT 1 FROM inv_purchase_invoices WHERE id = ANY({ids}) AND company_id = {cid} ORDER BY id FOR NO KEY UPDATE", ct);
        // populate_existing: invoices this context already holds are read again.
        foreach (var e in db.ChangeTracker.Entries().Where(e => e.Entity is InvInvoice i && docType == Sales && ids.Contains(i.Id)
                                                                  || e.Entity is InvPurchaseInvoice p && docType == Purchase && ids.Contains(p.Id)).ToList())
            await e.ReloadAsync(ct);
    }

    public sealed record AllocationEntry(object? DocId, object? Amount);

    private static bool TryInt(object? v, out int value)
    {
        value = 0;
        switch (v)
        {
            case int i: value = i; return true;
            case long l when l is >= int.MinValue and <= int.MaxValue: value = (int)l; return true;
            case decimal d when d is >= int.MinValue and <= int.MaxValue: value = (int)Math.Truncate(d); return true;
            case double f when !double.IsNaN(f) && f is >= int.MinValue and <= int.MaxValue: value = (int)Math.Truncate(f); return true;
            case bool b: value = b ? 1 : 0; return true;
            case string s: return int.TryParse(s.Trim(), System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out value);
            default: return false;
        }
    }

    /// <summary>Decimal(str(x)) of a JSON / form value; null when it is not a number.</summary>
    private static decimal? TryDecimal(object? v) => v switch
    {
        null => 0m,
        decimal d => d,
        int i => i,
        long l => l,
        double f when double.IsFinite(f) => decimal.Parse(TrackingRulesPy.Str(f), System.Globalization.NumberStyles.Float,
                                                          System.Globalization.CultureInfo.InvariantCulture),
        bool b => b ? 1m : 0m,
        string s when s.Length == 0 => 0m,
        string s => decimal.TryParse(s.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null,
        _ => null,
    };

    /// <summary>
    /// allocate: assign one receipt / payment to invoices. An amount of zero removes that invoice from the
    /// line's split. Validated whole before the first row is written; refusals are AllocationException.
    /// </summary>
    public Task<FeedRow?> AllocateAsync(int lineId, IReadOnlyList<AllocationEntry> entries, int? userId, string note = "",
        CancellationToken ct = default) => WriteAsync(async () =>
    {
        await LockLineAsync(lineId, ct);
        var row = await FeedRowAsync(lineId, ct)
                  ?? throw new AllocationException("That payment is not an approved customer receipt or supplier payment, so it cannot be assigned to invoices.");
        string docType = row.DocType;
        var existing = await db.Set<PaymentAllocation>().Where(a => a.VoucherLineId == lineId && a.DocType == docType)
            .ToDictionaryAsync(a => a.DocId, ct);

        var cleaned = new Dictionary<int, decimal>();
        var order = new List<int>();
        foreach (var e in entries)
        {
            object? rawAmount = e.Amount is null or false or 0 or 0L or 0m or 0.0 or "" ? 0 : e.Amount;
            if (!TryInt(e.DocId, out int docId) || TryDecimal(rawAmount) is not decimal raw)
                throw new AllocationException("Invalid invoice or amount in the assignment.");
            decimal amount = Q(raw);
            if (amount < 0)
                throw new AllocationException("An assigned amount cannot be negative.");
            if (!cleaned.ContainsKey(docId)) order.Add(docId);
            cleaned[docId] = cleaned.GetValueOrDefault(docId) + amount;
        }
        if (cleaned.Count == 0)
            throw new AllocationException("Select at least one invoice to assign this to.");

        await LockDocsAsync(docType, cleaned.Keys, ct);
        var docs = await Docs(docType).Where(d => cleaned.Keys.Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct);
        foreach (int docId in order)
        {
            var inv = docs.GetValueOrDefault(docId) ?? throw new AllocationException($"Invoice #{docId} was not found.");
            if (inv.Status != "approved")
                throw new AllocationException($"{inv.Number} is not approved yet, so nothing can be assigned to it.");
            if (inv.PartyId != row.PartyId)
                throw new AllocationException($"{inv.Number} belongs to a different {row.PartyType} than this {row.Flow}.");
        }

        var settledMap = await AllocatedByDocAsync(docType, cleaned.Keys, ct);
        var creditedMap = await CreditedByDocAsync(docType, cleaned.Keys, ct);
        foreach (int docId in order)
        {
            var inv = docs[docId];
            decimal mine = existing.TryGetValue(docId, out var ex) ? Q(ex.Amount) : 0m;
            decimal headroom = Q(Q(inv.Total) - settledMap.GetValueOrDefault(docId) - creditedMap.GetValueOrDefault(docId) + mine);
            if (cleaned[docId] > headroom + Tolerance)
                throw new AllocationException($"{inv.Number} only has {Comma2(headroom)} outstanding — {Comma2(cleaned[docId])} cannot be assigned to it.");
        }

        decimal totalAssigned = cleaned.Values.Sum();
        decimal otherLines = existing.Where(kv => !cleaned.ContainsKey(kv.Key)).Sum(kv => Q(kv.Value.Amount));
        decimal forced = (await ForcedByLineAsync([lineId], ct)).GetValueOrDefault(lineId);
        decimal capacity = Q(row.Amount);
        if (totalAssigned + otherLines + forced > capacity + Tolerance)
        {
            string detail = forced > Tolerance ? $" ({Comma2(forced)} of it is already closed as not applicable)" : "";
            throw new AllocationException(
                $"This {row.Flow} is {Comma2(capacity)}{detail}; assigning {Comma2(totalAssigned + otherLines + forced)} would exceed it.");
        }

        var touched = new List<int>();
        foreach (int docId in order)
        {
            decimal amount = cleaned[docId];
            existing.TryGetValue(docId, out var alloc);
            if (amount <= 0)
            {
                if (alloc is not null)
                {
                    db.Remove(alloc);
                    touched.Add(docId);
                }
                continue;
            }
            if (alloc is null)
            {
                alloc = new PaymentAllocation
                {
                    VoucherId = row.VoucherId, VoucherLineId = lineId, DocType = docType, DocId = docId,
                    PartyType = row.PartyType, PartyId = row.PartyId, CreatedBy = userId, CreatedAt = clock.UtcNow, Note = "",
                };
                db.Add(alloc);
            }
            alloc.Amount = amount;
            if (!string.IsNullOrEmpty(note))
                alloc.Note = note.Length > 300 ? note[..300] : note;
            touched.Add(docId);
        }
        await db.SaveChangesAsync(ct);
        foreach (int docId in touched)
            await RecomputeDocAsync(docType, docId, ct);
        await db.SaveChangesAsync(ct);
        return await FeedRowAsync(lineId, ct);
    }, ct);

    /// <summary>stale_allocations: assignments that no longer hold, with why (read only).</summary>
    public async Task<List<StaleRow>> StaleAllocationsAsync(List<(PaymentAllocation Allocation, string Reason)>? stale = null,
        CancellationToken ct = default)
    {
        stale ??= (await ClassifyAsync(await Allocations.ToListAsync(ct), ct: ct)).Stale;
        if (stale.Count == 0) return [];
        var voucherIds = stale.Select(s => s.Allocation.VoucherId).Distinct().ToList();
        var vouchers = await db.Set<AccountingVoucher>().AsNoTracking().Where(v => voucherIds.Contains(v.Id))
            .Select(v => new { v.Id, v.VoucherNumber, v.VoucherType, v.Status, v.VoucherDate }).ToDictionaryAsync(v => v.Id, ct);
        var docs = new Dictionary<(string, int), string>();
        foreach (var dt in new[] { Sales, Purchase })
        {
            var ids = stale.Where(s => s.Allocation.DocType == dt).Select(s => s.Allocation.DocId).Distinct().ToList();
            if (ids.Count == 0) continue;
            foreach (var d in await Docs(dt).Where(d => ids.Contains(d.Id)).Select(d => new { d.Id, d.Number }).ToListAsync(ct))
                docs[(dt, d.Id)] = d.Number;
        }
        var names = new Dictionary<(string, int), string>();
        foreach (var p in (await PartyAccountIndexAsync(ct)).Values)
            names[(p.Kind, p.Id)] = p.Name;
        var users = await UserNamesAsync(stale.Select(s => s.Allocation.CreatedBy), ct);
        return stale.Select(s =>
        {
            var a = s.Allocation;
            var v = vouchers.GetValueOrDefault(a.VoucherId);
            return new StaleRow
            {
                Id = a.Id,
                VoucherId = a.VoucherId,
                VoucherNumber = v?.VoucherNumber ?? "(deleted voucher)",
                VoucherType = v?.VoucherType ?? "",
                VoucherStatus = v is null ? "deleted" : v.Status ?? "",
                Date = v is not null ? v.VoucherDate : a.CreatedAt,
                LineId = a.VoucherLineId,
                DocType = a.DocType,
                DocId = a.DocId,
                DocNumber = a.DocType == NotApplicable ? "Not applicable to any invoice"
                    : docs.TryGetValue((a.DocType, a.DocId), out var num) ? num : $"#{a.DocId} (deleted)",
                PartyType = a.PartyType,
                PartyId = a.PartyId,
                PartyName = names.GetValueOrDefault((a.PartyType, a.PartyId), $"{a.PartyType} #{a.PartyId}"),
                Amount = Q(a.Amount),
                Reason = s.Reason,
                By = a.CreatedBy is int u ? users.GetValueOrDefault(u, "") : "",
            };
        }).OrderBy(r => r.Id).ToList();
    }

    /// <summary>clear_stale_allocations: delete broken assignments (all, or these ids), then refresh their invoices.</summary>
    public Task<int> ClearStaleAsync(IReadOnlyCollection<int>? allocIds = null, CancellationToken ct = default) => WriteAsync(async () =>
    {
        var stale = (await ClassifyAsync(await db.Set<PaymentAllocation>().ToListAsync(ct), ct: ct)).Stale;
        var rows = stale.Select(s => s.Allocation).ToList();
        if (allocIds is not null)
            rows = rows.Where(a => allocIds.Contains(a.Id)).ToList();
        if (rows.Count == 0) return 0;
        var touched = rows.Select(a => (a.DocType, a.DocId)).Distinct().ToList();
        db.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
        foreach (var (dt, id) in touched)
            if (dt is Sales or Purchase)
                await RecomputeDocAsync(dt, id, ct);
        return rows.Count;
    }, ct);

    /// <summary>reconcile: re-derive the cached columns of these invoices (or of every invoice an allocation points at).</summary>
    public Task<int> ReconcileAsync(string? docType = null, IReadOnlyCollection<int>? docIds = null, CancellationToken ct = default) =>
        WriteAsync(async () =>
        {
            var targets = new Dictionary<string, HashSet<int>> { [Sales] = [], [Purchase] = [] };
            if (docType is not null && docIds is not null)
                targets[docType] = docIds.ToHashSet();
            else
                foreach (var a in await Allocations.Select(a => new { a.DocType, a.DocId }).ToListAsync(ct))
                    if (targets.TryGetValue(a.DocType, out var set))
                        set.Add(a.DocId);
            int changed = 0;
            foreach (var (dt, ids) in targets)
                foreach (int id in ids)
                {
                    var inv = await RecomputeDocAsync(dt, id, ct);
                    if (inv is not null)
                    {
                        db.ChangeTracker.DetectChanges();
                        if (db.Entry(inv).State == EntityState.Modified) changed++;
                    }
                }
            return changed;
        }, ct);

    /// <summary>reconcile_voucher: refresh the invoices touched by one voucher's assignments.</summary>
    public async Task<int> ReconcileVoucherCountAsync(int voucherId, CancellationToken ct = default)
    {
        var allocs = await Allocations.Where(a => a.VoucherId == voucherId).Select(a => new { a.DocType, a.DocId }).ToListAsync(ct);
        if (allocs.Count == 0) return 0;
        int changed = 0;
        foreach (var dt in new[] { Sales, Purchase })
        {
            var ids = allocs.Where(a => a.DocType == dt).Select(a => a.DocId).ToHashSet();
            if (ids.Count > 0)
                changed += await ReconcileAsync(dt, ids, ct);
        }
        return changed;
    }

    public Task ReconcileVoucherAsync(int voucherId, CancellationToken ct = default) => ReconcileVoucherCountAsync(voucherId, ct);

    /// <summary>The accounting voucher delete's _drop_tracking_for_voucher: its assignments go, their invoices are refreshed.</summary>
    public async Task DropForVoucherAsync(int voucherId, CancellationToken ct = default)
    {
        var allocs = await db.Set<PaymentAllocation>().Where(a => a.VoucherId == voucherId).ToListAsync(ct);
        if (allocs.Count == 0) return;
        var touched = allocs.Select(a => (a.DocType, a.DocId)).Distinct().ToList();
        db.RemoveRange(allocs);
        await db.SaveChangesAsync(ct);
        foreach (var (dt, id) in touched)
            if (dt is Sales or Purchase)
                await RecomputeDocAsync(dt, id, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>sync_invoice: refresh one invoice's cache after the invoice itself changed.</summary>
    public async Task SyncInvoiceAsync(string docType, int docId, CancellationToken ct = default)
    {
        if (await RecomputeDocAsync(docType, docId, ct) is not null)
            await db.SaveChangesAsync(ct);
    }

    /// <summary>drop_allocations_for_doc: remove assignments pointing at an invoice about to be deleted.</summary>
    public async Task<int> DropForDocAsync(string docType, int docId, CancellationToken ct = default)
    {
        var allocs = await db.Set<PaymentAllocation>().Where(a => a.DocType == docType && a.DocId == docId).ToListAsync(ct);
        if (allocs.Count == 0) return 0;
        db.RemoveRange(allocs);
        await db.SaveChangesAsync(ct);
        return allocs.Count;
    }

    /// <summary>force_close: close a receipt / payment (or the rest of one) against no invoice, with a reason.</summary>
    public Task<FeedRow?> ForceCloseAsync(int lineId, object? amount, string? reason, int? userId, CancellationToken ct = default) =>
        WriteAsync(async () =>
        {
            await LockLineAsync(lineId, ct);
            var row = await FeedRowAsync(lineId, ct)
                      ?? throw new AllocationException("That payment is not an approved customer receipt or supplier payment, so it cannot be closed.");
            reason = (reason ?? "").Trim();
            if (reason.Length == 0)
                throw new AllocationException("Give a reason for closing this without an invoice.");
            decimal loose = Q(row.Unassigned);
            decimal value;
            if (amount is null or "")
                value = loose;
            else
                value = Q(TryDecimal(amount) ?? throw new AllocationException("The amount to close must be more than zero."));
            if (value <= 0)
                throw new AllocationException("The amount to close must be more than zero.");
            if (value > loose + Tolerance)
                throw new AllocationException($"Only {Comma2(loose)} of this {row.Flow} is still unassigned; {Comma2(value)} cannot be closed.");
            var alloc = await db.Set<PaymentAllocation>().OrderBy(a => a.Id)
                .FirstOrDefaultAsync(a => a.VoucherLineId == lineId && a.DocType == NotApplicable, ct);
            if (alloc is null)
            {
                alloc = new PaymentAllocation
                {
                    VoucherId = row.VoucherId, VoucherLineId = lineId, DocType = NotApplicable, DocId = NaDocId,
                    PartyType = row.PartyType, PartyId = row.PartyId, CreatedBy = userId, CreatedAt = clock.UtcNow, Note = "",
                };
                db.Add(alloc);
            }
            else
                value = Q(value + Q(alloc.Amount));
            alloc.Amount = value;
            alloc.Note = reason.Length > 300 ? reason[..300] : reason;
            await db.SaveChangesAsync(ct);
            return await FeedRowAsync(lineId, ct);
        }, ct);

    /// <summary>release_force: undo a forced close.</summary>
    public Task<FeedRow?> ReleaseForceAsync(int lineId, CancellationToken ct = default) => WriteAsync(async () =>
    {
        await LockLineAsync(lineId, ct);
        var alloc = await db.Set<PaymentAllocation>().OrderBy(a => a.Id)
                        .FirstOrDefaultAsync(a => a.VoucherLineId == lineId && a.DocType == NotApplicable, ct)
                    ?? throw new AllocationException("This payment has not been force-closed.");
        db.Remove(alloc);
        await db.SaveChangesAsync(ct);
        return await FeedRowAsync(lineId, ct);
    }, ct);

    /// <summary>unallocate: remove one assignment (locks its line first, as assigning does).</summary>
    public Task<(string DocType, int DocId, int LineId)> UnallocateAsync(int allocId, CancellationToken ct = default) => WriteAsync(async () =>
    {
        var first = await Allocations.FirstOrDefaultAsync(a => a.Id == allocId, ct)
                    ?? throw new AllocationException("That assignment no longer exists.");
        await LockLineAsync(first.VoucherLineId, ct);
        var alloc = await db.LockAsync<PaymentAllocation>(allocId, ct)
                    ?? throw new AllocationException("That assignment no longer exists.");
        var result = (alloc.DocType, alloc.DocId, alloc.VoucherLineId);
        db.Remove(alloc);
        await db.SaveChangesAsync(ct);
        if (result.DocType != NotApplicable)
            await RecomputeDocAsync(result.DocType, result.DocId, ct);
        return result;
    }, ct);

    /// <summary>auto_split: an oldest-first proposal for the whole line (never saved).</summary>
    public async Task<List<(int DocId, string Number, decimal Amount)>> AutoSplitAsync(int lineId, CancellationToken ct = default)
    {
        var row = await FeedRowAsync(lineId, ct);
        if (row is null) return [];
        decimal remaining = Q(Q(row.Amount) - Q(row.Forced));
        var output = new List<(int, string, decimal)>();
        foreach (var inv in await OpenInvoicesAsync(row.PartyType, row.PartyId, lineId, ct: ct))
        {
            decimal take = remaining > 0 ? Math.Min(remaining, Q(inv.Assignable)) : 0m;
            if (take > 0 || inv.AssignedHere > 0)
                output.Add((inv.Id, inv.Number, take));
            remaining = Q(remaining - take);
        }
        return output;
    }

    /// <summary>
    /// auto_match: every open receipt / payment (of one party, a side, or everyone) settles its party's invoices,
    /// oldest first. Queued per company; locks the lines, then the invoices. Existing splits, forced money,
    /// lines under review and ledger payers are left alone. Posts nothing.
    /// </summary>
    public Task<MatchResult> AutoMatchAsync(string? partyType = null, int? partyId = null, int? userId = null, CancellationToken ct = default) =>
        WriteAsync(async () =>
        {
            await db.SerializeAsync("payment-match", company.CompanyId);
            var filter = new FeedFilter { States = ["unassigned", "partial"], PartyType = partyType, PartyId = partyId };
            var ids = (await FeedRowsAsync(filter, ct: ct)).Where(r => !IsLedgerParty(r.PartyId)).Select(r => r.LineId).Distinct().Order().ToArray();
            if (ids.Length > 0 && db.Database.IsNpgsql())
            {
                int cid = company.CompanyId ?? 0;
                await db.Database.ExecuteSqlAsync(
                    $"SELECT 1 FROM accounting_voucher_lines WHERE id = ANY({ids}) AND company_id = {cid} ORDER BY id FOR NO KEY UPDATE", ct);
            }
            var rows = (await FeedRowsAsync(filter, ct: ct)).Where(r => !IsLedgerParty(r.PartyId))
                .OrderBy(r => DateOnly.FromDateTime(r.Date)).ThenBy(r => r.LineId).ToList();

            var queues = new Dictionary<(string Kind, int PartyId), List<(string DocType, int Id, decimal Left)>>();
            foreach (var (docType, kind) in new[] { (Sales, "customer"), (Purchase, "supplier") })
            {
                if (partyType is not null && kind != partyType) continue;
                var wanted = rows.Where(r => r.PartyType == kind).Select(r => r.PartyId).Distinct().ToList();
                if (wanted.Count == 0) continue;
                var invIds = await ApprovedDocs(docType).Where(d => wanted.Contains(d.PartyId))
                    .OrderBy(d => d.InvoiceDate).ThenBy(d => d.Id).Select(d => d.Id).ToListAsync(ct);
                await LockDocsAsync(docType, invIds, ct);
                var invs = await ApprovedDocs(docType).Where(d => wanted.Contains(d.PartyId))
                    .OrderBy(d => d.InvoiceDate).ThenBy(d => d.Id).ToListAsync(ct);
                var ids2 = invs.Select(i => i.Id).ToList();
                var settled = await AllocatedByDocAsync(docType, ids2, ct);
                var credited = await CreditedByDocAsync(docType, ids2, ct);
                foreach (var inv in invs)
                {
                    decimal left = Q(Q(inv.Total) - settled.GetValueOrDefault(inv.Id) - credited.GetValueOrDefault(inv.Id));
                    if (left > Tolerance)
                    {
                        var key = (kind, inv.PartyId);
                        (queues.TryGetValue(key, out var list) ? list : queues[key] = []).Add((docType, inv.Id, left));
                    }
                }
            }

            var lineIds = rows.Select(r => r.LineId).ToList();
            var existing = lineIds.Count == 0 ? []
                : await db.Set<PaymentAllocation>().Where(a => lineIds.Contains(a.VoucherLineId))
                    .ToDictionaryAsync(a => (a.VoucherLineId, a.DocType, a.DocId), ct);
            var used = new Dictionary<int, string>();
            var touched = new HashSet<(string DocType, int Id)>();
            decimal total = 0;
            foreach (var r in rows)
            {
                if (!queues.TryGetValue((r.PartyType, r.PartyId), out var queue)) continue;
                decimal loose = Q(r.Unassigned);
                while (queue.Count > 0 && loose > Tolerance)
                {
                    var (docType, invId, left) = queue[0];
                    decimal take = Math.Min(loose, left);
                    var key = (r.LineId, docType, invId);
                    if (!existing.TryGetValue(key, out var alloc))
                    {
                        alloc = new PaymentAllocation
                        {
                            VoucherId = r.VoucherId, VoucherLineId = r.LineId, DocType = docType, DocId = invId,
                            PartyType = r.PartyType, PartyId = r.PartyId, Amount = 0m, CreatedBy = userId, CreatedAt = clock.UtcNow,
                            Note = "Matched oldest first",
                        };
                        db.Add(alloc);
                        existing[key] = alloc;
                    }
                    alloc.Amount = Q(Q(alloc.Amount) + take);
                    loose = Q(loose - take);
                    total += take;
                    used[r.LineId] = r.Flow;
                    touched.Add((docType, invId));
                    left = Q(left - take);
                    if (left <= Tolerance)
                        queue.RemoveAt(0);
                    else
                        queue[0] = (docType, invId, left);
                }
            }
            await db.SaveChangesAsync(ct);

            // The invoices' cached columns, one pass per type.
            foreach (var docType in new[] { Sales, Purchase })
            {
                var ids3 = touched.Where(t => t.DocType == docType).Select(t => t.Id).ToList();
                if (ids3.Count == 0) continue;
                var settled = await AllocatedByDocAsync(docType, ids3, ct);
                var credited = await CreditedByDocAsync(docType, ids3, ct);
                if (docType == Sales)
                    foreach (var inv in await db.Set<InvInvoice>().Where(i => ids3.Contains(i.Id)).ToListAsync(ct))
                        SetCache(inv, settled.GetValueOrDefault(inv.Id), credited.GetValueOrDefault(inv.Id));
                else
                    foreach (var inv in await db.Set<InvPurchaseInvoice>().Where(i => ids3.Contains(i.Id)).ToListAsync(ct))
                        SetCache(inv, settled.GetValueOrDefault(inv.Id), credited.GetValueOrDefault(inv.Id));
            }
            var flows = used.Values.ToList();
            return new MatchResult(used.Count, flows.Count(f => f == "receipt"), flows.Count(f => f == "payment"), touched.Count, total);
        }, ct);
}

/// <summary>Python's str(float) for the few places a float is turned into a Decimal.</summary>
internal static class TrackingRulesPy
{
    public static string Str(double f)
    {
        string r = f.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        return r.Contains('.') || r.Contains('E') ? r : r + ".0";
    }
}
