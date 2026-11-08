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

    public static NeonServices From(IConfiguration config) => new()
    {
        HelloFunctionUrl = Url(config["NEON_FUNCTION_HELLO_BASE_URL"] ?? config["Accountix:Neon:HelloFunctionUrl"]),
        Branch = Blank(config["NEON_BRANCH"]),
    };

    private static string? Url(string? value) =>
        Blank(value) is { } url ? url.TrimEnd('/') : null;

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>What the function said: up/down, its status code, and the first part of its body.</summary>
public readonly record struct NeonFunctionAnswer(bool Ok, int? Status, string? Body);

/// <summary>
/// Calls the hello function deployed by <c>neon deploy</c>, which is what proves the Functions
/// service is reachable from this host. A failure is an answer, not an exception: the caller
/// (the /net/neon page) reports it.
/// </summary>
public sealed class NeonFunctionClient(IHttpClientFactory factory, NeonServices services)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Null when no function is configured; otherwise up/down and the first 200 characters.</summary>
    public async Task<NeonFunctionAnswer?> PingHelloAsync(CancellationToken ct = default)
    {
        if (services.HelloFunctionUrl is not { } url)
            return null;
        using var http = factory.CreateClient(nameof(NeonFunctionClient));
        try
        {
            using var response = await http.GetAsync(url, ct);
            string body = await response.Content.ReadAsStringAsync(ct);
            return new NeonFunctionAnswer(response.IsSuccessStatusCode, (int)response.StatusCode, Truncate(body));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            return new NeonFunctionAnswer(false, null, e.GetType().Name);
        }
    }

    private static string Truncate(string body)
    {
        body = body.Trim();
        return body.Length <= 200 ? body : body[..200];
    }
}
