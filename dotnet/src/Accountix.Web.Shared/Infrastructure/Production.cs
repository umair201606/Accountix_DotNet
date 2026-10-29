using System.Diagnostics;
using System.IO.Compression;
using System.Security.Claims;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

namespace Accountix.Web.Infrastructure;

/// <summary>
/// What the deployed app adds on top of <see cref="AccountixApp"/>
/// (docs/DEPLOY.md). Registered by the host (Program.cs) only, so module
/// test hosts are untouched. Every setting is an App Setting (environment
/// variable), the way the Python app is configured on Vercel.
/// </summary>
public sealed class ProductionOptions
{
    /// <summary>RESPONSE_COMPRESSION: Brotli/gzip for HTML, JSON, CSS, JS (default on); RESPONSE_COMPRESSION_LEVEL optimal|fastest.</summary>
    public bool Compression { get; init; } = true;

    /// <summary>HSTS_MAX_AGE_DAYS: Strict-Transport-Security on HTTPS answers; 0 switches it off.</summary>
    public int HstsMaxAgeDays { get; init; } = 365;

    /// <summary>REQUEST_LOG: one structured line per request (method, path, status, ms, company, user id).</summary>
    public bool RequestLog { get; init; } = true;

    public static ProductionOptions From(IConfiguration config) => new()
    {
        Compression = config.GetValue("RESPONSE_COMPRESSION", true),
        HstsMaxAgeDays = config.GetValue("HSTS_MAX_AGE_DAYS", 365),
        RequestLog = config.GetValue("REQUEST_LOG", true),
    };
}

public static class ProductionSetup
{
    /// <summary>Services for a deployed instance. Call after <see cref="AccountixApp.ConfigureServices"/>.</summary>
    public static void AddProduction(WebApplicationBuilder builder)
    {
        var config = builder.Configuration;
        bool production = !builder.Environment.IsDevelopment();
        var options = ProductionOptions.From(config);
        var s = builder.Services;
        s.AddSingleton(options);

        // Logs: one JSON object per line on stdout (App Service log stream,
        // Log Analytics). LOG_FORMAT=simple for people reading a console.
        string format = config["LOG_FORMAT"] ?? (production ? "json" : "simple");
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddJsonConsole(o =>
            {
                o.IncludeScopes = false;
                o.UseUtcTimestamp = true;
                o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
            });
        }

        // Shared key ring: antiforgery tokens and flash cookies work whichever
        // instance answers.
        var settings = AppSettings.From(config);
        DataProtectionStore.Add(s, config, DatabaseTuning.Apply(settings.ConnectionString, config, production), production);

        if (options.Compression)
        {
            s.AddResponseCompression(o =>
            {
                // Pages carry antiforgery tokens that change on every render and
                // the session cookie is never in a body, so compressing over
                // HTTPS does not expose a secret to BREACH-style guessing.
                o.EnableForHttps = true;
                o.Providers.Add<BrotliCompressionProvider>();
                o.Providers.Add<GzipCompressionProvider>();
                o.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["image/svg+xml", "text/csv"]);
            });
            // RESPONSE_COMPRESSION_LEVEL: "optimal" (default) or "fastest". Measured on
            // the 422 KB month voucher register and the 455 KB sales-order list
            // (docs/DEPLOY.md): Brotli optimal sends 33 / 15 KB for 3-5 ms of CPU;
            // fastest saves those milliseconds but, flushed in chunks by the
            // middleware, sent 50 / 35 KB, sometimes more than gzip.
            var level = string.Equals(config["RESPONSE_COMPRESSION_LEVEL"], "fastest", StringComparison.OrdinalIgnoreCase)
                ? CompressionLevel.Fastest : CompressionLevel.Optimal;
            s.Configure<BrotliCompressionProviderOptions>(o => o.Level = level);
            s.Configure<GzipCompressionProviderOptions>(o => o.Level = level);
        }

        if (options.HstsMaxAgeDays > 0)
            s.AddHsts(o =>
            {
                o.MaxAge = TimeSpan.FromDays(options.HstsMaxAgeDays);
                o.IncludeSubDomains = config.GetValue("HSTS_INCLUDE_SUBDOMAINS", false);
                o.Preload = false;
            });

        // Behind App Service's front end (and Vercel's rewrite or Front Door
        // in front of it): FORWARDED_LIMIT proxies are trusted to append
        // X-Forwarded-For; FORWARDED_ALLOWED_HOSTS limits which X-Forwarded-Host
        // values are believed (the public domain, the azurewebsites name).
        s.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardLimit = config.GetValue("FORWARDED_LIMIT", 1);
            if (config["FORWARDED_ALLOWED_HOSTS"] is { Length: > 0 } hosts)
                o.AllowedHosts = hosts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        });
    }

    /// <summary>The front of the pipeline, right after forwarded headers. No-op unless <see cref="AddProduction"/> ran.</summary>
    internal static void UseEdge(WebApplication app)
    {
        var options = app.Services.GetService<ProductionOptions>();
        if (options is null)
            return;
        if (options.RequestLog)
            app.UseMiddleware<RequestLogMiddleware>();
        if (options.HstsMaxAgeDays > 0 && !app.Environment.IsDevelopment())
            app.UseHsts();
        if (options.Compression)
            app.UseResponseCompression();
        app.UseMiddleware<DynamicCacheHeadersMiddleware>();
    }

    /// <summary>
    /// Cache headers for /static and module assets. The Python app's file
    /// names carry no version, so a file is revalidated on every use
    /// (ETag / Last-Modified, a 304 when unchanged) and a deployment is seen at
    /// once; a URL with ?v= can be cached for a year; fonts and images for a day.
    /// </summary>
    public static void StaticCacheHeaders(StaticFileResponseContext ctx)
    {
        var h = ctx.Context.Response.Headers;
        if (ctx.Context.Request.Query.ContainsKey("v"))
            h.CacheControl = "public, max-age=31536000, immutable";
        else if (LongLived.Contains(Path.GetExtension(ctx.File.Name)))
            h.CacheControl = "public, max-age=86400";
        else
            h.CacheControl = "public, no-cache";
    }

    private static readonly HashSet<string> LongLived = new(StringComparer.OrdinalIgnoreCase)
    {
        ".woff", ".woff2", ".ttf", ".otf", ".eot", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".ico",
    };
}

/// <summary>
/// Pages and JSON answers that name no caching are marked private: a CDN in
/// front (Vercel's rewrite, Front Door) must never keep one person's ledger
/// for the next. Static files set their own headers first.
/// </summary>
internal sealed class DynamicCacheHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext http)
    {
        http.Response.OnStarting(() =>
        {
            if (!http.Response.Headers.ContainsKey(HeaderNames.CacheControl))
                http.Response.Headers.CacheControl = "private, no-cache";
            return Task.CompletedTask;
        });
        return next(http);
    }
}

/// <summary>
/// One structured log line per request: method, path (no query string: it
/// can hold names and search terms), status, milliseconds, company and user
/// id (numbers, no names or emails). Static files and the health check log
/// at Debug.
/// </summary>
internal sealed class RequestLogMiddleware(RequestDelegate next, ILogger<RequestLogMiddleware> log)
{
    public async Task InvokeAsync(HttpContext http)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            await next(http);
        }
        finally
        {
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            string path = http.Request.Path.Value ?? "/";
            bool quiet = path.StartsWith("/static/", StringComparison.Ordinal)
                         || path.StartsWith("/_content/", StringComparison.Ordinal)
                         || path == "/net/health";
            var level = http.Response.StatusCode >= 500 ? LogLevel.Error : quiet ? LogLevel.Debug : LogLevel.Information;
            if (log.IsEnabled(level))
                log.Log(level, "HTTP {Method} {Path} {Status} {ElapsedMs:F0} ms company={CompanyId} user={UserId}",
                    http.Request.Method, path, http.Response.StatusCode, ms,
                    http.Access()?.CompanyId, http.User.FindFirstValue(ClaimTypes.NameIdentifier));
        }
    }
}
