using System.Diagnostics;
using System.Globalization;
using System.Net;
using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.Executive.Services;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Executive.Tests;

/// <summary>
/// Books Integrity while six people post, six admins saving the selection at once (reference company B:
/// the races post vouchers, so never in the golden company), and the speed of every executive page on the
/// full-year Samba books (docs/SPEED_AND_CONCURRENCY.md).
/// </summary>
public class ManyUsersAndSpeedTests
{
    private static int B => Exec.CompanyB;

    private static async Task<(int Cash, int Expense)> AccountsAsync()
    {
        await using var db = Exec.Db(B);
        return (await db.Set<ChartOfAccount>().Where(a => a.Code == "1-01-01-01-0001").Select(a => a.Id).SingleAsync(),
                await db.Set<ChartOfAccount>().Where(a => a.Code == "5-04-01-01-0001").Select(a => a.Id).SingleAsync());
    }

    private static List<KeyValuePair<string, string>> Cpv(int cash, int expense, string note, string amount = "40") =>
    [
        new("action", "approve"), new("voucher_type", "CPV"), new("voucher_date", "2026-05-12T11:00"),
        new("cash_bank_account_id", cash.ToString()), new("label_id", ""), new("notes", note),
        new("account_id[]", expense.ToString()), new("description[]", note), new("debit[]", amount), new("credit[]", ""), new("label_id[]", ""),
    ];

    private static bool AllPass(string html) => System.Net.WebUtility.HtmlDecode(html).Contains("All checks pass — the general ledger agrees", StringComparison.Ordinal);

    /// <summary>The failing rows of an integrity page, for the assertion message.</summary>
    private static string Failing(string html)
    {
        var labels = Hosts.Texts(html, "strong");
        var status = Hosts.Texts(html, "bi-status");
        var detail = Hosts.Texts(html, "bi-detail");
        var bad = Enumerable.Range(0, Math.Min(labels.Count, Math.Min(status.Count, detail.Count)))
            .Where(i => status[i] != "Pass").Select(i => $"{labels[i]}: {detail[i]}");
        return string.Join(" | ", bad) + (html.Contains("bi-banner") ? "" : html[..Math.Min(html.Length, 600)]);
    }

    [ReferenceFact]
    public async Task Books_integrity_still_passes_while_six_people_post_vouchers()
    {
        var (cash, expense) = await AccountsAsync();
        await using var host = await Hosts.WithAccountingAsync();
        var checker = host.ClientFor(Exec.OwnerB, B);
        string before = await checker.HtmlAsync("/executive/integrity?run=1");
        Assert.True(AllPass(before), "the books pass before the race: " + Failing(before));

        var posters = host.ClientsFor(Exec.OwnerB, B, 6);
        var tokens = await Task.WhenAll(posters.Select(c => c.TokenFromAsync("/accounting/vouchers?type=CPV")));
        var readers = host.ClientsFor(Exec.OwnerB, B, 3);
        string tag = Guid.NewGuid().ToString("N")[..6];
        var integrityPages = new System.Collections.Concurrent.ConcurrentBag<string>();

        var race = Race.RunAsync(9, async i =>
        {
            if (i < 6)
                return await posters[i].PostFormWithTokenAsync("/accounting/vouchers", Cpv(cash, expense, $"Exec race {tag} {i}", $"{40 + i}.25"), tokens[i]);
            HttpResponseMessage last = null!;
            for (int k = 0; k < 3; k++)
            {
                last = await readers[i - 6].GetAsync("/executive/integrity?run=1");
                integrityPages.Add(await last.Content.ReadAsStringAsync());
            }
            return last;
        });
        var rs = await race;
        Race.NoServerErrors(rs);
        Assert.All(rs.Take(6), r => Assert.Equal(HttpStatusCode.Redirect, r.StatusCode));
        Assert.Equal(9, integrityPages.Count);
        Assert.All(integrityPages, p => Assert.True(AllPass(p), "every run during the race saw consistent books: " + Failing(p)));
        await using var db = Exec.Db(B);
        Assert.Equal(6, await db.Set<AccountingVoucher>().CountAsync(v => v.Notes!.StartsWith($"Exec race {tag}") && v.Status == "approved"));
        Assert.All(await new BooksIntegrity(db).RunAllAsync(), c => Assert.True(c.Ok, $"{c.Key}: {c.Detail}"));
    }

    [ReferenceFact]
    public async Task Six_admins_saving_the_selection_at_once_leave_one_whole_selection()
    {
        int td = await Exec.AccountIdAsync("1-01-02-01", B), tc = await Exec.AccountIdAsync("2-01-01-01", B);
        try
        {
            await using var host = await Hosts.ExecutiveAsync();
            var clients = host.ClientsFor(Exec.OwnerB, B, 6);
            var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync("/executive/settings")));
            // Half pick Trade Debtors (with sub-accounts), half Trade Creditors.
            var rs = await Race.RunAsync(6, i => clients[i].PostFormWithTokenAsync("/executive/settings",
                [new("account", (i % 2 == 0 ? td : tc).ToString()), new("deep", (i % 2 == 0 ? td : tc).ToString())], tokens[i]));
            Race.NoServerErrors(rs);
            await using var db = Exec.Db(B);
            var rows = await db.Set<ExecAccountSelection>().Select(s => s.AccountId).ToListAsync();
            Assert.Single(rows);                                   // one save won whole; never a mix or a duplicate
            Assert.Contains(rows[0], new[] { td, tc });
        }
        finally
        {
            await Exec.ResetSelectionInAsync(B, td, tc);
        }
    }

    // ── speed: the full year of Samba (read only) ───────────────────────

    private static string YearEnd => Perf.YearEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The biggest customer of the speed-test books (its party ledger is the longest).</summary>
    private static async Task<int> BiggestCustomerAsync()
    {
        var company = new CompanyContext();
        company.Set(Perf.CompanyId);
        await using var db = Perf.NewDb(company);
        Assert.Equal(2, await db.Set<ExecAccountSelection>().CountAsync());     // Trade Debtors + Trade Creditors, saved as Settings saves them
        return (await Exec.Reports(db).PartyRowsAsync(ExecutiveReports.Receivable))[0].AccountId;
    }

    [PerfFact]
    public async Task Executive_pages_are_quick_on_a_full_year()
    {
        int biggest = await BiggestCustomerAsync();
        await using var host = await ModuleHost.StartOnAsync(Perf.ConnectionString, typeof(ExecutiveModule).Assembly);
        var c = host.ClientFor(Perf.AdminUserId, Perf.CompanyId);
        (await host.MeasureAsync(c, "/executive/")).Within(1500, 40);
        (await host.MeasureAsync(c, $"/executive/?as_of={YearEnd}")).Within(1500, 40);
        (await host.MeasureAsync(c, "/executive/receivables")).Within(1500, 40);
        (await host.MeasureAsync(c, $"/executive/receivables?as_of={YearEnd}")).Within(1500, 40);
        (await host.MeasureAsync(c, "/executive/payables")).Within(1500, 40);
        (await host.MeasureAsync(c, $"/executive/party/{biggest}")).Within(800, 40);
        (await host.MeasureAsync(c, "/executive/settings")).Within(800, 40);
        (await host.MeasureAsync(c, "/executive/integrity")).Within(800, 40);
        (await host.MeasureAsync(c, "/executive/integrity?run=1")).Within(1500, 40);
    }

#if WITH_FINANCE
    [PerfFact]
    public async Task Executive_exports_are_quick_on_a_full_year()
    {
        int biggest = await BiggestCustomerAsync();
        await using var host = await ModuleHost.StartOnAsync(Perf.ConnectionString, typeof(ExecutiveModule).Assembly, typeof(ExportPipelineForTests).Assembly);
        var c = host.ClientFor(Perf.AdminUserId, Perf.CompanyId);
        foreach (string fmt in new[] { "excel", "pdf", "csv" })
        {
            (await host.MeasureAsync(c, $"/executive/receivables?format={fmt}")).Within(3000, 40);
            (await host.MeasureAsync(c, $"/executive/payables?format={fmt}&as_of={YearEnd}")).Within(3000, 40);
            (await host.MeasureAsync(c, $"/executive/party/{biggest}?format={fmt}")).Within(3000, 40);
            (await host.MeasureAsync(c, $"/executive/integrity?format={fmt}")).Within(3000, 40);
        }
    }
#endif

    private static async Task<(T Value, double Ms, int Queries)> BestAsync<T>(Func<Task<T>> body, QueryCounter? counter = null)
    {
        await body();
        (T Value, double Ms, int Queries)? best = null;
        for (int i = 0; i < 3; i++)
        {
            counter?.Reset();
            var sw = Stopwatch.StartNew();
            T v = await body();
            sw.Stop();
            if (best is null || sw.Elapsed.TotalMilliseconds < best.Value.Ms)
                best = (v, sw.Elapsed.TotalMilliseconds, counter?.Count ?? 0);
        }
        return best!.Value;
    }

    /// <summary>The figures behind the pages, each with its query count: aging, the registers, party aging, the ledger.</summary>
    [PerfFact]
    public async Task Each_figure_is_a_fixed_handful_of_queries_with_every_party_in_scope()
    {
        var counter = new QueryCounter();
        var company = new CompanyContext();
        company.Set(Perf.CompanyId);
        await using var db = new AccountixDbContext(new DbContextOptionsBuilder<AccountixDbContext>()
            .UseNpgsql(Perf.ConnectionString).AddInterceptors(counter).Options, company);
        var lines = new List<string>();
        void Budget(string what, double ms, int queries, int maxMs, int maxQueries)
        {
            lines.Add($"{what}: {ms:F0} ms, {queries} queries");
            Assert.True(ms <= maxMs, $"{what} too slow: {ms:F0} ms (budget {maxMs})");
            Assert.True(queries <= maxQueries, $"{what}: {queries} queries (budget {maxQueries})");
        }
        var aging = await BestAsync(async () => { var r = Exec.Reports(db); return await r.AgingAsync(Perf.YearEnd); }, counter);
        Budget("aging (year end)", aging.Ms, aging.Queries, 1500, 6);
        Assert.True(aging.Value.Receivable.Parties > 10);
        var dash = await BestAsync(async () =>
        {
            var r = Exec.Reports(db);
            await r.BothSidesAsync(null); await r.AgingAsync(null); await r.ProfitabilityAsync(); await r.LiquidityAsync(null);
            return await r.SelectionsAsync();
        }, counter);
        Budget("dashboard figures", dash.Ms, dash.Queries, 1500, 10);
        var reg = await BestAsync(async () => { var r = Exec.Reports(db); return await r.PartyRowsAsync(ExecutiveReports.Receivable); }, counter);
        Budget("receivables register", reg.Ms, reg.Queries, 800, 4);
        var ages = await BestAsync(async () => { var r = Exec.Reports(db); return await r.PartyAgingAsync(Perf.YearEnd); }, counter);
        Budget("party aging (export)", ages.Ms, ages.Queries, 1500, 6);
        int biggest = reg.Value[0].AccountId;
        var ledger = await BestAsync(async () => { var r = Exec.Reports(db); return await r.AccountLedgerAsync(biggest, null, null); }, counter);
        Budget($"party ledger, all {ledger.Value.Count} postings", ledger.Ms, ledger.Queries, 800, 2);
        Console.WriteLine(string.Join("\n", lines));
        if (Environment.GetEnvironmentVariable("ACCOUNTIX_TIMINGS") is { Length: > 0 } file)
            await File.AppendAllLinesAsync(file, lines.Select(l => "service " + l));
    }
}
