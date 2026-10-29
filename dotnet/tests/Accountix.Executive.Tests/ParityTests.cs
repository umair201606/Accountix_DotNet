using System.Globalization;
using System.Text.Json.Nodes;
using Accountix.Core.Data;
using Accountix.Executive.Services;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Executive.Tests;

/// <summary>
/// The executive figures against fixed expectations: the Python app's own functions
/// (shared/executive_reports.py, shared/integrity.py) run once on the golden company's data and on the
/// full-year Samba books, kept in Golden/ (Golden/capture/executive.py, full_year.py). Aging (FIFO),
/// party aging, the registers, scope, liquidity, profitability and its chart geometry, the party ledger
/// and every Books Integrity check, word for word. Accounts are named by code.
/// </summary>
public class ParityTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string Key(DateOnly? d) => d?.ToString("yyyy-MM-dd", Inv) ?? "none";
    private static DateOnly? AsOf(string? text) => text is null ? null : DateOnly.Parse(text, Inv);

    internal static object Side(AgingSide s, IReadOnlyDictionary<int, string> codes) => new
    {
        total = s.Total, parties = s.Parties, avg_days = s.AvgDays, oldest_days = s.OldestDays,
        oldest_name = s.OldestName, oldest_code = s.OldestCode,
        oldest_account = s.OldestAccountId is int i ? codes[i] : null, peak = s.Peak,
        buckets = s.Buckets.Select(b => new { key = b.Key, label = b.Label, amount = b.Amount, share = b.Share, width = b.Width }),
        top = s.Top.Select(t => new { account = codes[t.AccountId], name = t.Name, code = t.Code, amount = t.Amount, share = t.Share }),
    };

    internal static object Ages(Dictionary<int, PartyAge> ages, IReadOnlyDictionary<int, string> codes) =>
        ages.ToDictionary(a => codes[a.Key], a => new
        {
            avg_days = a.Value.AvgDays, oldest_days = a.Value.OldestDays,
            buckets = a.Value.Buckets.ToDictionary(b => b.Key, b => b.Value),
        });

    /// <summary>Amounts in register order; each row by its code (Python leaves the order of equal amounts open).</summary>
    internal static object Rows(List<PartyRow> rows, IReadOnlyDictionary<int, string> codes) => new
    {
        amounts = rows.Select(r => r.Amount),
        by_code = rows.ToDictionary(r => r.Code, r => new
        {
            account = codes[r.AccountId], name = r.Name, type = r.Type, group = r.GroupId is int g ? codes[g] : null,
            group_label = r.Group, debit = r.Debit, credit = r.Credit, side = r.Side,
        }),
    };

    internal static object Liq(Liquidity l) => new
    {
        net_assets = l.NetAssets, total_assets = l.TotalAssets, total_liabilities = l.TotalLiabilities,
        current_assets = l.CurrentAssets, current_liabilities = l.CurrentLiabilities, inventory = l.Inventory,
        current_ratio = l.CurrentRatio, quick_ratio = l.QuickRatio,
    };

    /// <summary>
    /// The checks as the capture wrote them: product ids in a finding ("#23 layers …") as the product's SKU.
    /// One deliberate difference is put back: the repair the reporting-table finding names is the .NET
    /// admin command now (`dotnet Accountix.Web.dll admin rebuild-gl-summary --apply`), not Python's script.
    /// </summary>
    internal static object Checks(List<IntegrityCheck> checks, IReadOnlyDictionary<int, string>? skus = null) =>
        checks.Select(c => new
        {
            key = c.Key, label = c.Label, ok = c.Ok, expected = c.Expected, actual = c.Actual,
            detail = Ids(c.Detail.Replace("run `dotnet Accountix.Web.dll admin rebuild-gl-summary --apply`", "run tools/rebuild_gl_summary.py",
                                          StringComparison.Ordinal), skus),
        });

    private static string Ids(string detail, IReadOnlyDictionary<int, string>? skus) =>
        skus is null ? detail
            : System.Text.RegularExpressions.Regex.Replace(detail, @"#(\d+)\b",
                m => "#{" + (skus.TryGetValue(int.Parse(m.Groups[1].Value, Inv), out var sku) ? sku : "?" + m.Groups[1].Value) + "}");

    public static IEnumerable<object?[]> AsOfDates() => [[null], ["2026-06-30"], ["2026-05-01"], ["2025-12-31"]];

    [ReferenceTheory]
    [MemberData(nameof(AsOfDates))]
    public async Task Aging_is_what_python_ages(string? asOfText)
    {
        await Exec.SeedAsync();
        var asOf = AsOf(asOfText);
        var codes = await Exec.CodesAsync(Exec.Company);
        await using var db = Exec.Db(Exec.Company);
        var (recv, pay) = await Exec.Reports(db).AgingAsync(asOf);
        Golden.Equal(Golden.Load("Aging")[Key(asOf)], new { receivable = Side(recv, codes), payable = Side(pay, codes) }, $"Aging {Key(asOf)}");
        Assert.True(recv.Parties > 0 && pay.Parties > 0, "the seed carries open balances on both sides");
    }

    [ReferenceFact]
    public async Task Fifo_ages_the_seeded_parties_as_documented()
    {
        await Exec.SeedAsync();
        await using var db = Exec.Db(Exec.Company);
        var reports = Exec.Reports(db);
        var ages = await reports.PartyAgingAsync(Exec.AsOf);
        int c1 = await Exec.AccountIdAsync("1-01-02-01-9101", Exec.Company);
        int c3 = await Exec.AccountIdAsync("1-01-02-01-9103", Exec.Company);
        int c4 = await Exec.AccountIdAsync("1-01-02-01-9104", Exec.Company);
        int c6 = await Exec.AccountIdAsync("1-01-02-01-9106", Exec.Company);
        // 1,000 invoiced 45 days ago, 400 received 5 days ago: 600 still owed, aged 45 days.
        Assert.Equal(45, ages[c1].OldestDays);
        Assert.Equal(600.0, ages[c1].Buckets["d31"]);
        // An advance of 350 before an invoice of 1,000: 650 owed at the invoice's age (20 days), not 1,000.
        Assert.Equal(650.0, ages[c3].Buckets["current"]);
        Assert.Equal(20, ages[c3].OldestDays);
        // Over-received: the receivable account sits on the payable side.
        var rows = await reports.PartyRowsAsync(asOf: Exec.AsOf);
        Assert.Equal(ExecutiveReports.Payable, rows.Single(r => r.AccountId == c4).Side);
        // A posting after the as-of day is not there at all.
        Assert.DoesNotContain(rows, r => r.AccountId == c6);
    }

    [ReferenceTheory]
    [MemberData(nameof(AsOfDates))]
    public async Task Party_aging_and_registers_are_what_python_gives(string? asOfText)
    {
        await Exec.SeedAsync();
        var asOf = AsOf(asOfText);
        var codes = await Exec.CodesAsync(Exec.Company);
        await using var db = Exec.Db(Exec.Company);
        var reports = Exec.Reports(db);
        var (r, p) = await reports.BothSidesAsync(asOf);
        Golden.Equal(Golden.Load("PartyAging")[Key(asOf)], new
        {
            ages = Ages(await reports.PartyAgingAsync(asOf), codes),
            rows = Rows(await reports.PartyRowsAsync(asOf: asOf), codes),
            recv = Rows(await reports.PartyRowsAsync(ExecutiveReports.Receivable, asOf, "exec cust"), codes),
            groups = (await reports.GroupsAsync()).Select(g => new { group = codes[g.Id], label = g.Label }),
            both = new { receivable = new { amount = r.Amount, parties = r.Parties }, payable = new { amount = p.Amount, parties = p.Parties } },
        }, $"PartyAging {Key(asOf)}");
    }

    private static async Task<List<string>> ScopeCodesAsync(IReadOnlyDictionary<int, string> codes)
    {
        await using var db = Exec.Db(Exec.Company);
        return (await Exec.Reports(db).ScopeAsync()).Keys.Select(k => codes[k]).Order(StringComparer.Ordinal).ToList();
    }

    [ReferenceFact]
    public async Task One_group_filter_and_a_parent_without_its_children_match_python()
    {
        var (td, tc) = await Exec.SeedAsync();
        var codes = await Exec.CodesAsync(Exec.Company);
        var golden = Golden.Load("Scope");
        Golden.Equal(golden["deep_both"], await ScopeCodesAsync(codes), "scope, both with sub-accounts");
        try
        {
            // Trade Debtors with sub-accounts, Trade Creditors on its own (no balance there: nothing from it).
            await using (var db0 = Exec.Db(Exec.Company))
                await Exec.Reports(db0).SetSelectionAsync([td.ToString(), tc.ToString()], [td.ToString()], null, DateTime.UtcNow);
            await using var db = Exec.Db(Exec.Company);
            var rows = await Exec.Reports(db).PartyRowsAsync(groupId: td);
            Golden.Equal(golden["td_deep_tc_alone"], new
            {
                scope = await ScopeCodesAsync(codes),
                rows_group_td = rows.Select(x => codes[x.AccountId]).Order(StringComparer.Ordinal),
            }, "scope, Trade Debtors deep + Trade Creditors alone");
            Assert.All(rows, x => Assert.Equal(td, x.GroupId));
        }
        finally
        {
            await Exec.ResetSelectionAsync(td, tc);
        }
    }

    public static IEnumerable<object?[]> LiquidityDates() => [[null], ["2026-06-30"], ["2025-09-30"]];

    [ReferenceTheory]
    [MemberData(nameof(LiquidityDates))]
    public async Task Liquidity_is_what_python_derives(string? asOfText)
    {
        await Exec.SeedAsync();
        var asOf = AsOf(asOfText);
        await using var db = Exec.Db(Exec.Company);
        var liq = await Exec.Reports(db).LiquidityAsync(asOf);
        Golden.Equal(Golden.Load("Liquidity")[Key(asOf)], Liq(liq), $"Liquidity {Key(asOf)}");
        Assert.True(liq.Inventory > 0, "the opening stock counts as inventory");
    }

    internal static object Profit(Profitability p) => new
    {
        months = p.Months.Select(m => new
        {
            start = m.Start.ToString("yyyy-MM-dd", Inv), label = m.Label, full_label = m.FullLabel,
            revenue = m.Revenue, cogs = m.Cogs, gross = m.Gross, net = m.Net,
        }),
        totals = new[] { "revenue", "cogs", "gross", "net" }.ToDictionary(k => k, k => p.Totals[k]),
        prior = p.Prior is { } pr
            ? new { revenue = pr.Revenue, cogs = pr.Cogs, gross = pr.Gross, net = pr.Net }
            : null,
        deltas = new[] { "revenue", "gross", "net" }.ToDictionary(k => k, k => p.Deltas[k]),
        peak = p.Peak, trough = p.Trough, margin = p.Margin,
    };

    internal static object? Geo(ProfitChart? g) => g is null ? null : new
    {
        zero_y = g.ZeroY, band = g.Band,
        ticks = g.Ticks.Select(t => new { v = t.V, y = t.Y, dp = t.Dp }),
        series = g.SeriesList.Select(s => new { points = s.Points, area = s.Area, last = new { x = s.Last.X, y = s.Last.Y } }),
        cols = g.Cols.Select(c => c.X),
    };

    [ReferenceTheory]
    [InlineData(null)]
    [InlineData("2026-06-30")]
    [InlineData("2026-02-14")]
    public async Task Profitability_and_its_chart_are_what_python_draws(string? asOfText)
    {
        await Exec.SeedAsync();
        var asOf = AsOf(asOfText);
        await using var db = Exec.Db(Exec.Company);
        var p = await Exec.Reports(db).ProfitabilityAsync(asOf: asOf);
        Golden.Equal(Golden.Load("Profitability")[Key(asOf)], new { p = Profit(p), geo = Geo(ProfitChart.From(p)) }, $"Profitability {Key(asOf)}");
    }

    internal static object Ledger(List<LedgerLine> lines) => lines.Select(l => new
    {
        date = l.Date.ToString("yyyy-MM-dd HH:mm:ss", Inv), voucher_type = l.VoucherType, voucher_number = l.VoucherNumber,
        description = l.Description, debit = l.Debit, credit = l.Credit,
    });

    [ReferenceFact]
    public async Task Party_ledger_lists_what_python_lists()
    {
        await Exec.SeedAsync();
        int c7 = await Exec.AccountIdAsync("1-01-02-01-9107", Exec.Company);
        await using var db = Exec.Db(Exec.Company);
        var reports = Exec.Reports(db);
        Golden.Equal("Ledger", new
        {
            all = Ledger(await reports.AccountLedgerAsync(c7, null)),
            asof = Ledger(await reports.AccountLedgerAsync(c7, new DateOnly(2025, 11, 20), 2)),
        });
    }

    [ReferenceFact]
    public async Task Every_integrity_check_gives_what_python_gives()
    {
        await Exec.SeedAsync();
        await using var db = Exec.Db(Exec.Company);
        var net = await new BooksIntegrity(db).RunAllAsync();
        Golden.Equal("Integrity", Checks(net));
        Assert.All(net, c => Assert.True(c.Ok, $"{c.Key}: {c.Detail}"));
    }

    /// <summary>
    /// Break the golden company's books on purpose (an unbalanced line, a drifted reporting row, a wrong
    /// on-hand figure, a stray layer value: the first of each by id, as the capture chose them), compare the
    /// findings with Python's word for word, then put every row back.
    /// </summary>
    [ReferenceFact]
    public async Task Failing_checks_say_what_python_says()
    {
        await Exec.SeedAsync();
        int cid = Exec.Company;
        string restore;
        await using (var db = Exec.Db(cid))
        {
            var line = await db.Set<Core.Entities.JournalLine>().Where(l => l.JournalEntry.IsPosted == true && l.Debit > 0).OrderBy(l => l.Id).FirstAsync();
            var glb = await db.Set<Core.Entities.GlDailyBalance>().OrderBy(g => g.Id).FirstAsync();
            var product = await db.Set<Core.Entities.InvProduct>().Where(p => p.CurrentStock > 0).OrderBy(p => p.Id).FirstAsync();
            var layer = await db.Set<Core.Entities.StockLayer>().Where(l => l.QtyRemaining != 0).OrderBy(l => l.Id).FirstAsync();
            restore = string.Join(";", [
                $"UPDATE journal_lines SET debit = {line.Debit!.Value.ToString(Inv)} WHERE id = {line.Id}",
                $"UPDATE gl_daily_balances SET debit = {glb.Debit.ToString(Inv)} WHERE id = {glb.Id}",
                $"UPDATE inv_products SET current_stock = {product.CurrentStock!.Value.ToString(Inv)} WHERE id = {product.Id}",
                $"UPDATE stock_layers SET value_remaining = {layer.ValueRemaining.ToString(Inv)} WHERE id = {layer.Id}",
            ]);
            await db.Database.ExecuteSqlAsync($"UPDATE journal_lines SET debit = debit + 12.5 WHERE id = {line.Id}");
            await db.Database.ExecuteSqlAsync($"UPDATE gl_daily_balances SET debit = debit + 1 WHERE id = {glb.Id}");
            await db.Database.ExecuteSqlAsync($"UPDATE inv_products SET current_stock = current_stock + 2.5 WHERE id = {product.Id}");
            await db.Database.ExecuteSqlAsync($"UPDATE stock_layers SET value_remaining = value_remaining + 7.25 WHERE id = {layer.Id}");
        }
        try
        {
            await using var db = Exec.Db(cid);
            var net = await new BooksIntegrity(db).RunAllAsync();
            var skus = await db.Set<Core.Entities.InvProduct>().AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Sku);
            Golden.Equal("IntegrityBroken", Checks(net, skus));
            foreach (string key in new[] { "trial_balance", "journals_balanced", "reporting_table", "stock_layers", "stock_quantity" })
                Assert.False(net.Single(c => c.Key == key).Ok, key);
        }
        finally
        {
            await using var db = Exec.Db(cid);
            await db.Database.ExecuteSqlRawAsync(restore);
        }
        await using (var db = Exec.Db(cid))
            Assert.All(await new BooksIntegrity(db).RunAllAsync(), c => Assert.True(c.Ok, $"{c.Key} after restore: {c.Detail}"));
    }

    // ── the full-year Samba books (read only) ───────────────────────────

    private static AccountixDbContext PerfDb()
    {
        var company = new CompanyContext();
        company.Set(Perf.CompanyId);
        return Perf.NewDb(company);
    }

    private static async Task<Dictionary<int, string>> PerfCodesAsync()
    {
        await using var db = PerfDb();
        return await db.Set<Core.Entities.ChartOfAccount>().AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Code);
    }

    /// <summary>Every check on the full-year books agrees with Python's (captured on the same database), and passes.</summary>
    [PerfFact]
    public async Task On_a_full_year_of_books_every_check_matches_python()
    {
        await using var db = PerfDb();
        var net = await new BooksIntegrity(db).RunAllAsync();
        Golden.Equal(Golden.Load("FullYear")["integrity"], Checks(net), "full-year integrity");
    }

    /// <summary>The full year with Samba's selection (Trade Debtors + Trade Creditors, with sub-accounts).</summary>
    [PerfFact]
    public async Task On_a_full_year_aging_registers_and_liquidity_match_python()
    {
        var codes = await PerfCodesAsync();
        await using var db = PerfDb();
        Assert.Equal(2, await db.Set<Core.Entities.ExecAccountSelection>().CountAsync());
        var reports = Exec.Reports(db);
        var (recv, pay) = await reports.AgingAsync(Perf.YearEnd);
        var (recvNow, payNow) = await reports.AgingAsync(null);
        var profit = await reports.ProfitabilityAsync();
        var golden = Golden.Load("FullYear");
        Golden.Equal(golden["aging"], new { receivable = Side(recv, codes), payable = Side(pay, codes) }, "full-year aging at year end");
        Golden.Equal(golden["now"], new { receivable = Side(recvNow, codes), payable = Side(payNow, codes) }, "full-year aging today");
        Golden.Equal(golden["liq"], Liq(await reports.LiquidityAsync(Perf.YearEnd)), "full-year liquidity");
        Golden.Equal(golden["ages"], Ages(await reports.PartyAgingAsync(Perf.YearEnd), codes), "full-year party aging");
        Golden.Equal(golden["rows"], Rows(await reports.PartyRowsAsync(), codes), "full-year registers");
        Golden.Equal(golden["profit"], Profit(profit), "full-year profitability");
        Assert.True(recv.Parties > 10, $"{recv.Parties} receivable parties");
    }
}

/// <summary>A theory that needs the reference database, as ReferenceFact.</summary>
public sealed class ReferenceTheoryAttribute : TheoryAttribute
{
    public ReferenceTheoryAttribute()
    {
        if (!Reference.Available)
            Skip = "Reference database not available";
    }
}
