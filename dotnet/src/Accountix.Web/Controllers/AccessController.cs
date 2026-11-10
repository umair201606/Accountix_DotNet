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
public sealed class AccessController(AccountixDbContext db, NeonServices neon, NeonFunctionClient functions) : Controller
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
    /// The Neon services this deployment was provisioned with (neon.ts → `neon deploy`).
    /// Anonymous like /net/health, it answers only up/down — no names, ids or data. The
    /// details (branch, the function's address, its first 200 characters) are shown to
    /// signed-in people only, like /net/request. The probe's answer is kept a minute
    /// (NeonFunctionClient), so the page cannot flood the function. A function that is
    /// down does not take the app down with it: 503 only for Postgres.
    /// </summary>
    [HttpGet("neon")]
    [AllowAnonymous]
    public async Task<IActionResult> Neon(CancellationToken ct)
    {
        bool database = await db.Database.CanConnectAsync(ct);
        NeonFunctionAnswer? function = await functions.PingHelloAsync(ct);
        bool signedIn = HttpContext.Access() is not null;
        var answer = new
        {
            ok = database,
            branch = signedIn ? neon.Branch : null,
            database = new { ok = database },
            functions = new
            {
                configured = function is not null,
                ok = function?.Ok,
                status = function?.Status,
                url = signedIn ? neon.HelloFunctionUrl : null,
                body = signedIn ? function?.Body : null,
            },
        };
        return database ? Ok(answer) : StatusCode(503, answer);
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
