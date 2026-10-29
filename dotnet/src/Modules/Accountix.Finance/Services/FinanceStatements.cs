using System.Globalization;

namespace Accountix.Finance.Services;

// ── models ──────────────────────────────────────────────────────────────

public sealed class TbRow
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public decimal DrOpening { get; set; }
    public decimal CrOpening { get; set; }
    public decimal DrMovement { get; set; }
    public decimal CrMovement { get; set; }
    public decimal DrClosing { get; set; }
    public decimal CrClosing { get; set; }
    public bool IsSubtotal { get; init; }
    public List<decimal>? CompDrClosing { get; set; }
    public List<decimal>? CompCrClosing { get; set; }
}

public sealed class TrialBalanceResult
{
    public List<TbRow> Rows { get; init; } = [];
    public decimal TotalDrOpening, TotalCrOpening, TotalDrMovement, TotalCrMovement, TotalDrClosing, TotalCrClosing;
    public List<(decimal Dr, decimal Cr)> CompTotals { get; init; } = [];
    public DateOnly AsOf { get; init; }
    public DateOnly? From { get; init; }
}

public sealed record BsItem(string Code, string Name, decimal Amount);
public sealed record BsMerged(string Code, string Name, List<decimal> Amounts);

public sealed class BsData
{
    public List<BsItem> Assets { get; init; } = [];
    public List<BsItem> Liabilities { get; init; } = [];
    public List<BsItem> Equity { get; init; } = [];
    public decimal TotalAssets, TotalLiabilities, TotalEquity, NetIncome;
}

public sealed class BalanceSheetResult
{
    public required BsData Base { get; init; }
    public List<BsData> Comp { get; init; } = [];
    public List<BsMerged> MergedAssets { get; init; } = [];
    public List<BsMerged> MergedLiabilities { get; init; } = [];
    public List<BsMerged> MergedEquity { get; init; } = [];
    public DateOnly AsOf { get; init; }
}

public sealed class SocieColumn(string key, string label)
{
    public string Key { get; } = key;
    public string Label { get; } = label;
}

public sealed class SocieRow
{
    public required string Kind { get; set; }
    public required string Label { get; init; }
    public int Indent { get; init; }
    /// <summary>{column key: amount or null (not affected)}; empty for section rows.</summary>
    public Dictionary<string, decimal?> Values { get; init; } = [];
}

/// <summary>A cash-flow line: label, the period's figure and one per comparative period (null = none).</summary>
public sealed record CashItem(string Name, double Value, List<double?>? Comps = null);

public sealed class CashFlowResult
{
    public string Method { get; init; } = "indirect";
    public List<CashItem> Operating { get; set; } = [];
    public List<CashItem> Investing { get; set; } = [];
    public List<CashItem> Financing { get; set; } = [];
    public double NetOperating, NetInvesting, NetFinancing, NetChange, OpeningCash, ClosingCash, CashMovement;
    /// <summary>Per comparative period: item label → value, plus __net_op__ … __close__.</summary>
    public List<Dictionary<string, double>> CompMaps { get; init; } = [];
}

public sealed record LedgerLine(string Date, string Voucher, string Description, decimal Debit, decimal Credit, decimal Balance);

public sealed class LedgerSection
{
    public required AccountNode Account { get; init; }
    public List<LedgerLine> Rows { get; init; } = [];
    public bool Empty { get; init; }
    public decimal Opening { get; init; }
    public string OpeningSide { get; init; } = "Dr";
    public decimal TotalDebit { get; init; }
    public decimal TotalCredit { get; init; }
    public decimal Closing { get; init; }
    public string ClosingSide { get; init; } = "Dr";
    public decimal Subtotal { get; init; }
}

/// <summary>Trial balance, balance sheet, SOCIE, cash flow and ledger (finance_app/routes/reports.py).</summary>
public sealed class FinanceStatements(FinanceData data)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public FinanceData Data => data;
    private AccountixDbContext Db => data.Db;

    public static readonly IReadOnlyDictionary<string, string> AccountTypes = new Dictionary<string, string>
    {
        ["asset"] = "Asset", ["liability"] = "Liability", ["equity"] = "Equity", ["revenue"] = "Revenue", ["expense"] = "Expense",
    };

    private static readonly Dictionary<string, string> ClassNames = new()
    {
        ["1"] = "Assets", ["2"] = "Liabilities", ["3"] = "Equity", ["4"] = "Revenue", ["5"] = "Expenses",
    };

    // ── trial balance ───────────────────────────────────────────────────

    public async Task<TrialBalanceResult> TrialBalanceAsync(DateOnly? from, DateOnly asOf, IReadOnlyList<AccountingPeriod> compPeriods,
                                                            IReadOnlyCollection<int> labelIds, CancellationToken ct = default)
    {
        DateOnly? openingAsOf = from?.AddDays(-1);
        var opening = openingAsOf is null ? [] : await data.AllAccountBalancesAsync(openingAsOf, null, labelIds, ct);
        var closing = await data.AllAccountBalancesAsync(asOf, null, labelIds, ct);
        var closingMap = new Dictionary<string, AccountBalance>();
        foreach (var b in closing) closingMap[b.Code] = b;
        var openingMap = new Dictionary<string, AccountBalance>();
        foreach (var b in opening) openingMap[b.Code] = b;
        var allCodes = closingMap.Keys.Union(openingMap.Keys).Order(StringComparer.Ordinal).ToList();

        var result = new TrialBalanceResult { AsOf = asOf, From = from };
        var rows = new List<TbRow>();
        foreach (string code in allCodes)
        {
            closingMap.TryGetValue(code, out var cb);
            openingMap.TryGetValue(code, out var ob);
            decimal drOp = ob?.Dr ?? 0m, crOp = ob?.Cr ?? 0m, drCl = cb?.Dr ?? 0m, crCl = cb?.Cr ?? 0m;
            decimal drMv = drCl - drOp, crMv = crCl - crOp;
            if (drCl == 0 && crCl == 0 && drOp == 0 && crOp == 0) continue;
            var any = (cb ?? ob)!;
            result.TotalDrOpening += drOp; result.TotalCrOpening += crOp;
            result.TotalDrMovement += drMv; result.TotalCrMovement += crMv;
            result.TotalDrClosing += drCl; result.TotalCrClosing += crCl;
            rows.Add(new TbRow
            {
                Code = code, Name = any.Name, Type = AccountTypes.TryGetValue(any.Type, out var t) ? t : any.Type,
                DrOpening = drOp, CrOpening = crOp, DrMovement = drMv, CrMovement = crMv, DrClosing = drCl, CrClosing = crCl,
            });
        }

        // Roll-up subtotals per account class (code's first digit), after each class.
        TbRow? clsTot = null;
        string? prevCls = null;
        void Close()
        {
            if (clsTot is not null && prevCls is not null && ClassNames.TryGetValue(prevCls, out var name))
                result.Rows.Add(new TbRow
                {
                    Code = "", Name = $"Total {name}", Type = "", IsSubtotal = true,
                    DrOpening = clsTot.DrOpening, CrOpening = clsTot.CrOpening, DrMovement = clsTot.DrMovement,
                    CrMovement = clsTot.CrMovement, DrClosing = clsTot.DrClosing, CrClosing = clsTot.CrClosing,
                });
        }
        foreach (var r in rows)
        {
            string cls = (r.Code.Length > 0 ? r.Code : "?")[..1];
            if (cls != prevCls)
            {
                Close();
                clsTot = new TbRow();
                prevCls = cls;
            }
            result.Rows.Add(r);
            clsTot!.DrOpening += r.DrOpening; clsTot.CrOpening += r.CrOpening;
            clsTot.DrMovement += r.DrMovement; clsTot.CrMovement += r.CrMovement;
            clsTot.DrClosing += r.DrClosing; clsTot.CrClosing += r.CrClosing;
        }
        Close();

        if (compPeriods.Count > 0)
        {
            var maps = new List<Dictionary<string, AccountBalance>>();
            foreach (var cp in compPeriods)
            {
                var map = new Dictionary<string, AccountBalance>();
                foreach (var b in await data.AllAccountBalancesAsync(cp.EndDate, null, labelIds, ct)) map[b.Code] = b;
                maps.Add(map);
                decimal dr = 0m, cr = 0m;
                foreach (string code in allCodes)
                    if (map.TryGetValue(code, out var b)) { dr += b.Dr; cr += b.Cr; }
                result.CompTotals.Add((dr, cr));
            }
            foreach (var r in result.Rows)
            {
                r.CompDrClosing = maps.Select(m => m.TryGetValue(r.Code, out var b) ? b.Dr : 0m).ToList();
                r.CompCrClosing = maps.Select(m => m.TryGetValue(r.Code, out var b) ? b.Cr : 0m).ToList();
            }
        }
        return result;
    }

    // ── profit & loss ───────────────────────────────────────────────────

    /// <summary>The P&amp;L of the resolved window; comparative columns re-run the structure per period.</summary>
    public async Task<(List<PlRow> Rows, decimal Net)> ProfitLossAsync(DateOnly? from, DateOnly? to, IReadOnlyList<AccountingPeriod> compPeriods,
                                                                     IReadOnlyCollection<int> labelIds, CancellationToken ct = default)
    {
        var (rows, net) = await data.PlRowsAsync(from, to, labelIds, ct);
        if (compPeriods.Count > 0)
        {
            var lookups = new List<PlLookup>();
            foreach (var cp in compPeriods)
                lookups.Add(await data.PlLookupAsync(cp.StartDate, cp.EndDate, labelIds, ct));
            foreach (var row in rows.Where(r => r.Kind is "account" or "total" or "subtotal"))
                row.CompAmounts = [row.Amount, .. lookups.Select(lk => FinanceData.PlCompAmount(row, lk))];
        }
        return (rows, net);
    }

    public sealed record LabelPl(List<PlRow> Rows, decimal Net, List<decimal> Nets, decimal? UnlabelledNet);

    /// <summary>
    /// label_pl: the company P&amp;L with a column per label (and Unlabelled when no label is picked).
    /// One grouped read of the window by (label, account) feeds every column (Python reads twice per label).
    /// </summary>
    public async Task<LabelPl> LabelPlAsync(DateOnly from, DateOnly to, IReadOnlyList<ProjectLabel> labels, bool allLabels,
                                            CancellationToken ct = default)
    {
        var byLabel = await data.PlMovementsByLabelAsync(from, to, ct);
        var chart = await data.ChartAsync(ct);
        var settings = await data.SettingsAsync(ct);
        var structure = FinanceData.ParseStructure(settings?.PlStructureJson);
        var all = new Dictionary<int, (decimal Dr, decimal Cr)>();
        foreach (var per in byLabel.Values)
            foreach (var (aid, (dr, cr)) in per)
                all[aid] = all.TryGetValue(aid, out var cur) ? (cur.Dr + dr, cur.Cr + cr) : (dr, cr);
        var (rows, net) = FinanceData.PlRows(FinanceData.PlBySection(all, chart), settings);
        var lookups = new List<PlLookup>();
        var nets = new List<decimal>();
        foreach (var l in labels)
        {
            var bySection = FinanceData.PlBySection(byLabel.TryGetValue(l.Id, out var m) ? m : [], chart);
            lookups.Add(FinanceData.PlLookupOf(bySection, structure));
            nets.Add(FinanceData.PlRows(bySection, settings).Net);
        }
        foreach (var row in rows.Where(r => r.Kind is "account" or "total" or "subtotal"))
        {
            var per = lookups.Select(lk => FinanceData.PlCompAmount(row, lk)).ToList();
            row.ByLabel = per;
            row.Unlabelled = allLabels ? Math.Round(row.Amount - per.Sum(), 2, MidpointRounding.ToEven) : null;
        }
        decimal? unlabelledNet = allLabels ? Math.Round(net - nets.Sum(), 2, MidpointRounding.ToEven) : null;
        return new LabelPl(rows, net, nets, unlabelledNet);
    }

    // ── balance sheet ───────────────────────────────────────────────────

    /// <summary>_bs_data: the three sections at a date, net income (all postings to that date) added to equity.</summary>
    public async Task<BsData> BsDataAsync(DateOnly asOf, IReadOnlyCollection<int> labelIds, bool includeNi = true, CancellationToken ct = default)
    {
        var balances = await data.AllAccountBalancesAsync(asOf, null, labelIds, ct);
        decimal ni = includeNi ? FinanceData.NetIncome(balances) : 0m;
        var d = new BsData { NetIncome = ni };
        foreach (var b in balances)
        {
            switch (b.Type)
            {
                case "asset":
                {
                    decimal bal = b.Dr - b.Cr;
                    if (bal != 0) { d.Assets.Add(new BsItem(b.Code, b.Name, bal)); d.TotalAssets += bal; }
                    break;
                }
                case "liability":
                {
                    decimal bal = b.Cr - b.Dr;
                    if (bal != 0) { d.Liabilities.Add(new BsItem(b.Code, b.Name, bal)); d.TotalLiabilities += bal; }
                    break;
                }
                case "equity":
                {
                    decimal bal = b.Cr - b.Dr;
                    if (bal != 0) { d.Equity.Add(new BsItem(b.Code, b.Name, bal)); d.TotalEquity += bal; }
                    break;
                }
            }
        }
        if (includeNi)
        {
            d.Equity.Add(new BsItem("", ni >= 0 ? "Net Income (Current Period)" : "Net Loss (Current Period)", ni));
            d.TotalEquity += ni;
        }
        return d;
    }

    /// <summary>_merge_multi_period: one row per code with an amount per column, sorted by code.</summary>
    public static List<BsMerged> Merge(List<BsItem> baseItems, IReadOnlyList<List<BsItem>> compItems)
    {
        var merged = new Dictionary<string, BsMerged>();
        var all = new List<List<BsItem>> { baseItems };
        all.AddRange(compItems);
        for (int pi = 0; pi < all.Count; pi++)
            foreach (var item in all[pi])
            {
                if (!merged.TryGetValue(item.Code, out var m))
                    merged[item.Code] = m = new BsMerged(item.Code, item.Name, Enumerable.Repeat(0m, all.Count).ToList());
                m.Amounts[pi] = item.Amount;
            }
        return merged.Values.OrderBy(m => m.Code, StringComparer.Ordinal).ToList();
    }

    public async Task<BalanceSheetResult> BalanceSheetAsync(DateOnly asOf, IReadOnlyList<AccountingPeriod> compPeriods,
                                                            IReadOnlyCollection<int> labelIds, CancellationToken ct = default)
    {
        var bs = await BsDataAsync(asOf, labelIds, ct: ct);
        var comp = new List<BsData>();
        foreach (var cp in compPeriods)
            comp.Add(await BsDataAsync(cp.EndDate, labelIds, ct: ct));
        return new BalanceSheetResult
        {
            Base = bs, Comp = comp, AsOf = asOf,
            MergedAssets = comp.Count > 0 ? Merge(bs.Assets, comp.Select(c => c.Assets).ToList()) : [],
            MergedLiabilities = comp.Count > 0 ? Merge(bs.Liabilities, comp.Select(c => c.Liabilities).ToList()) : [],
            MergedEquity = comp.Count > 0 ? Merge(bs.Equity, comp.Select(c => c.Equity).ToList()) : [],
        };
    }

    // ── SOCIE ───────────────────────────────────────────────────────────

    private const decimal SocieEps = 0.005m;

    /// <summary>_socie_period_specs: current window plus the comparatives, de-duplicated, chronological.</summary>
    public static List<(DateOnly Start, DateOnly End)> SocieSpecs(DateOnly from, DateOnly to, IReadOnlyList<AccountingPeriod> comp)
    {
        var specs = new List<(DateOnly, DateOnly)> { (from, to) };
        specs.AddRange(comp.Select(p => (p.StartDate, p.EndDate)));
        return specs.Distinct().OrderBy(s => s.Item1).ThenBy(s => s.Item2).ToList();
    }

    /// <summary>_socie_matrix: the IFRS-style roll-forward of equity, one column per component.</summary>
    public async Task<(List<SocieColumn> Columns, List<SocieRow> Rows)> SocieAsync(IReadOnlyList<(DateOnly Start, DateOnly End)> specs,
                                                                                   IReadOnlyCollection<int> labelIds, CancellationToken ct = default)
    {
        if (specs.Count == 0) return ([], []);
        var chart = await data.ChartAsync(ct);
        var accounts = chart.ById.Values.Where(a => a.Type == "equity").OrderBy(a => a.Code, StringComparer.Ordinal).ThenBy(a => a.Id).ToList();
        var byCode = new Dictionary<string, AccountNode>();
        foreach (var a in accounts) byCode[a.Code] = a;
        const string scCode = "3-01-01-01-0001";
        byCode.TryGetValue(CoaCodes.RetainedEarnings, out var reClose);
        byCode.TryGetValue("3-02-01-01-0001", out var reOpen);
        byCode.TryGetValue(CoaCodes.Dividends, out var divAcct);
        byCode.TryGetValue(CoaCodes.OtherComprehensiveIncome, out var ociAcct);
        var reFamily = new HashSet<int>(new[] { reOpen, reClose, divAcct }.Where(a => a is not null).Select(a => a!.Id));

        var cache = new Dictionary<DateOnly, (Dictionary<int, decimal> Bals, decimal Ni)>();
        async Task<(Dictionary<int, decimal> Bals, decimal Ni)> At(DateOnly d)
        {
            if (!cache.TryGetValue(d, out var v))
            {
                // One read per date: equity balances and cumulative net income come from the same rows.
                var all = await data.AllAccountBalancesAsync(d, null, labelIds, ct);
                v = (all.Where(b => b.Type == "equity").ToDictionary(b => b.AccountId, b => b.Cr - b.Dr), FinanceData.NetIncome(all));
                cache[d] = v;
            }
            return v;
        }
        decimal Bal(Dictionary<int, decimal> bals, int id) => bals.TryGetValue(id, out var x) ? x : 0m;

        var cutDates = new List<DateOnly>();
        foreach (var (ps, pe) in specs) { cutDates.Add(ps.AddDays(-1)); cutDates.Add(pe); }
        foreach (var d in cutDates) await At(d);

        var compAccounts = new List<AccountNode>();
        foreach (var a in accounts)
        {
            if (a.Level < 5 || reFamily.Contains(a.Id)) continue;
            bool used = cutDates.Any(d => Math.Abs(Bal(cache[d].Bals, a.Id)) > SocieEps);
            if (used || a.Code == scCode) compAccounts.Add(a);
        }
        var columns = compAccounts.Select(a => new SocieColumn($"a{a.Id}", a.Name)).ToList();
        columns.Add(new SocieColumn("re", "Retained Earnings"));
        columns.Add(new SocieColumn("total", "Total Equity"));
        string? ociKey = ociAcct is not null && columns.Any(c => c.Key == $"a{ociAcct.Id}") ? $"a{ociAcct.Id}" : null;

        Dictionary<string, decimal> Balances(DateOnly d)
        {
            var (bals, ni) = cache[d];
            var vals = compAccounts.ToDictionary(a => $"a{a.Id}", a => Bal(bals, a.Id));
            decimal re = ni;
            foreach (var a in new[] { reOpen, reClose, divAcct })
                if (a is not null) re += Bal(bals, a.Id);
            vals["re"] = re;
            vals["total"] = vals.Values.Sum();
            return vals;
        }
        decimal Mv(AccountNode? acct, DateOnly d0, DateOnly d1) =>
            acct is null ? 0m : Bal(cache[d1].Bals, acct.Id) - Bal(cache[d0].Bals, acct.Id);

        SocieRow Row(string kind, string label, IReadOnlyDictionary<string, decimal> values, int indent = 0)
        {
            var full = new Dictionary<string, decimal?>();
            foreach (var c in columns)
                if (c.Key != "total")
                    full[c.Key] = values.TryGetValue(c.Key, out var v) ? v : null;
            full["total"] = full.Values.Where(v => v is not null).Sum(v => v!.Value);
            return new SocieRow { Kind = kind, Label = label, Indent = indent, Values = full };
        }
        static SocieRow Section(string label) => new() { Kind = "section", Label = label };

        var rows = new List<SocieRow>();
        DateOnly? prevEnd = null;
        Dictionary<string, decimal>? prevClose = null;
        foreach (var (ps, pe) in specs)
        {
            var cutoff = ps.AddDays(-1);
            var opening = Balances(cutoff);
            var closing = Balances(pe);
            bool spanYears = pe.DayNumber - ps.DayNumber >= 350;
            string term = spanYears ? "year" : "period";
            string ended = $"the {term} ended {pe.ToString("dd MMMM yyyy", Inv)}";

            bool adjoining = prevEnd is DateOnly pv && pv.AddDays(1) == ps;
            bool same = prevClose is not null && opening.Keys.All(k =>
                Math.Abs((prevClose.TryGetValue(k, out var a) ? a : 0m) - opening[k]) < 0.005m);
            if (!(adjoining && same))
            {
                string label = $"Balance as at {ps.ToString("dd MMMM yyyy", Inv)}";
                if (adjoining && !same) label += " (as restated)";
                rows.Add(Row("opening", label, opening));
            }

            var explained = new Dictionary<string, decimal>();
            void Explain(string k, decimal v) => explained[k] = (explained.TryGetValue(k, out var e) ? e : 0m) + v;

            decimal profit = cache[pe].Ni - cache[cutoff].Ni;
            decimal ociMv = ociKey is not null ? Mv(ociAcct, cutoff, pe) : 0m;
            var tci = new List<SocieRow>();
            if (Math.Abs(profit) > SocieEps)
            {
                tci.Add(Row("movement", $"{(profit >= 0 ? "Profit" : "Loss")} for {ended}", new Dictionary<string, decimal> { ["re"] = profit }, 1));
                Explain("re", profit);
            }
            if (Math.Abs(ociMv) > SocieEps && ociKey is not null)
            {
                tci.Add(Row("movement", $"Other comprehensive income for {ended}", new Dictionary<string, decimal> { [ociKey] = ociMv }, 1));
                Explain(ociKey, ociMv);
            }
            if (tci.Count > 0)
            {
                rows.Add(Section("Total comprehensive income"));
                rows.AddRange(tci);
                if (tci.Count > 1)
                {
                    var sub = new Dictionary<string, decimal> { ["re"] = profit };
                    if (ociKey is not null) sub[ociKey] = ociMv;
                    rows.Add(Row("subtotal", $"Total comprehensive income for {ended}", sub, 1));
                }
            }

            var owner = new List<SocieRow>();
            foreach (var a in compAccounts)
            {
                if (ociKey is not null && a.Id == ociAcct!.Id) continue;
                decimal mv = Mv(a, cutoff, pe);
                if (Math.Abs(mv) <= SocieEps) continue;
                string label = a.Code.StartsWith("3-01", StringComparison.Ordinal)
                    ? (mv > 0 ? "Shares issued during the " + term : "Capital reduction during the " + term)
                    : (mv > 0 ? $"Transfer to {a.Name}" : $"Transfer from {a.Name}");
                owner.Add(Row("movement", label, new Dictionary<string, decimal> { [$"a{a.Id}"] = mv }, 1));
                Explain($"a{a.Id}", mv);
            }
            decimal divMv = Mv(divAcct, cutoff, pe);
            if (Math.Abs(divMv) > SocieEps)
            {
                owner.Add(Row("movement", "Dividends declared", new Dictionary<string, decimal> { ["re"] = divMv }, 1));
                Explain("re", divMv);
            }
            if (owner.Count > 0)
            {
                rows.Add(Section("Transactions with owners"));
                rows.AddRange(owner);
                if (owner.Count > 1)
                {
                    var sub = new Dictionary<string, decimal>();
                    foreach (var r in owner)
                        foreach (var (k, v) in r.Values)
                            if (k != "total" && v is decimal x)
                                sub[k] = (sub.TryGetValue(k, out var s) ? s : 0m) + x;
                    rows.Add(Row("subtotal", "Total transactions with owners", sub, 1));
                }
            }

            var residual = new Dictionary<string, decimal>();
            foreach (var c in columns)
            {
                if (c.Key == "total") continue;
                decimal delta = closing[c.Key] - opening[c.Key] - (explained.TryGetValue(c.Key, out var e) ? e : 0m);
                if (Math.Abs(delta) > SocieEps) residual[c.Key] = delta;
            }
            if (residual.Count > 0)
            {
                rows.Add(Section("Other movements"));
                rows.Add(Row("movement", "Prior-period adjustments and transfers", residual, 1));
            }

            rows.Add(Row("closing", $"Balance as at {pe.ToString("dd MMMM yyyy", Inv)}", closing));
            prevEnd = pe;
            prevClose = closing;
        }
        if (rows.Count > 0) rows[^1].Kind = "grand";
        return (columns, rows);
    }

    // ── cash flow ───────────────────────────────────────────────────────

    private static double F(decimal d) => TwcfReports.F(d);

    /// <summary>_cash_flow_direct: receipts and payments through cash accounts, attributed to the other legs' activities.</summary>
    public async Task<(List<CashItem> Op, List<CashItem> Inv, List<CashItem> Fin, double Opening, double Closing)> CashFlowDirectAsync(
        DateOnly from, DateOnly to, IReadOnlyCollection<int> labelIds, CancellationToken ct = default)
    {
        var chart = await data.ChartAsync(ct);
        var cashIds = chart.CashIds();
        var cashSet = cashIds.ToHashSet();
        DateTime start = from.ToDateTime(TimeOnly.MinValue), end = to.ToDateTime(TimeOnly.MaxValue);
        var q = Db.Set<JournalLine>().AsNoTracking()
            .Where(l => l.JournalEntry.IsPosted == true && l.JournalEntry.EntryDate >= start && l.JournalEntry.EntryDate <= end);
        if (labelIds.Count > 0) q = q.Where(l => l.LabelId != null && labelIds.Contains(l.LabelId.Value));
        var lines = await q.Select(l => new { l.JournalEntryId, l.AccountId, Debit = l.Debit ?? 0m, Credit = l.Credit ?? 0m })
            .ToListAsync(ct);
        var groups = new Dictionary<string, Dictionary<string, double>>
        {
            ["operating"] = [], ["investing"] = [], ["financing"] = [],
        };
        foreach (var entry in lines.GroupBy(l => l.JournalEntryId))
        {
            var cash = entry.Where(l => cashSet.Contains(l.AccountId)).ToList();
            var other = entry.Where(l => !cashSet.Contains(l.AccountId)).ToList();
            if (cash.Count == 0) continue;
            double netCash = TwcfReports.PySum(cash.Select(l => F(l.Debit - l.Credit)));
            if (Math.Abs(netCash) < 0.005) continue;
            double otherTotal = TwcfReports.PySum(other.Select(l => F(l.Credit + l.Debit)));
            if (otherTotal == 0) continue;
            foreach (var o in other)
            {
                if (!chart.ById.TryGetValue(o.AccountId, out var oa)) continue;
                double weight = F(o.Credit + o.Debit) / otherTotal;
                double portion = netCash * weight;
                string activity = chart.CashFlowActivity(oa) ?? "operating";
                if (activity == "cash" || !groups.TryGetValue(activity, out var g)) continue;
                string head = chart.L3Head(oa);
                g[head] = (g.TryGetValue(head, out var cur) ? cur : 0.0) + portion;
            }
        }
        var op = new List<CashItem>();
        foreach (var (name, v) in groups["operating"].OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (v > 0) op.Add(new CashItem($"Cash received — {name}", v));
            else if (v < 0) op.Add(new CashItem($"Cash paid — {name}", v));
        }
        var inv = groups["investing"].OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new CashItem($"{(kv.Value > 0 ? "Proceeds from" : "Purchase of")} {kv.Key}", kv.Value)).ToList();
        var fin = groups["financing"].OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new CashItem($"{(kv.Value > 0 ? "Proceeds from" : "Repayment of")} {kv.Key}", kv.Value)).ToList();
        double opening = await CashBalanceAsync(cashIds, from.AddDays(-1), labelIds, ct);
        double closing = await CashBalanceAsync(cashIds, to, labelIds, ct);
        return (op, inv, fin, opening, closing);
    }

    private async Task<double> CashBalanceAsync(List<int> cashIds, DateOnly asOf, IReadOnlyCollection<int> labelIds, CancellationToken ct)
    {
        var (dr, cr) = await data.SumBalancesAsync(cashIds, asOf, labelIds, ct);
        return F(dr - cr);
    }

    private sealed record Indirect(List<CashItem> Op, List<CashItem> Inv, List<CashItem> Fin, double Movement);

    /// <summary>The indirect method: net profit, then working-capital and other balance movements by level-3 head.</summary>
    private async Task<Indirect> IndirectAsync(DateOnly from, DateOnly to, IReadOnlyCollection<int> labelIds, CancellationToken ct)
    {
        var chart = await data.ChartAsync(ct);
        var pl = await data.PeriodMovementsAsync(from, to, FinanceData.PlTypes, labelIds, ct);
        decimal netProfit = 0m;
        foreach (var (aid, (dr, cr)) in pl)
        {
            // Credit less debit for every type, in the period's own column and in every
            // comparative column alike: a comparative column shows what the statement shows
            // when that period is chosen on its own (both apps; the old expense arm is gone).
            if (chart.ById.ContainsKey(aid)) netProfit += cr - dr;
        }
        var bs = await data.PeriodMovementsAsync(from, to, FinanceData.BsTypes, labelIds, ct);
        var groups = new Dictionary<string, Dictionary<string, decimal>> { ["operating"] = [], ["investing"] = [], ["financing"] = [] };
        decimal movement = 0m;
        foreach (var (aid, (dr, cr)) in bs)
        {
            if (!chart.ById.TryGetValue(aid, out var acct)) continue;
            string activity = chart.CashFlowActivity(acct) ?? "operating";
            if (activity == "cash") { movement += dr - cr; continue; }
            decimal effect = cr - dr;
            if (effect != 0 && groups.TryGetValue(activity, out var g))
            {
                string head = chart.L3Head(acct);
                g[head] = (g.TryGetValue(head, out var cur) ? cur : 0m) + effect;
            }
        }
        var op = new List<CashItem> { new("Net Profit / (Loss) for the period", F(netProfit)) };
        op.AddRange(groups["operating"].OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new CashItem(
            kv.Value < 0 ? $"(Increase) / Decrease in {kv.Key}" : $"Decrease / (Increase) in {kv.Key}", F(kv.Value))));
        var inv = groups["investing"].OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new CashItem($"Movement in {kv.Key}", F(kv.Value))).ToList();
        var fin = groups["financing"].OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new CashItem($"Movement in {kv.Key}", F(kv.Value))).ToList();
        return new Indirect(op, inv, fin, F(movement));
    }

    private static double Sum(IEnumerable<CashItem> items) => TwcfReports.PySum(items.Select(i => i.Value));

    public async Task<CashFlowResult> CashFlowAsync(DateOnly from, DateOnly to, string method, IReadOnlyList<AccountingPeriod> compPeriods,
                                                    IReadOnlyCollection<int> labelIds, CancellationToken ct = default)
    {
        var chart = await data.ChartAsync(ct);
        var cashIds = chart.CashIds();
        var r = new CashFlowResult { Method = method };
        if (method == "direct")
        {
            var (op, inv, fin, oc, cc) = await CashFlowDirectAsync(from, to, labelIds, ct);
            (r.Operating, r.Investing, r.Financing, r.OpeningCash, r.ClosingCash) = (op, inv, fin, oc, cc);
            r.NetOperating = Sum(op); r.NetInvesting = Sum(inv); r.NetFinancing = Sum(fin);
            r.NetChange = r.NetOperating + r.NetInvesting + r.NetFinancing;
            r.CashMovement = r.NetChange;
        }
        else
        {
            var ind = await IndirectAsync(from, to, labelIds, ct);
            (r.Operating, r.Investing, r.Financing, r.CashMovement) = (ind.Op, ind.Inv, ind.Fin, ind.Movement);
            r.NetOperating = Sum(ind.Op); r.NetInvesting = Sum(ind.Inv); r.NetFinancing = Sum(ind.Fin);
            r.NetChange = r.NetOperating + r.NetInvesting + r.NetFinancing;
            r.OpeningCash = await CashBalanceAsync(cashIds, from.AddDays(-1), labelIds, ct);
            r.ClosingCash = await CashBalanceAsync(cashIds, to, labelIds, ct);
        }

        foreach (var cp in compPeriods)
        {
            var map = new Dictionary<string, double>();
            if (method == "direct")
            {
                var (op, inv, fin, oc, cc) = await CashFlowDirectAsync(cp.StartDate, cp.EndDate, labelIds, ct);
                foreach (var item in op.Concat(inv).Concat(fin)) map[item.Name] = item.Value;
                map["__net_op__"] = Sum(op);
                map["__net_inv__"] = Sum(inv);
                map["__net_fin__"] = Sum(fin);
                map["__net_chg__"] = map["__net_op__"] + map["__net_inv__"] + map["__net_fin__"];
                map["__open__"] = oc;
                map["__close__"] = cc;
            }
            else
            {
                var ind = await IndirectAsync(cp.StartDate, cp.EndDate, labelIds, ct);
                foreach (var item in ind.Op.Concat(ind.Inv).Concat(ind.Fin)) map[item.Name] = item.Value;
                double netOp = ind.Op[0].Value + TwcfReports.PySum(ind.Op.Skip(1).Select(i => i.Value));
                map["__net_op__"] = netOp;
                map["__net_inv__"] = Sum(ind.Inv);
                map["__net_fin__"] = Sum(ind.Fin);
                map["__net_chg__"] = netOp + map["__net_inv__"] + map["__net_fin__"];
                map["__open__"] = await CashBalanceAsync(cashIds, cp.StartDate.AddDays(-1), labelIds, ct);
                map["__close__"] = await CashBalanceAsync(cashIds, cp.EndDate, labelIds, ct);
            }
            r.CompMaps.Add(map);
        }
        if (r.CompMaps.Count > 0)
        {
            List<CashItem> Annotate(List<CashItem> items) =>
                items.Select(i => i with { Comps = r.CompMaps.Select(m => (double?)(m.TryGetValue(i.Name, out var v) ? v : 0.0)).ToList() }).ToList();
            r.Operating = Annotate(r.Operating);
            r.Investing = Annotate(r.Investing);
            r.Financing = Annotate(r.Financing);
        }
        return r;
    }

    // ── general ledger ──────────────────────────────────────────────────

    /// <summary>_get_leaf_descendant_ids: leaf descendants of a head in the active chart (the head excluded).</summary>
    public static List<int> LeafDescendants(Chart chart, int headId)
    {
        var kids = chart.ById.Values.Where(a => a.Active && a.ParentId is not null).GroupBy(a => a.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(a => a.Id).ToList());
        var leaves = new HashSet<int>();
        var stack = new Stack<int>(kids.TryGetValue(headId, out var first) ? first : []);
        while (stack.Count > 0)
        {
            int n = stack.Pop();
            if (kids.TryGetValue(n, out var ks) && ks.Count > 0)
                foreach (int k in ks) stack.Push(k);
            else
                leaves.Add(n);
        }
        return leaves.ToList();
    }

    /// <summary>
    /// _get_ledger_sections: opening (reporting table, before the window), the window's lines
    /// with a running balance, closing with its side. Three queries whatever the number of accounts.
    /// </summary>
    public async Task<List<LedgerSection>> LedgerSectionsAsync(IReadOnlyList<int> accountIds, DateOnly? from, DateOnly? to,
                                                               IReadOnlyCollection<int> labelIds, CancellationToken ct = default)
    {
        if (accountIds.Count == 0) return [];
        var ids = accountIds.Distinct().ToList();
        var chart = await data.ChartAsync(ct);
        var openings = new Dictionary<int, decimal>();
        if (from is DateOnly f)
        {
            var oq = Db.Set<GlDailyBalance>().AsNoTracking().Where(g => ids.Contains(g.AccountId) && g.Day < f);
            if (labelIds.Count > 0) oq = oq.Where(g => labelIds.Contains(g.LabelId));
            foreach (var o in await oq.GroupBy(g => g.AccountId)
                         .Select(g => new { g.Key, Dr = g.Sum(x => x.Debit), Cr = g.Sum(x => x.Credit) }).ToListAsync(ct))
                openings[o.Key] = o.Dr - o.Cr;
        }
        var lq = Db.Set<JournalLine>().AsNoTracking().Where(l => ids.Contains(l.AccountId) && l.JournalEntry.IsPosted == true);
        if (labelIds.Count > 0) lq = lq.Where(l => l.LabelId != null && labelIds.Contains(l.LabelId.Value));
        if (from is DateOnly f2) { var s = f2.ToDateTime(TimeOnly.MinValue); lq = lq.Where(l => l.JournalEntry.EntryDate >= s); }
        if (to is DateOnly t2) { var e = t2.ToDateTime(TimeOnly.MaxValue); lq = lq.Where(l => l.JournalEntry.EntryDate <= e); }
        var lines = await lq
            .OrderBy(l => l.AccountId).ThenBy(l => l.JournalEntry.EntryDate).ThenBy(l => l.JournalEntryId).ThenBy(l => l.Id)
            .Select(l => new
            {
                l.AccountId, l.Debit, l.Credit, LineDesc = l.Description, l.JournalEntry.EntryDate,
                l.JournalEntry.VoucherNumber, EntryDesc = l.JournalEntry.Description,
            })
            .ToListAsync(ct);
        var byAcc = lines.GroupBy(l => l.AccountId).ToDictionary(g => g.Key, g => g.ToList());

        var sections = new List<LedgerSection>();
        foreach (int aid in ids)
        {
            if (!chart.ById.TryGetValue(aid, out var account)) continue;
            decimal opening = openings.TryGetValue(aid, out var o) ? o : 0m;
            decimal balance = opening, totalDr = 0m, totalCr = 0m;
            var rows = new List<LedgerLine>();
            if (byAcc.TryGetValue(aid, out var mine))
                foreach (var l in mine)
                {
                    decimal dr = l.Debit ?? 0m, cr = l.Credit ?? 0m;
                    balance += dr - cr;
                    totalDr += dr;
                    totalCr += cr;
                    string desc = !string.IsNullOrEmpty(l.LineDesc) ? l.LineDesc : l.EntryDesc ?? "";
                    rows.Add(new LedgerLine(l.EntryDate.ToString("yyyy-MM-dd", Inv), l.VoucherNumber ?? "", desc, dr, cr, balance));
                }
            if (rows.Count == 0 && opening == 0)
            {
                sections.Add(new LedgerSection { Account = account, Empty = true });
                continue;
            }
            sections.Add(new LedgerSection
            {
                Account = account, Rows = rows, Opening = Math.Abs(opening), OpeningSide = opening >= 0 ? "Dr" : "Cr",
                TotalDebit = totalDr, TotalCredit = totalCr, Closing = Math.Abs(balance), ClosingSide = balance >= 0 ? "Dr" : "Cr",
                Subtotal = balance,
            });
        }
        return sections;
    }
}

/// <summary>The chart codes the statements name (shared/coa.py ROLE_CODES), identical to the Python app.</summary>
public static class CoaCodes
{
    public const string RetainedEarnings = "3-02-01-02-0001";
    public const string Dividends = "3-02-02-01-0001";
    public const string OtherComprehensiveIncome = "3-02-03-01-0001";
}
