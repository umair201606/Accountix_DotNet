using Accountix.Core.Data;
using Accountix.Core.Shared;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Web.Infrastructure;

/// <summary>
/// Expected refusals become answers, not crashes (app.py's error handlers):
/// a business rule (closed period, bad date, negative stock...) or a clash
/// with another user's change rolls back and is answered in words — JSON 409
/// for fetch() callers, a flash and a return to the page otherwise. No
/// company chosen goes to the portal. Anything else is a 500 page that never
/// shows the stack trace outside development.
/// </summary>
public sealed class ErrorAnswersMiddleware(RequestDelegate next, ILogger<ErrorAnswersMiddleware> log, IHostEnvironment env)
{
    public async Task InvokeAsync(HttpContext http, ITempDataDictionaryFactory tempData)
    {
        try
        {
            await next(http);
        }
        catch (UnknownExportFormatException) when (!http.Response.HasStarted)
        {
            http.Response.Clear();
            http.Response.StatusCode = 404;
            http.Response.ContentType = "text/html; charset=utf-8";
            await http.Response.WriteAsync(ErrorPage(404, "Not Found", "The page or record you're looking for doesn't exist."));
        }
        catch (NoActiveCompanyException) when (!http.Response.HasStarted)
        {
            await Answer(http, tempData, 409, "Open a company first.", "/portal/");
        }
        catch (BusinessRuleException e) when (!http.Response.HasStarted)
        {
            await Answer(http, tempData, 409, e.Message, null);
        }
        catch (Exception e) when (!http.Response.HasStarted && ConflictMessages.For(e) is string msg)
        {
            log.LogWarning(e, "Concurrent change refused");
            await Answer(http, tempData, 409, msg, null);
        }
        catch (Exception e) when (!http.Response.HasStarted && !env.IsDevelopment())
        {
            log.LogError(e, "Unhandled exception");
            http.Response.Clear();
            http.Response.StatusCode = 500;
            http.Response.ContentType = "text/html; charset=utf-8";
            await http.Response.WriteAsync(ErrorPage(500, "Server error", "Something went wrong on our side. The error has been logged."));
        }
    }

    private static async Task Answer(HttpContext http, ITempDataDictionaryFactory tempData, int status, string message, string? to)
    {
        http.Response.Clear();
        if (http.Request.WantsJson() || !HttpMethods.IsGet(http.Request.Method) && http.Request.HasJsonContentType())
        {
            http.Response.StatusCode = status;
            await http.Response.WriteAsJsonAsync(new { ok = false, error = message });
            return;
        }
        var td = tempData.GetTempData(http);
        Flashes.Add(td, message, "error");
        td.Save();
        http.Response.Redirect(to ?? SafeUrl.Back(http.Request));
    }

    public static string ErrorPage(int code, string title, string message) => $$$"""
        <!doctype html><html><head><meta charset='utf-8'>
        <meta name='viewport' content='width=device-width,initial-scale=1'>
        <title>{{{code}}} · {{{System.Net.WebUtility.HtmlEncode(title)}}}</title>
        <style>
        body{font-family:Inter,-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;background:#f1f5f9;color:#1e293b;display:flex;align-items:center;justify-content:center;min-height:100vh;margin:0;padding:16px}
        .box{background:#fff;border:1px solid #e2e8f0;border-radius:12px;box-shadow:0 4px 20px rgba(0,0,0,.06);padding:40px 44px;max-width:440px;text-align:center}
        .code{font-size:56px;font-weight:800;color:#2563eb;line-height:1;margin:0}h1{font-size:20px;margin:12px 0 6px}p{color:#64748b;font-size:14px;margin:0 0 22px}
        a{display:inline-block;background:#2563eb;color:#fff;text-decoration:none;padding:9px 20px;border-radius:6px;font-size:14px;font-weight:600}
        @media (prefers-color-scheme: dark){body{background:#0e0e0e;color:#e5e5e5}.box{background:#1a1a1a;border-color:#333}}
        </style></head><body><div class='box'>
        <p class='code'>{{{code}}}</p><h1>{{{System.Net.WebUtility.HtmlEncode(title)}}}</h1><p>{{{System.Net.WebUtility.HtmlEncode(message)}}}</p>
        <a href='/'>&larr; Back to dashboard</a></div></body></html>
        """;
}

/// <summary>
/// Every Postgres transaction waits at most DB_LOCK_TIMEOUT_MS (15 s) for a
/// row another user holds (shared/concurrency.install_lock_timeout).
/// </summary>
public sealed class LockTimeoutInterceptor(int milliseconds) : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor
{
    public override async ValueTask<System.Data.Common.DbTransaction> TransactionStartedAsync(
        System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.TransactionEndEventData eventData,
        System.Data.Common.DbTransaction result, CancellationToken ct = default)
    {
        if (connection is Npgsql.NpgsqlConnection)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = result;
            cmd.CommandText = $"SET LOCAL lock_timeout = {milliseconds}";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return result;
    }

    public override System.Data.Common.DbTransaction TransactionStarted(
        System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.TransactionEndEventData eventData,
        System.Data.Common.DbTransaction result)
    {
        if (connection is Npgsql.NpgsqlConnection)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = result;
            cmd.CommandText = $"SET LOCAL lock_timeout = {milliseconds}";
            cmd.ExecuteNonQuery();
        }
        return result;
    }
}

/// <summary>Stand-ins until the owning module registers the real service.</summary>
public static class NotYetPortedServices
{
    public static void AddStubs(IServiceCollection s)
    {
        s.AddScoped<ILedger>(_ => Proxy<ILedger>.Create("General-ledger posting (Accounting)"));
        s.AddScoped<IVoucherNumbers>(_ => Proxy<IVoucherNumbers>.Create("Voucher numbers (Accounting)"));
        s.AddScoped<IPeriods>(_ => Proxy<IPeriods>.Create("Financial periods (Accounting)"));
        s.AddScoped<IStockCosting>(_ => Proxy<IStockCosting>.Create("Stock costing (Inventory)"));
        s.AddScoped<IReportExport>(_ => Proxy<IReportExport>.Create("Report export (Finance)"));
        s.AddScoped<IAuditTrail>(_ => Proxy<IAuditTrail>.Create("Audit trail (Platform)"));
        s.AddScoped<IPaymentTracking>(_ => Proxy<IPaymentTracking>.Create("Invoice tracking (Parties)"));
    }

    private class Proxy<T> : System.Reflection.DispatchProxy where T : class
    {
        private string _what = "";

        public static T Create(string what)
        {
            var p = Create<T, Proxy<T>>();
            ((Proxy<T>)(object)p)._what = what;
            return p;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) =>
            throw new NotYetPortedException(_what);
    }
}
