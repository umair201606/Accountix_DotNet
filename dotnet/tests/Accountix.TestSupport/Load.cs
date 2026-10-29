using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Accountix.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Accountix.TestSupport;

/// <summary>
/// Counts the SQL commands the app sends (every ModuleHost has one: host.Queries).
/// Reset it, make a request, read Count. The test classes of one assembly
/// run one after another, so a host's count belongs to the test using it.
/// </summary>
public sealed partial class QueryCounter : DbCommandInterceptor
{
    private int _count;
    private readonly ConcurrentQueue<string> _sql = new();

    public int Count => Volatile.Read(ref _count);

    public void Reset()
    {
        Interlocked.Exchange(ref _count, 0);
        _sql.Clear();
    }

    /// <summary>The commands run since the last Reset, most repeated first: "N× SELECT …".
    /// A statement repeated once per row is an N+1 to fix.</summary>
    public IReadOnlyList<string> Repeated(int top = 10) =>
        _sql.GroupBy(s => Numbers().Replace(s, "?"))
            .OrderByDescending(g => g.Count())
            .Take(top)
            .Select(g => $"{g.Count()}× {Squash(g.Key)}")
            .ToList();

    private static string Squash(string sql)
    {
        string one = Regex.Replace(sql, @"\s+", " ").Trim();
        return one.Length > 220 ? one[..220] + "…" : one;
    }

    [GeneratedRegex(@"(?<![A-Za-z_])\d+(\.\d+)?|'[^']*'")]
    private static partial Regex Numbers();

    private void Seen(DbCommand command)
    {
        Interlocked.Increment(ref _count);
        if (_sql.Count < 5000)
            _sql.Enqueue(command.CommandText);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    { Seen(command); return result; }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
    { Seen(command); return ValueTask.FromResult(result); }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    { Seen(command); return result; }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken ct = default)
    { Seen(command); return ValueTask.FromResult(result); }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    { Seen(command); return result; }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    { Seen(command); return ValueTask.FromResult(result); }
}

/// <summary>What one page cost: the best of a few runs after a warm-up.</summary>
public sealed record PageTiming(string Url, System.Net.HttpStatusCode Status, TimeSpan Elapsed, int Queries, string Body, IReadOnlyList<string> Repeated)
{
    public override string ToString() =>
        $"{Url}: {(int)Status} in {Elapsed.TotalMilliseconds:F0} ms, {Queries} queries" +
        (Repeated.Count > 0 ? "\n  " + string.Join("\n  ", Repeated.Take(5)) : "");

    /// <summary>Fails with the timing and the most repeated SQL when over budget.</summary>
    public PageTiming Within(int maxMilliseconds, int maxQueries)
    {
        Assert.True((int)Status < 400, $"{this}\n{Body[..Math.Min(Body.Length, 1500)]}");
        Assert.True(Queries <= maxQueries, $"Too many queries (budget {maxQueries}): {this}");
        Assert.True(Elapsed.TotalMilliseconds <= maxMilliseconds, $"Too slow (budget {maxMilliseconds} ms): {this}");
        return this;
    }
}

/// <summary>
/// Fires requests at the same instant, as several people pressing a button
/// together. Prepare everything a request needs (clients, antiforgery tokens)
/// BEFORE the race, so the requests really overlap.
/// </summary>
public static class Race
{
    public static async Task<T[]> RunAsync<T>(int n, Func<int, Task<T>> body)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, n)
            .Select(i => Task.Run(async () => { await gate.Task; return await body(i); }))
            .ToArray();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    /// <summary>No request may end in a server error (a conflict must be answered, 409 or a flash).</summary>
    public static void NoServerErrors(IEnumerable<HttpResponseMessage> responses)
    {
        foreach (var r in responses)
        {
            if ((int)r.StatusCode < 500)
                continue;
            // Read the body only for a failure (and never slice past its end).
            string body = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Fail($"{(int)r.StatusCode} {r.RequestMessage?.Method} {r.RequestMessage?.RequestUri}: {body[..Math.Min(800, body.Length)]}");
        }
    }
}

/// <summary>
/// The speed-test database: one full financial year of Samba Private Limited
/// (tools/seed_samba.py, ~7,500 documents posted through the Python app's
/// routes) described by dotnet/tools/perf.json. Read-only for tests: time
/// pages and exports against it, never post to it (sessions rows excepted).
/// </summary>
public static class Perf
{
    private static readonly Lazy<JsonElement?> Data = new(Load);

    public static bool Available => Data.Value is not null && Reference.Available;
    public static JsonElement Json => Data.Value ?? throw new InvalidOperationException("perf.json missing");
    public static string ConnectionString => Json.GetProperty("connection_string").GetString()!;
    public static string DatabaseUrl => Json.GetProperty("database_url").GetString()!;
    public static int CompanyId => Json.GetProperty("company_id").GetInt32();
    public static int AdminUserId => Json.GetProperty("admin_user_id").GetInt32();
    /// <summary>The month with the most sales invoices, as "yyyy-MM" (also its first and last day).</summary>
    public static string BusiestMonth => Json.GetProperty("busiest_month").GetString()!;
    public static DateOnly BusiestMonthStart => DateOnly.ParseExact(BusiestMonth + "-01", "yyyy-MM-dd");
    public static DateOnly BusiestMonthEnd => BusiestMonthStart.AddMonths(1).AddDays(-1);
    public static DateOnly YearStart => DateOnly.Parse(Json.GetProperty("year_start").GetString()!);
    public static DateOnly YearEnd => DateOnly.Parse(Json.GetProperty("year_end").GetString()!);
    /// <summary>Row counts of the big tables (sales invoices, journal lines, ...), for messages.</summary>
    public static int Rows(string table) => Json.GetProperty("rows").GetProperty(table).GetInt32();

    public static AccountixDbContext NewDb(CompanyContext? company = null) =>
        new(new DbContextOptionsBuilder<AccountixDbContext>().UseNpgsql(ConnectionString).Options,
            company ?? new CompanyContext());

    private static JsonElement? Load()
    {
        if (Reference.Path is null)
            return null;
        string file = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Reference.Path)!, "perf.json");
        if (!File.Exists(file))
            return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        return doc.RootElement.Clone();
    }
}

/// <summary>
/// The 4× speed-test company (tools/seed_samba.py --scale 4: 22,786 documents, 10,405 sales invoices,
/// ~90k journal lines; busiest month 1,108 sales invoices): perf.json "large". Read-only, like <see cref="Perf"/>.
/// </summary>
public static class PerfLarge
{
    public static bool Available => Perf.Available && Perf.Json.TryGetProperty("large", out _);
    private static JsonElement J => Perf.Json.GetProperty("large");
    public static string ConnectionString => J.GetProperty("connection_string").GetString()!;
    public static string DatabaseUrl => J.GetProperty("database_url").GetString()!;
    public static int CompanyId => J.GetProperty("company_id").GetInt32();
    public static int AdminUserId => J.GetProperty("admin_user_id").GetInt32();
    public static string BusiestMonth => J.GetProperty("busiest_month").GetString()!;
    public static DateOnly BusiestMonthStart => DateOnly.ParseExact(BusiestMonth + "-01", "yyyy-MM-dd");
    public static DateOnly BusiestMonthEnd => BusiestMonthStart.AddMonths(1).AddDays(-1);
    public static DateOnly YearStart => DateOnly.Parse(J.GetProperty("year_start").GetString()!);
    public static DateOnly YearEnd => DateOnly.Parse(J.GetProperty("year_end").GetString()!);
    public static int Rows(string table) => J.GetProperty("rows").GetProperty(table).GetInt32();
}

/// <summary>Needs the 4× speed-test database (perf.json "large").</summary>
public sealed class PerfLargeFactAttribute : FactAttribute
{
    public PerfLargeFactAttribute()
    {
        if (!PerfLarge.Available)
            Skip = "4x speed-test database not available (perf.json \"large\")";
    }
}

/// <summary>Needs the speed-test database (dotnet/tools/perf.json).</summary>
public sealed class PerfFactAttribute : FactAttribute
{
    public PerfFactAttribute()
    {
        if (!Perf.Available)
            Skip = "Speed-test database not available (dotnet/tools/perf.json)";
    }
}

public static class TimingExtensions
{
    private static readonly object TimingsLock = new();

    /// <summary>
    /// GET <paramref name="url"/> once to warm up (JIT, view compilation, pools),
    /// then <paramref name="runs"/> times; returns the fastest run with its query count.
    /// </summary>
    public static async Task<PageTiming> MeasureAsync(this ModuleHost host, HttpClient client, string url, int runs = 3)
    {
        await (await client.GetAsync(url)).Content.ReadAsByteArrayAsync();
        PageTiming? best = null;
        for (int i = 0; i < runs; i++)
        {
            host.Queries.Reset();
            var sw = Stopwatch.StartNew();
            var r = await client.GetAsync(url);
            byte[] bytes = await r.Content.ReadAsByteArrayAsync();
            sw.Stop();
            string body = r.Content.Headers.ContentType?.MediaType is { } mt && (mt.StartsWith("text/") || mt.Contains("json"))
                ? System.Text.Encoding.UTF8.GetString(bytes) : $"<{bytes.Length} bytes {r.Content.Headers.ContentType}>";
            var t = new PageTiming(url, r.StatusCode, sw.Elapsed, host.Queries.Count, body, host.Queries.Repeated());
            if (best is null || t.Elapsed < best.Elapsed)
                best = t;
        }
        // ACCOUNTIX_TIMINGS=<file>: one line per measured page, for the "Speed" tables in docs/modules.
        if (Environment.GetEnvironmentVariable("ACCOUNTIX_TIMINGS") is { Length: > 0 } log)
            lock (TimingsLock)
                File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss}\t{best!.Elapsed.TotalMilliseconds:F0} ms\t{best.Queries} q\t{(int)best.Status}\t{url}\n");
        return best!;
    }
}
