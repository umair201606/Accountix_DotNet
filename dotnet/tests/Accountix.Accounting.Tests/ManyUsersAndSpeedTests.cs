using System.Globalization;
using System.Net;
using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Accounting.Tests;

/// <summary>
/// Several people in one company at the same moment, and the speed of the
/// voucher pages on a full year of data. The pattern every module copies
/// (docs/SPEED_AND_CONCURRENCY.md).
/// </summary>
public class ManyUsersAndSpeedTests
{
    private static int OwnerB => Reference.UserId(Reference.Email("owner_b"));
    private static int CompanyB => Reference.Company("b");

    private static AccountixDbContext Db()
    {
        var company = new CompanyContext();
        company.Set(CompanyB);
        return Reference.NewDb(company);
    }

    private static async Task<(int Cash, int Expense)> AccountsAsync()
    {
        await using var db = Db();
        return (db.Set<ChartOfAccount>().Single(a => a.Code == "1-01-01-01-0001").Id,
                db.Set<ChartOfAccount>().Single(a => a.Code == "5-04-01-01-0001").Id);
    }

    private static List<KeyValuePair<string, string>> Cpv(int cash, int expense, string action, string note) =>
    [
        new("action", action), new("voucher_type", "CPV"), new("voucher_date", "2026-05-12T11:00"),
        new("cash_bank_account_id", cash.ToString()), new("label_id", ""), new("notes", note),
        new("account_id[]", expense.ToString()), new("description[]", note), new("debit[]", "40"), new("credit[]", ""), new("label_id[]", ""),
    ];

    private static async Task<int> DraftAsync(PageClient c, int cash, int expense, string note)
    {
        var r = await c.PostFormAsync("/accounting/vouchers", Cpv(cash, expense, "save", note), "/accounting/vouchers?type=CPV");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        return int.Parse(r.Headers.Location!.OriginalString.Split('/').Last());
    }

    private static async Task<int> PostedEntriesAsync(int voucherId)
    {
        await using var db = Db();
        return await db.Set<JournalEntry>().CountAsync(e => e.VoucherType == "CPV" && e.VoucherId == voucherId && e.IsPosted == true);
    }

    [ReferenceFact]
    public async Task Six_people_approving_one_draft_at_once_post_it_once()
    {
        var (cash, expense) = await AccountsAsync();
        await using var host = await ModuleHost.StartAsync(typeof(AccountingModule).Assembly);
        int id = await DraftAsync(host.ClientFor(OwnerB, CompanyB), cash, expense, "Race approve");

        var clients = host.ClientsFor(OwnerB, CompanyB, 6);
        var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync($"/accounting/vouchers/{id}")));
        var rs = await Race.RunAsync(6, i => clients[i].PostFormWithTokenAsync($"/accounting/vouchers/{id}/approve", [], tokens[i]));

        Race.NoServerErrors(rs);
        Assert.Equal(1, await PostedEntriesAsync(id));
        await using var db = Db();
        Assert.Equal("approved", db.Set<AccountingVoucher>().Single(v => v.Id == id).Status);
    }

    [ReferenceFact]
    public async Task Six_vouchers_saved_at_once_get_six_consecutive_numbers()
    {
        var (cash, expense) = await AccountsAsync();
        await using var host = await ModuleHost.StartAsync(typeof(AccountingModule).Assembly);
        var clients = host.ClientsFor(OwnerB, CompanyB, 6);
        var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync("/accounting/vouchers?type=CPV")));
        string tag = Guid.NewGuid().ToString("N")[..6];
        var rs = await Race.RunAsync(6, i =>
            clients[i].PostFormWithTokenAsync("/accounting/vouchers", Cpv(cash, expense, "approve", $"Race number {tag} {i}"), tokens[i]));

        Race.NoServerErrors(rs);
        Assert.All(rs, r => Assert.Equal(HttpStatusCode.Redirect, r.StatusCode));
        var ids = rs.Select(r => int.Parse(r.Headers.Location!.OriginalString.Split('/').Last())).ToList();
        await using var db = Db();
        var numbers = db.Set<AccountingVoucher>().Where(v => ids.Contains(v.Id)).Select(v => v.VoucherNumber).ToList();
        Assert.Equal(6, numbers.Distinct().Count());
        var seq = numbers.Select(n => int.Parse(n[(n.LastIndexOf('-') + 1)..], CultureInfo.InvariantCulture)).Order().ToList();
        Assert.Equal(5, seq[^1] - seq[0]);   // gapless: the series is taken in commit order
        foreach (int id in ids)
            Assert.Equal(1, await PostedEntriesAsync(id));
    }

    [ReferenceFact]
    public async Task A_saved_voucher_reopens_with_its_lines_in_valid_script()
    {
        // E2E finding F1: the lines went into <script> HTML-encoded (code: &quot;5-04…&quot;), a syntax error.
        var (cash, expense) = await AccountsAsync();
        await using var host = await ModuleHost.StartAsync(typeof(AccountingModule).Assembly);
        var c = host.ClientFor(OwnerB, CompanyB);
        int id = await DraftAsync(c, cash, expense, "Quote \"and\" <tag> & co");
        string html = await (await c.GetAsync($"/accounting/vouchers/{id}")).Content.ReadAsStringAsync();
        int start = html.IndexOf("var EXISTING = [", StringComparison.Ordinal);
        Assert.True(start > 0, "EXISTING block missing");
        string block = html[start..html.IndexOf("];", start, StringComparison.Ordinal)];
        Assert.DoesNotContain("&quot;", block);
        Assert.DoesNotContain("&amp;", block);
        Assert.Contains("code: \"5-04-01-01-0001\"", block);
        // Escaped for script (\u0022, \u003C, \u0026), never for HTML, and never raw.
        Assert.Matches(@"desc: ""Quote \\u0022and\\u0022 \\u003Ctag\\u003E \\u0026 co""", block);
        Assert.DoesNotContain("<tag>", block);
    }

    [ReferenceFact]
    public async Task Pages_stamp_the_antiforgery_token_on_forms_submitted_from_script()
    {
        // E2E finding F4: form.submit() fires no submit event, so in-page confirms posted without the token (400).
        await using var host = await ModuleHost.StartAsync(typeof(AccountingModule).Assembly);
        var c = host.ClientFor(OwnerB, CompanyB);
        string html = await (await c.GetAsync("/accounting/vouchers?type=CPV")).Content.ReadAsStringAsync();
        Assert.Contains("HTMLFormElement.prototype.submit = function", html);
        Assert.Contains("document.addEventListener('submit'", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<meta name=\"csrf-token\""));
    }

    [ReferenceFact]
    public async Task Voucher_pages_run_a_fixed_number_of_queries()
    {
        await using var host = await ModuleHost.StartAsync(typeof(AccountingModule).Assembly);
        var c = host.ClientFor(OwnerB, CompanyB);
        foreach (string url in new[]
                 {
                     "/accounting/", "/accounting/vouchers/list", "/accounting/vouchers?type=CPV",
                     "/accounting/registers/vouchers?view=1&mode=year", "/accounting/registers/vouchers/data?mode=year",
                     "/accounting/coa/",
                 })
            (await host.MeasureAsync(c, url, runs: 1)).Within(maxMilliseconds: 5000, maxQueries: 40);
    }

    // --- Full year of Samba (read only) ---

    private static string Month => $"mode=month&month={Perf.BusiestMonth}";
    private static string MonthRange =>
        $"filter_mode=custom&from={Perf.BusiestMonthStart:yyyy-MM-dd}&to={Perf.BusiestMonthEnd:yyyy-MM-dd}";

    [PerfFact]
    public async Task Voucher_list_and_register_for_the_busiest_month_are_quick()
    {
        await using var host = await ModuleHost.StartOnAsync(Perf.ConnectionString, typeof(AccountingModule).Assembly);
        var c = host.ClientFor(Perf.AdminUserId, Perf.CompanyId);
        (await host.MeasureAsync(c, $"/accounting/vouchers/list?{MonthRange}")).Within(800, 40);
        (await host.MeasureAsync(c, $"/accounting/registers/vouchers?view=1&{Month}")).Within(800, 40);
        (await host.MeasureAsync(c, $"/accounting/registers/vouchers/data?{Month}")).Within(800, 40);
        (await host.MeasureAsync(c, $"/accounting/registers/vouchers/data?{Month}&full=1&limit=100")).Within(1500, 40);
        (await host.MeasureAsync(c, $"/accounting/registers/vouchers/documents?view=1&{Month}")).Within(1500, 40);
        (await host.MeasureAsync(c, "/accounting/")).Within(800, 40);
    }

    [PerfFact]
    public async Task Voucher_register_for_the_whole_year_is_quick()
    {
        // Its exports need IReportExport (Finance): timed in the whole-app tests (Accountix.Tests).
        await using var host = await ModuleHost.StartOnAsync(Perf.ConnectionString, typeof(AccountingModule).Assembly);
        var c = host.ClientFor(Perf.AdminUserId, Perf.CompanyId);
        string year = $"mode=custom&from={Perf.YearStart:yyyy-MM-dd}&to={Perf.YearEnd:yyyy-MM-dd}";
        (await host.MeasureAsync(c, $"/accounting/registers/vouchers?view=1&{year}")).Within(1500, 40);
        (await host.MeasureAsync(c, $"/accounting/registers/vouchers/data?{year}")).Within(1500, 40);
    }
}
