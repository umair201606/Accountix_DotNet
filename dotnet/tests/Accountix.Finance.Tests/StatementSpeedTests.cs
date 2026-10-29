using System.Diagnostics;
using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.Finance.Services;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Accountix.Finance.Tests;

/// <summary>
/// The statements on a full financial year of Samba (read only): one month, the
/// whole year and a three-column comparative, with their Excel / PDF files, against
/// the budgets in docs/SPEED_AND_CONCURRENCY.md (Python's times on the same URLs are kept in
/// docs/modules/finance.md as history).
/// </summary>
public class StatementSpeedTests(ITestOutputHelper output)
{
    private static string Month => $"filter_mode=custom&from={Perf.BusiestMonthStart:yyyy-MM-dd}&to={Perf.BusiestMonthEnd:yyyy-MM-dd}";

    private static (string Year, string Comp) Windows()
    {
        var company = new CompanyContext();
        company.Set(Perf.CompanyId);
        using var db = Perf.NewDb(company);
        var periods = db.Set<AccountingPeriod>().OrderBy(p => p.StartDate).Select(p => new { p.Id, p.StartDate }).ToList();
        int year = periods.First(p => p.StartDate == Perf.YearStart).Id;
        string others = string.Join(",", periods.Select(p => p.Id));
        return ($"filter_mode=period&period_id={year}", $"filter_mode=comparative&period_id={year}&comp_mode=1&comp_period_ids={others}");
    }

    private async Task TimeAsync(params (string Url, int Budget)[] pages)
    {
        await using var host = await ModuleHost.StartOnAsync(Perf.ConnectionString, typeof(FinanceModule).Assembly);
        var c = host.ClientFor(Perf.AdminUserId, Perf.CompanyId);
        var mine = new List<PageTiming>();
        foreach (var (url, budget) in pages)
            mine.Add((await host.MeasureAsync(c, url)).Within(budget, 40));
        // Python's times on the same URLs are recorded in docs/modules/finance.md (history: the Python app is retired).
        output.WriteLine("| URL | .NET ms | queries | budget |");
        for (int i = 0; i < pages.Length; i++)
            output.WriteLine($"| {pages[i].Url} | {mine[i].Elapsed.TotalMilliseconds:F0} | {mine[i].Queries} | {pages[i].Budget} |");
    }

    private static (string, int)[] Statement(string page)
    {
        var (year, comp) = Windows();
        return
        [
            ($"/finance/{page}?{Month}", 800), ($"/finance/{page}?{year}", 1500), ($"/finance/{page}?{comp}", 1500),
            ($"/finance/{page}?{year}&format=excel", 3000), ($"/finance/{page}?{year}&format=pdf", 3000),
            ($"/finance/{page}?{comp}&format=excel", 3000), ($"/finance/{page}?{comp}&format=pdf", 3000),
        ];
    }

    [PerfFact] public Task Trial_balance_is_quick() => TimeAsync(Statement("trial-balance"));
    [PerfFact] public Task Profit_and_loss_is_quick() => TimeAsync(Statement("profit-loss"));
    [PerfFact] public Task Balance_sheet_is_quick() => TimeAsync(Statement("balance-sheet"));
    [PerfFact] public Task Socie_is_quick() => TimeAsync(Statement("socie"));
    [PerfFact] public Task Cash_flow_is_quick() => TimeAsync(Statement("cash-flow"));

    [PerfFact]
    public Task Pl_by_label_and_the_dashboard_are_quick()
    {
        string year = $"from={Perf.YearStart:yyyy-MM-dd}&to={Perf.YearEnd:yyyy-MM-dd}";
        return TimeAsync(($"/finance/label-pl?from={Perf.BusiestMonthStart:yyyy-MM-dd}&to={Perf.BusiestMonthEnd:yyyy-MM-dd}", 800),
                         ($"/finance/label-pl?{year}", 1500), ($"/finance/label-pl?{year}&format=excel", 3000),
                         ($"/finance/label-pl?{year}&format=pdf", 3000), ("/finance/", 800));
    }

    [PerfFact]
    public Task General_ledger_all_accounts_for_a_month_is_quick() =>
        TimeAsync(($"/finance/ledger?{Month}&mode=all", 1500), ($"/finance/ledger?{Month}&mode=all&format=excel", 3000),
                  ($"/finance/ledger?{Month}&mode=all&format=pdf", 3000));

    [PerfFact]
    public async Task Direct_cash_flow_for_the_year_is_quick()
    {
        // Samba is set to the indirect method (read only here): the direct engine is timed on its own.
        var company = new CompanyContext();
        company.Set(Perf.CompanyId);
        await using var db = Perf.NewDb(company);
        var st = new FinanceStatements(new FinanceData(db));
        await st.CashFlowAsync(Perf.YearStart, Perf.YearEnd, "direct", [], []);
        var best = TimeSpan.MaxValue;
        CashFlowResult? r = null;
        for (int i = 0; i < 3; i++)
        {
            var sw = Stopwatch.StartNew();
            r = await new FinanceStatements(new FinanceData(db)).CashFlowAsync(Perf.YearStart, Perf.YearEnd, "direct", [], []);
            best = sw.Elapsed < best ? sw.Elapsed : best;
        }
        output.WriteLine($"direct cash flow, year: .NET {best.TotalMilliseconds:F0} ms");
        Assert.True(best.TotalMilliseconds <= 1500);
        // The figures are the Python engine's for the same year (Golden/capture/perf_direct_cash_flow.py).
        static IEnumerable<object> Items(List<CashItem> items) => items.Select(i => new { name = i.Name, value = i.Value });
        Golden.Equal("CashFlowDirectPerf", new
        {
            op_items = Items(r!.Operating), inv_items = Items(r.Investing), fin_items = Items(r.Financing),
            opening_cash = r.OpeningCash, closing_cash = r.ClosingCash,
        }, tolerance: 1e-12);
    }
}
