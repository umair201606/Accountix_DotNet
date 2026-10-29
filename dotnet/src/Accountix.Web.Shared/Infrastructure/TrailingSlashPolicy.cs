using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;

namespace Accountix.Web.Infrastructure;

/// <summary>
/// Flask treats "/dashboard" and "/dashboard/" as two URLs (the HR dashboard
/// and the hub); ASP.NET routing ignores a trailing slash, so with both
/// modules in one host every request to either was ambiguous (a 500). Where
/// two routes differ only by the trailing slash, the request's own slash
/// picks the route written the same way. Nowhere else does this change
/// anything: a URL with only one route keeps matching with or without it.
/// </summary>
public sealed class TrailingSlashPolicy : MatcherPolicy, IEndpointSelectorPolicy
{
    // After the HTTP-method policy and the rest, just before the final choice.
    public override int Order => 10_000;

    public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints)
    {
        var seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in endpoints.OfType<RouteEndpoint>())
        {
            if (Slash(e) is not bool slash) continue;
            string key = Normal(e.RoutePattern.RawText).TrimEnd('/');
            if (seen.TryGetValue(key, out bool other) && other != slash)
                return true;
            seen.TryAdd(key, slash);
        }
        return false;
    }

    public Task ApplyAsync(HttpContext httpContext, CandidateSet candidates)
    {
        string path = httpContext.Request.Path.Value ?? "/";
        if (path.Length <= 1)
            return Task.CompletedTask;
        bool wantSlash = path.EndsWith('/');
        bool anyExact = false;
        for (int i = 0; i < candidates.Count; i++)
            if (candidates.IsValidCandidate(i) && Slash(candidates[i].Endpoint) == wantSlash)
                anyExact = true;
        if (!anyExact)
            return Task.CompletedTask;
        for (int i = 0; i < candidates.Count; i++)
            if (candidates.IsValidCandidate(i) && Slash(candidates[i].Endpoint) is bool s && s != wantSlash)
                candidates.SetValidity(i, false);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Whether the route was written with a trailing slash. MVC drops it when it
    /// builds the template ("/dashboard/" becomes "dashboard"), so for an
    /// attribute route the slash is read from the [HttpGet("/...")] that made
    /// it (absolute templates, the form both apps' clashing routes use).
    /// Unknown (null) leaves the endpoint alone.
    /// </summary>
    public static bool? Slash(Endpoint e)
    {
        if (e is not RouteEndpoint r)
            return null;
        string raw = Normal(r.RoutePattern.RawText);
        string bare = raw.TrimEnd('/');
        bool? found = null;
        foreach (var p in e.Metadata.OfType<Microsoft.AspNetCore.Mvc.Routing.IRouteTemplateProvider>())
        {
            if (p.Template is not { Length: > 1 } t || !(t.StartsWith('/') || t.StartsWith("~/")))
                continue;
            if (string.Equals(Normal(t.TrimStart('~')).TrimEnd('/'), bare, StringComparison.OrdinalIgnoreCase))
                found = found is null || found == t.EndsWith('/') ? t.EndsWith('/') : null;
        }
        if (found is not null || e.Metadata.GetMetadata<Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor>() is not null)
            return found;
        return raw.Length > 1 ? raw.EndsWith('/') : null;     // minimal APIs keep the slash in the template
    }

    private static string Normal(string? raw) => "/" + (raw ?? "").TrimStart('/');
}
