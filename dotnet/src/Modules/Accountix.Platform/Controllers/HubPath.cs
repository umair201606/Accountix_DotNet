using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;

namespace Accountix.Platform.Controllers;

/// <summary>
/// Python serves two URLs that ASP.NET routing treats as one: "/dashboard/"
/// (the hub, dashboard.hub) and "/dashboard" (HR's dashboard). Before routing,
/// a request for exactly "/dashboard/" is rewritten to the hub's internal
/// route; the original path is kept in Items[OriginalPathKey].
/// </summary>
public sealed class HubPath : IStartupFilter
{
    public const string OriginalPathKey = "accountix.original_path";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((http, nextMw) =>
        {
            if (string.Equals(http.Request.Path.Value, "/dashboard/", StringComparison.Ordinal))
            {
                http.Items[OriginalPathKey] = http.Request.Path.Value;
                http.Request.Path = HubController.HubRoute;
            }
            return nextMw(http);
        });
        next(app);
    };
}
