using System.Net;
using Accountix.TestSupport;

namespace Accountix.Platform.Tests;

/// <summary>"/dashboard/" is the hub and "/dashboard" HR's dashboard, as in Python, in one host with both modules.</summary>
public class HubRouteTests
{
    [ReferenceFact]
    public async Task The_trailing_slash_tells_the_hub_from_the_hr_dashboard()
    {
        await using var host = await ModuleHost.StartAsync(typeof(PlatformModule).Assembly, typeof(Accountix.Hr.HrModule).Assembly);
        var c = host.ClientFor(P.OwnerB, P.CompanyB);
        var hub = await c.GetAsync("/dashboard/");
        Assert.Equal(HttpStatusCode.OK, hub.StatusCode);
        string hh = await hub.Content.ReadAsStringAsync();
        Assert.Contains("class=\"hub-grid\"", hh);
        var hr = await c.GetAsync("/dashboard");
        Assert.True((int)hr.StatusCode < 500, $"{(int)hr.StatusCode}");
        Assert.DoesNotContain("class=\"hub-grid\"", await hr.Content.ReadAsStringAsync());
        Assert.Equal("/dashboard/", (await c.GetAsync("/hub")).Headers.Location?.OriginalString);
    }
}
