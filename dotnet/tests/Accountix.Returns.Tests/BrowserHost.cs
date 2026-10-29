using Accountix.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Accountix.Returns.Tests;

/// <summary>Runs only with RETURNS_SERVE=&lt;seconds&gt;: the module on a real port for a browser.</summary>
public sealed class ServeFactAttribute : FactAttribute
{
    public ServeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RETURNS_SERVE") is not { Length: > 0 })
            Skip = "Set RETURNS_SERVE=<seconds> to serve the return pages on http://localhost:5310 for a browser";
    }
}

public class BrowserHost
{
    /// <summary>
    /// Serves Returns + Accounting (the shell, the GL panel API) on port 5310 with the reference
    /// database and writes a signed-in session cookie for owner_b to $RETURNS_COOKIE (the browser
    /// sets it as "ax_session"). For the 320px checks; not part of the normal run.
    /// </summary>
    [ServeFact]
    public async Task Serve_for_the_browser()
    {
        int seconds = int.Parse(Environment.GetEnvironmentVariable("RETURNS_SERVE")!);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ApplicationName = typeof(ReturnsModule).Assembly.GetName().Name,
        });
        builder.WebHost.UseUrls("http://localhost:5310");
        builder.Configuration["SECRET_KEY"] = Reference.SecretKey;
        builder.Configuration["ConnectionStrings:Accountix"] = Reference.ConnectionString;
        Accountix.Web.Infrastructure.AccountixApp.ConfigureServices(builder, SalesReturnTests.Modules);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = builder.Build();
        Accountix.Web.Infrastructure.AccountixApp.ConfigurePipeline(app);
        await app.StartAsync();
        string cookie = TestSignIn.CookieFor(World.Owner, World.CompanyB, Reference.ConnectionString);
        if (Environment.GetEnvironmentVariable("RETURNS_COOKIE") is { Length: > 0 } file)
            await File.WriteAllTextAsync(file, cookie);
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        await app.StopAsync();
    }
}
