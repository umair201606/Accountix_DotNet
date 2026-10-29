using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Accountix.Core.Shared;
using Accountix.TestSupport;
using Accountix.Web.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Accountix.Executive.Tests;

/// <summary>
/// The real export pipeline (Finance's ReportExport) without Finance's controllers: the Finance
/// project still carries an early copy of the executive pages, whose routes would clash with these.
/// </summary>
public sealed class ExportPipelineForTests : IAccountixModule
{
    public string Key => "executive-tests";

    public void Register(IServiceCollection services, IConfiguration config)
    {
#if WITH_FINANCE
        services.AddHttpContextAccessor();
        services.AddScoped<Accountix.Finance.Services.ReportExport>();
        services.AddScoped<IReportExport>(sp => sp.GetRequiredService<Accountix.Finance.Services.ReportExport>());
#endif
    }

    public ModuleNav? Navigation => null;
}

/// <summary>
/// The company clock of every test host of this project, stopped at <see cref="Exec.Now"/> (29 Oct 2026,
/// 12:00 in Lahore): pages without an as-of date age to that day, as the golden capture did. Sign-in
/// and sessions keep the real clock (they read TimeProvider, not CompanyClock).
/// </summary>
public sealed class FixedClockForTests : IAccountixModule
{
    public string Key => "executive-tests-clock";

    public void Register(IServiceCollection services, IConfiguration config) =>
        services.AddScoped(sp => new CompanyClock(sp.GetRequiredService<Accountix.Core.Data.AccountixDbContext>(),
                                                  sp.GetRequiredService<Accountix.Core.Data.CompanyContext>(), new FixedTime(Exec.Now)));

    public ModuleNav? Navigation => null;
}

internal static class Hosts
{
    public static Task<ModuleHost> ExecutiveAsync() =>
        ModuleHost.StartAsync(typeof(ExecutiveModule).Assembly, typeof(ExportPipelineForTests).Assembly);

    public static Task<ModuleHost> WithAccountingAsync() =>
        ModuleHost.StartAsync(typeof(ExecutiveModule).Assembly, typeof(ExportPipelineForTests).Assembly,
                              typeof(Accountix.Accounting.AccountingModule).Assembly);

    public static async Task<string> HtmlAsync(this HttpClient c, string url)
    {
        var r = await c.GetAsync(url);
        string body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url}: {(int)r.StatusCode} {r.Headers.Location}\n{body[..Math.Min(1500, body.Length)]}");
        return body;
    }

    private static string Clean(string html) =>
        Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")), @"\s+", " ").Trim();

    /// <summary>The text of every element carrying this class (elements without a same-tag child).</summary>
    public static List<string> Texts(string html, string cssClass) =>
        Regex.Matches(html, $"<(\\w+)\\b[^>]*\\bclass=\"(?:[^\"]*\\s)?{Regex.Escape(cssClass)}(?:\\s[^\"]*)?\"[^>]*>(.*?)</\\1>", RegexOptions.Singleline)
            .Select(m => Clean(m.Groups[2].Value)).ToList();

    /// <summary>Every value of one attribute, in page order (SVG points, bar widths...).</summary>
    public static List<string> Attrs(string html, string attribute) =>
        Regex.Matches(html, $"\\s{Regex.Escape(attribute)}=\"([^\"]*)\"").Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();

    /// <summary>The content of the page's main area (between the shells), for whole-text comparisons.</summary>
    public static string MainText(string html, string marker = "<div class=\"exr")
    {
        int i = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(i >= 0, "page has no executive content");
        return Clean(html[i..]);
    }
}
