using System.Globalization;

namespace Accountix.Executive.Services;

// shared/executive_reports.py — receivables and payables, derived from the ledger.
//
// The whole module is a READ of the general ledger: every figure is recomputed
// from posted activity (the reporting table gl_daily_balances) on each request,
// and the only thing it stores is the list of accounts to watch. Which side a
// party appears on is decided by the sign of its balance, not the account type.

/// <summary>One account of the chart, held in memory for the request.</summary>
public sealed record ChartNode(int Id, string Code, string Name, string Type, int? ParentId, int Level, string? PlSection);

/// <summary>A register row (party_rows).</summary>
public sealed record PartyRow(int AccountId, string Code, string Name, string Type, int? GroupId, string Group,
                              decimal Debit, decimal Credit, decimal Amount, string Side);

public sealed record SideTotals(decimal Amount, int Parties);

public sealed record GroupOption(int Id, string Label);

/// <summary>A posting behind a balance (account_ledger).</summary>
public sealed record LedgerLine(DateTime Date, string VoucherType, string VoucherNumber, string Description, decimal Debit, decimal Credit);

/// <summary>A row of the settings tree (selectable_accounts).</summary>
public sealed record SelectableAccount(int Id, string Code, string Name, string Type, int Level, int? ParentId,
                                       bool Selected, bool IncludeChildren, bool HasChildren);

/// <summary>One FIFO-aged open balance (_age_party): what survives on the stack, and how old it is.</summary>
public sealed class AgedParty
{
    public int AccountId { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public decimal Amount { get; init; }
    public double AvgDays { get; init; }
    public int OldestDays { get; init; }
    public List<(int Days, decimal Amount)> Layers { get; init; } = [];
}

public sealed class AgingBucket
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public decimal Amount { get; init; }
    /// <summary>Percentage of its own side.</summary>
    public decimal Share { get; init; }
    /// <summary>Bar length against the largest bucket on either side (_scale_buckets).</summary>
    public decimal Width { get; set; }
}

public sealed record TopParty(string Name, string Code, int AccountId, decimal Amount, decimal Share);

/// <summary>One side of the aging dashboard (_aging_side).</summary>
public sealed class AgingSide
{
    public decimal Total { get; init; }
    public int Parties { get; init; }
    public double AvgDays { get; init; }
    public int OldestDays { get; init; }
    public string OldestName { get; init; } = "";
    public string OldestCode { get; init; } = "";
    public int? OldestAccountId { get; init; }
    public List<AgingBucket> Buckets { get; init; } = [];
    public List<TopParty> Top { get; init; } = [];
    public decimal Peak { get; set; }
}

/// <summary>party_aging(): one party's buckets for the register exports.</summary>
public sealed record PartyAge(IReadOnlyDictionary<string, double> Buckets, double AvgDays, int OldestDays);

/// <summary>liquidity(): net assets and the current / quick ratios.</summary>
public sealed class Liquidity
{
    public decimal? NetAssets { get; init; }
    public decimal TotalAssets { get; init; }
    public decimal TotalLiabilities { get; init; }
    public decimal CurrentAssets { get; init; }
    public decimal CurrentLiabilities { get; init; }
    public decimal Inventory { get; init; }
    public decimal? CurrentRatio { get; init; }
    public decimal? QuickRatio { get; init; }
}

public sealed record ProfitMonth(DateOnly Start, string Label, string FullLabel, decimal Revenue, decimal Cogs, decimal Gross, decimal Net)
{
    public decimal this[string key] => key switch
    {
        "revenue" => Revenue, "cogs" => Cogs, "gross" => Gross, "net" => Net,
        _ => throw new KeyNotFoundException(key),
    };
}

/// <summary>profitability(): monthly revenue / gross / net profit, the latest month and its year-on-year deltas.</summary>
public sealed class Profitability
{
    public List<ProfitMonth> Months { get; init; } = [];
    public Dictionary<string, decimal> Totals { get; init; } = [];
    public ProfitMonth Latest { get; init; } = null!;
    public ProfitMonth? Prior { get; init; }
    public Dictionary<string, decimal?> Deltas { get; init; } = [];
    public double Peak { get; init; }
    public double Trough { get; init; }
    public decimal? Margin { get; init; }
}

public sealed class ExecutiveReports(AccountixDbContext db, CompanyContext company, CompanyClock clock)
{
    public const string Receivable = "receivable";
    public const string Payable = "payable";
    private const decimal Tolerance = 0.01m;

    public sealed record SideMeta(string Title, string Noun, string PartyWord, string Empty);

    public static readonly IReadOnlyDictionary<string, SideMeta> Meta = new Dictionary<string, SideMeta>
    {
        [Receivable] = new("Receivables", "receivable", "owes us", "No selected account is carrying a debit balance."),
        [Payable] = new("Payables", "payable", "we owe", "No selected account is carrying a credit balance."),
    };

    /// <summary>BUCKET_DEFS: (key, label, min days, max days — null = unbounded).</summary>
    public static readonly IReadOnlyList<(string Key, string Label, int Lo, int? Hi)> BucketDefs =
    [
        ("current", "Current", 0, 30),
        ("d31", "31–60 days", 31, 60),
        ("d61", "61–90 days", 61, 90),
        ("d91", "90+ days", 91, null),
    ];

    public AccountixDbContext Db => db;

    // ── per-request reads ────────────────────────────────────────────────

    private List<ChartNode>? _chart;
    private Dictionary<int, ChartNode>? _byId;
    private Dictionary<int, List<ChartNode>>? _kids;
    private List<(ChartNode Account, bool IncludeChildren)>? _picks;

    /// <summary>The whole chart, in code order (one query per request).</summary>
    public async Task<List<ChartNode>> ChartAsync(CancellationToken ct = default)
    {
        if (_chart is not null) return _chart;
        _chart = await db.Set<ChartOfAccount>().AsNoTracking().OrderBy(a => a.Code)
            .Select(a => new ChartNode(a.Id, a.Code, a.Name, a.Type, a.ParentId, a.Level, a.PlSection))
            .ToListAsync(ct);
        _byId = new Dictionary<int, ChartNode>(_chart.Count);
        foreach (var a in _chart) _byId.TryAdd(a.Id, a);
        _kids = new Dictionary<int, List<ChartNode>>();
        foreach (var a in _chart)
            if (a.ParentId is int p)
                (_kids.TryGetValue(p, out var l) ? l : _kids[p] = []).Add(a);
        return _chart;
    }

    private async Task<Dictionary<int, ChartNode>> ByIdAsync(CancellationToken ct)
    {
        await ChartAsync(ct);
        return _byId!;
    }

    private async Task<Dictionary<int, List<ChartNode>>> KidsAsync(CancellationToken ct)
    {
        await ChartAsync(ct);
        return _kids!;
    }

    /// <summary>selections(): the raw picks joined to the chart, in chart code order.</summary>
    public async Task<List<(ChartNode Account, bool IncludeChildren)>> SelectionsAsync(CancellationToken ct = default)
    {
        if (_picks is not null) return _picks;
        var byId = await ByIdAsync(ct);
        var rows = await db.Set<ExecAccountSelection>().AsNoTracking()
            .Join(db.Set<ChartOfAccount>(), s => s.AccountId, a => a.Id, (s, a) => new { s.AccountId, s.IncludeChildren, a.Code, s.Id })
            .OrderBy(x => x.Code).ThenBy(x => x.Id)
            .Select(x => new { x.AccountId, x.IncludeChildren })
            .ToListAsync(ct);
        _picks = rows.Where(r => byId.ContainsKey(r.AccountId)).Select(r => (byId[r.AccountId], r.IncludeChildren)).ToList();
        return _picks;
    }

    /// <summary>scope(as_dict=True): every account in scope → (picked root id, "code · name").</summary>
    public async Task<Dictionary<int, (int RootId, string RootLabel)>> ScopeAsync(CancellationToken ct = default)
    {
        var picks = await SelectionsAsync(ct);
        var output = new Dictionary<int, (int, string)>();
        if (picks.Count == 0) return output;
        var kids = await KidsAsync(ct);
        foreach (var (root, deep) in picks)
        {
            string label = $"{root.Code} · {root.Name}";
            var stack = new Stack<ChartNode>();
            stack.Push(root);
            bool first = true;
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                // The pick itself is always in scope; descendants only when the selection says so.
                if (first || deep)
                    output[node.Id] = (root.Id, label);
                if (deep && kids.TryGetValue(node.Id, out var children))
                    foreach (var c in children) stack.Push(c);
                first = false;
            }
        }
        return output;
    }

    /// <summary>_balances: {account: (debit, credit)} of posted activity, each quantized, from gl_daily_balances.</summary>
    public async Task<Dictionary<int, (decimal Dr, decimal Cr)>> BalancesAsync(IReadOnlyCollection<int> accountIds, DateOnly? asOf,
                                                                              CancellationToken ct = default)
    {
        if (accountIds.Count == 0) return [];
        var ids = accountIds.ToArray();
        var q = db.Set<GlDailyBalance>().AsNoTracking().Where(g => ids.Contains(g.AccountId));
        if (asOf is DateOnly d) q = q.Where(g => g.Day <= d);
        var rows = await q.GroupBy(g => g.AccountId)
            .Select(g => new { g.Key, Dr = g.Sum(x => x.Debit), Cr = g.Sum(x => x.Credit) })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Key, r => (Py.Q(r.Dr), Py.Q(r.Cr)));
    }

    // ── the two registers ────────────────────────────────────────────────

    /// <summary>party_rows: one row per account in scope carrying a balance, largest first.</summary>
    public async Task<List<PartyRow>> PartyRowsAsync(string? side = null, DateOnly? asOf = null, string? search = null,
                                                     int? groupId = null, CancellationToken ct = default)
    {
        var inScope = await ScopeAsync(ct);
        if (inScope.Count == 0) return [];
        var balances = await BalancesAsync(inScope.Keys, asOf, ct);
        if (balances.Count == 0) return [];
        var byId = await ByIdAsync(ct);
        string term = (search ?? "").Trim().ToLowerInvariant();
        var rows = new List<PartyRow>();
        foreach (var (accountId, (dr, cr)) in balances.OrderBy(b => b.Key))
        {
            if (!byId.TryGetValue(accountId, out var acct)) continue;
            decimal net = Py.Q(dr - cr);
            if (Math.Abs(net) < Tolerance) continue;
            // The sign decides the side, not the account's type.
            string rowSide = net > 0 ? Receivable : Payable;
            if (!string.IsNullOrEmpty(side) && rowSide != side) continue;
            var (rootId, rootLabel) = inScope[accountId];
            if (groupId is int gid && gid != 0 && rootId != gid) continue;
            if (term.Length > 0 && !acct.Name.ToLowerInvariant().Contains(term) && !acct.Code.ToLowerInvariant().Contains(term))
                continue;
            rows.Add(new PartyRow(accountId, acct.Code, acct.Name, acct.Type, rootId, rootLabel, dr, cr, Math.Abs(net), rowSide));
        }
        return rows.OrderByDescending(r => r.Amount).ToList();
    }

    public static SideTotals Totals(IReadOnlyCollection<PartyRow> rows) =>
        new(rows.Count > 0 ? Py.Q(rows.Sum(r => r.Amount)) : 0m, rows.Count);

    /// <summary>both_sides: totals for each side in one pass.</summary>
    public async Task<(SideTotals Receivable, SideTotals Payable)> BothSidesAsync(DateOnly? asOf, CancellationToken ct = default)
    {
        var rows = await PartyRowsAsync(asOf: asOf, ct: ct);
        return (Totals(rows.Where(r => r.Side == Receivable).ToList()), Totals(rows.Where(r => r.Side == Payable).ToList()));
    }

    /// <summary>groups(): the picked control accounts, for the register's group filter.</summary>
    public async Task<List<GroupOption>> GroupsAsync(CancellationToken ct = default) =>
        (await SelectionsAsync(ct)).Select(p => new GroupOption(p.Account.Id, $"{p.Account.Code} · {p.Account.Name}")).ToList();

    /// <summary>account_ledger: the postings behind one balance, newest first (limit null = all).</summary>
    public async Task<List<LedgerLine>> AccountLedgerAsync(int accountId, DateOnly? asOf, int? limit = 200, CancellationToken ct = default)
    {
        var q = db.Set<JournalLine>().AsNoTracking()
            .Where(l => l.AccountId == accountId && l.JournalEntry.IsPosted == true);
        if (asOf is DateOnly d)
        {
            var end = d.AddDays(1).ToDateTime(TimeOnly.MinValue);       // _eod: the whole as-of day
            q = q.Where(l => l.JournalEntry.EntryDate < end);
        }
        var ordered = q.OrderByDescending(l => l.JournalEntry.EntryDate).ThenByDescending(l => l.JournalEntryId).ThenByDescending(l => l.Id)
            .Select(l => new
            {
                l.JournalEntry.EntryDate, l.JournalEntry.VoucherType, l.JournalEntry.VoucherNumber,
                LineDesc = l.Description, EntryDesc = l.JournalEntry.Description, l.Debit, l.Credit,
            });
        var rows = limit is int n ? await ordered.Take(n).ToListAsync(ct) : await ordered.ToListAsync(ct);
        return rows.Select(r => new LedgerLine(r.EntryDate, r.VoucherType, r.VoucherNumber,
                                               !string.IsNullOrEmpty(r.LineDesc) ? r.LineDesc : r.EntryDesc ?? "",
                                               Py.Q(r.Debit ?? 0), Py.Q(r.Credit ?? 0))).ToList();
    }

    // ── settings ─────────────────────────────────────────────────────────

    /// <summary>selectable_accounts(): the whole chart for the tree picker, with selection state.</summary>
    public async Task<List<SelectableAccount>> SelectableAccountsAsync(CancellationToken ct = default)
    {
        var chart = await ChartAsync(ct);
        var picked = new Dictionary<int, bool>();
        foreach (var s in await db.Set<ExecAccountSelection>().AsNoTracking().OrderBy(s => s.Id)
                     .Select(s => new { s.AccountId, s.IncludeChildren }).ToListAsync(ct))
            picked[s.AccountId] = s.IncludeChildren;
        var parents = chart.Where(a => a.ParentId is not null).Select(a => a.ParentId!.Value).ToHashSet();
        return chart.Select(a => new SelectableAccount(
            a.Id, a.Code, a.Name, a.Type, a.Level != 0 ? a.Level : 5, a.ParentId,
            picked.ContainsKey(a.Id), !picked.TryGetValue(a.Id, out bool deep) || deep, parents.Contains(a.Id))).ToList();
    }

    /// <summary>
    /// set_selection: replace the whole selection (the screen posts the full picture).
    /// Non-numeric ids are ignored; ids outside this company's chart are ignored too.
    /// </summary>
    public async Task<int> SetSelectionAsync(IEnumerable<string?> accountIds, IEnumerable<string?> includeChildrenIds, int? userId,
                                             DateTime utcNow, CancellationToken ct = default)
    {
        static HashSet<int> Ids(IEnumerable<string?> values)
        {
            var output = new HashSet<int>();
            foreach (var v in values)
                if (int.TryParse((v ?? "").Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int i))
                    output.Add(i);
            return output;
        }
        var wanted = Ids(accountIds);
        var deep = Ids(includeChildrenIds);
        int count = wanted.Count;
        var own = (await ByIdAsync(ct)).Keys.ToHashSet();
        wanted.IntersectWith(own);

        await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        // Two admins saving at once queue here instead of colliding on the unique key.
        await db.SerializeAsync("exec_selection", company.CompanyId);
        var existing = await db.Set<ExecAccountSelection>().ToListAsync(ct);
        var have = new HashSet<int>();
        foreach (var row in existing)
        {
            if (!wanted.Contains(row.AccountId) || !have.Add(row.AccountId))
                db.Remove(row);
            else
                row.IncludeChildren = deep.Contains(row.AccountId);
        }
        foreach (int id in wanted.Where(w => !have.Contains(w)).Order())
            db.Add(new ExecAccountSelection { AccountId = id, IncludeChildren = deep.Contains(id), CreatedBy = userId, CreatedAt = utcNow });
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        _picks = null;
        return count;
    }

    // ── aging ────────────────────────────────────────────────────────────

    /// <summary>The day ages are measured to: the as-of date, else the company's today (shared/clock.local_today).</summary>
    public DateOnly AgeDay(DateOnly? asOf) => asOf ?? clock.Today;

    /// <summary>_age_party: FIFO-age one account's daily postings; null when nothing survives.</summary>
    public static AgedParty? AgeParty(int accountId, ChartNode acct, IReadOnlyList<(DateOnly Day, decimal Dr, decimal Cr)> rows,
                                      decimal balance, DateOnly asOfDay, string side)
    {
        var layers = new List<(DateOnly Day, decimal Amount)>();   // oldest first
        // A settlement that arrives before anything it could settle (an advance) is held and applied to
        // the next layers as they open, so the layers never sum to more than the balance.
        decimal unapplied = 0m;
        bool recv = side == Receivable;
        foreach (var (day, dr, cr) in rows)
        {
            decimal add = Py.Q(recv ? dr : cr);
            if (add != 0 && unapplied != 0)
            {
                decimal used = Math.Min(add, unapplied);
                add = Py.Q(add - used);
                unapplied = Py.Q(unapplied - used);
            }
            if (add != 0) layers.Add((day, add));
            decimal? take = Py.Q(recv ? cr : dr);
            while (take is decimal t && t != 0 && layers.Count > 0)
            {
                if (layers[0].Amount <= t)
                {
                    take = Py.Q(t - layers[0].Amount);
                    layers.RemoveAt(0);
                }
                else
                {
                    layers[0] = (layers[0].Day, Py.Q(layers[0].Amount - t));
                    take = null;
                }
            }
            if (take is decimal left && left != 0)
                unapplied = Py.Q(unapplied + left);
        }
        if (layers.Count == 0) return null;

        decimal total = Py.Q(balance);
        var ages = layers.Select(l => (Days: Math.Max(asOfDay.DayNumber - l.Day.DayNumber, 0), l.Amount)).ToList();
        double weighted = 0;
        foreach (var (d, a) in ages) weighted += d * Py.ToDouble(a);
        return new AgedParty
        {
            AccountId = accountId, Code = acct.Code, Name = acct.Name, Amount = total,
            AvgDays = weighted / Py.ToDouble(total), OldestDays = ages.Max(a => a.Days), Layers = ages,
        };
    }

    /// <summary>_aged_parties: FIFO-age every open in-scope account from ONE grouped read of the reporting table.</summary>
    public async Task<(List<AgedParty> Receivable, List<AgedParty> Payable)> AgedPartiesAsync(
        IReadOnlyDictionary<int, (int RootId, string RootLabel)> inScope, DateOnly? asOf, CancellationToken ct = default)
    {
        var asOfDay = AgeDay(asOf);
        var ids = inScope.Keys.ToArray();
        // Daily totals are exactly what FIFO aging needs: within one day every layer is the same age.
        var q = db.Set<GlDailyBalance>().AsNoTracking().Where(g => ids.Contains(g.AccountId));
        if (asOf is DateOnly d) q = q.Where(g => g.Day <= d);
        var daily = await q.GroupBy(g => new { g.AccountId, g.Day })
            .Select(g => new { g.Key.AccountId, g.Key.Day, Dr = g.Sum(x => x.Debit), Cr = g.Sum(x => x.Credit) })
            .OrderBy(x => x.Day).ThenBy(x => x.AccountId)
            .ToListAsync(ct);
        var postings = new Dictionary<int, List<(DateOnly, decimal, decimal)>>();
        var order = new List<int>();
        foreach (var r in daily)
        {
            if (!postings.TryGetValue(r.AccountId, out var list))
            {
                postings[r.AccountId] = list = [];
                order.Add(r.AccountId);
            }
            list.Add((r.Day, Py.Q(r.Dr), Py.Q(r.Cr)));
        }
        var byId = await ByIdAsync(ct);
        List<AgedParty> receivable = [], payable = [];
        foreach (int accountId in order)
        {
            var rows = postings[accountId];
            decimal net = Py.Q(rows.Sum(r => r.Item2 - r.Item3));
            if (Math.Abs(net) < Tolerance) continue;
            if (!byId.TryGetValue(accountId, out var acct)) continue;
            string side = net > 0 ? Receivable : Payable;
            var party = AgeParty(accountId, acct, rows, Math.Abs(net), asOfDay, side);
            if (party is not null)
                (side == Receivable ? receivable : payable).Add(party);
        }
        return (receivable, payable);
    }

    /// <summary>party_aging: {account: buckets, avg_days, oldest_days} for every open in-scope balance.</summary>
    public async Task<Dictionary<int, PartyAge>> PartyAgingAsync(DateOnly? asOf, CancellationToken ct = default)
    {
        var inScope = await ScopeAsync(ct);
        if (inScope.Count == 0) return [];
        var (recv, pay) = await AgedPartiesAsync(inScope, asOf, ct);
        var output = new Dictionary<int, PartyAge>();
        foreach (var party in recv.Concat(pay))
        {
            var buckets = BucketDefs.ToDictionary(b => b.Key, _ => 0.0);
            foreach (var (days, amount) in party.Layers)
                foreach (var (key, _, lo, hi) in BucketDefs)
                    if (days >= lo && (hi is null || days <= hi))
                    {
                        buckets[key] += Py.ToDouble(amount);
                        break;
                    }
            output[party.AccountId] = new PartyAge(buckets, party.AvgDays, party.OldestDays);
        }
        return output;
    }

    private static decimal Share(decimal part, decimal total) => total != 0 ? Py.Q(part / total * 100m) : 0m;

    /// <summary>_aging_side: collapse the per-party stacks into one side's dashboard view.</summary>
    public static AgingSide AgingSideOf(IReadOnlyList<AgedParty> parties)
    {
        decimal total = parties.Count > 0 ? Py.Q(parties.Sum(p => p.Amount)) : 0m;
        var buckets = new List<AgingBucket>();
        foreach (var (key, label, lo, hi) in BucketDefs)
        {
            decimal amount = 0m;
            foreach (var p in parties)
                foreach (var (days, a) in p.Layers)
                    if (lo <= days && (hi is null || days <= hi))
                        amount += a;
            buckets.Add(new AgingBucket { Key = key, Label = label, Amount = Py.Q(amount), Share = Share(amount, total) });
        }
        double avg = 0.0;
        if (total != 0)
        {
            double sum = 0;
            foreach (var p in parties) sum += p.AvgDays * Py.ToDouble(p.Amount);
            avg = sum / Py.ToDouble(total);
        }
        var top = parties.OrderByDescending(p => p.Amount).Take(5).ToList();
        AgedParty? oldest = null;
        foreach (var p in parties)
            if (oldest is null || p.OldestDays > oldest.OldestDays)
                oldest = p;
        return new AgingSide
        {
            Total = total, Parties = parties.Count, AvgDays = avg,
            OldestDays = oldest?.OldestDays ?? 0, OldestName = oldest?.Name ?? "", OldestCode = oldest?.Code ?? "",
            OldestAccountId = oldest?.AccountId, Buckets = buckets,
            Top = top.Select(p => new TopParty(p.Name, p.Code, p.AccountId, p.Amount, Share(p.Amount, total))).ToList(),
        };
    }

    /// <summary>_scale_buckets: every bar measured against the largest bucket on either side.</summary>
    public static void ScaleBuckets(AgingSide recv, AgingSide pay)
    {
        decimal peak = recv.Buckets.Concat(pay.Buckets).Select(b => b.Amount).DefaultIfEmpty(0m).Max();
        foreach (var side in new[] { recv, pay })
        {
            foreach (var b in side.Buckets)
                b.Width = peak != 0 ? Py.Q(b.Amount / peak * 100m) : 0m;
            side.Peak = peak;
        }
    }

    /// <summary>aging(): every open balance in scope, receivable against payable.</summary>
    public async Task<(AgingSide Receivable, AgingSide Payable)> AgingAsync(DateOnly? asOf, CancellationToken ct = default)
    {
        var inScope = await ScopeAsync(ct);
        AgingSide recv, pay;
        if (inScope.Count == 0)
        {
            recv = AgingSideOf([]);
            pay = AgingSideOf([]);
        }
        else
        {
            var (r, p) = await AgedPartiesAsync(inScope, asOf, ct);
            recv = AgingSideOf(r);
            pay = AgingSideOf(p);
        }
        ScaleBuckets(recv, pay);
        return (recv, pay);
    }

    // ── liquidity ────────────────────────────────────────────────────────

    private static List<int> Subtree(Dictionary<int, List<ChartNode>> kids, int id)
    {
        var output = new List<int>();
        var stack = new Stack<int>();
        stack.Push(id);
        while (stack.Count > 0)
        {
            int n = stack.Pop();
            output.Add(n);
            if (kids.TryGetValue(n, out var children))
                foreach (var c in children) stack.Push(c.Id);
        }
        return output;
    }

    /// <summary>liquidity(): net assets plus current / quick ratios, from the whole chart.</summary>
    public async Task<Liquidity> LiquidityAsync(DateOnly? asOf, CancellationToken ct = default)
    {
        var chart = await ChartAsync(ct);
        var assetsRoot = chart.Where(a => a.Type == "asset" && a.Level == 1).MinBy(a => a.Id);
        var liabRoot = chart.Where(a => a.Type == "liability" && a.Level == 1).MinBy(a => a.Id);
        if (assetsRoot is null || liabRoot is null)
            return new Liquidity();

        var kids = await KidsAsync(ct);
        var byId = await ByIdAsync(ct);
        var allIds = Subtree(kids, assetsRoot.Id).Concat(Subtree(kids, liabRoot.Id)).ToList();
        var balances = await BalancesAsync(allIds, asOf, ct);
        var accounts = allIds.Distinct().Where(byId.ContainsKey).Select(i => byId[i]).ToList();

        decimal Net(int id) => balances.TryGetValue(id, out var b) ? Py.Q(b.Dr - b.Cr) : 0m;

        decimal totalAssets = Py.Q(accounts.Where(a => a.Type == "asset").Sum(a => Net(a.Id)));
        decimal totalLiabilities = Py.Q(-accounts.Where(a => a.Type == "liability").Sum(a => Net(a.Id)));

        decimal Current(ChartNode root, string name, bool liability)
        {
            var node = kids.TryGetValue(root.Id, out var children) ? children.FirstOrDefault(c => c.Name == name) : null;
            decimal total = node is null ? 0m : Subtree(kids, node.Id).Sum(Net);
            // Liabilities are held in credit: flip to a positive magnitude, as total_liabilities does.
            return Py.Q(liability ? -total : total);
        }

        decimal ca = Current(assetsRoot, "Current Assets", false);
        decimal cl = Current(liabRoot, "Current Liabilities", true);
        decimal inventory = 0m;
        var invNode = accounts.Where(a => a.Name == "Inventories").MinBy(a => a.Id);
        if (invNode is not null)
            inventory = Py.Q(Subtree(kids, invNode.Id).Sum(Net));

        // A negative "liability" balance is really an asset: no ratio against it.
        bool ratios = cl > 0;
        double caD = Py.ToDouble(ca), clD = Py.ToDouble(cl), invD = Py.ToDouble(inventory);
        return new Liquidity
        {
            NetAssets = Py.F(Py.ToDouble(totalAssets) - Py.ToDouble(totalLiabilities)),
            TotalAssets = totalAssets, TotalLiabilities = totalLiabilities,
            CurrentAssets = ca, CurrentLiabilities = cl, Inventory = inventory,
            CurrentRatio = ratios ? Py.F(caD / clD) : null,
            QuickRatio = ratios ? Py.F((caD - invD) / clD) : null,
        };
    }

    // ── profitability ────────────────────────────────────────────────────

    private static readonly string[] PlTypes = ["revenue", "expense", "contra-expense"];
    private static readonly string[] RevenueSections = ["sales", "sales_returns"];
    private static readonly string[] CogsSections = ["cost_of_sales"];

    private static string? PlSectionOf(ChartNode a, Dictionary<int, ChartNode> byId)
    {
        ChartNode? n = a;
        int guard = 0;
        while (n is not null && guard++ < 64)
        {
            if (!string.IsNullOrEmpty(n.PlSection)) return n.PlSection;
            n = n.ParentId is int p && byId.TryGetValue(p, out var parent) ? parent : null;
        }
        return null;
    }

    private static DateOnly MonthEnd(DateOnly start) => start.AddMonths(1).AddDays(-1);

    /// <summary>
    /// profitability(): revenue, gross and net profit month by month (12 months to the as-of month)
    /// plus the same month a year earlier. _pl_by_section's arithmetic on one grouped read of the
    /// reporting table for all 13 months, instead of one read per month.
    /// </summary>
    public async Task<Profitability> ProfitabilityAsync(int months = 12, DateOnly? asOf = null, CancellationToken ct = default)
    {
        var endDay = asOf ?? clock.Today;          // the company's month, not the server's UTC one
        var last = new DateOnly(endDay.Year, endDay.Month, 1);
        var starts = Enumerable.Range(0, months).Select(i => last.AddMonths(i - months + 1)).ToList();
        var priorStart = new DateOnly(endDay.Year - 1, endDay.Month, 1);
        var windowStart = starts.Count > 0 && starts[0] < priorStart ? starts[0] : priorStart;
        var windowEnd = MonthEnd(last);

        var byId = await ByIdAsync(ct);
        var rows = await db.Set<GlDailyBalance>().AsNoTracking()
            .Where(g => g.Day >= windowStart && g.Day <= windowEnd)
            .GroupBy(g => new { g.AccountId, g.Day.Year, g.Day.Month })
            .Select(g => new { g.Key.AccountId, g.Key.Year, g.Key.Month, Dr = g.Sum(x => x.Debit), Cr = g.Sum(x => x.Credit) })
            .ToListAsync(ct);
        var byMonth = rows.GroupBy(r => new DateOnly(r.Year, r.Month, 1))
            .ToDictionary(g => g.Key, g => g.Select(r => (r.AccountId, r.Dr, r.Cr)).ToList());

        (decimal Revenue, decimal Cogs, decimal Gross, decimal Net) Figures(DateOnly start)
        {
            decimal revenue = 0m, cogsContrib = 0m, net = 0m;
            if (byMonth.TryGetValue(start, out var movements))
                foreach (var (aid, dr, cr) in movements)
                {
                    if (!byId.TryGetValue(aid, out var a) || !PlTypes.Contains(a.Type)) continue;
                    decimal contrib = a.Type == "expense" ? (cr > dr ? dr - cr : cr - dr) : cr - dr;
                    if (dr == 0 && cr == 0) continue;
                    string section = PlSectionOf(a, byId) ?? (a.Type == "revenue" ? "other_income" : "other_operating");
                    net += contrib;
                    if (RevenueSections.Contains(section)) revenue += contrib;
                    if (CogsSections.Contains(section)) cogsContrib += contrib;
                }
            return (Py.Q(revenue), Py.Q(-cogsContrib), Py.Q(revenue + cogsContrib), Py.Q(net));
        }

        var series = new List<ProfitMonth>();
        foreach (var start in starts)
        {
            var (rev, cogs, gross, net) = Figures(start);
            series.Add(new ProfitMonth(start, start.ToString("MMM", CultureInfo.InvariantCulture),
                                       start.ToString("MMM yyyy", CultureInfo.InvariantCulture), rev, cogs, gross, net));
        }
        var latest = series.Count > 0 ? series[^1] : new ProfitMonth(last, "", "", 0m, 0m, 0m, 0m);
        // Same month last year, so a seasonal business is compared like for like.
        ProfitMonth? prior = null;
        if (series.Count > 0)
        {
            var want = new DateOnly(series[^1].Start.Year - 1, series[^1].Start.Month, 1);
            prior = series.FirstOrDefault(m => m.Start == want);
            if (prior is null)
            {
                var (rev, cogs, gross, net) = Figures(priorStart);
                prior = new ProfitMonth(priorStart, "", "", rev, cogs, gross, net);
            }
        }
        string[] keys = ["revenue", "cogs", "gross", "net"];
        var totals = keys.ToDictionary(k => k, k => series.Count > 0 ? Py.Q(series.Sum(m => m[k])) : 0m);
        var values = series.SelectMany(m => new[] { Py.ToDouble(m.Revenue), Py.ToDouble(m.Net) }).ToList();
        return new Profitability
        {
            Months = series, Totals = totals, Latest = latest, Prior = prior,
            Deltas = new[] { "revenue", "gross", "net" }.ToDictionary(k => k, k => Delta(latest[k], prior?[k])),
            Peak = values.Count > 0 ? values.Max() : 0.0,
            Trough = Math.Min(values.Count > 0 ? values.Min() : 0.0, 0.0),
            Margin = totals["revenue"] != 0 ? Py.Q(totals["net"] / totals["revenue"] * 100m) : null,
        };
    }

    /// <summary>_delta: percentage change, or null where the base makes one meaningless (zero or negative).</summary>
    public static decimal? Delta(decimal now, decimal? before)
    {
        if (before is not decimal b || b <= 0) return null;
        decimal diff = Py.FromDouble(Py.ToDouble(now) - Py.ToDouble(b));     // float subtraction, as Python
        return Py.Q(diff / b * 100m);
    }
}
