using System.Text.Json.Nodes;
using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.Core.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Accountix.TestSupport;

/// <summary>
/// Sign a test client in as anyone in the reference database, the way the
/// app's sign-in does: a server session row (user_sessions) plus the
/// <c>ax_session</c> cookie (<see cref="SessionCookie"/>) signed with SECRET_KEY.
/// </summary>
public static class TestSignIn
{
    /// <summary>The sign-in cookie's name: put <see cref="CookieFor(int, int?)"/>'s value under it.</summary>
    public const string CookieName = SessionCookie.CookieName;

    public static string CookieFor(int userId, int? companyId = null) => CookieFor(userId, companyId, null);

    /// <summary>The same, on another database (the speed-test one: <see cref="Perf.ConnectionString"/>).</summary>
    public static string CookieFor(int userId, int? companyId, string? connectionString)
    {
        using var db = connectionString is null ? Reference.NewDb()
            : new AccountixDbContext(new DbContextOptionsBuilder<AccountixDbContext>().UseNpgsql(connectionString).Options, new CompanyContext());
        var user = db.Users.AsNoTracking().First(u => u.Id == userId);
        string token = SessionCookie.NewToken();
        var now = DateTime.UtcNow;
        db.UserSessions.Add(new UserSession
        {
            TokenHash = SessionCookie.TokenHash(token),
            UserId = userId, CreatedAt = now, LastSeenAt = now,
        });
        db.SaveChanges();
        var session = SessionCookie.NewSession(userId, SessionFingerprint.For(user.PasswordHash, Reference.SecretKey), token);
        if (companyId is int cid)
            session[SessionCookie.CompanyKey] = cid;
        return SessionCookie.Sign(session, Reference.SecretKey, DateTimeOffset.UtcNow);
    }

    /// <summary>A client of the .NET app signed in as this person (in this company).</summary>
    public static HttpClient ClientFor<TEntry>(WebApplicationFactory<TEntry> app, int userId, int? companyId = null) where TEntry : class
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{CookieName}={CookieFor(userId, companyId)}");
        return client;
    }

    /// <summary>
    /// The <c>ax_session</c> value for a session written as JSON (keys <c>_user_id</c>, <c>_sid</c>,
    /// <c>company_id</c>). The value is compressed when that is shorter, whatever <paramref name="compress"/> says
    /// (kept for callers written against the old cookie).
    /// </summary>
    public static string Sign(string json, string secret, DateTimeOffset at, bool compress = false) =>
        SessionCookie.Sign(JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("not a JSON object", nameof(json)),
                           secret, at);
}

/// <summary>The whole .NET app on the reference database, for page tests. Use as an xUnit class fixture.</summary>
public class ReferenceApp<TEntry> : WebApplicationFactory<TEntry> where TEntry : class
{
    public ReferenceApp()
    {
        if (!Reference.Available)
            return;
        Environment.SetEnvironmentVariable("SECRET_KEY", Reference.SecretKey);
        Environment.SetEnvironmentVariable("DATABASE_URL", null);
        Environment.SetEnvironmentVariable("ConnectionStrings__Accountix", Reference.ConnectionString);
    }
}

/// <summary>
/// A test server running Core + Web.Shared + only the named module
/// assemblies, on the reference database (no dependency on modules still
/// being written by others). Dispose after use.
/// </summary>
public sealed class ModuleHost : IAsyncDisposable
{
    private readonly Microsoft.AspNetCore.Builder.WebApplication _app;
    private readonly string _connectionString;

    private ModuleHost(Microsoft.AspNetCore.Builder.WebApplication app, string connectionString, QueryCounter queries)
    {
        _app = app;
        _connectionString = connectionString;
        Queries = queries;
    }

    /// <summary>Every SQL command this host's app sends (reset it before the request you measure).</summary>
    public QueryCounter Queries { get; }

    public string ConnectionString => _connectionString;

    public static Task<ModuleHost> StartAsync(params System.Reflection.Assembly[] moduleAssemblies) =>
        StartOnAsync(Reference.ConnectionString, moduleAssemblies);

    /// <summary>The same app on another database, e.g. <see cref="Perf.ConnectionString"/> for speed tests.</summary>
    public static async Task<ModuleHost> StartOnAsync(string connectionString, params System.Reflection.Assembly[] moduleAssemblies)
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(new Microsoft.AspNetCore.Builder.WebApplicationOptions
        {
            EnvironmentName = "Development",
            ApplicationName = moduleAssemblies.FirstOrDefault()?.GetName().Name,
        });
        Microsoft.AspNetCore.TestHost.WebHostBuilderExtensions.UseTestServer(builder.WebHost);
        builder.Configuration["SECRET_KEY"] = Reference.SecretKey;
        builder.Configuration["ConnectionStrings:Accountix"] = connectionString;
        Accountix.Web.Infrastructure.AccountixApp.ConfigureServices(builder, moduleAssemblies);
        var queries = new QueryCounter();
        builder.Services.AddSingleton<Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor>(queries);
        // Quiet logs: per-command logging would dominate timings.
        builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning);
        var app = builder.Build();
        Accountix.Web.Infrastructure.AccountixApp.ConfigurePipeline(app);
        await app.StartAsync();
        return new ModuleHost(app, connectionString, queries);
    }

    public IServiceProvider Services => _app.Services;

    /// <summary>A browser-like client signed in as this person (in this company): keeps cookies, does not follow redirects.</summary>
    public PageClient ClientFor(int userId, int? companyId = null)
    {
        var server = Microsoft.AspNetCore.TestHost.HostBuilderTestServerExtensions.GetTestServer(_app);
        var client = new PageClient(server.CreateHandler());
        client.Cookies.Add(new Uri("http://localhost/"), new System.Net.Cookie(TestSignIn.CookieName, TestSignIn.CookieFor(userId, companyId, _connectionString)));
        return client;
    }

    /// <summary>n people signed in as this person (each its own session), for races.</summary>
    public PageClient[] ClientsFor(int userId, int? companyId, int n) =>
        Enumerable.Range(0, n).Select(_ => ClientFor(userId, companyId)).ToArray();

    public PageClient AnonymousClient() =>
        new(Microsoft.AspNetCore.TestHost.HostBuilderTestServerExtensions.GetTestServer(_app).CreateHandler());

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}


/// <summary>
/// An HttpClient that keeps cookies like a browser and posts forms the way
/// the app's pages do: with the antiforgery token the page carries.
/// </summary>
public sealed class PageClient : HttpClient
{
    public System.Net.CookieContainer Cookies { get; }

    public PageClient(HttpMessageHandler inner) : this(inner, new System.Net.CookieContainer()) { }

    private PageClient(HttpMessageHandler inner, System.Net.CookieContainer jar) : base(new CookieHandler(inner, jar))
    {
        Cookies = jar;
        BaseAddress = new Uri("http://localhost/");
    }

    /// <summary>The antiforgery token from a page's &lt;meta name="csrf-token"&gt;.</summary>
    public async Task<string> TokenFromAsync(string pageUrl)
    {
        string html = await (await GetAsync(pageUrl)).Content.ReadAsStringAsync();
        var m = System.Text.RegularExpressions.Regex.Match(html, "<meta name=\"csrf-token\" content=\"([^\"]+)\"");
        if (!m.Success)
            throw new InvalidOperationException($"No antiforgery token on {pageUrl}");
        return System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
    }

    /// <summary>POST a form as the page at <paramref name="tokenPage"/> would (repeat keys for list fields).</summary>
    public async Task<HttpResponseMessage> PostFormAsync(string url, IEnumerable<KeyValuePair<string, string>> fields, string tokenPage) =>
        await PostFormWithTokenAsync(url, fields, await TokenFromAsync(tokenPage));

    /// <summary>POST a form with a token fetched beforehand (races: fetch tokens first, then fire together).</summary>
    public Task<HttpResponseMessage> PostFormWithTokenAsync(string url, IEnumerable<KeyValuePair<string, string>> fields, string token) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields.Append(new("__RequestVerificationToken", token))),
        });

    /// <summary>POST JSON as the pages' fetch() calls do: the antiforgery token in X-CSRF-Token.</summary>
    public Task<HttpResponseMessage> PostJsonWithTokenAsync(string url, object payload, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = System.Net.Http.Json.JsonContent.Create(payload) };
        req.Headers.Add("X-CSRF-Token", token);
        req.Headers.Add("Accept", "application/json");
        return SendAsync(req);
    }

    public async Task<HttpResponseMessage> PostJsonAsync(string url, object payload, string tokenPage) =>
        await PostJsonWithTokenAsync(url, payload, await TokenFromAsync(tokenPage));

    private sealed class CookieHandler(HttpMessageHandler inner, System.Net.CookieContainer jar) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!.IsAbsoluteUri ? request.RequestUri : new Uri(new Uri("http://localhost/"), request.RequestUri);
            string cookies = jar.GetCookieHeader(uri);
            if (cookies.Length > 0)
                request.Headers.TryAddWithoutValidation("Cookie", cookies);
            var response = await base.SendAsync(request, ct);
            if (response.Headers.TryGetValues("Set-Cookie", out var set))
                foreach (string c in set)
                    jar.SetCookies(uri, c);
            return response;
        }
    }
}
