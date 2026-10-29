using Accountix.Core.Access;
using Accountix.Core.Data;
using Accountix.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Web.Controllers;

/// <summary>
/// Foundation pages: who the app thinks you are, which company you are in
/// and what you may open. Used to check that the .NET app reads access
/// exactly as the Python app does before any module moves over.
/// </summary>
[Route("net")]
public sealed class AccessController(AccountixDbContext db) : Controller
{
    [HttpGet("access")]
    [Authorize]
    public IActionResult MyAccess()
    {
        var access = HttpContext.Access();
        if (access is null)
            return Challenge();
        return View(access);
    }

    /// <summary>Health check for the host: the app is up and reaches the database.</summary>
    [HttpGet("health")]
    [AllowAnonymous]
    public async Task<IActionResult> Health(CancellationToken ct)
    {
        bool db_ok = await db.Database.CanConnectAsync(ct);
        return db_ok ? Ok(new { ok = true }) : StatusCode(503, new { ok = false });
    }

    /// <summary>
    /// The request as this app sees it behind the proxies (Vercel rewrite or
    /// Front Door, then App Service): scheme, host, client address and the
    /// forwarded headers, before and after ForwardedHeaders. The cut-over's
    /// first check (docs/CUTOVER.md, phase 0). Signed-in people only; cookie
    /// names are listed, never their values.
    /// </summary>
    [HttpGet("request")]
    [Authorize]
    public IActionResult RequestSeen()
    {
        var r = Request;
        string? H(string name) => r.Headers.TryGetValue(name, out var v) ? v.ToString() : null;
        return Json(new
        {
            ok = true,
            scheme = r.Scheme,
            host = r.Host.Value,
            path = r.Path.Value,
            remote_ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
            https = r.IsHttps,
            signed_in_as = HttpContext.Access()?.User.Id,
            company_id = HttpContext.Access()?.CompanyId,
            headers = new
            {
                x_forwarded_for = H("X-Forwarded-For"),
                x_forwarded_proto = H("X-Forwarded-Proto"),
                x_forwarded_host = H("X-Forwarded-Host"),
                x_original_for = H("X-Original-For"),
                x_original_proto = H("X-Original-Proto"),
                x_original_host = H("X-Original-Host"),
                x_real_ip = H("X-Real-IP"),
                x_azure_fdid = H("X-Azure-FDID"),
                x_vercel_id = H("X-Vercel-Id"),
                accept_encoding = H("Accept-Encoding"),
                origin = H("Origin"),
                referer_host = Uri.TryCreate(H("Referer"), UriKind.Absolute, out var u) ? u.Authority : null,
            },
            cookies = r.Cookies.Keys.Order().ToArray(),
        });
    }

    [HttpGet("error")]
    [AllowAnonymous]
    public IActionResult Error() => View();
}
