using System.Reflection;
using Accountix.Core.Access;
using Accountix.Core.Data;
using Accountix.Core.Shared;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;

namespace Accountix.Web.Infrastructure;

/// <summary>
/// The app's set-up, shared by the real host (src/Accountix.Web) and by each
/// module's tests, which boot only Core + Web.Shared + the modules they name.
/// </summary>
public static class AccountixApp
{
    public static void ConfigureServices(WebApplicationBuilder builder, IReadOnlyList<Assembly> moduleAssemblies)
    {
        var settings = AppSettings.From(builder.Configuration);
        var s = builder.Services;
        s.AddSingleton(settings);
        s.AddSingleton(TimeProvider.System);
        // Pages keep non-ASCII text (—, Urdu names, Rs. signs) as UTF-8 like Jinja, instead of &#x…;
        // entities: smaller pages, and the same text bytes as the Python app. <, >, &, quotes stay encoded.
        s.AddWebEncoders(o => o.TextEncoderSettings = new System.Text.Encodings.Web.TextEncoderSettings(System.Text.Unicode.UnicodeRanges.All));

        // One CompanyContext per request; the DbContext reads it on every query.
        s.AddScoped<CompanyContext>();
        int lockTimeoutMs = builder.Configuration.GetValue("DB_LOCK_TIMEOUT_MS", 15000);
        s.AddSingleton<IInterceptor>(new LockTimeoutInterceptor(lockTimeoutMs));
        // Pool size, timeouts and keepalive for Neon (DatabaseTuning); unchanged in Development.
        string connectionString = DatabaseTuning.Apply(settings.ConnectionString, builder.Configuration,
                                                       production: !builder.Environment.IsDevelopment());
        s.AddDbContext<AccountixDbContext>((sp, o) => o
            .UseNpgsql(connectionString)
            .AddInterceptors(sp.GetServices<IInterceptor>()));
        s.AddScoped<AccessService>();
        s.AddScoped<ServerSessions>();
        s.AddScoped<CompanyClock>();

        // Stand-ins first, so a module that owns a shared service replaces its stub.
        NotYetPortedServices.AddStubs(s);
        var modules = moduleAssemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(IAccountixModule).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .Select(t => (IAccountixModule)Activator.CreateInstance(t)!)
            .ToList();
        foreach (var m in modules)
        {
            s.AddSingleton(m);
            m.Register(s, builder.Configuration);
        }
        s.AddSingleton<NavigationService>();
        // Flask's strict slashes: "/dashboard" and "/dashboard/" are two pages.
        s.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.AspNetCore.Routing.MatcherPolicy, TrailingSlashPolicy>());

        // Sign-in: the ax_session cookie over user_sessions (SessionAuthHandler, SessionCookieStore).
        s.AddScoped<ISessionCookie, SessionCookieStore>();
        s.AddAuthentication(SessionAuthHandler.SchemeName)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, SessionAuthHandler>(
                SessionAuthHandler.SchemeName, _ => { });
        s.AddAuthorization();

        // Every POST/PUT/DELETE needs the antiforgery token the page was rendered
        // with: forms get it from the form tag helper, fetch() from the app shell.
        s.AddAntiforgery(o =>
        {
            o.Cookie.Name = "ax_af";
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.HeaderName = "X-CSRF-Token";
        });
        var mvc = s.AddControllersWithViews(o => o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
        mvc.AddApplicationPart(typeof(AccountixApp).Assembly);
        foreach (var a in moduleAssemblies)
            mvc.AddApplicationPart(a);

        s.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
        });
    }

    public static void ConfigurePipeline(WebApplication app)
    {
        app.UseForwardedHeaders();
        // Deployed hosts only (ProductionSetup.AddProduction): request log, HSTS, compression.
        ProductionSetup.UseEdge(app);
        app.UseMiddleware<SecurityHeadersMiddleware>();
        // /static: the CSS/JS every page links, from src/Accountix.Web.Shared/static, which the
        // build copies next to the binaries of the host, every test host and the publish output.
        if (StaticFolder(app.Environment.ContentRootPath) is { } staticDir)
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(staticDir),
                RequestPath = "/static",
                OnPrepareResponse = ProductionSetup.StaticCacheHeaders,
            });
        app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = ProductionSetup.StaticCacheHeaders });
        app.UseRouting();
        app.UseAuthentication();
        app.UseMiddleware<CompanyContextMiddleware>();
        app.UseMiddleware<ErrorAnswersMiddleware>();
        app.UseAuthorization();
        app.MapControllers();
    }

    /// <summary>
    /// The folder served at /static: static/ next to the app's binaries (build or publish
    /// output: Accountix.Web.Shared.csproj copies it there), else under <paramref name="contentRoot"/>.
    /// </summary>
    public static string? StaticFolder(string? contentRoot = null)
    {
        foreach (string? root in new[] { AppContext.BaseDirectory, contentRoot })
            if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, "static", "css", "app.css")))
                return Path.Combine(root, "static");
        return null;
    }
}
