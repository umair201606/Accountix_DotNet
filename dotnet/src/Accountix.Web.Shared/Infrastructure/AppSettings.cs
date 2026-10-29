using Npgsql;

namespace Accountix.Web.Infrastructure;

/// <summary>
/// The app's core settings, from the environment (App Settings in Azure):
/// SECRET_KEY, DATABASE_URL (or ConnectionStrings:Accountix), the sign-in page.
/// </summary>
public sealed class AppSettings
{
    /// <summary>SECRET_KEY: signs the ax_session cookie and keys the password fingerprint. Changing it signs everyone out.</summary>
    public required string SecretKey { get; init; }

    /// <summary>Npgsql connection string built from DATABASE_URL (or ConnectionStrings:Accountix).</summary>
    public required string ConnectionString { get; init; }

    /// <summary>The sign-in page signed-out visitors are sent to.</summary>
    public string LoginUrl { get; init; } = "/auth/login";

    public static AppSettings From(IConfiguration config)
    {
        string secret = config["SECRET_KEY"] ?? config["Accountix:SecretKey"] ?? "";
        if (secret.Length < 16)
            throw new InvalidOperationException(
                "SECRET_KEY is missing or too short (16+ characters). It signs the sign-in cookie: keep the " +
                "value the deployment already has, or everyone has to sign in again.");
        string? url = config["DATABASE_URL"];
        string cs = !string.IsNullOrWhiteSpace(url)
            ? FromDatabaseUrl(url)
            : config.GetConnectionString("Accountix")
              ?? throw new InvalidOperationException("Set DATABASE_URL or ConnectionStrings:Accountix.");
        return new AppSettings
        {
            SecretKey = secret,
            ConnectionString = cs,
            LoginUrl = config["Accountix:LoginUrl"] ?? "/auth/login",
        };
    }

    /// <summary>postgres://user:pass@host:5432/db?sslmode=require → Npgsql key/value form (what Neon and Vercel hand out).</summary>
    public static string FromDatabaseUrl(string url)
    {
        url = url.Trim().Trim('"', '\'');
        int scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0)
            return url;      // already a key/value connection string
        // postgresql://user:pass@/db?host=/socket/dir (a local socket; SQLAlchemy reads it
        // too) has no host name, which Uri refuses: parse it with a stand-in, then
        // take the host from ?host= below.
        string rest = url[(scheme + 3)..];
        int at = rest.IndexOf('@');
        if (rest.StartsWith('/') || (at >= 0 && rest.Length > at + 1 && rest[at + 1] == '/'))
            rest = at >= 0 ? rest[..(at + 1)] + "localhost" + rest[(at + 1)..] : "localhost" + rest;
        var uri = new Uri("postgres://" + rest);
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort || uri.Port <= 0 ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
        };
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            string[] up = uri.UserInfo.Split(':', 2);
            b.Username = Uri.UnescapeDataString(up[0]);
            if (up.Length > 1)
                b.Password = Uri.UnescapeDataString(up[1]);
        }
        foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] kv = pair.Split('=', 2);
            string key = Uri.UnescapeDataString(kv[0]).ToLowerInvariant();
            string value = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
            switch (key)
            {
                case "sslmode":
                    b.SslMode = value.ToLowerInvariant() switch
                    {
                        "disable" => SslMode.Disable,
                        "allow" => SslMode.Allow,
                        "prefer" => SslMode.Prefer,
                        "verify-ca" => SslMode.VerifyCA,
                        "verify-full" => SslMode.VerifyFull,
                        _ => SslMode.Require,
                    };
                    break;
                case "host":
                    b.Host = value;
                    break;
                case "port" when int.TryParse(value, out int port):
                    b.Port = port;
                    break;
                case "application_name":
                    b.ApplicationName = value;
                    break;
                case "channel_binding":
                    b.ChannelBinding = value == "require" ? ChannelBinding.Require : ChannelBinding.Prefer;
                    break;
            }
        }
        return b.ConnectionString;
    }
}
