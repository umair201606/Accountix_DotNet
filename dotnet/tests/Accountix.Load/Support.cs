using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Accountix.Core.Security;
using Accountix.TestSupport;
using Npgsql;

namespace Accountix.Load;

public sealed class Args
{
    public string Command { get; private init; } = "";
    public string BaseUrl { get; private init; } = "http://127.0.0.1:5321";
    public string Db { get; private init; } = "";
    public string Secret { get; private init; } = "";
    public int Company { get; private init; }
    public string Users { get; private init; } = "";
    public int User { get; private init; }
    public int Vus { get; private init; } = 30;
    public int Seconds { get; private init; } = 180;
    public (int Min, int Max) Think { get; private init; } = (500, 2000);
    public int Hot { get; private init; } = 4;
    public string Month { get; private init; } = "2026-05";
    /// <summary>The financial year for year reports (--year-from / --year-to).</summary>
    public string YearFrom { get; private init; } = "2025-07-01";
    public string YearTo { get; private init; } = "2026-06-30";
    public string? Out { get; private init; }
    /// <summary>seed-users: how many load people, and how many of them admins.</summary>
    public int Count { get; private init; } = 29;
    public int Admins { get; private init; } = 9;
    public string Url { get; private init; } = "/";

    public static Args Parse(string[] argv)
    {
        var d = new Dictionary<string, string>();
        for (int i = 1; i < argv.Length - 1; i += 2)
            d[argv[i].TrimStart('-')] = argv[i + 1];
        string Get(string k, string dflt = "") => d.TryGetValue(k, out var v) ? v : dflt;
        string[] think = Get("think", "500-2000").Split('-');
        return new Args
        {
            Command = argv.Length > 0 ? argv[0] : "",
            BaseUrl = Get("base", "http://127.0.0.1:5321").TrimEnd('/'),
            Db = Get("db"),
            Secret = Get("secret", Environment.GetEnvironmentVariable("SECRET_KEY") ?? ""),
            Company = int.Parse(Get("company", "0"), CultureInfo.InvariantCulture),
            Users = Get("users"),
            User = int.Parse(Get("user", "0"), CultureInfo.InvariantCulture),
            Vus = int.Parse(Get("vus", "30"), CultureInfo.InvariantCulture),
            Seconds = int.Parse(Get("seconds", "180"), CultureInfo.InvariantCulture),
            Think = (int.Parse(think[0], CultureInfo.InvariantCulture), int.Parse(think[^1], CultureInfo.InvariantCulture)),
            Hot = int.Parse(Get("hot", "4"), CultureInfo.InvariantCulture),
            Month = Get("month", "2026-05"),
            YearFrom = Get("year-from", "2025-07-01"),
            YearTo = Get("year-to", "2026-06-30"),
            Out = d.GetValueOrDefault("out"),
            Count = int.Parse(Get("count", "29"), CultureInfo.InvariantCulture),
            Admins = int.Parse(Get("admins", "9"), CultureInfo.InvariantCulture),
            Url = Get("url", "/"),
        };
    }

    /// <summary>"1,6-34" → [1, 6, 7, ..., 34].</summary>
    public static List<int> Ids(string spec) =>
        spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(p => p.Contains('-')
                ? Enumerable.Range(int.Parse(p.Split('-')[0], CultureInfo.InvariantCulture),
                    int.Parse(p.Split('-')[1], CultureInfo.InvariantCulture) - int.Parse(p.Split('-')[0], CultureInfo.InvariantCulture) + 1)
                : [int.Parse(p, CultureInfo.InvariantCulture)])
            .ToList();
}

/// <summary>Signs a person in the way the app's sign-in does: a user_sessions row plus the session cookie.</summary>
public static class Sessions
{
    public static async Task<string> CookieAsync(NpgsqlDataSource ds, int userId, int companyId, string secret)
    {
        await using var conn = await ds.OpenConnectionAsync();
        string hash;
        await using (var q = new NpgsqlCommand("SELECT password_hash FROM users WHERE id = @id", conn))
        {
            q.Parameters.AddWithValue("id", userId);
            hash = (string)(await q.ExecuteScalarAsync() ?? throw new InvalidOperationException($"No user {userId}"));
        }
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        await using (var ins = new NpgsqlCommand(
            "INSERT INTO user_sessions (token_hash, user_id, created_at, last_seen_at, user_agent) VALUES (@h, @u, @t, @t, 'Accountix.Load')", conn))
        {
            ins.Parameters.AddWithValue("h", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))));
            ins.Parameters.AddWithValue("u", userId);
            ins.Parameters.AddWithValue("t", now);
            await ins.ExecuteNonQueryAsync();
        }
        string json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["_fresh"] = true,
            ["_sid"] = token,
            ["_user_id"] = $"{userId}:{SessionFingerprint.For(hash, secret)}",
            ["company_id"] = companyId,
        });
        return TestSignIn.Sign(json, secret, DateTimeOffset.UtcNow);
    }
}

/// <summary>One browser: its own connections and cookie jar; follows nothing by itself.</summary>
public sealed class Browser : IDisposable
{
    private static readonly Regex Token = new("<meta name=\"csrf-token\" content=\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex Flash = new("class=\"app-flash app-flash-(\\w+)\"[^>]*>.*?<span class=\"app-flash-msg\">(.*?)</span>",
                                              RegexOptions.Compiled | RegexOptions.Singleline);
    private readonly HttpClient _http;
    public int UserId { get; }

    public Browser(string baseUrl, string sessionCookie, int userId, bool decompress = true)
    {
        UserId = userId;
        var jar = new CookieContainer();
        jar.Add(new Uri(baseUrl), new Cookie(TestSignIn.CookieName, sessionCookie));
        _http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            CookieContainer = jar,
            UseCookies = true,
            AutomaticDecompression = decompress ? DecompressionMethods.All : DecompressionMethods.None,
            MaxConnectionsPerServer = 6,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(120) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Accountix.Load/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/json;q=0.9,*/*;q=0.8");
    }

    public sealed record Answer(int Status, string Body, double Ms, long Bytes, string? Location)
    {
        public string? CsrfToken => Token.Match(Body) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;
        public IReadOnlyList<(string Kind, string Text)> Flashes =>
            Flash.Matches(Body).Select(m => (m.Groups[1].Value, WebUtility.HtmlDecode(m.Groups[2].Value))).ToList();
    }

    public Task<Answer> GetAsync(string url) => SendAsync(new HttpRequestMessage(HttpMethod.Get, url));

    public Task<Answer> PostFormAsync(string url, IEnumerable<KeyValuePair<string, string>> fields, string token) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields.Append(new("__RequestVerificationToken", token))),
        });

    public async Task<Answer> SendAsync(HttpRequestMessage req)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var r = await _http.SendAsync(req);
            byte[] body = await r.Content.ReadAsByteArrayAsync();
            sw.Stop();
            bool text = r.Content.Headers.ContentType?.MediaType is { } mt && (mt.StartsWith("text/") || mt.Contains("json"));
            return new Answer((int)r.StatusCode, text ? Encoding.UTF8.GetString(body) : "", sw.Elapsed.TotalMilliseconds,
                body.Length, r.Headers.Location?.OriginalString);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return new Answer(599, e.Message, sw.Elapsed.TotalMilliseconds, 0, null);   // no answer at all
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Bytes of one page as sent: uncompressed, gzip, Brotli.</summary>
public static class PageSize
{
    public static async Task<List<(string Encoding, long Bytes, double Ms)>> MeasureAsync(Args a)
    {
        await using var ds = NpgsqlDataSource.Create(a.Db);
        string cookie = await Sessions.CookieAsync(ds, a.User, a.Company, a.Secret);
        using var b = new Browser(a.BaseUrl, cookie, a.User, decompress: false);
        var list = new List<(string, long, double)>();
        await b.GetAsync(a.Url);                                  // warm up
        foreach (string enc in new[] { "identity", "gzip", "br" })
        {
            var req = new HttpRequestMessage(HttpMethod.Get, a.Url);
            req.Headers.TryAddWithoutValidation("Accept-Encoding", enc);
            var ans = await b.SendAsync(req);
            list.Add((enc, ans.Bytes, ans.Ms));
        }
        return list;
    }
}
