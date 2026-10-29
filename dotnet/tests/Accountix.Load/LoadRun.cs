using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace Accountix.Load;

/// <summary>
/// N people in one company for a few minutes. Each virtual user is its own
/// browser (session, cookies, connections) and, between short pauses,
/// reads registers, statements, the ledger and lists, or writes vouchers —
/// new ones, and a few "hot" drafts that many users approve, unapprove and
/// edit at the same moment. Redirects after a POST are followed like a
/// browser does and the page's flash tells success, refusal or conflict.
/// </summary>
public sealed class LoadRun(Args a)
{
    private readonly ConcurrentBag<Sample> _samples = [];
    private readonly ConcurrentBag<WriteOutcome> _writes = [];
    private readonly List<(string Name, string Url, int Status)> _unavailable = [];
    private List<(string Name, string Url, int Weight)> _reads = [];
    private int[] _hot = [];
    private int _cash, _expense;
    private long _seq;

    public bool Passed { get; private set; }

    private sealed record Sample(string Page, string Method, int Status, double Ms, long Bytes);
    private sealed record WriteOutcome(string Action, int Status, string Outcome, string Message);

    public async Task<string> ExecuteAsync()
    {
        await using var ds = NpgsqlDataSource.Create(a.Db);
        var users = a.Users.Length > 0 ? Args.Ids(a.Users) : await MembersAsync(ds);
        (_cash, _expense) = await AccountsAsync(ds);
        long deadlocksBefore = await DataChecks.DeadlocksAsync(ds);
        int maxVoucherBefore = await ScalarAsync(ds, "SELECT COALESCE(MAX(id), 0) FROM accounting_vouchers");
        var before = await DataChecks.RunAsync(a.Db, a.Company, booksIntegrity: false);

        // Sign everyone in (own session each, users round-robin).
        var browsers = new List<Browser>();
        for (int i = 0; i < a.Vus; i++)
        {
            int uid = users[i % users.Count];
            browsers.Add(new Browser(a.BaseUrl, await Sessions.CookieAsync(ds, uid, a.Company, a.Secret), uid));
        }

        _hot = await CreateHotDraftsAsync(browsers[0], a.Hot);
        await DiscoverReadsAsync(browsers[0]);
        _samples.Clear();
        _writes.Clear();

        // Watch the database while the run goes: connections the host holds, lock waits.
        using var watchStop = new CancellationTokenSource();
        var watch = WatchAsync(ds, watchStop.Token);

        var started = Stopwatch.StartNew();
        var until = DateTime.UtcNow.AddSeconds(a.Seconds);
        await Task.WhenAll(browsers.Select((b, i) => Task.Run(() => VirtualUserAsync(b, i, until))));
        started.Stop();
        watchStop.Cancel();
        var (maxConns, maxLockWaits) = await watch;

        await Task.Delay(1500);   // let pg_stat_database catch up
        long deadlocks = await DataChecks.DeadlocksAsync(ds) - deadlocksBefore;
        var after = await DataChecks.RunAsync(a.Db, a.Company, booksIntegrity: true);
        var series = await RunSeriesAsync(ds, maxVoucherBefore);
        foreach (var b in browsers) b.Dispose();

        int serverErrors = _samples.Count(s => s.Status >= 500);
        int antiforgery = _samples.Count(s => s.Status == 400);
        Passed = serverErrors == 0 && antiforgery == 0 && deadlocks == 0 && after.All(c => c.Ok) && series.Ok;
        string report = Report(started.Elapsed, users.Count, deadlocks, maxConns, maxLockWaits, before, after, series, serverErrors, antiforgery);
        if (a.Out is not null)
        {
            Directory.CreateDirectory(a.Out);
            File.WriteAllText(Path.Combine(a.Out, "report.md"), report);
            File.WriteAllText(Path.Combine(a.Out, "samples.json"), JsonSerializer.Serialize(new
            {
                samples = _samples.ToArray(),
                writes = _writes.ToArray(),
                unavailable = _unavailable,
            }));
        }
        return report;
    }

    // ── what a person does ───────────────────────────────────────────────

    private async Task VirtualUserAsync(Browser b, int index, DateTime until)
    {
        var rnd = new Random(1000 + index);
        await Task.Delay(rnd.Next(0, 1500));                // people do not all start on the same tick
        while (DateTime.UtcNow < until)
        {
            int roll = rnd.Next(100);
            if (roll < 70)
                await ReadAsync(b, rnd);
            else if (roll < 80)
                await NewCashPaymentAsync(b, rnd);
            else if (roll < 85)
                await NewJournalThenApproveAsync(b, rnd);
            else
                await HotVoucherAsync(b, rnd);
            await Task.Delay(rnd.Next(a.Think.Min, a.Think.Max + 1));
        }
    }

    private async Task ReadAsync(Browser b, Random rnd)
    {
        int total = _reads.Sum(r => r.Weight), pick = rnd.Next(total);
        foreach (var (name, url, weight) in _reads)
        {
            if ((pick -= weight) >= 0) continue;
            string u = url.Replace("{hot}", _hot[rnd.Next(_hot.Length)].ToString(CultureInfo.InvariantCulture));
            await GetAsync(b, name, u);
            return;
        }
    }

    private async Task NewCashPaymentAsync(Browser b, Random rnd)
    {
        var form = await GetAsync(b, "voucher form (new CPV)", "/accounting/vouchers?type=CPV");
        if (form.CsrfToken is not { } token) return;
        long n = Interlocked.Increment(ref _seq);
        decimal amount = rnd.Next(100, 50_000);
        await PostAsync(b, "POST new CPV (save & approve)", "/accounting/vouchers",
            Cpv("approve", amount, $"Load CPV {n}", rnd), token);
    }

    private async Task NewJournalThenApproveAsync(Browser b, Random rnd)
    {
        var form = await GetAsync(b, "voucher form (new JV)", "/accounting/vouchers?type=JV");
        if (form.CsrfToken is not { } token) return;
        long n = Interlocked.Increment(ref _seq);
        decimal amount = rnd.Next(100, 20_000);
        var saved = await PostAsync(b, "POST new JV (save draft)", "/accounting/vouchers",
        [
            new("action", "save"), new("voucher_type", "JV"), new("voucher_date", Day(rnd)),
            new("cash_bank_account_id", ""), new("label_id", ""), new("notes", $"Load JV {n}"),
            new("account_id[]", _expense.ToString(CultureInfo.InvariantCulture)), new("description[]", "load"), new("debit[]", Money(amount)), new("credit[]", ""), new("label_id[]", ""),
            new("account_id[]", _cash.ToString(CultureInfo.InvariantCulture)), new("description[]", "load"), new("debit[]", ""), new("credit[]", Money(amount)), new("label_id[]", ""),
        ], token);
        if (saved.Page?.CsrfToken is { } t2 && saved.Location is { } loc && int.TryParse(loc.Split('/').Last(), out int id))
        {
            await Task.Delay(rnd.Next(200, 800));            // reads it over, then approves
            await PostAsync(b, "POST approve (own JV)", $"/accounting/vouchers/{id}/approve", [], t2);
        }
    }

    /// <summary>Several people on the same few drafts: approve, unapprove, edit, at the same moment.</summary>
    private async Task HotVoucherAsync(Browser b, Random rnd)
    {
        int id = _hot[rnd.Next(_hot.Length)];
        var page = await GetAsync(b, "voucher form (hot)", $"/accounting/vouchers/{id}");
        if (page.CsrfToken is not { } token) return;
        switch (rnd.Next(3))
        {
            case 0:
                await PostAsync(b, "POST approve (hot)", $"/accounting/vouchers/{id}/approve", [], token);
                break;
            case 1:
                await PostAsync(b, "POST unapprove (hot)", $"/accounting/vouchers/{id}/unapprove", [], token);
                break;
            default:
                await PostAsync(b, "POST edit (hot)", $"/accounting/vouchers/{id}",
                    Cpv("save", rnd.Next(100, 9_000), $"Hot voucher edited", rnd), token);
                break;
        }
    }

    private List<KeyValuePair<string, string>> Cpv(string action, decimal amount, string note, Random rnd) =>
    [
        new("action", action), new("voucher_type", "CPV"), new("voucher_date", Day(rnd)),
        new("cash_bank_account_id", _cash.ToString(CultureInfo.InvariantCulture)), new("label_id", ""), new("notes", note),
        new("account_id[]", _expense.ToString(CultureInfo.InvariantCulture)), new("description[]", note), new("debit[]", Money(amount)), new("credit[]", ""), new("label_id[]", ""),
    ];

    /// <summary>A day in the busiest month, so postings land on the busiest gl_daily_balances rows.</summary>
    private string Day(Random rnd) => $"{a.Month}-{rnd.Next(1, 29):00}T10:00";

    private static string Money(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    // ── requests ─────────────────────────────────────────────────────────

    private async Task<Browser.Answer> GetAsync(Browser b, string page, string url)
    {
        var ans = await b.GetAsync(url);
        _samples.Add(new Sample(page, "GET", ans.Status, ans.Ms, ans.Bytes));
        return ans;
    }

    private sealed record PostResult(int Status, string? Location, Browser.Answer? Page);

    private async Task<PostResult> PostAsync(Browser b, string action, string url, List<KeyValuePair<string, string>> fields, string token)
    {
        var ans = await b.PostFormAsync(url, fields, token);
        _samples.Add(new Sample(action, "POST", ans.Status, ans.Ms, ans.Bytes));
        Browser.Answer? page = null;
        if (ans.Status is 302 or 303 && ans.Location is { } loc)
            page = await GetAsync(b, "→ page after POST", loc);
        string outcome, message = "";
        if (ans.Status == 409)
            (outcome, message) = ("conflict 409", ans.Body.Length > 200 ? ans.Body[..200] : ans.Body);
        else if (ans.Status >= 500)
            (outcome, message) = ("server error", ans.Body.Length > 300 ? ans.Body[..300] : ans.Body);
        else if (ans.Status == 400)
            (outcome, message) = ("refused: antiforgery", "");
        else if (page?.Flashes.FirstOrDefault() is var (kind, text) && text is not null)
        {
            message = text;
            outcome = kind == "success" ? "ok"
                : text.Contains("at the same moment") || text.Contains("clashes with") || text.Contains("removed by another user")
                    ? "conflict (flash)" : "refused (business rule)";
        }
        else
            outcome = ans.Status is 302 or 303 ? "ok (no flash)" : $"status {ans.Status}";
        _writes.Add(new WriteOutcome(action, ans.Status, outcome, message));
        return new PostResult(ans.Status, ans.Location, page);
    }

    // ── set-up ───────────────────────────────────────────────────────────

    private async Task DiscoverReadsAsync(Browser b)
    {
        string m = a.Month;
        string first = $"{m}-01", last = DateOnly.ParseExact(first, "yyyy-MM-dd").AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd");
        var candidates = new List<(string, string, int)>
        {
            ("accounting dashboard", "/accounting/", 6),
            ("voucher list", "/accounting/vouchers/list", 8),
            ("voucher register (month)", $"/accounting/registers/vouchers?mode=month&month={m}&view=1", 8),
            ("voucher register data (month)", $"/accounting/registers/vouchers/data?mode=month&month={m}", 4),
            ("voucher register (year)", "/accounting/registers/vouchers?mode=year&view=1", 3),
            ("chart of accounts", "/accounting/coa/", 3),
            ("voucher form (hot)", "/accounting/vouchers/{hot}", 4),
            ("accounting entry panel", "/accounting/gl-entry?type=CPV&id={hot}", 3),
            ("cash balance API", $"/accounting/api/account-balance?id={_cash}", 3),
            ("executive dashboard", "/executive/", 5),
            ("aged receivables", "/executive/receivables", 4),
            ("aged payables", "/executive/payables", 3),
            ("books integrity (run)", "/executive/integrity?run=1", 1),
            ("cash flow (TWCF)", "/finance/twcf", 2),
            ("trial balance (year)", $"/finance/trial-balance?filter_mode=custom&from={a.YearFrom}&to={a.YearTo}", 4),
            ("profit & loss (year)", $"/finance/profit-loss?filter_mode=custom&from={a.YearFrom}&to={a.YearTo}", 4),
            ("balance sheet (year end)", $"/finance/balance-sheet?filter_mode=custom&from={a.YearFrom}&to={a.YearTo}", 3),
            ("cash flow statement (year)", $"/finance/cash-flow?filter_mode=custom&from={a.YearFrom}&to={a.YearTo}", 2),
            ("SOCIE (year)", $"/finance/socie?filter_mode=custom&from={a.YearFrom}&to={a.YearTo}", 1),
            ("general ledger, all accounts (month)", $"/finance/ledger?filter_mode=custom&from={first}&to={last}", 4),
            ("sales invoice list (month)", $"/inventory/invoices/list?month={m}", 8),
            ("purchase invoice list (month)", $"/inventory/purchase-invoice/list?month={m}", 4),
            ("sales register (month)", $"/invoicing/registers/?doc=sales&mode=month&month={m}", 6),
            ("purchase register (month)", $"/invoicing/registers/?doc=purchase&mode=month&month={m}", 3),
            ("payment & receipt feed", "/invoicing/tracking/", 3),
            ("sales tracking", "/invoicing/tracking/sales", 3),
            ("customers", "/inventory/customers/", 3),
            ("products", "/inventory/products/", 3),
            ("sales orders", "/inventory/sales/list", 2),
            ("purchase orders", "/inventory/purchases/list", 1),
            ("sales returns", "/invoicing/sales-return/list", 1),
            ("fixed asset register", "/fixed-assets/assets/", 1),
            ("hub (module tiles)", "/dashboard/", 2),
        };
        foreach (var (name, url, weight) in candidates)
        {
            string u = url.Replace("{hot}", _hot[0].ToString(CultureInfo.InvariantCulture));
            var ans = await b.GetAsync(u);
            if (ans.Status == 200)
                _reads.Add((name, url, weight));
            else
                _unavailable.Add((name, u, ans.Status));
        }
        _reads = _reads.ToList();
    }

    private async Task<int[]> CreateHotDraftsAsync(Browser b, int n)
    {
        var ids = new List<int>();
        for (int i = 0; i < n; i++)
        {
            var form = await b.GetAsync("/accounting/vouchers?type=CPV");
            var r = await b.PostFormAsync("/accounting/vouchers", Cpv("save", 1000 + i, $"Load hot draft {i + 1}", new Random(i)),
                form.CsrfToken ?? throw new InvalidOperationException("No voucher form: is the Accounting module in the host? " + form.Status));
            if (r.Location is not { } loc || !int.TryParse(loc.Split('/').Last(), out int id))
                throw new InvalidOperationException($"Could not create a draft voucher: {r.Status} {r.Body[..Math.Min(300, r.Body.Length)]}");
            ids.Add(id);
        }
        return [.. ids];
    }

    private async Task<List<int>> MembersAsync(NpgsqlDataSource ds)
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT m.user_id FROM company_memberships m JOIN users u ON u.id = m.user_id " +
            "WHERE m.company_id = @c AND m.status = 'active' AND u.is_active ORDER BY m.user_id", conn);
        cmd.Parameters.AddWithValue("c", a.Company);
        var ids = new List<int>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) ids.Add(r.GetInt32(0));
        return ids;
    }

    private async Task<(int Cash, int Expense)> AccountsAsync(NpgsqlDataSource ds) =>
        (await ScalarAsync(ds, $"SELECT id FROM chart_of_accounts WHERE company_id = {a.Company} AND code = '1-01-01-01-0001'"),
         await ScalarAsync(ds, $"SELECT id FROM chart_of_accounts WHERE company_id = {a.Company} AND code = '5-04-01-01-0001'"));

    private static async Task<int> ScalarAsync(NpgsqlDataSource ds, string sql)
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<(int MaxConnections, int MaxLockWaits)> WatchAsync(NpgsqlDataSource ds, CancellationToken stop)
    {
        int conns = 0, waits = 0;
        await using var conn = await ds.OpenConnectionAsync();
        while (!stop.IsCancellationRequested)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT COUNT(*) FILTER (WHERE application_name = 'accountix-dotnet'), " +
                "COUNT(*) FILTER (WHERE wait_event_type = 'Lock') FROM pg_stat_activity WHERE datname = current_database()", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            await r.ReadAsync();
            conns = Math.Max(conns, (int)r.GetInt64(0));
            waits = Math.Max(waits, (int)r.GetInt64(1));
            await r.DisposeAsync();
            try { await Task.Delay(250, stop); } catch (TaskCanceledException) { }
        }
        return (conns, waits);
    }

    private sealed record SeriesCheck(bool Ok, string Detail);

    /// <summary>Vouchers made during the run: one number each, no duplicates, no gaps (numbers are taken in commit order).</summary>
    private async Task<SeriesCheck> RunSeriesAsync(NpgsqlDataSource ds, int maxIdBefore)
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT voucher_type, COUNT(*), COUNT(DISTINCT voucher_number), " +
            "MIN(CAST(SUBSTRING(voucher_number FROM '([0-9]+)$') AS INTEGER)), MAX(CAST(SUBSTRING(voucher_number FROM '([0-9]+)$') AS INTEGER)), " +
            "COUNT(*) FILTER (WHERE status = 'approved') " +
            "FROM accounting_vouchers WHERE id > @id AND company_id = @c GROUP BY voucher_type ORDER BY 1", conn);
        cmd.Parameters.AddWithValue("id", maxIdBefore);
        cmd.Parameters.AddWithValue("c", a.Company);
        var parts = new List<string>();
        bool ok = true;
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            long n = r.GetInt64(1), distinct = r.GetInt64(2);
            int lo = r.GetInt32(3), hi = r.GetInt32(4);
            bool gapless = hi - lo + 1 == n && distinct == n;
            ok &= gapless;
            parts.Add($"{r.GetString(0)}: {n} new ({r.GetInt64(5)} approved), numbers {lo}–{hi} {(gapless ? "gapless, unique" : "GAPS OR DUPLICATES")}");
        }
        return new SeriesCheck(ok, string.Join("; ", parts));
    }

    // ── report ───────────────────────────────────────────────────────────

    private string Report(TimeSpan elapsed, int people, long deadlocks, int maxConns, int maxLockWaits,
                          List<CheckResult> before, List<CheckResult> after, SeriesCheck series, int serverErrors, int antiforgery)
    {
        var all = _samples.ToArray();
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Load test: {a.Vus} virtual users, {people} people, company {a.Company}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Host {a.BaseUrl}; {elapsed.TotalSeconds:F0} s; think time {a.Think.Min}–{a.Think.Max} ms; {a.Hot} hot drafts shared by everyone");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Requests: {all.Length:N0} ({all.Length / elapsed.TotalSeconds:F1}/s); server errors (5xx or no answer): **{serverErrors}**; antiforgery refusals (400): **{antiforgery}**; 409 answers: **{all.Count(s => s.Status == 409)}**");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Deadlocks (pg_stat_database): **{deadlocks}**; most connections held by the host: {maxConns}; most sessions waiting on a lock at once: {maxLockWaits}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Vouchers made in the run: {series.Detail}");
        sb.AppendLine();
        sb.AppendLine("| Page | n | p50 ms | p95 ms | p99 ms | max ms | 4xx | 5xx | avg KB |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var g in all.GroupBy(s => s.Page).OrderByDescending(g => g.Count()))
        {
            var ms = g.Select(s => s.Ms).Order().ToArray();
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {g.Key} | {ms.Length} | {Pct(ms, 50):F0} | {Pct(ms, 95):F0} | {Pct(ms, 99):F0} | {ms[^1]:F0} | {g.Count(s => s.Status is >= 400 and < 500)} | {g.Count(s => s.Status >= 500)} | {g.Average(s => s.Bytes) / 1024:F0} |");
        }
        sb.AppendLine();
        sb.AppendLine("| Write | n | ok | refused (business rule) | conflict | other |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var g in _writes.GroupBy(w => w.Action).OrderBy(g => g.Key))
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {g.Key} | {g.Count()} | {g.Count(w => w.Outcome.StartsWith("ok"))} | {g.Count(w => w.Outcome.StartsWith("refused (b"))} | {g.Count(w => w.Outcome.StartsWith("conflict"))} | {g.Count(w => !w.Outcome.StartsWith("ok") && !w.Outcome.StartsWith("refused (b") && !w.Outcome.StartsWith("conflict"))} |");
        var refusals = _writes.Where(w => !w.Outcome.StartsWith("ok")).GroupBy(w => w.Outcome + ": " + w.Message).OrderByDescending(g => g.Count()).Take(8).ToList();
        if (refusals.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Answers other than success, by text:");
            foreach (var g in refusals)
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {g.Count()}× {g.Key}");
        }
        if (_unavailable.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Pages left out (not in this host yet, or refused): " +
                          string.Join(", ", _unavailable.Select(u => $"{u.Name} ({u.Status})")));
        }
        sb.AppendLine();
        sb.AppendLine("| Data check | before | after | detail (after) |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var c in after)
        {
            string b4 = before.FirstOrDefault(x => x.Name == c.Name) is { } x ? (x.Ok ? "pass" : "FAIL") : "–";
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {c.Name} | {b4} | {(c.Ok ? "pass" : "**FAIL**")} | {c.Detail.Replace("|", "/")} |");
        }
        sb.AppendLine();
        sb.AppendLine(Passed ? "**PASSED**: no server errors, no deadlocks, every check holds."
                             : "**FAILED**: see server errors, deadlocks or failed checks above.");
        return sb.ToString();
    }

    private static double Pct(double[] sorted, int p) =>
        sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(p / 100.0 * sorted.Length) - 1, 0, sorted.Length - 1)];
}
