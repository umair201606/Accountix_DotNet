using System.Net.Http.Headers;
using System.Text;

namespace Accountix.Fbr.Services;

/// <summary>What one call to FBR gave: requests' (r.ok, r.status_code, _body(r)); status 0 when nothing came back.</summary>
public sealed record FbrAnswer(bool Ok, int StatusCode, object? Data);

/// <summary>
/// HTTP to FBR's Digital Invoicing API (fbr_app/services/fbr_client.py):
/// sandbox or production by the company's settings, Bearer token, 30 s
/// timeout. Network failures come back as <c>(False, 0, {"error": …})</c>
/// and an answer that is not JSON (a gateway's HTML error page) as
/// <c>{"error": "FBR answered 502: …"}</c>, never as an exception.
///
/// Uses the named HttpClient <see cref="HttpClientName"/>; tests replace its
/// handler, so they never reach the real FBR.
/// </summary>
public sealed class FbrClient(IHttpClientFactory factory)
{
    public const string HttpClientName = "fbr";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>heartbeat(): (r.ok, r.text), or (False, the error).</summary>
    public async Task<(bool Ok, string Message)> HeartbeatAsync(FbrConfig config)
    {
        var url = Url(config, "/heartbeat");
        try
        {
            using var r = await SendAsync(config, HttpMethod.Get, url, null);
            return ((int)r.StatusCode < 400, await TextAsync(r));
        }
        catch (Exception e) when (IsRequestFailure(e))
        {
            return (false, Describe(e, url));
        }
    }

    public Task<FbrAnswer> ValidateAsync(FbrConfig config, PyDict payload) => PostAsync(config, "/sales/validate", payload);

    public Task<FbrAnswer> SubmitAsync(FbrConfig config, PyDict payload) => PostAsync(config, "/sales/send", payload);

    public async Task<FbrAnswer> StatusAsync(FbrConfig config, string fbrInvoiceNumber)
    {
        var url = Url(config, $"/sales/{fbrInvoiceNumber}");
        try
        {
            using var r = await SendAsync(config, HttpMethod.Get, url, null);
            return new FbrAnswer((int)r.StatusCode < 400, (int)r.StatusCode, await BodyAsync(r));
        }
        catch (Exception e) when (IsRequestFailure(e))
        {
            return new FbrAnswer(false, 0, new PyDict { ["error"] = Describe(e, url) });
        }
    }

    /// <summary>The request body requests sends for json=payload: json.dumps(payload) (", " and ": ").</summary>
    public static string WireBody(PyDict payload) => Py.Dumps(payload);

    private async Task<FbrAnswer> PostAsync(FbrConfig config, string path, PyDict payload)
    {
        var url = Url(config, path);
        try
        {
            using var r = await SendAsync(config, HttpMethod.Post, url, WireBody(payload));
            return new FbrAnswer((int)r.StatusCode < 400, (int)r.StatusCode, await BodyAsync(r));
        }
        catch (Exception e) when (IsRequestFailure(e))
        {
            return new FbrAnswer(false, 0, new PyDict { ["error"] = Describe(e, url) });
        }
    }

    private static Uri Url(FbrConfig config, string path) => new(FbrConfigs.BaseUrl(config) + path);

    private async Task<HttpResponseMessage> SendAsync(FbrConfig config, HttpMethod method, Uri url, string? json)
    {
        var client = factory.CreateClient(HttpClientName);
        client.Timeout = Timeout;
        var request = new HttpRequestMessage(method, url);
        // Never tied to the browser's request: once FBR has the invoice, the
        // answer must be recorded even if the person closed the page.
        if (!request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {config.AccessToken}"))
            throw new HttpRequestException("Invalid Authorization header value");
        if (json is not null)
        {
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        return await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, CancellationToken.None);
    }

    private static bool IsRequestFailure(Exception e) =>
        e is HttpRequestException or TaskCanceledException or TimeoutException or InvalidOperationException
            or FormatException or IOException;

    /// <summary>str(e) of the requests exception, in its shape: host, port, what failed.</summary>
    private static string Describe(Exception e, Uri url)
    {
        string pool = $"HTTPSConnectionPool(host='{url.Host}', port={url.Port})";
        if (e is TaskCanceledException or TimeoutException)
            return $"{pool}: Read timed out. (read timeout={(int)Timeout.TotalSeconds})";
        string cause = e.InnerException?.Message is { Length: > 0 } inner ? inner : e.Message;
        return $"{pool}: Max retries exceeded with url: {url.PathAndQuery} (Caused by {cause})";
    }

    /// <summary>r.text: the charset in Content-Type, ISO-8859-1 for text/* without one, else UTF-8.</summary>
    private static async Task<string> TextAsync(HttpResponseMessage r)
    {
        byte[] bytes = await r.Content.ReadAsByteArrayAsync();
        var ct = r.Content.Headers.ContentType;
        Encoding enc = Encoding.UTF8;
        if (ct?.CharSet is { Length: > 0 } cs)
        {
            try { enc = Encoding.GetEncoding(cs.Trim('\'', '"')); }
            catch (ArgumentException) { enc = Encoding.UTF8; }
        }
        else if (ct?.MediaType?.Contains("text", StringComparison.Ordinal) == true)
            enc = Encoding.Latin1;
        string text = enc.GetString(bytes);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    /// <summary>_body(r): {} for an empty answer, the parsed JSON, or {"error": "FBR answered N: …"}.</summary>
    private static async Task<object?> BodyAsync(HttpResponseMessage r)
    {
        string text = await TextAsync(r);
        if (text.Length == 0)
            return new PyDict();
        try
        {
            // r.json() without a declared encoding reads the bytes as utf-8-sig (a BOM is skipped);
            // with one it parses r.text, where a BOM is not JSON.
            bool declared = r.Content.Headers.ContentType?.CharSet is { Length: > 0 }
                            || r.Content.Headers.ContentType?.MediaType is { } mt && (mt.Contains("text") || mt.Contains("application/json"));
            return Py.Loads(!declared && text[0] == '\uFEFF' ? text[1..] : text);
        }
        catch (FormatException)
        {
            return new PyDict { ["error"] = $"FBR answered {(int)r.StatusCode}: {Py.Slice(text, 300)}" };
        }
    }
}
