using System.Text.Json.Nodes;
using Accountix.Core.Entities;
using Accountix.Finance.Services;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Accountix.Finance.Tests;

/// <summary>
/// Every statement's figures for the golden books (StatementSeed: built through .NET) equal the
/// Python app's figures for the same books, captured once (Golden/capture/statements.py: the
/// Python route with render_template recorded). Each query of a fixture is run through the .NET
/// service with this copy's period / label ids and compared row by row, to 1e-9.
/// </summary>
public class StatementGoldenTests(ITestOutputHelper output)
{
    /// <summary>The fixtures' queries, as the capture wrote them (the keys of each file).</summary>
    private static readonly string[] Queries =
    [
        "filter_mode=period&period_id={FY 2026}",
        "filter_mode=custom&from=2025-03-01&to=2026-06-30&label_ids={Proj Alpha}",
        "filter_mode=comparative&period_id={FY 2026}&comp_mode=1&comp_period_ids={FY 2025},{FY 2024}",
        "filter_mode=custom&from=2025-01-01&to=2026-12-31&label_ids={Proj Alpha},{Proj Beta}",
        "filter_mode=custom&from=2025-06-01&to=2025-06-30",
    ];

    private static async Task<(ReportPeriod P, FinanceStatements St, Core.Data.AccountixDbContext Db)> NetAsync(string symbolic)
    {
        var db = StatementSeed.Db();
        var p = await StatementSeed.ResolveAsync(db, await StatementSeed.RealAsync(symbolic));
        return (p, new FinanceStatements(new FinanceData(db)), db);
    }

    private static async Task<JsonObject> EachQueryAsync(IEnumerable<string> queries, Func<string, Task<object>> one)
    {
        await StatementSeed.EnsureAsync();
        var all = new JsonObject();
        foreach (string q in queries)
            all[q] = Golden.Node(await one(q));
        return all;
    }

    [ReferenceFact]
    public async Task Trial_balance_is_the_python_trial_balance()
    {
        var net = await EachQueryAsync(Queries, async q =>
        {
            var (p, st, db) = await NetAsync(q);
            await using var _ = db;
            var r = await st.TrialBalanceAsync(p.From, p.To!.Value, p.ActiveComp, p.LabelIds);
            return new
            {
                rows = r.Rows.Select(a => new
                {
                    code = a.Code, name = a.Name, type = a.Type, is_subtotal = a.IsSubtotal,
                    dr_opening = a.DrOpening, cr_opening = a.CrOpening, dr_movement = a.DrMovement, cr_movement = a.CrMovement,
                    dr_closing = a.DrClosing, cr_closing = a.CrClosing, comp_dr_closing = a.CompDrClosing, comp_cr_closing = a.CompCrClosing,
                }),
                totals = new
                {
                    total_dr_opening = r.TotalDrOpening, total_cr_opening = r.TotalCrOpening, total_dr_movement = r.TotalDrMovement,
                    total_cr_movement = r.TotalCrMovement, total_dr_closing = r.TotalDrClosing, total_cr_closing = r.TotalCrClosing,
                },
                comp_class_totals = r.CompTotals.Select(t => new { total_dr_closing = t.Dr, total_cr_closing = t.Cr }),
            };
        });
        Golden.Equal("TrialBalance", net);
        output.WriteLine($"TB: {Queries.Length} queries equal");
    }

    [ReferenceFact]
    public async Task Profit_and_loss_is_the_python_profit_and_loss()
    {
        var net = await EachQueryAsync(Queries, async q =>
        {
            var (p, st, db) = await NetAsync(q);
            await using var _ = db;
            var (rows, netProfit) = await st.ProfitLossAsync(p.From, p.To, p.ActiveComp, p.LabelIds);
            return new
            {
                pl_rows = rows.Select(a => new
                {
                    kind = a.Kind, label = a.Label, code = a.Code, name = a.Name,
                    amount = a.Kind == "header" ? (decimal?)null : a.Amount, comp_amounts = a.CompAmounts,
                }),
                net_profit = netProfit,
            };
        });
        Golden.Equal("ProfitLoss", net);
    }

    [ReferenceFact]
    public async Task Pl_by_label_is_the_python_pl_by_label()
    {
        string[] queries = ["from=2025-01-01&to=2026-12-31", "from=2025-01-01&to=2026-12-31&label_id={Proj Beta}", "run=1&from=2026-01-01&to=2026-06-30"];
        var net = await EachQueryAsync(queries, async q =>
        {
            await using var db = StatementSeed.Db();
            var st = new FinanceStatements(new FinanceData(db));
            var labelsAll = await db.Set<ProjectLabel>().OrderBy(l => l.Name).ToListAsync();
            var qs = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(await StatementSeed.RealAsync(q));
            var picked = qs.TryGetValue("label_id", out var lid) ? lid.Select(x => int.Parse(x!)).ToList() : [];
            var labels = labelsAll.Where(l => picked.Count == 0 || picked.Contains(l.Id)).ToList();
            var lp = await st.LabelPlAsync(DateOnly.Parse(qs["from"]!), DateOnly.Parse(qs["to"]!), labels, picked.Count == 0);
            return new
            {
                labels = labels.Select(l => l.Name), loaded = true,
                rows = lp.Rows.Select(a => a.Kind == "header"
                    ? (object)new { kind = "header", amount = (decimal?)null, by_label = (List<decimal>?)null, unlabelled = (decimal?)null }
                    : new { kind = a.Kind, amount = (decimal?)a.Amount, by_label = a.ByLabel, unlabelled = a.Unlabelled }),
                label_nets = lp.Nets, net_profit = lp.Net, unlabelled_net = lp.UnlabelledNet,
            };
        });
        Golden.Equal("LabelPl", net);
    }

    [ReferenceFact]
    public async Task Balance_sheet_is_the_python_balance_sheet()
    {
        var net = await EachQueryAsync(Queries, async q =>
        {
            var (p, st, db) = await NetAsync(q);
            await using var _ = db;
            var r = await st.BalanceSheetAsync(p.To!.Value, p.ActiveComp, p.LabelIds);
            static IEnumerable<object> Items(List<BsItem> items) => items.Select(i => new { code = i.Code, name = i.Name, amount = i.Amount });
            static IEnumerable<object> Merged(List<BsMerged> items) => items.Select(i => new { code = i.Code, name = i.Name, amounts = i.Amounts });
            return new
            {
                assets = Items(r.Base.Assets), liabilities = Items(r.Base.Liabilities), equity = Items(r.Base.Equity),
                total_assets = r.Base.TotalAssets, total_liabilities = r.Base.TotalLiabilities, total_equity = r.Base.TotalEquity,
                merged_assets = Merged(r.MergedAssets), merged_liabilities = Merged(r.MergedLiabilities), merged_equity = Merged(r.MergedEquity),
                comp_totals = r.Comp.Select(c => new { total_equity = c.TotalEquity }),
            };
        });
        Golden.Equal("BalanceSheet", net);
    }

    [ReferenceFact]
    public async Task Socie_is_the_python_socie()
    {
        var net = await EachQueryAsync(Queries, async q =>
        {
            var (p, st, db) = await NetAsync(q);
            await using var _ = db;
            var (cols, rows) = await st.SocieAsync(FinanceStatements.SocieSpecs(p.From!.Value, p.To!.Value, p.ActiveComp), p.LabelIds);
            var codes = await db.Set<ChartOfAccount>().ToDictionaryAsync(a => a.Id, a => a.Code);
            // A component column of one account is keyed "a<id>": the capture wrote "a:<code>".
            string Key(string k) => System.Text.RegularExpressions.Regex.IsMatch(k, @"^a\d+$") ? "a:" + codes[int.Parse(k[1..])] : k;
            return new
            {
                columns = cols.Select(c => new { key = Key(c.Key), label = c.Label }),
                rows = rows.Select(r => new
                {
                    kind = r.Kind, label = r.Label, indent = r.Indent,
                    values = cols.ToDictionary(c => Key(c.Key), c => r.Values.TryGetValue(c.Key, out var v) ? v : null),
                }),
            };
        });
        Golden.Equal("Socie", net);
    }

    [ReferenceFact]
    public async Task Cash_flow_indirect_and_direct_are_the_python_cash_flow()
    {
        var all = new JsonObject();
        foreach (string method in new[] { "indirect", "direct" })
            all[method] = await EachQueryAsync(Queries, async q =>
            {
                var (p, st, db) = await NetAsync(q);
                await using var _ = db;
                var r = await st.CashFlowAsync(p.From!.Value, p.To!.Value, method, p.ActiveComp, p.LabelIds);
                static IEnumerable<object> Items(List<CashItem> items) => items.Select(i => new { name = i.Name, value = i.Value, comps = i.Comps });
                return new
                {
                    method = r.Method, op_items = Items(r.Operating), inv_items = Items(r.Investing), fin_items = Items(r.Financing),
                    net_operating = r.NetOperating, net_investing = r.NetInvesting, net_financing = r.NetFinancing, net_change = r.NetChange,
                    opening_cash = r.OpeningCash, closing_cash = r.ClosingCash, cash_movement = r.CashMovement,
                    comp_item_maps = r.CompMaps,
                };
            });
        Golden.Equal("CashFlow", all);
    }

    [ReferenceFact]
    public async Task Ledger_sections_are_the_python_ledger()
    {
        await StatementSeed.EnsureAsync();
        var (alpha, _) = await StatementSeed.LabelsAsync();
        await using var db = StatementSeed.Db();
        var data = new FinanceData(db);
        var st = new FinanceStatements(data);
        var chart = await data.ChartAsync();
        var all = chart.ById.Values.Where(a => a.Active).OrderBy(a => a.Code, StringComparer.Ordinal).Select(a => a.Id).ToList();
        var leaves = FinanceStatements.LeafDescendants(chart, chart.ByCode["5-02"].Id).Order().ToList();
        var net = new JsonObject();
        foreach (var (name, from, to, labels, accounts) in new (string, string?, string?, int[], List<int>)[]
                 {
                     ("2026-01-01 to 2026-06-30, all accounts", "2026-01-01", "2026-06-30", [], all),
                     ("2025-06-01 to 2025-06-30, all accounts, Proj Alpha", "2025-06-01", "2025-06-30", [alpha], all),
                     ("no dates, all accounts", null, null, [], all),
                     ("2025-01-01 to 2026-12-31, leaves of 5-02", "2025-01-01", "2026-12-31", [], leaves),
                 })
        {
            var sections = await st.LedgerSectionsAsync(accounts, from is null ? null : DateOnly.Parse(from), to is null ? null : DateOnly.Parse(to), labels);
            net[name] = Golden.Node(sections.Select(s => new
            {
                account = s.Account.Code, empty = s.Empty, opening = s.Opening, opening_side = s.OpeningSide, closing = s.Closing,
                closing_side = s.ClosingSide, total_debit = s.TotalDebit, total_credit = s.TotalCredit,
                rows = s.Rows.Select(r => new { date = r.Date, voucher = r.Voucher, description = r.Description, debit = r.Debit, credit = r.Credit, balance = r.Balance }),
            }));
        }
        Golden.Equal("LedgerSections", net);
    }

    [ReferenceFact]
    public async Task Period_resolution_is_the_python_resolution()
    {
        string[] queries =
        [
            "", "filter_mode=period&period_id={FY 2026}", "filter_mode=custom&from=2026-02-03&to=05/06/2026",
            "filter_mode=&period_id={FY 2026}&from=&to=", "filter_mode=custom&from=&to=", "filter_mode=custom&period_id={FY 2026}&from=2026-01-01",
            "filter_mode=period&period_id=abc&to=2026-03-31", "filter_mode=comparative&period_id={FY 2026}&comp_mode=1&comp_period_ids=x,{FY 2026}, 3",
        ];
        await StatementSeed.EnsureAsync();
        Dictionary<int, string> names;
        await using (var db0 = StatementSeed.Db())
            names = await db0.Set<AccountingPeriod>().ToDictionaryAsync(p => p.Id, p => p.PeriodName!);
        string Name(int id) => names.TryGetValue(id, out var n) ? n : $"#{id}";
        // Keyed "?" + query, as the capture wrote them (the first query is empty).
        var net = await EachQueryAsync(queries.Select(q => "?" + q), async q =>
        {
            await using var db = StatementSeed.Db();
            var p = await StatementSeed.ResolveAsync(db, await StatementSeed.RealAsync(q[1..]));
            return new
            {
                from = p.From?.ToString("yyyy-MM-dd"), to = p.To?.ToString("yyyy-MM-dd"),
                selected = p.SelectedPeriodId is int s ? Name(s) : null, filter_mode = p.FilterMode, from_str = p.FromStr, to_str = p.ToStr,
                comp_mode = p.CompMode, comp_periods = p.CompPeriods.Select(c => Name(c.Id)),
            };
        });
        Golden.Equal("PeriodResolution", net);
    }
}
