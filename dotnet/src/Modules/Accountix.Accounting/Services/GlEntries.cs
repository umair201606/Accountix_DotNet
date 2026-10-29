namespace Accountix.Accounting.Services;

/// <summary>
/// The journals behind a document, for the "Accounting entry" panel
/// (shared/gl_entry.py): its own entries, related codes it also posts under,
/// and CADJ re-costings; un-posted earlier versions come back as history.
/// </summary>
public sealed class GlEntries(AccountixDbContext db)
{
    private static readonly IReadOnlyDictionary<string, string[]> Related = new Dictionary<string, string[]>
    {
        ["PI"] = ["PMT"], ["FA-ACQ"] = ["FA-DISP"], ["PRL"] = ["PRL-PAY"],
    };

    public static readonly IReadOnlyDictionary<string, string> Titles = new Dictionary<string, string>
    {
        ["SI"] = "Sales invoice", ["PI"] = "Purchase invoice", ["SR"] = "Sales return",
        ["PR"] = "Purchase return", ["PMT"] = "Payment", ["JV"] = "Journal voucher",
        ["CPV"] = "Cash payment", ["CRV"] = "Cash receipt", ["BPV"] = "Bank payment",
        ["BRV"] = "Bank receipt", ["CONS"] = "Consumption", ["SCRAP"] = "Scrap",
        ["ADJ"] = "Stock adjustment", ["FA-ACQ"] = "Asset acquisition",
        ["FA-DEP"] = "Depreciation", ["FA-DISP"] = "Asset disposal",
        ["FA-CAP"] = "Capitalisation", ["FA-TRF"] = "Transfer to inventory",
        ["PRL"] = "Payroll", ["PRL-PAY"] = "Salary payment", ["OPENING"] = "Opening stock",
        ["LOAN-DSB"] = "Staff loan paid out", ["PF-WD"] = "PF withdrawal", ["PF-LOAN"] = "PF loan",
        ["PF-SETTLE"] = "PF settlement", ["PF-PROFIT"] = "PF profit", ["PF-ADJ"] = "PF adjustment",
    };

    public sealed record Line(int account_id, string code, string name, string type, string description, string? label,
                              double debit, double credit);

    public sealed record Entry(int id, string voucher_type, string kind, string title, string number, string? date,
                               string description, bool posted, IReadOnlyList<Line> lines, double total_debit,
                               double total_credit, bool balanced);

    public async Task<(List<Entry> Posted, List<Entry> History)> ForAsync(string voucherType, int voucherId,
        IReadOnlyList<(string Type, int Id)> also, CancellationToken ct = default)
    {
        voucherType = voucherType.Trim().ToUpperInvariant();
        var related = Related.GetValueOrDefault(voucherType) ?? [];
        var codes = new[] { voucherType, $"CADJ-{voucherType}" }.Concat(related).ToList();
        var entries = await db.Set<JournalEntry>().AsNoTracking()
            .Where(e => codes.Contains(e.VoucherType) && e.VoucherId == voucherId)
            .ToListAsync(ct);
        foreach (var (t, i) in also)
            entries.AddRange(await db.Set<JournalEntry>().AsNoTracking().Where(e => e.VoucherType == t && e.VoucherId == i).ToListAsync(ct));
        entries = entries.DistinctBy(e => e.Id).OrderBy(e => e.EntryDate).ThenBy(e => e.Id).ToList();
        if (entries.Count == 0)
            return ([], []);
        var ids = entries.Select(e => e.Id).ToList();
        var lines = await db.Set<JournalLine>().AsNoTracking().Where(l => ids.Contains(l.JournalEntryId)).ToListAsync(ct);
        var accountIds = lines.Select(l => l.AccountId).Distinct().ToList();
        var accounts = await db.Set<ChartOfAccount>().AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var labelIds = lines.Where(l => l.LabelId is not null).Select(l => l.LabelId!.Value).Distinct().ToList();
        var labels = await db.Set<ProjectLabel>().AsNoTracking().Where(p => labelIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var extraTypes = also.Select(a => a.Type).ToHashSet();
        var byEntry = lines.GroupBy(l => l.JournalEntryId).ToDictionary(g => g.Key, g => g.ToList());
        List<Entry> posted = [], history = [];
        foreach (var e in entries)
        {
            var d = Describe(e, byEntry.GetValueOrDefault(e.Id) ?? [], accounts, labels);
            if (related.Contains(e.VoucherType) || extraTypes.Contains(e.VoucherType))
                d = d with { kind = "related" };
            (e.IsPosted == true ? posted : history).Add(d);
        }
        return (posted, history);
    }

    private static Entry Describe(JournalEntry e, List<JournalLine> entryLines, Dictionary<int, ChartOfAccount> accounts,
                                  Dictionary<int, string> labels)
    {
        decimal dr = 0, cr = 0;
        var lines = new List<Line>();
        // Debits first, then credits — the order an accountant reads a journal in.
        foreach (var ln in entryLines.OrderBy(l => (l.Credit ?? 0) > 0).ThenBy(l => l.Id))
        {
            var a = accounts.GetValueOrDefault(ln.AccountId);
            decimal d = ln.Debit ?? 0, c = ln.Credit ?? 0;
            dr += d;
            cr += c;
            lines.Add(new Line(ln.AccountId, a?.Code ?? "", a?.Name ?? $"Account #{ln.AccountId}", a?.Type ?? "",
                ln.Description ?? "", ln.LabelId is int lid ? labels.GetValueOrDefault(lid) : null, (double)d, (double)c));
        }
        string vtype = e.VoucherType ?? "";
        bool cadj = vtype.StartsWith("CADJ-");
        return new Entry(e.Id, vtype, cadj ? "cost_adjustment" : "main",
            cadj ? "Cost re-adjustment" : Titles.GetValueOrDefault(vtype, vtype), e.VoucherNumber,
            e.EntryDate.ToString("yyyy-MM-dd"), e.Description ?? "", e.IsPosted == true, lines,
            (double)dr, (double)cr, Math.Abs(dr - cr) < 0.005m);
    }
}
