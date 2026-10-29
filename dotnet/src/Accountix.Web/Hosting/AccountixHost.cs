using System.Reflection;
using Accountix.Web.Infrastructure;

namespace Accountix.Web.Hosting;

/// <summary>
/// The deployed app, built the same way by Program.cs and by the tests that
/// check production behaviour (shared key ring, compression, HSTS...).
/// </summary>
public static class AccountixHost
{
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        // Every Accountix.* assembly deployed next to the host is a module (or the
        // shared core). Found on disk, not through compile-time references: the
        // compiler drops a project reference the host's own code never uses, and a
        // module missed here would run on its stand-in services.
        var moduleAssemblies = Directory.GetFiles(AppContext.BaseDirectory, "Accountix.*.dll")
            .Where(p => !Path.GetFileName(p).Contains(".Tests", StringComparison.Ordinal)
                        && !Path.GetFileName(p).Equals("Accountix.TestSupport.dll", StringComparison.Ordinal)
                        && !Path.GetFileName(p).Equals("Accountix.Load.dll", StringComparison.Ordinal))
            .Select(p => Assembly.Load(AssemblyName.GetAssemblyName(p)))
            .Append(typeof(AccountixHost).Assembly)
            .Distinct()
            .ToList();

        AddSentry(builder);
        AccountixApp.ConfigureServices(builder, moduleAssemblies);
        ProductionSetup.AddProduction(builder);

        var app = builder.Build();
        if (!app.Environment.IsDevelopment())
            app.UseExceptionHandler("/net/error");
        AccountixApp.ConfigurePipeline(app);
        return app;
    }

    /// <summary>
    /// Error reports to Sentry when SENTRY_DSN is set, as the Python app does:
    /// no request bodies, no cookies, no user details (they hold salaries,
    /// bank details and customer data). Unset, nothing is installed.
    /// </summary>
    public static void AddSentry(WebApplicationBuilder builder)
    {
        string dsn = (builder.Configuration["SENTRY_DSN"] ?? "").Trim();
        if (dsn.Length == 0)
            return;
        builder.WebHost.UseSentry(o =>
        {
            o.Dsn = dsn;
            o.SendDefaultPii = false;
            o.MaxRequestBodySize = Sentry.Extensibility.RequestSize.None;
            o.Environment = builder.Configuration["SENTRY_ENVIRONMENT"] ?? builder.Environment.EnvironmentName.ToLowerInvariant();
            o.Release = builder.Configuration["SENTRY_RELEASE"] is { Length: > 0 } r ? r : null;
            o.TracesSampleRate = builder.Configuration.GetValue("SENTRY_TRACES_SAMPLE_RATE", 0.0);
            o.MinimumBreadcrumbLevel = LogLevel.Warning;
            o.SetBeforeSend(e =>
            {
                e.User = new Sentry.SentryUser();
                e.Request.Cookies = null;
                e.Request.Data = null;
                e.Request.QueryString = null;
                return e;
            });
        });
    }
}
