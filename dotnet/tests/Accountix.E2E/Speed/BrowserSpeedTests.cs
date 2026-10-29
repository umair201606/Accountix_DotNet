using System.Text.Json;
using Accountix.Core.Entities;
using Accountix.E2E.Harness;
using Accountix.TestSupport;
using Xunit.Abstractions;

namespace Accountix.E2E.Speed;

/// <summary>
/// The biggest pages, timed in the browser on the full-year speed-test database
/// (dotnet/tools/perf.json: Samba, ~7,500 documents; read-only): navigation start →
/// load event, desktop, after one warm-up, best of three. Budget 2 s
/// (docs/SPEED_AND_CONCURRENCY.md sets the server-side budgets; this is what a person waits).
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class BrowserSpeedTests(AccountingPerfApp accounting, FullPerfApp full, ITestOutputHelper output)
    : IClassFixture<AccountingPerfApp>, IClassFixture<FullPerfApp>
{
    private const double BudgetMs = 2000;

    private static string Month => Perf.BusiestMonth;
    private static string YearFrom => Perf.YearStart.ToString("yyyy-MM-dd");
    private static string YearTo => Perf.YearEnd.ToString("yyyy-MM-dd");
    private static string MonthFrom => Perf.BusiestMonthStart.ToString("yyyy-MM-dd");
    private static string MonthTo => Perf.BusiestMonthEnd.ToString("yyyy-MM-dd");

    private static int YearPeriodId()
    {
        using var db = Db.For(Perf.CompanyId, Perf.ConnectionString);
        return db.Set<AccountingPeriod>().Where(p => p.StartDate <= Perf.YearStart && p.EndDate >= Perf.YearEnd)
            .OrderBy(p => p.Id).Select(p => p.Id).First();
    }

    // ── Accounting (done) ──

    [PerfE2EFact("Accounting")]
    public Task Voucher_register_for_the_busiest_month() =>
        TimeAsync(accounting, $"/accounting/registers/vouchers?mode=month&month={Month}&view=1", ".content-area");

    [PerfE2EFact("Accounting")]
    public Task Voucher_register_for_the_year() =>
        TimeAsync(accounting, $"/accounting/registers/vouchers?mode=year&period_id={YearPeriodId()}&view=1", ".content-area");

    /// <summary>The way a person usually builds it: open the register, press View; rows stream in 200 at a time.</summary>
    [PerfE2EFact("Accounting")]
    public async Task Voucher_register_for_the_year_built_by_pressing_view()
    {
        await using var s = await accounting.SessionAsync(Viewport.Desktop, Perf.AdminUserId, Perf.CompanyId, test: "speed_register_view");
        var p = s.Page;
        string url = $"/accounting/registers/vouchers?mode=year&period_id={YearPeriodId()}";
        var runs = new List<double>();
        for (int i = 0; i < 4; i++)   // the first is the warm-up
        {
            await s.GoAsync(url, checkLayout: false);
            double ms = await p.EvaluateAsync<double>("""
                () => new Promise(resolve => {
                  const t0 = performance.now();
                  const loaded = document.getElementById('regLoaded');
                  document.getElementById('regLoadBtn').click();
                  const done = () => {
                    const btn = document.getElementById('regLoadBtn');
                    const total = parseInt(document.getElementById('rowCount').textContent || '0', 10);
                    const rows = document.querySelectorAll('#regBody tr').length;
                    if (!btn.disabled && loaded.style.display !== 'none' && total > 0 && rows >= total) {
                      requestAnimationFrame(() => resolve(performance.now() - t0));
                    } else setTimeout(done, 10);
                  };
                  done();
                })
                """);
            if (i > 0)
                runs.Add(ms);
        }
        int rowsShown = await p.Locator("#regBody tr").CountAsync();
        string line = $"{url} + View: all {rowsShown} rows in {runs.Min():F0} ms; runs {string.Join(" / ", runs.Select(r => r.ToString("F0")))} ms";
        output.WriteLine(line);
        Console.WriteLine("[speed] " + line);
        Assert.True(runs.Min() <= BudgetMs, $"Too slow in the browser (budget {BudgetMs:F0} ms): {line}");
        await s.AssertCleanAsync();
    }

    [PerfE2EFact("Accounting")]
    public Task Voucher_list() => TimeAsync(accounting, "/accounting/vouchers/list", ".voucher-table");

    [PerfE2EFact("Accounting")]
    public Task Accounting_dashboard() => TimeAsync(accounting, "/accounting/", ".content-area");

    [PerfE2EFact("Accounting")]
    public Task Chart_of_accounts() => TimeAsync(accounting, "/accounting/coa/", "#coaTree");

    // ── the rest, as their modules are marked done ──

    [PerfE2EFact("Parties")]
    public Task Sales_invoice_register_for_the_busiest_month() =>
        TimeAsync(full, $"/invoicing/registers/?doc=sales&mode=month&month={Month}&view=1", ".content-area");

    [PerfE2EFact("Finance")]
    public Task Trial_balance_for_the_year() =>
        TimeAsync(full, $"/finance/trial-balance?filter_mode=custom&from={YearFrom}&to={YearTo}", ".content-area");

    [PerfE2EFact("Finance")]
    public Task Profit_and_loss_for_the_year() =>
        TimeAsync(full, $"/finance/profit-loss?filter_mode=custom&from={YearFrom}&to={YearTo}", ".content-area");

    [PerfE2EFact("Finance")]
    public Task Balance_sheet_for_the_year() =>
        TimeAsync(full, $"/finance/balance-sheet?filter_mode=custom&from={YearFrom}&to={YearTo}", ".content-area");

    [PerfE2EFact("Finance")]
    public Task General_ledger_all_accounts_for_the_busiest_month() =>
        TimeAsync(full, $"/finance/ledger?filter_mode=custom&from={MonthFrom}&to={MonthTo}&mode=all", ".content-area");

    private async Task TimeAsync(AppFixture app, string url, string mustShow)
    {
        await using var s = await app.SessionAsync(Viewport.Desktop, Perf.AdminUserId, Perf.CompanyId,
            test: "speed_" + new string(url.Where(char.IsLetterOrDigit).Take(40).ToArray()));
        await s.GoAsync(url, checkLayout: false);   // warm-up: JIT, views, pools, browser cache
        var runs = new List<(double Load, double Dom, double Ttfb, long Bytes)>();
        for (int i = 0; i < 3; i++)
        {
            await s.GoAsync(url, checkLayout: false, networkIdle: false);
            var t = await s.Page.EvaluateAsync<JsonElement>("""
                () => new Promise(resolve => {
                  const done = () => {
                    const n = performance.getEntriesByType('navigation')[0];
                    if (!n || !n.loadEventEnd) { setTimeout(done, 20); return; }
                    resolve({ load: n.loadEventEnd - n.startTime, dom: n.domContentLoadedEventEnd - n.startTime,
                              ttfb: n.responseStart - n.startTime, bytes: n.transferSize || n.encodedBodySize || 0 });
                  };
                  done();
                })
                """);
            runs.Add((t.GetProperty("load").GetDouble(), t.GetProperty("dom").GetDouble(),
                      t.GetProperty("ttfb").GetDouble(), (long)t.GetProperty("bytes").GetDouble()));
        }
        var best = runs.MinBy(r => r.Load);
        Assert.True(await s.Page.Locator(mustShow).First.IsVisibleAsync(), $"{url}: {mustShow} not shown");
        string line = $"{url}: load {best.Load:F0} ms (DOM {best.Dom:F0} ms, first byte {best.Ttfb:F0} ms, {best.Bytes / 1024} KB); " +
                      $"runs {string.Join(" / ", runs.Select(r => r.Load.ToString("F0")))} ms";
        output.WriteLine(line);
        Console.WriteLine("[speed] " + line);
        Assert.True(best.Load <= BudgetMs, $"Too slow in the browser (budget {BudgetMs:F0} ms): {line}");
        await s.SettleAsync();
        await s.AssertCleanAsync();
    }
}
