using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Accountix.Fbr.Services;
using Accountix.Web.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Accountix.Fbr.Tests;

/// <summary>
/// FBR's API as the tests see it: every request the app makes to FBR lands
/// here (the named HttpClient's handler is replaced), is logged, and gets the
/// answer the test asked for. Nothing ever reaches sandbox.fbr.gov.pk.
/// Tests in this assembly run one at a time, so one shared instance is enough.
/// </summary>
public static class FakeFbr
{
    public sealed record Call(string Method, string Url, string? Body, string? Authorization, string? ContentType);
    public sealed record Reply(int Status, string Body, string ContentType = "application/json", TimeSpan Delay = default, bool Fail = false);

    private static readonly ConcurrentQueue<Call> Log = new();
    private static readonly ConcurrentQueue<Reply> Queue = new();
    private static Reply _default = new(503, "{\"error\": \"fake FBR: no answer set\"}");

    public static IReadOnlyList<Call> Calls => Log.ToList();
    public static int Sends => Log.Count(c => c.Url.EndsWith("/sales/send", StringComparison.Ordinal));

    /// <summary>Forget earlier calls; answer <paramref name="always"/> unless a queued reply comes first.</summary>
    public static void Reset(Reply? always = null)
    {
        Log.Clear();
        Queue.Clear();
        _default = always ?? new(503, "{\"error\": \"fake FBR: no answer set\"}");
    }

    public static void Then(Reply reply) => Queue.Enqueue(reply);

    public static Reply Json(int status, string json, int delayMs = 0) => new(status, json, Delay: TimeSpan.FromMilliseconds(delayMs));

    internal sealed class Handler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Log.Enqueue(new Call(request.Method.Method, request.RequestUri!.ToString(), body,
                                 request.Headers.TryGetValues("Authorization", out var a) ? a.First() : null,
                                 request.Content?.Headers.ContentType?.ToString()));
            var reply = Queue.TryDequeue(out var next) ? next : _default;
            if (reply.Delay > TimeSpan.Zero)
                await Task.Delay(reply.Delay, ct);
            if (reply.Fail)
                throw new HttpRequestException("Name or service not known");
            var r = new HttpResponseMessage((HttpStatusCode)reply.Status)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(reply.Body)),
                RequestMessage = request,
            };
            if (reply.ContentType.Length > 0)
                r.Content.Headers.TryAddWithoutValidation("Content-Type", reply.ContentType);
            return r;
        }
    }
}

/// <summary>Booted with the FBR module in the tests: points FBR's HttpClient at <see cref="FakeFbr"/>.</summary>
public sealed class FakeFbrModule : IAccountixModule
{
    public string Key => "fbr-test";

    public void Register(IServiceCollection services, IConfiguration config) =>
        services.AddHttpClient(FbrClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeFbr.Handler());

    public ModuleNav? Navigation => null;
}
