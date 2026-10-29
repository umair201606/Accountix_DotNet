using System.Net;
using System.Text.RegularExpressions;
using Accountix.TestSupport;
using Accountix.Web.Hosting;
using Accountix.Web.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Accountix.Tests;

/// <summary>Npgsql settings for Neon (DatabaseTuning): applied outside Development, explicit values win.</summary>
public class DatabaseTuningTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private const string Neon = "Host=ep-cool-name-123456-pooler.eastus2.azure.neon.tech;Database=neondb;Username=app;Password=x";

    [Fact]
    public void Production_gets_the_neon_settings()
    {
        var b = new NpgsqlConnectionStringBuilder(DatabaseTuning.Apply(Neon, Config(), production: true));
        Assert.Equal(20, b.MaxPoolSize);
        Assert.Equal(30, b.Timeout);
        Assert.Equal(60, b.CommandTimeout);
        Assert.Equal(30, b.KeepAlive);
        Assert.Equal(60, b.ConnectionIdleLifetime);
        Assert.True(b.NoResetOnClose);               // pooled endpoint: PgBouncer, transaction mode
        Assert.Equal(SslMode.Require, b.SslMode);
        Assert.Equal("accountix-dotnet", b.ApplicationName);
    }

    [Fact]
    public void App_settings_and_explicit_connection_string_values_win()
    {
        string cs = Neon + ";Maximum Pool Size=7;SSL Mode=VerifyFull";
        var b = new NpgsqlConnectionStringBuilder(DatabaseTuning.Apply(cs, Config(("DB_MAX_POOL_SIZE", "40"), ("DB_COMMAND_TIMEOUT_S", "90")), true));
        Assert.Equal(7, b.MaxPoolSize);              // named in the connection string
        Assert.Equal(90, b.CommandTimeout);          // App Setting
        Assert.Equal(SslMode.VerifyFull, b.SslMode);
    }

    [Fact]
    public void Database_urls_from_neon_and_from_a_local_socket_both_read()
    {
        var neon = new NpgsqlConnectionStringBuilder(AppSettings.FromDatabaseUrl(
            "postgresql://app:p%40ss@ep-cool-name-123456-pooler.eastus2.azure.neon.tech/neondb?sslmode=require&channel_binding=require"));
        Assert.Equal("ep-cool-name-123456-pooler.eastus2.azure.neon.tech", neon.Host);
        Assert.Equal(("app", "p@ss", "neondb", 5432), (neon.Username, neon.Password, neon.Database, neon.Port));
        Assert.Equal(SslMode.Require, neon.SslMode);
        Assert.Equal(ChannelBinding.Require, neon.ChannelBinding);

        var socket = new NpgsqlConnectionStringBuilder(AppSettings.FromDatabaseUrl(
            "postgresql://postgres:@/accountix_ref_load?host=/private/tmp/pg"));
        Assert.Equal(("/private/tmp/pg", "postgres", "accountix_ref_load"), (socket.Host, socket.Username, socket.Database));
    }

    [Fact]
    public void Direct_endpoint_keeps_connection_reset_and_development_is_untouched()
    {
        string direct = Neon.Replace("-pooler", "");
        Assert.False(new NpgsqlConnectionStringBuilder(DatabaseTuning.Apply(direct, Config(), true)).NoResetOnClose);

        string local = "Host=/tmp/pg;Database=accountix_ref;Username=postgres";
        var dev = new NpgsqlConnectionStringBuilder(DatabaseTuning.Apply(local, Config(), production: false));
        Assert.Equal(100, dev.MaxPoolSize);          // Npgsql's own default
        Assert.Equal(0, dev.KeepAlive);
        // A unix socket is never switched to SSL.
        Assert.NotEqual(SslMode.Require, new NpgsqlConnectionStringBuilder(DatabaseTuning.Apply(local, Config(), true)).SslMode);
        // An App Setting applies in Development too (the load-test host's pool cap).
        Assert.Equal(20, new NpgsqlConnectionStringBuilder(DatabaseTuning.Apply(local, Config(("DB_MAX_POOL_SIZE", "20")), false)).MaxPoolSize);
    }
}

/// <summary>The deployed app (AccountixHost, Production) on the reference database, in-process.</summary>
internal static class ProductionHost
{
    public static async Task<WebApplication> StartAsync(string connectionString, params (string Key, string? Value)[] settings)
    {
        var b = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
            ApplicationName = typeof(Program).Assembly.GetName().Name,
            ContentRootPath = Path.Combine(Reference.DotnetRoot, "src", "Accountix.Web"),
        });
        b.WebHost.UseTestServer();
        b.Configuration["SECRET_KEY"] = Reference.SecretKey;
        b.Configuration["DATABASE_URL"] = "";
        b.Configuration["ConnectionStrings:Accountix"] = connectionString;
        b.Configuration["LOG_FORMAT"] = "simple";
        b.Configuration["DB_MAX_POOL_SIZE"] = "5";
        foreach (var (k, v) in settings)
            b.Configuration[k] = v;
        b.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = AccountixHost.Build(b);
        await app.StartAsync();
        return app;
    }

    public static PageClient Client(WebApplication app, int userId, int companyId, string connectionString)
    {
        var c = new PageClient(app.GetTestServer().CreateHandler());
        c.Cookies.Add(new Uri("http://localhost/"), new Cookie(TestSignIn.CookieName, TestSignIn.CookieFor(userId, companyId, connectionString)));
        return c;
    }

    /// <summary>The Data Protection key table (0017) is in the reference database (the fixture is at 0018).</summary>
    public static bool HasKeyTable()
    {
        using var conn = new NpgsqlConnection(Reference.ConnectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand($"SELECT to_regclass('{PostgresXmlRepository.Table}') IS NOT NULL", conn);
        return cmd.ExecuteScalar() is true;
    }

    /// <summary>The key table exists; brought there by the app's own migrations (Accountix.Migrations) when not.</summary>
    public static void EnsureKeyTable()
    {
        if (HasKeyTable())
            return;
        new Accountix.Migrations.Migrator(Reference.ConnectionString).MigrateAsync().GetAwaiter().GetResult();
        Assert.True(HasKeyTable(), $"{PostgresXmlRepository.Table} still missing after Accountix.Migrations ran");
    }
}

/// <summary>
/// Several instances behind App Service's load balancer: a page rendered by
/// one and posted to another must be accepted, and a flash set by one shown
/// by the other. Both need the shared Data Protection key ring in Postgres.
/// </summary>
public class SharedKeyRingTests
{
    private static int OwnerB => Reference.UserId(Reference.Email("owner_b"));
    private static int Restricted => Reference.UserId(Reference.Email("restricted"));
    private static int CompanyB => Reference.Company("b");

    [ReferenceFact]
    public async Task A_form_rendered_by_one_instance_is_accepted_by_another()
    {
        ProductionHost.EnsureKeyTable();
        string cs = Reference.ConnectionString;
        await using var a = await ProductionHost.StartAsync(cs);
        await using var b = await ProductionHost.StartAsync(cs);

        var browser = ProductionHost.Client(a, OwnerB, CompanyB, cs);
        string token = await browser.TokenFromAsync("/accounting/vouchers/list");     // rendered by A
        // The same browser (cookies: session + antiforgery) now lands on B.
        var onB = new PageClient(b.GetTestServer().CreateHandler());
        foreach (Cookie c in browser.Cookies.GetAllCookies())
            onB.Cookies.Add(new Uri("http://localhost/"), new Cookie(c.Name, c.Value));

        // A voucher that does not exist: past the antiforgery check the action answers 404.
        var ok = await onB.PostFormWithTokenAsync("/accounting/vouchers/999999999/delete", [], token);
        Assert.Equal(HttpStatusCode.NotFound, ok.StatusCode);
        // The check is real: a token no instance issued is refused.
        var forged = await onB.PostFormWithTokenAsync("/accounting/vouchers/999999999/delete", [], token[..^4] + "AAAA");
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);

        // Both instances read one key ring from the table.
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {PostgresXmlRepository.Table}", conn);
        Assert.True((long)(await cmd.ExecuteScalarAsync())! >= 1);
    }

    [ReferenceFact]
    public async Task A_flash_set_by_one_instance_is_shown_by_another()
    {
        ProductionHost.EnsureKeyTable();
        string cs = Reference.ConnectionString;
        await using var a = await ProductionHost.StartAsync(cs);
        await using var b = await ProductionHost.StartAsync(cs);

        // "restricted" may not create cash payments: A refuses with a flash and a redirect.
        var browser = ProductionHost.Client(a, Restricted, CompanyB, cs);
        var refused = await browser.GetAsync("/accounting/vouchers?type=CPV");
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);

        var onB = new PageClient(b.GetTestServer().CreateHandler());
        foreach (Cookie c in browser.Cookies.GetAllCookies())
            onB.Cookies.Add(new Uri("http://localhost/"), new Cookie(c.Name, c.Value));
        string? url = refused.Headers.Location!.OriginalString;
        string html = "";
        for (int hop = 0; hop < 4 && url is not null; hop++)
        {
            var r = await onB.GetAsync(url);
            html = await r.Content.ReadAsStringAsync();
            url = r.StatusCode == HttpStatusCode.Redirect ? r.Headers.Location!.OriginalString : null;
        }
        Assert.Contains("No &#x27;create&#x27; rights here", html);
    }

    [ReferenceFact]
    public async Task A_database_without_the_key_table_stops_the_start_with_the_fix_in_words()
    {
        string cs = new NpgsqlConnectionStringBuilder(Reference.ConnectionString) { Database = "postgres" }.ConnectionString;
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => ProductionHost.StartAsync(cs));
        Assert.Contains("dotnet_data_protection_keys is missing", e.Message);
        Assert.Contains("0017_dotnet_data_protection", e.Message);
    }
}

/// <summary>Compression, HSTS, cache headers and Sentry on the deployed app.</summary>
public class ProductionPipelineTests
{
    private static int OwnerB => Reference.UserId(Reference.Email("owner_b"));
    private static int CompanyB => Reference.Company("b");

    [ReferenceFact]
    public async Task Pages_are_compressed_marked_private_and_carry_hsts_over_https()
    {
        ProductionHost.EnsureKeyTable();
        string cs = Reference.ConnectionString;
        await using var app = await ProductionHost.StartAsync(cs);
        var c = ProductionHost.Client(app, OwnerB, CompanyB, cs);

        var plain = await c.GetAsync("/accounting/vouchers/list");
        byte[] raw = await plain.Content.ReadAsByteArrayAsync();
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Empty(plain.Content.Headers.ContentEncoding);

        foreach (string enc in new[] { "br", "gzip" })
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/accounting/vouchers/list");
            req.Headers.TryAddWithoutValidation("Accept-Encoding", enc);
            var r = await c.SendAsync(req);
            Assert.Equal(enc, Assert.Single(r.Content.Headers.ContentEncoding));
            int size = (await r.Content.ReadAsByteArrayAsync()).Length;
            Assert.True(size * 2 < raw.Length, $"{enc}: {size} bytes of {raw.Length} should be under half");
        }

        // Pages that render a form token are never stored by a cache; the rest say private.
        Assert.Contains("no-store", plain.Headers.CacheControl!.ToString());
        var health = await c.GetAsync("/net/health");
        Assert.Equal("private, no-cache", health.Headers.NonValidated["Cache-Control"].ToString());

        // HSTS only on HTTPS (as forwarded by App Service's front end), never on plain HTTP.
        var https = new HttpRequestMessage(HttpMethod.Get, "/net/health");
        https.Headers.Host = "erp.example.com";
        https.Headers.Add("X-Forwarded-Proto", "https");
        var hs = await c.SendAsync(https);
        Assert.Equal("max-age=31536000", string.Join(",", hs.Headers.GetValues("Strict-Transport-Security")));
        Assert.False(health.Headers.Contains("Strict-Transport-Security"));
    }

    [ReferenceFact]
    public async Task Behind_the_proxies_the_app_sees_the_public_host_scheme_and_client()
    {
        string cs = Reference.ConnectionString;
        await using var app = await ProductionHost.StartAsync(cs, ("DATA_PROTECTION_STORE", "none"),
            ("FORWARDED_LIMIT", "2"), ("FORWARDED_ALLOWED_HOSTS", "erp.example.com,accountix-net.azurewebsites.net"));
        var c = ProductionHost.Client(app, OwnerB, CompanyB, cs);
        // As App Service's front end hands it on after a Vercel rewrite: Vercel added
        // the visitor, App Service added Vercel's address.
        var req = new HttpRequestMessage(HttpMethod.Get, "/net/request");
        req.Headers.Host = "accountix-net.azurewebsites.net";
        req.Headers.Add("X-Forwarded-For", "203.0.113.7, 198.51.100.20");
        req.Headers.Add("X-Forwarded-Proto", "https");
        req.Headers.Add("X-Forwarded-Host", "erp.example.com");
        var r = await c.SendAsync(req);
        string json = await r.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("https", root.GetProperty("scheme").GetString());
        Assert.Equal("erp.example.com", root.GetProperty("host").GetString());
        Assert.Equal("203.0.113.7", root.GetProperty("remote_ip").GetString());
        Assert.Contains(TestSignIn.CookieName, root.GetProperty("cookies").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain(Reference.SecretKey, json);

        // A host nobody configured is not believed.
        var spoof = new HttpRequestMessage(HttpMethod.Get, "/net/request");
        spoof.Headers.Add("X-Forwarded-Host", "evil.example.net");
        spoof.Headers.Add("X-Forwarded-Proto", "https");
        using var d2 = System.Text.Json.JsonDocument.Parse(await (await c.SendAsync(spoof)).Content.ReadAsStringAsync());
        Assert.NotEqual("evil.example.net", d2.RootElement.GetProperty("host").GetString());

        Assert.Equal(HttpStatusCode.Redirect, (await new PageClient(app.GetTestServer().CreateHandler()).GetAsync("/net/request")).StatusCode);
    }

    [ReferenceFact]
    public async Task Static_files_revalidate_unless_versioned_and_images_keep_a_day()
    {
        await using var host = await ModuleHost.StartAsync(typeof(AccountixHost).Assembly);
        var c = host.AnonymousClient();
        var css = await c.GetAsync("/static/css/app.css");
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Equal("public, no-cache", css.Headers.NonValidated["Cache-Control"].ToString());
        Assert.NotNull(css.Headers.ETag);
        var versioned = await c.GetAsync("/static/css/app.css?v=3");
        Assert.Equal("public, max-age=31536000, immutable", versioned.Headers.NonValidated["Cache-Control"].ToString());

        string folder = AccountixApp.StaticFolder()!;
        string? image = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(folder, p).Replace('\\', '/'))
            .FirstOrDefault(p => Regex.IsMatch(p, @"\.(png|svg|ico|woff2?)$"));
        if (image is not null)
        {
            var img = await c.GetAsync("/static/" + image);
            Assert.Equal("public, max-age=86400", img.Headers.NonValidated["Cache-Control"].ToString());
        }
    }

    [ReferenceFact]
    public async Task Sentry_is_installed_only_with_a_dsn_and_never_sends_bodies_or_people()
    {
        string cs = new NpgsqlConnectionStringBuilder(Reference.ConnectionString).ConnectionString;
        await using (var off = await ProductionHost.StartAsync(cs, ("DATA_PROTECTION_STORE", "none")))
            Assert.Null(off.Services.GetService<IOptions<Sentry.AspNetCore.SentryAspNetCoreOptions>>()?.Value.Dsn);

        await using var on = await ProductionHost.StartAsync(cs, ("DATA_PROTECTION_STORE", "none"),
            ("SENTRY_DSN", "https://public@o0.ingest.sentry.io/0"));
        var o = on.Services.GetRequiredService<IOptions<Sentry.AspNetCore.SentryAspNetCoreOptions>>().Value;
        Assert.Equal("https://public@o0.ingest.sentry.io/0", o.Dsn);
        Assert.False(o.SendDefaultPii);
        Assert.Equal(Sentry.Extensibility.RequestSize.None, o.MaxRequestBodySize);
    }
}

/// <summary>
/// The whole host: no two actions answer the same URL and method. Two
/// modules claiming one route compile and pass their own tests, then every
/// request to it fails in the combined app (AmbiguousMatchException, a 500).
/// </summary>
public class RouteTableTests
{
    [ReferenceFact]
    public async Task No_two_actions_answer_the_same_url()
    {
        await using var app = await ProductionHost.StartAsync(Reference.ConnectionString, ("DATA_PROTECTION_STORE", "none"));
        var endpoints = app.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>();
        var clashes = endpoints
            .SelectMany(e => (e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IHttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(m => (Key: m + " /" + Shape(e.RoutePattern.RawText).TrimEnd('/') + (TrailingSlashPolicy.Slash(e) == true ? "/" : ""),
                              Name: e.DisplayName ?? "?")))
            .GroupBy(x => x.Key)
            .Where(g => g.Select(x => x.Name).Distinct().Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(" + ", g.Select(x => x.Name).Distinct())}")
            .ToList();
        Assert.True(clashes.Count == 0, "Routes claimed twice:\n" + string.Join("\n", clashes.Take(30)));
    }

    /// <summary>
    /// "vouchers/{id:int}" and "vouchers/{vid:int}" are the same URL shape. The
    /// trailing slash is kept: TrailingSlashPolicy tells "/x" from "/x/" as Flask does.
    /// </summary>
    private static string Shape(string? template) =>
        Regex.Replace(template ?? "", @"\{[^}:]+(:[^}]+)?\}", m => "{" + m.Groups[1].Value + "}").TrimStart('/');

    [Fact]
    public async Task A_trailing_slash_picks_between_two_routes_as_flask_does()
    {
        var b = WebApplication.CreateBuilder();
        b.WebHost.UseTestServer();
        b.Services.AddSingleton<Microsoft.AspNetCore.Routing.MatcherPolicy, TrailingSlashPolicy>();
        await using var app = b.Build();
        app.MapGet("/dashboard", () => "hr");
        app.MapGet("/dashboard/", () => "hub");
        app.MapGet("/only", () => "only");
        await app.StartAsync();
        var c = app.GetTestClient();
        Assert.Equal("hr", await c.GetStringAsync("/dashboard"));
        Assert.Equal("hub", await c.GetStringAsync("/dashboard/"));
        Assert.Equal("only", await c.GetStringAsync("/only/"));      // one route: the slash does not matter
    }

    [Fact]
    public async Task The_same_holds_for_attribute_routes_whose_template_mvc_strips()
    {
        var b = WebApplication.CreateBuilder();
        b.WebHost.UseTestServer();
        b.Services.AddControllers().AddApplicationPart(typeof(SlashHrProbe).Assembly);
        b.Services.AddSingleton<Microsoft.AspNetCore.Routing.MatcherPolicy, TrailingSlashPolicy>();
        await using var app = b.Build();
        app.MapControllers();
        await app.StartAsync();
        var c = app.GetTestClient();
        Assert.Equal("hr", await c.GetStringAsync("/probe-dashboard"));
        Assert.Equal("hub", await c.GetStringAsync("/probe-dashboard/"));
    }
}

/// <summary>Two modules' routes that differ only by the trailing slash (HR's /dashboard, Platform's hub /dashboard/).</summary>
public sealed class SlashHrProbe : Microsoft.AspNetCore.Mvc.ControllerBase
{
    [Microsoft.AspNetCore.Mvc.HttpGet("/probe-dashboard")]
    public string Index() => "hr";
}

public sealed class SlashHubProbe : Microsoft.AspNetCore.Mvc.ControllerBase
{
    [Microsoft.AspNetCore.Mvc.HttpGet("/probe-dashboard/")]
    public string Index() => "hub";
}

