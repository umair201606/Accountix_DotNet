using Microsoft.Extensions.Logging;
using Npgsql;

namespace Accountix.Migrations;

/// <summary>The migrator's one connection and transaction, as a migration sees it.</summary>
public sealed class MigrationContext
{
    private readonly List<string> _warnings;

    internal MigrationContext(NpgsqlConnection connection, NpgsqlTransaction transaction, ILogger log, List<string> warnings)
    {
        Connection = connection;
        Transaction = transaction;
        Log = log;
        _warnings = warnings;
    }

    public NpgsqlConnection Connection { get; }
    public NpgsqlTransaction Transaction { get; }
    public ILogger Log { get; }

    /// <summary>Runs SQL (one or more statements). No time limit: an index build on a big table may take a while.</summary>
    public async Task<int> ExecuteAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var cmd = Command(sql, parameters);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<object?> ScalarAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var cmd = Command(sql, parameters);
        object? v = await cmd.ExecuteScalarAsync(ct);
        return v is DBNull ? null : v;
    }

    public async Task<List<string>> QueryStringsAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var cmd = Command(sql, parameters);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<string>();
        while (await reader.ReadAsync(ct))
            list.Add(reader.IsDBNull(0) ? "" : Convert.ToString(reader.GetValue(0))!);
        return list;
    }

    public async Task<bool> TableExistsAsync(string table, CancellationToken ct) =>
        await ScalarAsync("SELECT to_regclass(@t) IS NOT NULL", ct, ("t", "public." + QuoteIdent(table))) is true;

    public Task<List<string>> ColumnsAsync(string table, CancellationToken ct) =>
        QueryStringsAsync("""
            SELECT a.attname FROM pg_attribute a
             WHERE a.attrelid = to_regclass(@t) AND a.attnum > 0 AND NOT a.attisdropped
             ORDER BY a.attnum
            """, ct, ("t", "public." + QuoteIdent(table)));

    /// <summary>Something the person running the migration should read (also logged).</summary>
    public void Warn(string message)
    {
        _warnings.Add(message);
        Log.LogWarning("{Message}", message);
    }

    internal NpgsqlCommand Command(string sql, (string Name, object? Value)[] parameters)
    {
        var cmd = new NpgsqlCommand(sql, Connection, Transaction) { CommandTimeout = 0 };
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private static string QuoteIdent(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
}
