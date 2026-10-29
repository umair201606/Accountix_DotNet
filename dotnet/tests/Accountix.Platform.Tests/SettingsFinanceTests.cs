using System.Net;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Platform.Tests;

/// <summary>Settings: My Account, Company &amp; Finance and Inventory tabs (test_settings_access.py, test_settings_cleanup.py, test_account_security.py).</summary>
public class SettingsFinanceTests
{
    /// <summary>The audit rows of one entity type in a company, made comparable across two companies.</summary>
    internal static async Task<List<string>> AuditRowsAsync(int cid, string entityType, int afterId = 0)
    {
        await using var db = P.Db(cid);
        var rows = await db.AuditLogs.AsNoTracking().Where(a => a.CompanyId == cid && a.EntityType == entityType && a.Id > afterId)
            .OrderBy(a => a.Id).ToListAsync();
        return rows.Select(r =>
        {
            string ch = System.Text.RegularExpressions.Regex.Replace(r.Changes ?? "", "\"company_id\": \\d+", "\"company_id\": C");
            string? reference = r.Reference == r.EntityId?.ToString() ? "ID" : r.Reference;
            string? summary = r.Reference == r.EntityId?.ToString() ? r.Summary?.Replace(" " + r.Reference + " ", " ID ") : r.Summary;
            return $"{r.Action}|{r.Module}|{r.EntityType}|{reference}|{summary}|{ch}";
        }).ToList();
    }

    internal static async Task<int> MaxAuditIdAsync()
    {
        await using var db = P.Db();
        return await db.AuditLogs.MaxAsync(a => (int?)a.Id) ?? 0;
    }

    public static readonly string[] Tabs = ["account", "company", "periods", "reports", "cash_flow", "labels", "inventory", "invites"];

    [ReferenceFact]
    public async Task Every_tab_renders_for_an_admin_within_the_query_budget()
    {
        await using var host = await P.HostAsync();
        var (cid, admin) = await P.FreshCompanyAsync(host);
        var c = host.ClientFor(admin, cid);
        foreach (string tab in Tabs)
        {
            await c.GetAsync($"/settings/?tab={tab}");          // first use made
            host.Queries.Reset();
            var r = await c.GetAsync($"/settings/?tab={tab}");
            string html = await r.Content.ReadAsStringAsync();
            Assert.True(r.StatusCode == HttpStatusCode.OK, $"{tab}: {(int)r.StatusCode} {html[..Math.Min(html.Length, 2000)]}");
            Assert.True(host.Queries.Count <= 40, $"{tab}: {host.Queries.Count} queries\n{string.Join("\n", host.Queries.Repeated(5))}");
            Assert.Contains("class=\"set-main\"", html);
            Assert.Contains($"href=\"/settings/?tab={tab}\" class=\"nav-item active\"", html);
        }
    }

    [ReferenceFact]
    public async Task An_employee_sees_account_and_invites_only_and_deep_links_fall_back()
    {
        await using var host = await P.HostAsync();
        var (cid, _) = await P.FreshCompanyAsync(host);
        int emp = await P.AddMemberAsync(host, cid, Role.Employee, await P.NewUserAsync());
        await using (var db = P.Db())
        {
            var m = await db.Memberships.SingleAsync(x => x.UserId == emp && x.CompanyId == cid);
            m.SetModuleFlags(new Dictionary<string, bool> { ["hr"] = true });
            await db.SaveChangesAsync();
        }
        var c = host.ClientFor(emp, cid);
        string html = await (await c.GetAsync("/settings/")).Content.ReadAsStringAsync();
        Assert.Contains("href=\"/settings/?tab=account\" class=\"nav-item active\"", html);    // plain URL highlights the first section
        Assert.Contains("?tab=invites", html);
        Assert.DoesNotContain("?tab=company", html);
        Assert.DoesNotContain("?tab=rights", html);
        Assert.DoesNotContain("Administration", html);
        var deep = await c.GetAsync("/settings/?tab=company");
        Assert.Equal(HttpStatusCode.OK, deep.StatusCode);
        Assert.Contains("My Account", await deep.Content.ReadAsStringAsync());

        foreach (var (url, field) in new[] { ("/settings/company", ("company_name", "Hijacked")), ("/settings/inventory", ("valuation_method", "fifo")),
                                             ("/settings/periods/rule", ("start_month", "7")), ("/settings/reports", ("reset_pl", "1")) })
        {
            var r = await P.PostAsync(c, url, "/settings/", field);
            Assert.Equal("/settings/", r.Headers.Location?.OriginalString);
            Assert.Contains("You don't have access to that setting.", await P.FollowAsync(c, r));
        }
        await using var db2 = P.Db(cid);
        Assert.NotEqual("Hijacked", (await db2.Set<CompanyInfo>().SingleAsync()).CompanyName);
        Assert.Equal("weighted_average", (await db2.Set<InventorySetting>().SingleAsync()).ValuationMethod);
        Assert.Equal(1, (await db2.Set<FiscalYearRule>().SingleAsync()).StartMonth);
    }

    private static readonly (string, string)[] CompanyForm =
    [
        ("company_name", "Parity Traders"), ("logo_url", ""), ("phone", "042-111"), ("email", "acc@parity.test"), ("website", ""),
        ("tax_id", "NTN-77"), ("registration_number", ""), ("city", "Lahore"), ("state", "Punjab"), ("country", "Pakistan"),
        ("address", "1 Mall Road"), ("bank_name", "HBL"), ("bank_account_title", "Parity"), ("bank_account_number", "0001"),
        ("currency", "PKR"), ("currency_symbol", "Rs."), ("fiscal_year_start_month", "7"), ("date_format", "d/m/Y"), ("timezone", "Asia/Karachi"),
    ];

    /// <summary>The company profile row and its audit trail, normalised (golden: CompanyProfile).</summary>
    private static async Task<object> ProfileShapeAsync(int cid, int afterAuditId)
    {
        string tag = await P.TagOfAsync(cid);
        await using var db = P.Db(cid);
        return new
        {
            company_info = P.Norm(ProvisioningTests.Row(db, await db.Set<CompanyInfo>().AsNoTracking().SingleAsync()), tag),
            audit = (await AuditRowsAsync(cid, "Company profile", afterAuditId)).Select(r => P.Norm(r, tag)).ToList(),
        };
    }

    [ReferenceFact]
    public async Task Saving_the_company_profile_writes_what_python_wrote_and_keeps_the_company_name_in_the_header()
    {
        await using var host = await P.HostAsync();
        var (net, netAdmin) = await P.FreshCompanyAsync(host);
        int before = await MaxAuditIdAsync();
        var c = host.ClientFor(netAdmin, net);
        var r = await P.PostAsync(c, "/settings/company", "/settings/?tab=company", CompanyForm);
        Assert.Equal("/settings/?tab=company", r.Headers.Location?.OriginalString);
        string page = await P.FollowAsync(c, r);
        Assert.Contains("Company profile updated.", page);
        await using (var db = P.Db())
        {
            string name = (await db.Companies.SingleAsync(x => x.Id == net)).Name;
            Assert.Contains($"<h2 title=\"{name}\">{name}</h2>", page);    // the shell's company name is not blanked
        }
        Golden.Match("CompanyProfile", await ProfileShapeAsync(net, before));
    }

    /// <summary>The report-structure form of the test: row 1 moved to the top, "Gross Profit" renamed, the admin label left empty.</summary>
    private static List<(string, string)> ReportStructureForm()
    {
        var rows = Accountix.Platform.Controllers.SettingsController.DefaultPlStructure;
        var form = new List<(string, string)> { ("pl_detail_rows", "12") };
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            // Move row 1 to the top; rename "Gross Profit"; one empty label falls back to the key's title.
            form.Add(($"row_order_{i}", (i == 1 ? -1 : i).ToString()));
            form.Add(($"row_ident_{i}", row.Section is not null ? "section:" + row.Section : "subtotal:" + row.Subtotal));
            form.Add(($"row_label_{i}", row.Subtotal == "gross_profit" ? "Gross Margin" : row.Section == "admin" ? "" : row.Label));
            if (row.Negate) form.Add(($"row_negate_{i}", "1"));
        }
        return form;
    }

    private static async Task<object> ReportStructureShapeAsync(int cid)
    {
        await using var db = P.Db(cid);
        var s = await db.Set<ReportSetting>().AsNoTracking().SingleAsync();
        return new { pl_detail_rows = s.PlDetailRows, pl_structure_json = s.PlStructureJson };
    }

    [ReferenceFact]
    public async Task Report_structure_rows_move_rename_and_save_as_python_saved_them()
    {
        await using var host = await P.HostAsync();
        var form = ReportStructureForm();
        var (net, netAdmin) = await P.FreshCompanyAsync(host);
        var c = host.ClientFor(netAdmin, net);
        var r = await P.PostAsync(c, "/settings/reports", "/settings/?tab=reports", [.. form]);
        Assert.Equal("/settings/?tab=reports", r.Headers.Location?.OriginalString);
        Golden.Match("ReportStructure", await ReportStructureShapeAsync(net));
        await using var dn = P.Db(net);
        var n = await dn.Set<ReportSetting>().SingleAsync();
        Assert.Equal(12, n.PlDetailRows);
        Assert.StartsWith("[{\"label\": \"Less: Sales Returns & Discounts\"", n.PlStructureJson);
        Assert.Contains("{\"label\": \"Admin\", \"section\": \"admin\", \"negate\": true}", n.PlStructureJson);
        string page = await (await c.GetAsync("/settings/?tab=reports")).Content.ReadAsStringAsync();
        Assert.Contains("value=\"Gross Margin\"", page);
    }

    [ReferenceFact]
    public async Task The_fiscal_rule_states_the_real_year_end_and_regenerates_periods()
    {
        await using var host = await P.HostAsync();
        var (cid, admin) = await P.FreshCompanyAsync(host);
        var c = host.ClientFor(admin, cid);
        var r = await P.PostAsync(c, "/settings/periods/rule", "/settings/?tab=periods", ("start_month", "7"), ("start_day", "1"));
        string page = await P.FollowAsync(c, r);
        Assert.Contains("Fiscal year rule updated to 1 July. Periods regenerated.", page);
        Assert.Contains("Each financial year runs <strong>1 July</strong> \u2192\n    <strong>30 June</strong>.", page);
        await using var db = P.Db(cid);
        Assert.All(await db.Set<AccountingPeriod>().ToListAsync(), p => Assert.Equal(7, p.StartDate.Month));
        var bad = await P.PostAsync(c, "/settings/periods/rule", "/settings/?tab=periods", ("start_month", "13"), ("start_day", "1"));
        Assert.Contains("Start month must be between 1 and 12.", await P.FollowAsync(c, bad));
        var gen = await P.PostAsync(c, "/settings/periods/generate", "/settings/?tab=periods", ("from_year", "2024"), ("to_year", "2025"));
        Assert.Contains("Periods generated from 2024 to 2025.", await P.FollowAsync(c, gen));
        Assert.Equal(["FY 2024-2025", "FY 2025-2026"], await db.Set<AccountingPeriod>().OrderBy(p => p.StartDate).Select(p => p.PeriodName).ToListAsync());
    }

    [ReferenceFact]
    public async Task Closing_a_period_lists_open_drafts_then_closes_anyway_and_reopens()
    {
        await using var host = await P.HostAsync();
        var (cid, admin) = await P.FreshCompanyAsync(host);
        int pid;
        await using (var db = P.Db(cid))
        {
            var period = await db.Set<AccountingPeriod>().OrderBy(p => p.StartDate).FirstAsync();
            pid = period.Id;
            db.Add(new AccountingVoucher
            {
                VoucherType = "JV", VoucherNumber = "JV-T-" + P.Tag(), VoucherDate = period.StartDate.ToDateTime(new TimeOnly(10, 0)),
                Status = "unapproved", CreatedBy = admin, CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var c = host.ClientFor(admin, cid);
        var r = await P.PostAsync(c, $"/settings/periods/{pid}/close", "/settings/?tab=periods");
        string page = await P.FollowAsync(c, r);
        Assert.Contains("still has open items", page);
        Assert.Contains("<li>1 unapproved accounting voucher(s)</li>", page);
        Assert.Contains("Close anyway", page);
        await using (var db = P.Db(cid))
            Assert.NotEqual(true, (await db.Set<AccountingPeriod>().SingleAsync(p => p.Id == pid)).IsClosed);
        var forced = await P.PostAsync(c, $"/settings/periods/{pid}/close", "/settings/?tab=periods", ("force", "1"));
        Assert.Contains("closed.", await P.FollowAsync(c, forced));
        await using (var db = P.Db(cid))
        {
            var p = await db.Set<AccountingPeriod>().SingleAsync(x => x.Id == pid);
            Assert.True(p.IsClosed == true && p.IsOpen == false && p.ClosedAt is not null);
        }
        var reopen = await P.PostAsync(c, $"/settings/periods/{pid}/reopen", "/settings/?tab=periods");
        Assert.Contains("reopened.", await P.FollowAsync(c, reopen));
        await using (var db = P.Db(cid))
            Assert.True((await db.Set<AccountingPeriod>().SingleAsync(x => x.Id == pid)).IsOpen == true);
    }

    /// <summary>
    /// Five people approve cash payments dated in a period while a sixth closes it ("Close anyway"), all at
    /// once. Postings take the period FOR SHARE, the close takes it exclusively (FOR NO KEY UPDATE), so a
    /// posting either committed before the close (its journal is in the period, made before closed_at) or
    /// waited for the close and was refused with the closed-period message, posting nothing: the voucher
    /// form then keeps it as an unapproved draft, as Python's did. (Replaces the race between the two apps
    /// dropped on 29 Oct.)
    /// </summary>
    [ReferenceFact]
    public async Task Postings_racing_a_period_close_land_before_it_or_are_refused_and_post_nothing()
    {
        await using var host = await P.HostWithAccountingAsync();
        var (cid, admin) = await P.FreshCompanyAsync(host);
        string tag = P.Tag();
        int pid, cash, expense;
        DateOnly start;
        await using (var db = P.Db(cid))
        {
            var period = await db.Set<AccountingPeriod>().Where(p => p.StartDate.Year == DateTime.Now.Year - 1).SingleAsync();
            (pid, start) = (period.Id, period.StartDate);
            cash = (await db.Set<ChartOfAccount>().SingleAsync(a => a.Code == "1-01-01-01-0001")).Id;
            expense = (await db.Set<ChartOfAccount>().SingleAsync(a => a.Code == "5-04-01-01-0001")).Id;
        }
        List<KeyValuePair<string, string>> Payment(int i, decimal amount, DateOnly? on = null) =>
        [
            new("action", "approve"), new("voucher_type", "CPV"),
            new("voucher_date", (on ?? start.AddDays(10 + i)).ToString("yyyy-MM-dd") + "T10:00"),
            new("cash_bank_account_id", cash.ToString()), new("label_id", ""), new("notes", $"race-{tag}-{i}"),
            new("account_id[]", expense.ToString()), new("description[]", $"race {i}"),
            new("debit[]", amount.ToString(System.Globalization.CultureInfo.InvariantCulture)), new("credit[]", ""), new("label_id[]", ""),
        ];
        const int closer = 2;     // one of the six closes; the other five post 101, 102, ... so each journal is recognisable
        var clients = host.ClientsFor(admin, cid, 6);
        var tokens = new string[6];
        for (int i = 0; i < 6; i++)
            tokens[i] = await clients[i].TokenFromAsync("/settings/?tab=periods");

        // Warm-up: two payments into this year's (open) period, the second (warm) one timed. The racing payments
        // queue on the CPV number series, so the close, started about two payments later, usually lands between them.
        var sw = new System.Diagnostics.Stopwatch();
        foreach (int w in new[] { 7, 8 })
        {
            sw.Restart();
            var warm = await clients[0].PostFormWithTokenAsync("/accounting/vouchers", Payment(w, 100 + w, new DateOnly(DateTime.Now.Year, 1, 6)), tokens[0]);
            sw.Stop();
            Assert.Contains("approved.", await P.FollowAsync(clients[0], warm));
        }
        var delay = TimeSpan.FromMilliseconds(Math.Clamp(sw.Elapsed.TotalMilliseconds * 2, 10, 500));

        var rs = await Race.RunAsync(6, async i =>
        {
            if (i != closer)
                return await clients[i].PostFormWithTokenAsync("/accounting/vouchers", Payment(i, 100 + i), tokens[i]);
            await Task.Delay(delay);
            return await clients[i].PostFormWithTokenAsync($"/settings/periods/{pid}/close", [new("force", "1")], tokens[i]);
        });
        Race.NoServerErrors(rs);

        var flashes = new Dictionary<int, string>();
        foreach (int i in new[] { 0, 1, 3, 4, 5 })
            flashes[i] = await P.FollowAsync(clients[i], rs[i]);

        // One more payment after the race: the period is closed, so it must be refused too.
        var late = await clients[0].PostFormWithTokenAsync("/accounting/vouchers", Payment(9, 109), tokens[0]);
        Race.NoServerErrors([late]);
        string lateFlash = await P.FollowAsync(clients[0], late);

        await using var check = P.Db(cid);
        var p = await check.Set<AccountingPeriod>().AsNoTracking().SingleAsync(x => x.Id == pid);
        Assert.True(p.IsClosed == true && p.IsOpen == false && p.ClosedAt is not null, "the period ends closed");
        var periodStart = p.StartDate.ToDateTime(TimeOnly.MinValue);
        var periodEnd = p.EndDate.ToDateTime(TimeOnly.MaxValue);

        int committed = 0, refused = 0;
        decimal committedTotal = 107m + 108m;    // the warm-ups, posted in this year's period
        foreach (int i in new[] { 0, 1, 3, 4, 5, 9 })
        {
            string flash = i == 9 ? lateFlash : flashes[i];
            var v = await check.Set<AccountingVoucher>().AsNoTracking().SingleAsync(x => x.Notes == $"race-{tag}-{i}");
            var journals = await check.Set<JournalEntry>().AsNoTracking()
                .Where(j => j.VoucherType == "CPV" && j.VoucherId == v.Id).ToListAsync();
            if (v.Status == "approved")
            {
                committed++;
                committedTotal += 100m + i;
                Assert.NotEqual(9, i);
                Assert.Contains($"{v.VoucherNumber} approved.", flash);
                var j = Assert.Single(journals);
                Assert.True(j.IsPosted == true && j.EntryDate >= periodStart && j.EntryDate <= periodEnd, $"{v.VoucherNumber}: journal in the period");
                Assert.True(j.CreatedAt <= p.ClosedAt, $"{v.VoucherNumber} posted at {j.CreatedAt:O}, after the close at {p.ClosedAt:O}");
                var lines = await check.Set<JournalLine>().AsNoTracking().Where(l => l.JournalEntryId == j.Id).ToListAsync();
                Assert.Equal(100m + i, lines.Sum(l => l.Debit ?? 0));
                Assert.Equal(100m + i, lines.Sum(l => l.Credit ?? 0));
            }
            else
            {
                refused++;
                Assert.Equal("unapproved", v.Status);
                Assert.Null(v.ApprovedAt);
                Assert.Contains($"{v.VoucherNumber} saved as unapproved — could not approve: Cannot post {v.VoucherNumber} into", flash);
                Assert.Contains($"the period '{p.PeriodName}'", flash);
                Assert.Contains("its figures are final", flash);
                Assert.Empty(journals);      // nothing posted: no journal, so no lines and no reporting-table figure
            }
        }
        Assert.Equal(6, committed + refused);
        Assert.True(refused >= 1);           // the late one at least
        Console.WriteLine($"period-close race: {committed} posted before the close, {refused - 1} refused in the race, the late one refused (close started after {delay.TotalMilliseconds:F0} ms)");

        // Distinct voucher numbers: drafts and posted vouchers each drew their own.
        var numbers = await check.Set<AccountingVoucher>().AsNoTracking().Where(x => x.Notes!.StartsWith($"race-{tag}-")).Select(x => x.VoucherNumber).ToListAsync();
        Assert.Equal(numbers.Count, numbers.Distinct().Count());

        // The trial balance balances, the reporting table holds exactly the posted figures, and Books Integrity passes.
        var posted = check.Set<JournalLine>().AsNoTracking().Where(l => l.JournalEntry.IsPosted == true);
        decimal dr = await posted.SumAsync(l => l.Debit ?? 0), cr = await posted.SumAsync(l => l.Credit ?? 0);
        Assert.Equal(dr, cr);
        Assert.Equal(dr, await check.Set<GlDailyBalance>().AsNoTracking().SumAsync(g => g.Debit));
        Assert.Equal(committedTotal, dr);     // a fresh company: the committed payments are all it has posted
        var failures = await new Accountix.Executive.Services.BooksIntegrity(check).FailuresAsync();
        Assert.True(failures.Count == 0, string.Join("\n", failures.Select(f => $"{f.Label}: expected {f.Expected} got {f.Actual} ({f.Detail})")));
    }

    /// <summary>
    /// The period lock made visible, without timing luck: a posting in flight (the period held FOR SHARE) keeps
    /// the close waiting until it commits; a close in flight (the period held FOR NO KEY UPDATE and marked closed,
    /// not yet committed) keeps a posting waiting, and once it commits the posting re-reads the period and is refused.
    /// </summary>
    [ReferenceFact]
    public async Task The_close_waits_for_a_posting_in_flight_and_a_posting_waits_for_a_close_in_flight()
    {
        await using var host = await P.HostWithAccountingAsync();
        var (cid, admin) = await P.FreshCompanyAsync(host);
        string tag = P.Tag();
        int pid, cash, expense;
        DateOnly start;
        await using (var db = P.Db(cid))
        {
            var period = await db.Set<AccountingPeriod>().Where(p => p.StartDate.Year == DateTime.Now.Year - 1).SingleAsync();
            (pid, start) = (period.Id, period.StartDate);
            cash = (await db.Set<ChartOfAccount>().SingleAsync(a => a.Code == "1-01-01-01-0001")).Id;
            expense = (await db.Set<ChartOfAccount>().SingleAsync(a => a.Code == "5-04-01-01-0001")).Id;
        }
        var c = host.ClientFor(admin, cid);
        string token = await c.TokenFromAsync("/settings/?tab=periods");
        async Task<bool> ClosedAsync()
        {
            await using var db = P.Db(cid);
            return (await db.Set<AccountingPeriod>().AsNoTracking().SingleAsync(x => x.Id == pid)).IsClosed == true;
        }
        static async Task<bool> StillWaitingAsync(Task t) => await Task.WhenAny(t, Task.Delay(1500)) != t;

        // 1. A posting in flight holds the period FOR SHARE: the close waits for it.
        await using (var conn = new Npgsql.NpgsqlConnection(Reference.ConnectionString))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await using (var cmd = new Npgsql.NpgsqlCommand($"SELECT 1 FROM accounting_periods WHERE id = {pid} FOR SHARE", conn, tx))
                await cmd.ExecuteScalarAsync();
            var close = c.PostFormWithTokenAsync($"/settings/periods/{pid}/close", [new("force", "1")], token);
            Assert.True(await StillWaitingAsync(close), "the close went ahead while a posting held the period");
            Assert.False(await ClosedAsync());
            await tx.CommitAsync();
            var r = await close;
            Assert.Equal("/settings/?tab=periods", r.Headers.Location?.OriginalString);
            Assert.True(await ClosedAsync());
        }
        Assert.Contains("reopened.", await P.FollowAsync(c, await c.PostFormWithTokenAsync($"/settings/periods/{pid}/reopen", [], token)));

        // 2. A close in flight: the posting waits, then sees the period closed and posts nothing.
        await using (var conn = new Npgsql.NpgsqlConnection(Reference.ConnectionString))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await using (var cmd = new Npgsql.NpgsqlCommand(
                $"SELECT 1 FROM accounting_periods WHERE id = {pid} FOR NO KEY UPDATE; " +
                $"UPDATE accounting_periods SET is_closed = true, is_open = false, closed_at = (now() AT TIME ZONE 'utc') WHERE id = {pid}", conn, tx))
                await cmd.ExecuteNonQueryAsync();
            var post = c.PostFormWithTokenAsync("/accounting/vouchers",
            [
                new("action", "approve"), new("voucher_type", "CPV"), new("voucher_date", start.AddDays(20).ToString("yyyy-MM-dd") + "T10:00"),
                new("cash_bank_account_id", cash.ToString()), new("label_id", ""), new("notes", $"lock-{tag}"),
                new("account_id[]", expense.ToString()), new("description[]", "lock"), new("debit[]", "55"), new("credit[]", ""), new("label_id[]", ""),
            ], token);
            Assert.True(await StillWaitingAsync(post), "the posting went ahead while the period was being closed");
            await tx.CommitAsync();
            string flash = await P.FollowAsync(c, await post);
            Assert.Contains("saved as unapproved — could not approve: Cannot post", flash);
            Assert.Contains("its figures are final", flash);
        }
        await using var check = P.Db(cid);
        var v = await check.Set<AccountingVoucher>().AsNoTracking().SingleAsync(x => x.Notes == $"lock-{tag}");
        Assert.Equal("unapproved", v.Status);
        Assert.False(await check.Set<JournalEntry>().AnyAsync(j => j.VoucherType == "CPV" && j.VoucherId == v.Id));
        Assert.Empty(await new Accountix.Executive.Services.BooksIntegrity(check).FailuresAsync());
    }

    [ReferenceFact]
    public async Task Six_first_requests_at_once_make_one_settings_row_each()
    {
        await using var host = await P.HostAsync();
        var (cid, admin) = await P.FreshCompanyAsync(host, provision: false);
        var clients = host.ClientsFor(admin, cid, 6);
        string[] tabs = ["company", "inventory", "periods", "reports", "company", "inventory"];
        var rs = await Race.RunAsync(6, i => clients[i].GetAsync($"/settings/?tab={tabs[i]}"));
        Race.NoServerErrors(rs);
        await using var db = P.Db(cid);
        Assert.Equal(1, await db.Set<CompanyInfo>().CountAsync());
        Assert.Equal(1, await db.Set<InventorySetting>().CountAsync());
        Assert.Equal(1, await db.Set<FiscalYearRule>().CountAsync());
        Assert.Equal(1, await db.Set<ReportSetting>().CountAsync());
    }

    [ReferenceFact]
    public async Task The_account_tab_needs_the_current_password_and_a_good_new_one()
    {
        await using var host = await P.HostAsync();
        var (cid, admin) = await P.FreshCompanyAsync(host);
        var c = host.ClientFor(admin, cid);
        var other = host.ClientFor(admin, cid);       // another session of the same login
        string email;
        await using (var db = P.Db()) email = (await db.Users.SingleAsync(u => u.Id == admin)).Email;

        async Task<string> Save(params (string, string)[] f) =>
            await P.FollowAsync(c, await P.PostAsync(c, "/settings/account", "/settings/?tab=account", f));
        Assert.Contains("Enter your current password", await Save(("full_name", "A"), ("email", "new." + email), ("login_id", "")));
        Assert.Contains("Password must be at least 8 characters.", await Save(("new_password", "short"), ("confirm_password", "short"), ("current_password", P.Password)));
        Assert.Contains("Passwords do not match", await Save(("new_password", "Long-enough-1"), ("confirm_password", "Long-enough-2"), ("current_password", P.Password)));
        Assert.Contains("That User ID is already taken.", await Save(("login_id", Reference.Email("owner_b")), ("email", email), ("current_password", P.Password)));
        Assert.Contains("Account updated.", await Save(("full_name", "Renamed Admin"), ("email", email), ("login_id", "")));
        Assert.Contains("Account updated.", await Save(("new_password", "Brand-new-pass-7"), ("confirm_password", "Brand-new-pass-7"), ("current_password", P.Password)));
        // This session carries on, the other one is signed out.
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/settings/?tab=account")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await other.GetAsync("/settings/?tab=account")).StatusCode);
        await using var db2 = P.Db();
        var u = await db2.Users.SingleAsync(x => x.Id == admin);
        Assert.Equal("Renamed Admin", u.FullName);
        Assert.True(Accountix.Core.Security.PasswordHasher.Verify(u.PasswordHash, "Brand-new-pass-7"));
        var audit = await db2.AuditLogs.Where(a => a.EntityType == "User" && a.EntityId == admin && a.Action == "update").OrderBy(a => a.Id).ToListAsync();
        Assert.Contains(audit, a => a.Changes!.Contains("\"field\": \"password_hash\", \"old\": \"\\u2022\\u2022\\u2022\", \"new\": \"\\u2022\\u2022\\u2022\""));
    }

    [ReferenceFact]
    public async Task Labels_are_created_renamed_defaulted_and_archived()
    {
        await using var host = await P.HostAsync();
        var (cid, admin) = await P.FreshCompanyAsync(host);
        var c = host.ClientFor(admin, cid);
        Assert.Contains("Label \"Site A\" created.", await P.FollowAsync(c, await P.PostAsync(c, "/settings/labels", "/settings/?tab=labels", ("name", "Site A"))));
        Assert.Contains("already exists", await P.FollowAsync(c, await P.PostAsync(c, "/settings/labels", "/settings/?tab=labels", ("name", "site a"))));
        int id;
        await using (var db = P.Db(cid)) id = (await db.Set<ProjectLabel>().SingleAsync()).Id;
        await P.PostAsync(c, "/settings/labels/defaults", "/settings/?tab=labels", ("default_voucher_label_id", id.ToString()), ("default_invoice_label_id", ""));
        await P.PostAsync(c, $"/settings/labels/{id}/rename", "/settings/?tab=labels", ("name", "Site B"));
        Assert.Contains("Per-line labeling for invoices enabled.", await P.FollowAsync(c, await P.PostAsync(c, "/settings/labels/per-line", "/settings/?tab=labels", ("scope", "invoice"), ("enabled", "on"))));
        Assert.Contains("deactivated", await P.FollowAsync(c, await P.PostAsync(c, $"/settings/labels/{id}/toggle", "/settings/?tab=labels")));
        await using var db2 = P.Db(cid);
        var label = await db2.Set<ProjectLabel>().SingleAsync();
        var s = await db2.Set<InventorySetting>().SingleAsync();
        Assert.Equal("Site B", label.Name);
        Assert.False(label.IsActive);
        Assert.Null(s.DefaultVoucherLabelId);
        Assert.True(s.PerLineLabelingInvoice);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/settings/labels")).StatusCode);
    }
}
