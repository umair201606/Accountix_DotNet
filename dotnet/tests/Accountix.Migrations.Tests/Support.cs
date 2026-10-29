using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

// Tests create their own databases, but the Python-parity ones start processes
// that use a few connections each; one at a time keeps the shared server calm.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace Accountix.Migrations.Tests;

/// <summary>
/// The Postgres server the tests make their throwaway databases (mig_t_*) on:
/// ACCOUNTIX_MIGRATIONS_PG (a connection string with rights to create
/// databases), else the server named in dotnet/tools/reference.json. Without
/// either, the database tests are skipped.
/// </summary>
public static class Pg
{
    private static readonly Lazy<string?> Admin = new(() =>
    {
        string? cs = Environment.GetEnvironmentVariable("ACCOUNTIX_MIGRATIONS_PG");
        if (string.IsNullOrWhiteSpace(cs))
        {
            string? file = FindUp(Path.Combine("dotnet", "tools", "reference.json"));
            if (file is null)
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            cs = doc.RootElement.GetProperty("connection_string").GetString();
        }
        var b = new NpgsqlConnectionStringBuilder(cs) { Database = "postgres", Pooling = false };
        try
        {
            using var conn = new NpgsqlConnection(b.ConnectionString);
            conn.Open();
            return b.ConnectionString;
        }
        catch (NpgsqlException)
        {
            return null;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return null;
        }
    });

    public static string? AdminConnectionString => Admin.Value;

    public static string ConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(AdminConnectionString!) { Database = database, Pooling = false }.ConnectionString;

    public static async Task ExecAsync(string database, string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString(database));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 0 };
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<object?> ScalarAsync(string database, string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString(database));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var v = await cmd.ExecuteScalarAsync();
        return v is DBNull ? null : v;
    }

    public static async Task<List<string>> ColumnAsync(string database, string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString(database));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<string>();
        while (await r.ReadAsync())
            list.Add(r.IsDBNull(0) ? "" : Convert.ToString(r.GetValue(0))!);
        return list;
    }

    /// <summary>Settings every client prints rows with when digests are compared (table_digests.sql).</summary>
    public const string Session =
        "SET DateStyle = 'ISO, MDY'; SET TimeZone = 'UTC'; SET extra_float_digits = 1; " +
        "SET bytea_output = 'hex'; SET IntervalStyle = 'postgres'; SET search_path = public;";

    /// <summary>The single text value the last statement of a fixture SQL file returns, as JSON.</summary>
    public static async Task<JsonNode> JsonAsync(string database, string fixtureSql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString(database));
        await conn.OpenAsync();
        await using (var set = new NpgsqlCommand(Session, conn))
            await set.ExecuteNonQueryAsync();
        await using var cmd = new NpgsqlCommand(Fixtures.Text(fixtureSql), conn) { CommandTimeout = 0 };
        await using var r = await cmd.ExecuteReaderAsync();
        string? last = null;
        do
        {
            while (await r.ReadAsync())
                last = r.GetString(0);
        } while (await r.NextResultAsync());
        return JsonNode.Parse(last!)!;
    }

    public static Task<JsonNode> DigestsAsync(string database) => JsonAsync(database, "table_digests.sql");

    public static Task<JsonNode> AccessRowsAsync(string database) => JsonAsync(database, "access_rows.sql");

    public static Task<JsonNode> SchemaAsync(string database) => SchemaDescription.DescribeAsync(ConnectionString(database));

    /// <summary>A file in this directory or one above, from the build output or from the source tree (builds may go elsewhere).</summary>
    public static string? FindUp(string relative)
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(SourceFile())! })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                string p = Path.Combine(dir.FullName, relative);
                if (File.Exists(p))
                    return p;
            }
        return null;
    }

    private static string SourceFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}

/// <summary>A database of its own, dropped afterwards (even when the test fails).</summary>
public sealed class TempDb : IAsyncDisposable
{
    public string Name { get; } = "mig_t_" + Guid.NewGuid().ToString("N")[..10];
    public string ConnectionString => Pg.ConnectionString(Name);

    private TempDb() { }

    public static async Task<TempDb> CreateAsync()
    {
        var db = new TempDb();
        await Pg.ExecAsync("postgres", $"CREATE DATABASE \"{db.Name}\" TEMPLATE template0");
        return db;
    }

    /// <summary>A database the Python app at Alembic 0015 built (Fixtures/db_0015.sql.gz).</summary>
    public static async Task<TempDb> From0015Async()
    {
        var db = await CreateAsync();
        await Pg.ExecAsync(db.Name, Fixtures.Dump0015);
        return db;
    }

    public Migrator Migrator(MigratorOptions? options = null) => new(ConnectionString, options);

    public Task ExecAsync(string sql) => Pg.ExecAsync(Name, sql);
    public Task<object?> ScalarAsync(string sql) => Pg.ScalarAsync(Name, sql);
    public Task<List<string>> ColumnAsync(string sql) => Pg.ColumnAsync(Name, sql);

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await Pg.ExecAsync("postgres", $"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)");
    }
}

public static class Fixtures
{
    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static string Text(string name) => File.ReadAllText(Path(name));

    public static JsonNode Json(string name) => JsonNode.Parse(Text(name))!;

    private static readonly Lazy<string> Dump = new(() =>
    {
        using var gz = new GZipStream(File.OpenRead(Path("db_0015.sql.gz")), CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        return reader.ReadToEnd();
    });

    public static string Dump0015 => Dump.Value;
}

/// <summary>Needs the test Postgres server.</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (Pg.AdminConnectionString is null)
            Skip = "No Postgres for the migration tests (set ACCOUNTIX_MIGRATIONS_PG, or build tools/reference.json).";
    }
}



public static class AssertSchema
{
    public static void Same(JsonNode expected, JsonNode actual, string what)
    {
        var diffs = SchemaDescription.Differences(expected, actual, 60);
        Assert.True(diffs.Count == 0, $"{what}: {diffs.Count} difference(s)\n" + string.Join("\n", diffs));
    }
}
