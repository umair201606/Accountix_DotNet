using System.Security.Claims;
using Accountix.Core.Access;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Accountix.Web.Infrastructure;

public static class HttpAccessExtensions
{
    private static readonly object Key = new();

    /// <summary>This request's access (null when not signed in).</summary>
    public static EffectiveAccess? Access(this HttpContext http) =>
        http.Items.TryGetValue(Key, out var v) ? v as EffectiveAccess : null;

    internal static void SetAccess(this HttpContext http, EffectiveAccess access) => http.Items[Key] = access;

    public static bool WantsJson(this HttpRequest request)
    {
        if (request.HasJsonContentType())
            return true;
        if (request.Headers.XRequestedWith == "XMLHttpRequest")
            return true;
        string accept = request.Headers.Accept.ToString();
        return accept.Contains("application/json", StringComparison.OrdinalIgnoreCase)
               && !accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// After sign-in: works out the person's company and access once, and sets
/// the request's company for every database query that follows. An account
/// switched off since the session began is treated as signed out.
/// </summary>
public sealed class CompanyContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, AccessService access)
    {
        if (http.User.Identity?.IsAuthenticated == true
            && int.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out int userId))
        {
            int? wanted = http.Items.TryGetValue(SessionAuthHandler.SessionCompanyKey, out var c) ? c as int? : null;
            var resolved = await access.ResolveAsync(userId, wanted, http.RequestAborted);
            if (resolved is null)
                http.User = new ClaimsPrincipal(new ClaimsIdentity());
            else
                http.SetAccess(resolved);
        }
        await next(http);
    }
}

/// <summary>Answers for refused requests: JSON for fetch() callers, a page otherwise.</summary>
public static class Refusal
{
    public static IActionResult SignIn() => new ChallengeResult();

    public static IActionResult NoCompany(HttpRequest request) => request.WantsJson()
        ? new ObjectResult(new { ok = false, error = "Open a company first." }) { StatusCode = 409 }
        : new RedirectResult("/portal/");

    public static IActionResult Forbidden(HttpRequest request, string message)
    {
        if (request.WantsJson())
            return new ObjectResult(new { ok = false, error = message }) { StatusCode = 403 };
        return new ViewResult
        {
            ViewName = "AccessDenied",
            StatusCode = 403,
            ViewData = new ViewDataDictionary(new Microsoft.AspNetCore.Mvc.ModelBinding.EmptyModelMetadataProvider(),
                                              new Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary())
            {
                ["Message"] = message,
            },
        };
    }

    /// <summary>
    /// A missing section right (shared/rights_guard.refusal): JSON 403 for
    /// scripts and api_ endpoints, else a flash and a return to the referring
    /// page (never the refusing page itself) or somewhere in the module the
    /// person may open, or the hub.
    /// </summary>
    public static IActionResult Right(HttpContext http, string action, bool json = false)
    {
        var request = http.Request;
        string path = request.Path.Value ?? "/";
        if (json || request.WantsJson() || path.Contains("/api/"))
            return new ObjectResult(new
            {
                ok = false,
                error = $"You don't have '{action}' rights for this section. Ask an administrator to grant access in Settings.",
            }) { StatusCode = 403 };
        var tempData = http.RequestServices.GetRequiredService<ITempDataDictionaryFactory>().GetTempData(http);
        Flashes.Add(tempData, $"No '{action}' rights here. Ask an admin in Settings.", "error");
        // A [Right] refusal short-circuits at the authorization filter, before the
        // filter that saves TempData runs: saved here, or the flash is lost.
        tempData.Save();
        return new RedirectResult(SafeReferrer(request) ?? Landing(http));
    }

    private static string? SafeReferrer(HttpRequest request)
    {
        if (!Uri.TryCreate(request.Headers.Referer.ToString(), UriKind.Absolute, out var uri))
            return null;
        if (!string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase))
            return null;
        if (uri.AbsolutePath == request.Path.Value && !HttpMethods.IsPost(request.Method))
            return null;            // the page refusing itself: no loop
        return SafeUrl.Local(uri.PathAndQuery);
    }

    private static string Landing(HttpContext http)
    {
        string? module = http.Items[ModuleAttribute.ItemKey] as string;
        var nav = http.RequestServices.GetService<NavigationService>();
        string here = http.Request.Path.Value ?? "/";
        var link = nav?.For(module, http).SelectMany(b => b.Items)
            .FirstOrDefault(l => !string.Equals(l.Url.TrimEnd('/'), here.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
        return link?.Url ?? "/hub";
    }
}

/// <summary>Checks for rights that depend on the request (a voucher's type, an action field).</summary>
public static class RightsExtensions
{
    /// <summary>null when the person has the right, else the refusal to return (rights_guard semantics).</summary>
    public static IActionResult? Deny(this ControllerBase c, string section, string action, bool json = false)
    {
        var access = c.HttpContext.Access();
        if (access is null) return Refusal.SignIn();
        return access.Can(section, action) ? null : Refusal.Right(c.HttpContext, action, json);
    }

    /// <summary>shared/permissions.deny_page: flash the refusal (the caller redirects where Python does).</summary>
    public static bool DenyPage(this Controller c, string section, string action)
    {
        if (c.HttpContext.Access()?.Can(section, action) == true) return false;
        c.Flash($"No '{action}' rights here. Ask an admin in Settings.", "error");
        return true;
    }
}

/// <summary>
/// The module this controller belongs to. Refused unless the active company
/// is entitled to it and the person may open it (admin, or their module
/// switch is on). Put on every module controller.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class ModuleAttribute(string module) : Attribute, IAsyncAuthorizationFilter
{
    public string Module { get; } = module;

    public const string ItemKey = "accountix.module";

    public Task OnAuthorizationAsync(AuthorizationFilterContext ctx)
    {
        ctx.HttpContext.Items[ItemKey] = Module;     // the app shell's sidebar and badge
        var access = ctx.HttpContext.Access();
        if (access is null)
            ctx.Result = Refusal.SignIn();
        else if (access.CompanyId is null)
            ctx.Result = Refusal.NoCompany(ctx.HttpContext.Request);
        else if (!access.CanOpenModule(Module))
            ctx.Result = Refusal.Forbidden(ctx.HttpContext.Request,
                access.ModuleEntitled(Module)
                    ? "You don't have access to this module. Ask an administrator in Settings."
                    : "This company does not have this module.");
        return Task.CompletedTask;
    }
}

/// <summary>
/// The section right an action needs (Settings → Rights). Declared next to
/// the action instead of in a central map; a test fails when an action of a
/// module controller declares neither a right nor <see cref="AnyMemberAttribute"/>.
/// Several on one action must all be met.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class RightAttribute(string section, string action) : Attribute, IAsyncAuthorizationFilter
{
    public string Section { get; } = section;
    public string Action { get; } = action;

    // Runs after ModuleAttribute (class-level filters run first at equal order).
    public Task OnAuthorizationAsync(AuthorizationFilterContext ctx)
    {
        if (ctx.Result is not null)
            return Task.CompletedTask;
        var access = ctx.HttpContext.Access();
        if (access is null)
            ctx.Result = Refusal.SignIn();
        else if (access.CompanyId is null)
            ctx.Result = Refusal.NoCompany(ctx.HttpContext.Request);
        else if (!access.Can(Section, Action))
            ctx.Result = Refusal.Right(ctx.HttpContext, Action);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Any ONE of these rights is enough (rights_guard AnyOf), as "section:action"
/// pairs. The refusal names the first pair's action.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class AnyRightAttribute(params string[] pairs) : Attribute, IAsyncAuthorizationFilter
{
    public IReadOnlyList<(string Section, string Action)> Pairs { get; } =
        pairs.Select(p => p.Split(':', 2)).Select(p => (p[0], p[1])).ToList();

    public Task OnAuthorizationAsync(AuthorizationFilterContext ctx)
    {
        if (ctx.Result is not null)
            return Task.CompletedTask;
        var access = ctx.HttpContext.Access();
        if (access is null)
            ctx.Result = Refusal.SignIn();
        else if (access.CompanyId is null)
            ctx.Result = Refusal.NoCompany(ctx.HttpContext.Request);
        else if (!Pairs.Any(p => access.Can(p.Section, p.Action)))
            ctx.Result = Refusal.Right(ctx.HttpContext, Pairs[0].Action);
        return Task.CompletedTask;
    }
}

/// <summary>Open to anyone who may open the module (no section right needed). Explicit, so it is reviewed.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AnyMemberAttribute : Attribute;

/// <summary>Company admins (and super admins) only.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class CompanyAdminAttribute : Attribute, IAsyncAuthorizationFilter
{
    public Task OnAuthorizationAsync(AuthorizationFilterContext ctx)
    {
        var access = ctx.HttpContext.Access();
        if (access is null)
            ctx.Result = Refusal.SignIn();
        else if (access.CompanyId is null)
            ctx.Result = Refusal.NoCompany(ctx.HttpContext.Request);
        else if (!access.IsAdmin)
            ctx.Result = Refusal.Forbidden(ctx.HttpContext.Request, "Only a company administrator can do this.");
        return Task.CompletedTask;
    }
}

/// <summary>Headers every answer carries (same as shared/csrf_guard.py).</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext http)
    {
        http.Response.OnStarting(() =>
        {
            var h = http.Response.Headers;
            h.TryAdd("X-Frame-Options", "SAMEORIGIN");
            h.TryAdd("X-Content-Type-Options", "nosniff");
            h.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");
            h.TryAdd("Content-Security-Policy", "frame-ancestors 'self'");
            return Task.CompletedTask;
        });
        return next(http);
    }
}

/// <summary>
/// Which module's sidebar and badge the app shell shows, for pages that are
/// not behind a module gate (Settings, the hub). [Module] sets it too.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class ShellAttribute(string module) : Attribute, IAsyncAuthorizationFilter
{
    public string Module { get; } = module;

    public Task OnAuthorizationAsync(AuthorizationFilterContext ctx)
    {
        ctx.HttpContext.Items.TryAdd(ModuleAttribute.ItemKey, Module);
        return Task.CompletedTask;
    }
}
