using Accountix.TestSupport;
using Accountix.Web.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Accountix.HrRecords.Tests;

/// <summary>
/// This project's app on Kestrel (a free local port) for a real browser, and
/// Playwright's Chromium for .NET (the revision Microsoft.Playwright 1.64.1
/// expects, chromium-1248 in ~/Library/Caches/ms-playwright, as
/// tests/Accountix.E2E uses; the installed Google Chrome otherwise).
/// </summary>
public sealed class LiveApp : IAsyncDisposable
{
    private readonly WebApplication _app;
    private LiveApp(WebApplication app, string baseUrl) { _app = app; BaseUrl = baseUrl; }

    public string BaseUrl { get; }

    public static async Task<LiveApp> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development", ApplicationName = typeof(HrRecordsModule).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Configuration["SECRET_KEY"] = Reference.SecretKey;
        builder.Configuration["ConnectionStrings:Accountix"] = Reference.ConnectionString;
        AccountixApp.ConfigureServices(builder, [typeof(HrRecordsModule).Assembly]);
        builder.Logging.ClearProviders();
        var app = builder.Build();
        // /static: the shared CSS/JS, which the build copies next to the test binaries.
        AccountixApp.ConfigurePipeline(app);
        await app.StartAsync();
        string url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new LiveApp(app, url.Replace("[::1]", "127.0.0.1").TrimEnd('/'));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    /// <summary>A new browser context at this width, signed in with this session cookie.</summary>
    public async Task<IBrowserContext> ContextAsync(IBrowser browser, string sessionCookie, int width, int height = 800)
    {
        var ctx = await browser.NewContextAsync(new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = width, Height = height } });
        await ctx.AddCookiesAsync([new Cookie { Name = TestSignIn.CookieName, Value = sessionCookie, Url = BaseUrl }]);
        return ctx;
    }

    /// <summary>Chromium, headless (E2E_HEADED=1 to watch). Dispose the returned Playwright after the browser.</summary>
    public static async Task<(IPlaywright Playwright, IBrowser Browser)> BrowserAsync()
    {
        var pw = await Playwright.CreateAsync();
        var errors = new List<string>();
        foreach (string? channel in new[] { null, "chrome" })
        {
            try
            {
                var b = await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = Environment.GetEnvironmentVariable("E2E_HEADED") != "1", Channel = channel, Timeout = 60_000,
                });
                return (pw, b);
            }
            catch (PlaywrightException e)
            {
                errors.Add($"{channel ?? "bundled chromium"}: {e.Message.Split('\n')[0]}");
            }
        }
        pw.Dispose();
        throw new Xunit.Sdk.XunitException("No browser for Playwright (install chromium for Microsoft.Playwright 1.64.1, see docs/E2E.md): "
                                           + string.Join(" | ", errors));
    }
}
