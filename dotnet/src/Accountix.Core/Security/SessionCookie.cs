using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Accountix.Core.Security;

/// <summary>
/// The sign-in cookie, <c>ax_session</c>. It carries a few values, signed with
/// SECRET_KEY so nobody can change them:
/// <list type="bullet">
/// <item><c>_user_id</c> = "&lt;id&gt;:&lt;password fingerprint&gt;" (<see cref="SessionFingerprint"/>):
/// a new password signs out every other session of the login;</item>
/// <item><c>_sid</c>: a random token whose SHA-256 names the server's row in
/// <c>user_sessions</c> (signed out, idle, too old or ended elsewhere = signed out);</item>
/// <item><c>company_id</c>: the company the person is working in.</item>
/// </list>
/// Format: <c>[.]base64url(json) . base64url(timestamp) . base64url(HMAC-SHA256)</c>; a
/// leading dot means the JSON is zlib-compressed (only when that is shorter). The HMAC key is
/// HMAC-SHA256(SECRET_KEY, "accountix-session"); the signature covers "payload.timestamp".
/// No ASP.NET data-protection keys are involved, so any number of instances read it.
/// </summary>
public static class SessionCookie
{
    public const string CookieName = "ax_session";
    public const string UserKey = "_user_id";
    public const string TokenKey = "_sid";
    public const string CompanyKey = "company_id";
    private const string Salt = "accountix-session";

    /// <summary>The server row lives 12 h at most; the cookie a little longer, so the row decides.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(13);

    /// <summary>A new session: the person, their password fingerprint and the server token.</summary>
    public static JsonObject NewSession(int userId, string fingerprint, string token) => new()
    {
        [TokenKey] = token,
        [UserKey] = $"{userId}:{fingerprint}",
    };

    /// <summary>The cookie value for these values, stamped <paramref name="at"/>.</summary>
    public static string Sign(JsonObject session, string secretKey, DateTimeOffset at)
    {
        byte[] payload = Encoding.UTF8.GetBytes(Json(session));
        string body = B64(payload);
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(payload);
            byte[] compressed = ms.ToArray();
            if (compressed.Length < payload.Length - 1)
                body = "." + B64(compressed);
        }
        byte[] tsBytes = BitConverter.GetBytes(at.ToUnixTimeSeconds()).Reverse().SkipWhile(x => x == 0).ToArray();
        string value = body + "." + B64(tsBytes);
        return value + "." + B64(Mac(secretKey, value));
    }

    /// <summary>The session's values, or null: missing, not signed with this key, changed, or older than <see cref="MaxAge"/>.</summary>
    public static JsonObject? Read(string? cookie, string secretKey, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(cookie) || string.IsNullOrEmpty(secretKey))
            return null;
        int sigDot = cookie.LastIndexOf('.');
        if (sigDot <= 0)
            return null;
        string signed = cookie[..sigDot];
        byte[]? sig = B64Decode(cookie[(sigDot + 1)..]);
        if (sig is null || !CryptographicOperations.FixedTimeEquals(sig, Mac(secretKey, signed)))
            return null;
        int tsDot = signed.LastIndexOf('.');
        if (tsDot <= 0)
            return null;
        byte[]? tsBytes = B64Decode(signed[(tsDot + 1)..]);
        if (tsBytes is null || tsBytes.Length is 0 or > 8)
            return null;
        long ts = 0;
        foreach (byte b in tsBytes)
            ts = (ts << 8) | b;
        var age = now - DateTimeOffset.FromUnixTimeSeconds(ts);
        if (age > MaxAge || age < TimeSpan.FromMinutes(-5))
            return null;
        string payload = signed[..tsDot];
        bool compressed = payload.StartsWith('.');
        byte[]? raw = B64Decode(compressed ? payload[1..] : payload);
        if (raw is null)
            return null;
        try
        {
            if (compressed)
            {
                using var input = new MemoryStream(raw);
                using var z = new ZLibStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                z.CopyTo(output);
                raw = output.ToArray();
            }
            return JsonNode.Parse(raw) as JsonObject;
        }
        catch (Exception e) when (e is InvalidDataException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The signed-in user id and password fingerprint from <c>_user_id</c> ("12:ab34…" or a bare "12").</summary>
    public static (int UserId, string? Fingerprint)? UserOf(JsonObject session)
    {
        if (session[UserKey] is not JsonValue v || !v.TryGetValue(out string? raw) || raw is null)
            return null;
        int colon = raw.IndexOf(':');
        if (!int.TryParse(colon < 0 ? raw : raw[..colon], out int id))
            return null;
        return (id, colon < 0 ? null : raw[(colon + 1)..]);
    }

    public static int? CompanyOf(JsonObject session)
    {
        if (session[CompanyKey] is not JsonValue v)
            return null;
        if (v.TryGetValue(out int i))
            return i;
        if (v.TryGetValue(out string? s) && int.TryParse(s, out i))
            return i;
        return null;
    }

    /// <summary>The server session token (<c>_sid</c>), or null.</summary>
    public static string? TokenOf(JsonObject? session) =>
        session?[TokenKey] is JsonValue v && v.TryGetValue(out string? t) && !string.IsNullOrEmpty(t) ? t : null;

    /// <summary>A fresh server session token: 32 random bytes, base64url.</summary>
    public static string NewToken() => B64(RandomNumberGenerator.GetBytes(32));

    /// <summary>SHA-256 of a token, hex: the user_sessions.token_hash it names.</summary>
    public static string TokenHash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Compact JSON, keys sorted (the same values always sign to the same bytes).</summary>
    public static string Json(JsonObject session)
    {
        var o = new JsonObject();
        foreach (var (k, v) in session.OrderBy(p => p.Key, StringComparer.Ordinal))
            o[k] = v?.DeepClone();
        return o.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    private static byte[] Mac(string secretKey, string value)
    {
        byte[] key = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secretKey), Encoding.UTF8.GetBytes(Salt));
        return HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(value));
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[]? B64Decode(string s)
    {
        try
        {
            string b = s.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(b.PadRight(b.Length + (4 - b.Length % 4) % 4, '='));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
