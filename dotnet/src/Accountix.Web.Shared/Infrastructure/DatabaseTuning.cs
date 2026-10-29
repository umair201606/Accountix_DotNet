using Npgsql;

namespace Accountix.Web.Infrastructure;

/// <summary>
/// Npgsql settings for Neon behind Azure App Service (docs/DEPLOY.md, "Database").
///
/// Outside Development, the values below apply when the connection string
/// does not name them already (an explicit connection-string value always
/// wins), and each can be set with the App Setting named next to it. In
/// Development (the module test hosts) nothing changes unless a setting is
/// given, so the shared test Postgres keeps its own defaults.
///
/// - <c>Maximum Pool Size</c> 20 (<c>DB_MAX_POOL_SIZE</c>): connections per
///   instance. Neon's pooled endpoint takes many clients, but each open
///   transaction holds a real server connection; 20 per instance keeps
///   two or three instances far below the compute's limit, with the
///   Python app on Vercel sharing it.
/// - <c>Timeout</c> 30 s (<c>DB_CONNECT_TIMEOUT_S</c>): a suspended Neon
///   compute takes a few seconds to wake.
/// - <c>Command Timeout</c> 60 s (<c>DB_COMMAND_TIMEOUT_S</c>): year reports
///   and exports run long statements; locks give up much earlier
///   (DB_LOCK_TIMEOUT_MS, 15 s).
/// - <c>Keepalive</c> 30 s (<c>DB_KEEPALIVE_S</c>) and
///   <c>Connection Idle Lifetime</c> 60 s (<c>DB_IDLE_LIFETIME_S</c>):
///   Azure drops outbound TCP connections idle for 4 minutes (SNAT) and
///   Neon closes them when the compute suspends; idle pooled connections
///   are closed after a minute instead of failing on next use.
/// - <c>No Reset On Close</c> on Neon's pooled endpoint (host contains
///   "-pooler"): PgBouncer in transaction mode hands each transaction any
///   server connection, so there is no session state to reset. The app
///   keeps none: lock timeouts are SET LOCAL, advisory locks are
///   transaction-scoped (pg_advisory_xact_lock), no prepared statements.
/// - <c>SSL Mode</c> Require for a remote host left at the default (Prefer).
/// - <c>Application Name</c> accountix-dotnet, to tell the two apps apart in
///   pg_stat_activity.
///
/// No automatic retries (no EnableRetryOnFailure): the modules open their
/// own transactions (lock, change, post, commit), which EF's retrying
/// strategy refuses, and replaying a write whose commit reply was lost
/// could post it twice.
/// </summary>
public static class DatabaseTuning
{
    public const string ApplicationName = "accountix-dotnet";

    public static string Apply(string connectionString, IConfiguration config, bool production)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        // Keys written in the connection string itself, spaces and case ignored
        // ("Maximum Pool Size" = "MaxPoolSize" = "max pool size").
        var given = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString }
            .Keys.Cast<string>().Select(Norm).ToHashSet();
        bool Named(string key) => (Synonyms.TryGetValue(Norm(key), out var all) ? all : [Norm(key)]).Any(given.Contains);

        void Set(string key, string setting, int productionDefault, Action<int> assign)
        {
            if (Named(key))
                return;
            int? value = config.GetValue<int?>(setting) ?? (production ? productionDefault : null);
            if (value is int v)
                assign(v);
        }

        Set("Maximum Pool Size", "DB_MAX_POOL_SIZE", 20, v => b.MaxPoolSize = v);
        Set("Timeout", "DB_CONNECT_TIMEOUT_S", 30, v => b.Timeout = v);
        Set("Command Timeout", "DB_COMMAND_TIMEOUT_S", 60, v => b.CommandTimeout = v);
        Set("Keepalive", "DB_KEEPALIVE_S", 30, v => b.KeepAlive = v);
        Set("Connection Idle Lifetime", "DB_IDLE_LIFETIME_S", 60, v => b.ConnectionIdleLifetime = v);
        if (b.ConnectionIdleLifetime < b.ConnectionPruningInterval)
            b.ConnectionPruningInterval = Math.Max(1, b.ConnectionIdleLifetime);

        if (production)
        {
            string host = b.Host ?? "";
            if (!Named("No Reset On Close") && host.Contains("-pooler", StringComparison.OrdinalIgnoreCase))
                b.NoResetOnClose = true;
            if (!Named("SSL Mode") && IsRemote(host))
                b.SslMode = SslMode.Require;
            if (!Named("Application Name"))
                b.ApplicationName = ApplicationName;
        }
        return b.ConnectionString;
    }

    private static string Norm(string key) => key.Replace(" ", "").ToLowerInvariant();

    private static readonly Dictionary<string, string[]> Synonyms = new[]
    {
        new[] { "maximumpoolsize", "maxpoolsize" },
        ["timeout"],
        ["commandtimeout"],
        ["keepalive"],
        ["connectionidlelifetime"],
        ["noresetonclose"],
        ["sslmode"],
        ["applicationname"],
    }.SelectMany(g => g.Select(k => (k, g))).ToDictionary(p => p.k, p => p.g);

    /// <summary>A TCP host other than this machine (not a unix socket folder, not localhost).</summary>
    public static bool IsRemote(string host) =>
        host.Length > 0 && !host.StartsWith('/') && !host.Contains(',')
        && host is not ("localhost" or "127.0.0.1" or "::1");
}
