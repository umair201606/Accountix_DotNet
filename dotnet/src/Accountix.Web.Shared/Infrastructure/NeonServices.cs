using Microsoft.Extensions.Configuration;

namespace Accountix.Web.Infrastructure;

/// <summary>
/// The Neon services this deployment was provisioned with: <c>neon.ts</c> declares them and
/// <c>neon deploy</c> pulls their addresses into <c>.env.local</c> (see <see cref="EnvFile"/>);
/// App Settings do the same job on Azure. Postgres is read by <see cref="AppSettings"/>;
/// this holds the rest.
/// </summary>
public sealed class NeonServices
{
    /// <summary>The function declared in neon.ts (functions.hello → hello.ts).</summary>
    public const string HelloFunctionName = "hello";

    /// <summary>NEON_FUNCTION_HELLO_BASE_URL: the deployed function's base address, no trailing slash.</summary>
    public string? HelloFunctionUrl { get; init; }

    /// <summary>NEON_BRANCH: the branch the variables were pulled from (production, dev…).</summary>
    public string? Branch { get; init; }

    public bool HasFunctions => HelloFunctionUrl is not null;

    /// <summary>
    /// NEON_FUNCTION_HELLO_BASE_URL, else the App Setting <c>Accountix:Neon:HelloFunctionUrl</c>.
    /// A blank variable means "not set" and falls back like a missing one, so an empty App
    /// Setting cannot silently hide a configured URL.
    /// </summary>
    public static NeonServices From(IConfiguration config) => new()
    {
        HelloFunctionUrl = Url(Blank(config["NEON_FUNCTION_HELLO_BASE_URL"]) ?? config["Accountix:Neon:HelloFunctionUrl"]),
        Branch = Blank(config["NEON_BRANCH"]),
    };

    /// <summary>
    /// The address or null. Blank is "not configured", and only an absolute http(s) URL is
    /// accepted: anything else (a scheme-less host, ftp://…) would make HttpClient throw
    /// instead of answering, and /net/neon must never 500 on a mistyped setting.
    /// </summary>
    private static string? Url(string? value) =>
        Blank(value) is { } url
        && Uri.TryCreate(url, UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            ? url.TrimEnd('/')
            : null;

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>What the function said: up/down, its status code, and the first part of its body.</summary>
public readonly record struct NeonFunctionAnswer(bool Ok, int? Status, string? Body);

/// <summary>
/// Calls the hello function deployed by <c>neon deploy</c>, which is what proves the Functions
/// service is reachable from this host. A failure is an answer, not an exception: the caller
/// (the /net/neon page) reports it. One answer is kept for <see cref="CacheFor"/>: the page is
/// anonymous up/down, and one probe per request would let anyone flood the function.
/// </summary>
public sealed class NeonFunctionClient(IHttpClientFactory factory, NeonServices services)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>How long one answer is reused before the function is asked again.</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private NeonFunctionAnswer? _answer;
    private DateTimeOffset _answeredAt;

    /// <summary>Null when no function is configured; otherwise up/down and the first 200 characters.</summary>
    public async Task<NeonFunctionAnswer?> PingHelloAsync(CancellationToken ct = default)
    {
        if (services.HelloFunctionUrl is not { } url)
            return null;
        lock (_gate)
            if (_answer is { } fresh && DateTimeOffset.UtcNow - _answeredAt < CacheFor)
                return fresh;
        var answer = await AskAsync(url, ct);
        lock (_gate)
        {
            _answer = answer;
            _answeredAt = DateTimeOffset.UtcNow;
        }
        return answer;
    }

    private async Task<NeonFunctionAnswer> AskAsync(string url, CancellationToken ct)
    {
        using var http = factory.CreateClient(nameof(NeonFunctionClient));
        // A mistyped or hostile address must not make the app buffer a huge body just to keep
        // 200 characters (ResponseHeadersRead) nor hang a request (the linked timeout covers
        // the body read too, which HttpClient.Timeout no longer does once the headers are in).
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Timeout);
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            return new NeonFunctionAnswer(response.IsSuccessStatusCode, (int)response.StatusCode,
                                          Truncate(await ReadHeadAsync(response, deadline.Token)));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException
                                       or NotSupportedException or UriFormatException)
        {
            ct.ThrowIfCancellationRequested();
            return new NeonFunctionAnswer(false, null, e.GetType().Name);
        }
    }

    /// <summary>The first 256 characters of the body, and never more of it.</summary>
    private static async Task<string> ReadHeadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        var buffer = new char[256];
        int read = await reader.ReadAsync(buffer.AsMemory(), ct);
        return new string(buffer, 0, read);
    }

    private static string Truncate(string body)
    {
        body = body.Trim();
        return body.Length <= 200 ? body : body[..200];
    }
}
