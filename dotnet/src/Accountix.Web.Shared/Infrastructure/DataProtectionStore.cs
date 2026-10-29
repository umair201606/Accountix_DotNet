using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Accountix.Web.Infrastructure;

/// <summary>
/// The Data Protection key ring kept in Postgres, so every instance (and
/// every deployment slot) protects and reads antiforgery tokens and the
/// flash (TempData) cookie with the same keys. Without it each instance
/// makes its own keys: a form rendered by one instance and posted to
/// another is refused (400), and a flash set before a redirect is lost.
///
/// The table, <c>dotnet_data_protection_keys</c>, is created by migration
/// 0017_dotnet_data_protection (src/Accountix.Migrations; first written as
/// the Python app's Alembic revision 0017), which runs before the app starts.
/// docs/DEPLOY.md explains why Postgres rather than Blob Storage.
/// </summary>
public sealed class PostgresXmlRepository(NpgsqlDataSource dataSource) : IXmlRepository
{
    public const string Table = "dotnet_data_protection_keys";

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var conn = dataSource.OpenConnection();
        using var cmd = new NpgsqlCommand($"SELECT xml FROM {Table} ORDER BY id", conn);
        using var reader = cmd.ExecuteReader();
        var list = new List<XElement>();
        while (reader.Read())
            list.Add(XElement.Parse(reader.GetString(0)));
        return list;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var conn = dataSource.OpenConnection();
        using var cmd = new NpgsqlCommand(
            $"INSERT INTO {Table} (friendly_name, xml, created_at) VALUES (@n, @x, @t) " +
            "ON CONFLICT (friendly_name) DO NOTHING", conn);
        cmd.Parameters.AddWithValue("n", friendlyName);
        cmd.Parameters.AddWithValue("x", element.ToString(SaveOptions.DisableFormatting));
        cmd.Parameters.AddWithValue("t", DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified));
        cmd.ExecuteNonQuery();
    }
}

public static class DataProtectionStore
{
    /// <summary>Same value on every instance and slot: keys made by one are used by all.</summary>
    public const string ApplicationName = "accountix";

    /// <summary>pg_advisory_xact_lock key for the first key's creation (Python's migration lock is 815204117).</summary>
    public const long WarmupLockKey = 815_204_118;

    /// <summary>
    /// DATA_PROTECTION_STORE: "postgres" (the default outside Development)
    /// or "none" (ASP.NET's per-machine default; one instance only).
    /// DATA_PROTECTION_CERT_PATH (+ _PASSWORD): a .pfx/.p12 whose key
    /// encrypts the keys at rest; on App Service, a Key Vault certificate
    /// loaded with WEBSITE_LOAD_CERTIFICATES lands in /var/ssl/private.
    /// DATA_PROTECTION_OLD_CERT_PATHS: earlier certificates (comma-separated)
    /// kept for reading keys written before a rotation.
    /// </summary>
    public static void Add(IServiceCollection services, IConfiguration config, string connectionString, bool production)
    {
        string store = (config["DATA_PROTECTION_STORE"] ?? (production ? "postgres" : "none")).Trim().ToLowerInvariant();
        if (store == "none")
            return;
        if (store != "postgres")
            throw new InvalidOperationException($"DATA_PROTECTION_STORE '{store}' is not supported (postgres or none).");

        var cs = new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = 2, MinPoolSize = 0 };
        var dataSource = NpgsqlDataSource.Create(cs.ConnectionString);
        services.AddSingleton(new DataProtectionKeySource(dataSource));
        var dp = services.AddDataProtection().SetApplicationName(ApplicationName);
        services.Configure<KeyManagementOptions>(o => o.XmlRepository = new PostgresXmlRepository(dataSource));

        if (config["DATA_PROTECTION_CERT_PATH"] is { Length: > 0 } certPath)
        {
            var cert = LoadCertificate(certPath, config["DATA_PROTECTION_CERT_PASSWORD"]);
            dp.ProtectKeysWithCertificate(cert);
            var readers = new List<X509Certificate2> { cert };
            foreach (string old in (config["DATA_PROTECTION_OLD_CERT_PATHS"] ?? "")
                         .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                readers.Add(LoadCertificate(old, config["DATA_PROTECTION_CERT_PASSWORD"]));
            dp.UnprotectKeysWithAnyCertificate([.. readers]);
        }
        services.AddHostedService<KeyRingWarmup>();
    }

    private static X509Certificate2 LoadCertificate(string path, string? password) =>
        X509CertificateLoader.LoadPkcs12FromFile(path, password,
            X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
}

/// <summary>The key store's own small connection pool (2): it is read at start-up and about once a day.</summary>
public sealed record DataProtectionKeySource(NpgsqlDataSource DataSource);

/// <summary>
/// Loads (or, on a fresh database, creates) the key ring before the app
/// takes requests. Creation runs under a transaction-scoped advisory lock,
/// so instances starting together make ONE first key instead of one each
/// (a token protected with a key the other instance has not loaded yet
/// would be refused). Transaction-scoped, so it also works through Neon's
/// pooled endpoint. A missing table stops the start with the fix in words.
/// </summary>
public sealed class KeyRingWarmup(DataProtectionKeySource source, IDataProtectionProvider provider, ILogger<KeyRingWarmup> log)
    : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await using var conn = await source.DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await using (var exists = new NpgsqlCommand($"SELECT to_regclass('{PostgresXmlRepository.Table}') IS NOT NULL", conn, tx))
                if (await exists.ExecuteScalarAsync(ct) is not true)
                    throw new InvalidOperationException(
                        $"Table {PostgresXmlRepository.Table} is missing. Run the schema migrations " +
                        "(`dotnet Accountix.Web.dll migrate`, or Accountix:MigrateOnStart=true, the default in " +
                        "Production; the table comes with 0017_dotnet_data_protection), or set " +
                        "DATA_PROTECTION_STORE=none for a single instance.");
            await using (var lk = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({DataProtectionStore.WarmupLockKey})", conn, tx))
                await lk.ExecuteNonQueryAsync(ct);
            // Protecting once makes the key manager read the ring and, when it
            // is empty, write the first key (on its own connection).
            provider.CreateProtector("Accountix.KeyRingWarmup").Protect([1]);
            await tx.CommitAsync(ct);
            log.LogInformation("Data Protection key ring loaded from {Table}", PostgresXmlRepository.Table);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
