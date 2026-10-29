using Microsoft.AspNetCore.Mvc.Filters;

namespace Accountix.SuperAdmin.Infrastructure;

/// <summary>
/// Flask-Login's @login_required with the console's own login view, then
/// _require_super_admin():
/// <list type="bullet">
/// <item>signed out: "Please log in to access this page." (info) and a redirect
/// to /superadmin/login?next=&lt;this page&gt; (blueprint_login_views["superadmin"]);
/// the customer sign-in refuses super admins, so sending them there was a dead end;</item>
/// <item>signed in without the super admin flag: 403 (the app's friendly error page).</item>
/// </list>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class SuperAdminOnlyAttribute : Attribute, IAsyncAuthorizationFilter
{
    public const string LoginMessage = "Please log in to access this page.";

    public Task OnAuthorizationAsync(AuthorizationFilterContext ctx)
    {
        var http = ctx.HttpContext;
        var access = http.Access();
        if (access is null)
        {
            var tempData = http.RequestServices
                .GetRequiredService<Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataDictionaryFactory>().GetTempData(http);
            Flashes.Add(tempData, LoginMessage, "info");
            tempData.Save();        // a short-circuiting filter skips the filter that would save it
            string here = http.Request.PathBase + http.Request.Path + http.Request.QueryString;
            ctx.Result = new RedirectResult("/superadmin/login?next=" + Py.QuotePlus(here));
        }
        else if (!access.IsSuperAdmin)
            ctx.Result = ConsolePages.Error(403);
        return Task.CompletedTask;
    }
}

/// <summary>The app's friendly pages for abort(403) / abort(404) (app.py handle_http).</summary>
public static class ConsolePages
{
    public static IActionResult Error(int code) => code switch
    {
        403 => Page(403, "Forbidden", "You don't have permission to view this page."),
        404 => Page(404, "Not Found", "The page or record you're looking for doesn't exist."),
        _ => Page(code, "Error", "Something went wrong."),
    };

    private static ContentResult Page(int code, string title, string message) => new()
    {
        StatusCode = code,
        ContentType = "text/html; charset=utf-8",
        Content = ErrorAnswersMiddleware.ErrorPage(code, title, message),
    };
}
