using Accountix.HrRecords.Services;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Accountix.HrRecords.Controllers;

/// <summary>
/// The notification bell's API (hr_app/routes/auth.py api_notifications,
/// mark_notification_read, mark_all_read). Every module's chrome calls it,
/// so it is open to anyone signed in whatever modules their company has
/// (module_guard.ALWAYS_OPEN): no [Module] here. One query each.
/// </summary>
[Area("HrRecords")]
[Route("auth/api/notifications")]
[BellSignedIn]
public sealed class NotificationsApiController(AccountixDbContext db, CompanyClock clock) : RecordsController
{
    private sealed record Item(int Id, string Title, string Message, string? Type, string? Module, DateTime? CreatedAt);

    /// <summary>The person's 20 newest unread notifications in this company.</summary>
    [HttpGet("")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var rows = await db.Set<NotificationRecipient>().AsNoTracking()
            .Where(r => r.UserId == Me && r.IsRead == false)
            .OrderByDescending(r => r.Id).Take(20)
            .Select(r => new Item(r.Id, r.Notification.Title, r.Notification.Message, r.Notification.NotificationType,
                                  r.Notification.Module, r.Notification.CreatedAt))
            .ToListAsync(ct);
        return PyJson(rows.Select(r => new Dictionary<string, object?>
        {
            ["id"] = r.Id,
            ["title"] = r.Title,
            ["message"] = r.Message,
            ["type"] = r.Type,
            ["module"] = r.Module,
            ["created_at"] = r.CreatedAt is DateTime at ? Py.Iso(at) : null,
        }).ToList());
    }

    /// <summary>Mark one of the person's own notifications read (someone else's id changes nothing).</summary>
    [HttpPost("{nid:int}/read")]
    [IgnoreAntiforgeryToken]   // csrf.exempt in Python; the origin guard still applies
    public async Task<IActionResult> MarkRead(int nid, CancellationToken ct)
    {
        if (OriginGuard.Refusal(Request) is { } refused) return refused;
        var now = clock.UtcNow;
        await db.Set<NotificationRecipient>()
            .Where(r => r.Id == nid && r.UserId == Me)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsRead, true).SetProperty(r => r.ReadAt, now), ct);
        return PySuccess();
    }

    [HttpPost("read-all")]
    [IgnoreAntiforgeryToken]   // csrf.exempt in Python; the origin guard still applies
    public async Task<IActionResult> MarkAllRead(CancellationToken ct)
    {
        if (OriginGuard.Refusal(Request) is { } refused) return refused;
        var now = clock.UtcNow;
        await db.Set<NotificationRecipient>()
            .Where(r => r.UserId == Me && r.IsRead == false)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsRead, true).SetProperty(r => r.ReadAt, now), ct);
        return PySuccess();
    }
}

/// <summary>@login_required alone: signed in, with a company open (the rows are the company's).</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class BellSignedInAttribute : Attribute, IAsyncAuthorizationFilter
{
    public Task OnAuthorizationAsync(AuthorizationFilterContext ctx)
    {
        var access = ctx.HttpContext.Access();
        if (access is null)
            ctx.Result = Refusal.SignIn();
        else if (access.CompanyId is null)
            ctx.Result = Refusal.NoCompany(ctx.HttpContext.Request);
        return Task.CompletedTask;
    }
}

/// <summary>
/// shared/csrf_guard.py for the two token-exempt writes: a write whose
/// Origin (or, without one, Referer) names another site is refused; one with
/// neither header passes, as in Python.
/// </summary>
public static class OriginGuard
{
    public const string Message = "This request came from another website, so it was refused. Open the page in this app and try again.";

    public static IActionResult? Refusal(HttpRequest request)
    {
        if (!Foreign(request)) return null;
        if (request.WantsJson())
            return new PyJsonResult(new Dictionary<string, object?> { ["ok"] = false, ["error"] = Message }, 403);
        return new ContentResult { StatusCode = 403, Content = Message, ContentType = "text/plain; charset=utf-8" };
    }

    public static bool Foreign(HttpRequest request)
    {
        string? origin = request.Headers.Origin.FirstOrDefault();
        if (origin is not null)
        {
            origin = origin.Trim();
            if (origin.Length == 0 || origin.Equals("null", StringComparison.OrdinalIgnoreCase)) return true;
            return !SameSite(request, origin, hostless: false);
        }
        string? referer = request.Headers.Referer.FirstOrDefault();
        return !string.IsNullOrEmpty(referer) && !SameSite(request, referer, hostless: true);
    }

    private static bool SameSite(HttpRequest request, string url, bool hostless)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Authority))
            return hostless;
        string key = HostKey(u.Authority, u.Scheme);
        var own = new HashSet<string>(StringComparer.Ordinal) { HostKey(request.Host.Value ?? "", request.Scheme) };
        foreach (string part in request.Headers["X-Forwarded-Host"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
            own.Add(HostKey(part, request.Headers["X-Forwarded-Proto"].ToString().Split(',')[0].Trim() is { Length: > 0 } p ? p : request.Scheme));
        foreach (string trusted in (Environment.GetEnvironmentVariable("TRUSTED_ORIGINS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string t = trusted.Trim();
            if (Uri.TryCreate(t.Contains("//") ? t : "https://" + t, UriKind.Absolute, out var tu))
                own.Add(HostKey(tu.Authority, tu.Scheme));
        }
        own.Remove("");
        return own.Contains(key);
    }

    private static string HostKey(string netloc, string scheme)
    {
        netloc = netloc.Trim().ToLowerInvariant();
        int at = netloc.LastIndexOf('@');
        if (at >= 0) netloc = netloc[(at + 1)..];
        string host, port;
        if (netloc.StartsWith('['))
        {
            int end = netloc.IndexOf(']');
            host = end < 0 ? netloc : netloc[..(end + 1)];
            port = end < 0 ? "" : netloc[(end + 1)..].TrimStart(':');
        }
        else
        {
            int colon = netloc.IndexOf(':');
            host = colon < 0 ? netloc : netloc[..colon];
            port = colon < 0 ? "" : netloc[(colon + 1)..];
        }
        if ((scheme == "http" && port == "80") || (scheme == "https" && port == "443")) port = "";
        return host + (port.Length > 0 ? ":" + port : "");
    }
}
