using System.Globalization;

namespace Accountix.Parties.Services;

/// <summary>Who an account stands for: (party_type, party_id, party_name); negative ids are whole ledgers.</summary>
public sealed record PartyRef(string Kind, int Id, string Name);

/// <summary>A customer or supplier as the tracker needs it.</summary>
public sealed record PartyInfo(int Id, string Name, string? PaymentTerms);

/// <summary>A receipt or payment in the feed (_feed_dict).</summary>
public sealed class FeedRow
{
    public int LineId { get; init; }
    public int VoucherId { get; init; }
    public string VoucherNumber { get; init; } = "";
    public string VoucherType { get; init; } = "";
    public DateTime Date { get; init; }
    public string DateStr => TrackingRules.FmtDate(DateOnly.FromDateTime(Date));
    public string Flow { get; init; } = "";
    public string PartyType { get; init; } = "";
    public int PartyId { get; init; }
    public string PartyName { get; init; } = "";
    public string DocType => TrackingRules.DocForParty[PartyType];
    public string CashAccount { get; init; } = "";
    public string LedgerCode { get; init; } = "";
    public string LedgerName { get; init; } = "";
    public string Description { get; init; } = "";
    public string Notes { get; init; } = "";
    public decimal Amount { get; init; }
    public decimal Assigned { get; init; }
    public decimal Forced { get; init; }
    public string ForcedReason { get; init; } = "";
    public decimal ToInvoices => TrackingRules.Q(Assigned - Forced);
    public decimal Unassigned => Amount - Assigned > 0 ? TrackingRules.Q(Amount - Assigned) : 0m;
    public decimal OverAssigned => Amount - Assigned < 0 ? TrackingRules.Q(Assigned - Amount) : 0m;
    public double Progress => Amount > 0 ? (double)(Assigned / Amount * 100) : 0.0;
    public string State => TrackingRules.AssignState(Amount, Assigned, Forced);
    public string StateLabel => TrackingRules.AssignStateLabels.GetValueOrDefault(State, "");
    /// <summary>Set by the pages: a whole-ledger payer (no invoices to assign to).</summary>
    public bool IsLedger => TrackingRules.IsLedgerParty(PartyId);

    public Dictionary<string, object?> ToJson() => new()
    {
        ["line_id"] = LineId,
        ["voucher_id"] = VoucherId,
        ["voucher_number"] = VoucherNumber,
        ["voucher_type"] = VoucherType,
        ["date"] = TrackingJson.HttpDate(Date),
        ["date_str"] = DateStr,
        ["flow"] = Flow,
        ["party_type"] = PartyType,
        ["party_id"] = PartyId,
        ["party_name"] = PartyName,
        ["doc_type"] = DocType,
        ["cash_account"] = CashAccount,
        ["ledger_code"] = LedgerCode,
        ["ledger_name"] = LedgerName,
        ["description"] = Description,
        ["notes"] = Notes,
        ["amount"] = (double)Amount,
        ["assigned"] = (double)Assigned,
        ["to_invoices"] = (double)ToInvoices,
        ["forced"] = (double)Forced,
        ["forced_reason"] = ForcedReason,
        ["unassigned"] = (double)Unassigned,
        ["over_assigned"] = (double)OverAssigned,
        ["progress"] = Progress,
        ["state"] = State,
        ["state_label"] = StateLabel,
    };
}

/// <summary>One invoice's settlement (_doc_dict), plus the workspace's headroom fields.</summary>
public sealed class InvoiceRow
{
    public string DocType { get; init; } = "";
    public int Id { get; init; }
    public string Number { get; init; } = "";
    public string VoucherNumber { get; init; } = "";
    public DateOnly? Date { get; init; }
    public DateOnly? DueDate { get; init; }
    public string DateStr => TrackingRules.FmtDate(Date);
    public string DueStr => TrackingRules.FmtDate(DueDate);
    public string PartyType => TrackingRules.PartyForDoc[DocType];
    public int PartyId { get; init; }
    public string PartyName { get; init; } = "—";
    public decimal Total { get; init; }
    public decimal Settled { get; init; }
    public decimal Credited { get; init; }
    public decimal Outstanding => TrackingRules.Q(Total - Settled - Credited);
    public double Progress => Total > 0 ? (double)((Settled + Credited) / Total * 100) : 0.0;
    public string PayState => TrackingRules.PayState(Total, Settled + Credited);
    public int OverdueDays { get; init; }
    public string AgeBucket => TrackingRules.AgeBucket(OverdueDays);
    public string AgeLabel => TrackingRules.BucketLabel(AgeBucket);
    public bool IsOverdue => OverdueDays > 0;
    /// <summary>open_invoices only: what this line already put on the invoice.</summary>
    public decimal? AssignedHere { get; set; }
    /// <summary>open_invoices only: outstanding + assigned_here.</summary>
    public decimal? Assignable { get; set; }

    public Dictionary<string, object?> ToJson()
    {
        var d = new Dictionary<string, object?>
        {
            ["doc_type"] = DocType,
            ["id"] = Id,
            ["number"] = Number,
            ["voucher_number"] = VoucherNumber,
            ["date"] = TrackingJson.HttpDate(Date),
            ["due_date"] = TrackingJson.HttpDate(DueDate),
            ["date_str"] = DateStr,
            ["due_str"] = DueStr,
            ["party_type"] = PartyType,
            ["party_id"] = PartyId,
            ["party_name"] = PartyName,
            ["total"] = (double)Total,
            ["settled"] = (double)Settled,
            ["credited"] = (double)Credited,
            ["outstanding"] = (double)Outstanding,
            ["progress"] = Progress,
            ["pay_state"] = PayState,
            ["overdue_days"] = OverdueDays,
            ["age_bucket"] = AgeBucket,
            ["age_label"] = AgeLabel,
            ["is_overdue"] = IsOverdue,
        };
        if (AssignedHere is decimal a) d["assigned_here"] = (double)a;
        if (Assignable is decimal b) d["assignable"] = (double)b;
        return d;
    }
}

/// <summary>An entry of an invoice's settlement history: money assigned, or a return (history_for_doc).</summary>
public sealed class HistoryEvent
{
    public string Kind { get; init; } = "money";
    public int Id { get; init; }
    public int VoucherId { get; init; }
    public int LineId { get; init; }
    public string VoucherNumber { get; init; } = "";
    public string VoucherType { get; init; } = "";
    public DateTime? Date { get; init; }
    public string CashAccount { get; init; } = "";
    public string Description { get; init; } = "";
    public decimal Amount { get; init; }
    public bool Counts { get; init; }
    public string Note { get; init; } = "";
    public string By { get; init; } = "";
    public DateTime? CreatedAt { get; init; }
    public decimal BalanceAfter { get; set; }
}

/// <summary>One invoice a receipt / payment was split across (history_for_line).</summary>
public sealed class SplitRow
{
    public int Id { get; init; }
    public string DocType { get; init; } = "";
    public int DocId { get; init; }
    public bool Forced { get; init; }
    public string Number { get; init; } = "";
    public string PartyName { get; init; } = "";
    public decimal Total { get; init; }
    public decimal Outstanding { get; init; }
    public decimal Amount { get; init; }
    public string Note { get; init; } = "";
    public string By { get; init; } = "";
    public DateTime? CreatedAt { get; init; }

    public Dictionary<string, object?> ToJson() => new()
    {
        ["id"] = Id,
        ["doc_type"] = DocType,
        ["doc_id"] = DocId,
        ["forced"] = Forced,
        ["number"] = Number,
        ["party_name"] = PartyName,
        ["total"] = (double)Total,
        ["outstanding"] = (double)Outstanding,
        ["amount"] = (double)Amount,
        ["note"] = Note,
        ["by"] = By,
        ["created_at"] = TrackingJson.HttpDate(CreatedAt),
    };
}

/// <summary>An assignment that no longer holds, and why (stale_allocations).</summary>
public sealed class StaleRow
{
    public int Id { get; init; }
    public int VoucherId { get; init; }
    public string VoucherNumber { get; init; } = "";
    public string VoucherType { get; init; } = "";
    public string VoucherStatus { get; init; } = "";
    public DateTime? Date { get; init; }
    public int LineId { get; init; }
    public string DocType { get; init; } = "";
    public int DocId { get; init; }
    public string DocNumber { get; init; } = "";
    public string PartyType { get; init; } = "";
    public int PartyId { get; init; }
    public string PartyName { get; init; } = "";
    public decimal Amount { get; init; }
    public string Reason { get; init; } = "";
    public string By { get; init; } = "";
}

/// <summary>The feed's tiles (feed_summary).</summary>
public sealed class FeedSummary
{
    public int ReceiptsOpen { get; set; }
    public decimal ReceiptsOpenValue { get; set; }
    public int PaymentsOpen { get; set; }
    public decimal PaymentsOpenValue { get; set; }
    public Dictionary<string, int> Counts { get; } = TrackingRules.AssignStates.ToDictionary(s => s, _ => 0);
    public decimal ForcedValue { get; set; }
    public decimal ReviewValue { get; set; }
    public decimal UnassignedValue { get; set; }

    public int Count(string state) => Counts.GetValueOrDefault(state);

    public static FeedSummary Of(IEnumerable<FeedRow> rows)
    {
        var s = new FeedSummary();
        foreach (var r in rows)
        {
            string state = r.State;
            s.Counts[state] = s.Counts.GetValueOrDefault(state) + 1;
            s.ForcedValue += r.Forced;
            s.ReviewValue += r.OverAssigned;
            if (state is not ("assigned" or "forced"))
            {
                s.UnassignedValue += r.Unassigned;
                if (r.Flow == "receipt")
                {
                    s.ReceiptsOpen++;
                    s.ReceiptsOpenValue += r.Unassigned;
                }
                else
                {
                    s.PaymentsOpen++;
                    s.PaymentsOpenValue += r.Unassigned;
                }
            }
        }
        return s;
    }
}

/// <summary>The tracking pages' headline figures (outstanding_summary).</summary>
public sealed class OutstandingSummary
{
    public int Count { get; init; }
    public int OpenCount { get; init; }
    public decimal Outstanding { get; init; }
    public decimal Overdue { get; init; }
    public int OverdueCount { get; init; }
    public Dictionary<string, decimal> Buckets { get; init; } = new();

    public static OutstandingSummary Of(IReadOnlyList<InvoiceRow> rows)
    {
        var buckets = TrackingRules.AgeBuckets.ToDictionary(b => b.Key, _ => 0m);
        foreach (var r in rows)
            if (r.Outstanding > 0)
                buckets[r.AgeBucket] += r.Outstanding;
        return new OutstandingSummary
        {
            Count = rows.Count,
            OpenCount = rows.Count(r => r.PayState != "paid"),
            Outstanding = rows.Sum(r => r.Outstanding),
            Overdue = rows.Where(r => r.IsOverdue).Sum(r => r.Outstanding),
            OverdueCount = rows.Count(r => r.IsOverdue),
            Buckets = buckets,
        };
    }
}

/// <summary>What auto_match did.</summary>
public sealed record MatchResult(int Lines, int Receipts, int Payments, int Invoices, decimal Amount);

/// <summary>The feed's filters (feed_rows / filter_feed arguments).</summary>
public sealed record FeedFilter
{
    public IReadOnlyCollection<string>? States { get; init; }
    public string? Flow { get; init; }
    public string? PartyType { get; init; }
    public int? PartyId { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public string? Search { get; init; }
}

/// <summary>The tracking registers' filters (invoice_rows / filter_invoices arguments).</summary>
public sealed record InvoiceFilter
{
    public int? PartyId { get; init; }
    public IReadOnlyCollection<string>? PayStates { get; init; }
    public IReadOnlyCollection<string>? AgeBuckets { get; init; }
    public bool OverdueOnly { get; init; }
    public string? Search { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
}

/// <summary>Flask's JSON rendering of dates (http_date): "Wed, 29 Oct 2026 00:00:00 GMT".</summary>
public static class TrackingJson
{
    public static string? HttpDate(DateTime? value) =>
        value?.ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", CultureInfo.InvariantCulture);

    public static string? HttpDate(DateOnly? value) => HttpDate(value?.ToDateTime(TimeOnly.MinValue));
}
