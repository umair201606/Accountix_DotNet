using System.Globalization;
using System.Reflection;
using Accountix.Core.Data;
using Accountix.Executive.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Accountix.Load;

public sealed record CheckResult(string Name, bool Ok, string Detail);

/// <summary>
/// The database after (or before) a run: deploy/checks/data_checks.sql
/// (each block returns the rows that are wrong) and, when asked, the app's own
/// Books Integrity (Executive's BooksIntegrity, the port of shared/integrity.py
/// run_all, whose findings were checked word for word against Python's) on the
/// same database.
/// </summary>
public static class DataChecks
{
    public static IReadOnlyList<(string Name, string Description, string Sql)> Blocks()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("data_checks.sql")!;
        string all = new StreamReader(s).ReadToEnd();
        return all.Split("-- check: ").Skip(1).Select(block =>
        {
            string header = block[..block.IndexOf('\n')];
            string[] hd = header.Split('|', 2, StringSplitOptions.TrimEntries);
            return (hd[0], hd.Length > 1 ? hd[1] : "", block[(block.IndexOf('\n') + 1)..]);
        }).ToList();
    }

    public static async Task<List<CheckResult>> RunAsync(string connectionString, int companyId, bool booksIntegrity)
    {
        var results = new List<CheckResult>();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        foreach (var (name, description, sql) in Blocks())
        {
            string text = sql.Replace(":company_id", companyId.ToString(CultureInfo.InvariantCulture));
            try
            {
                await using var cmd = new NpgsqlCommand(text, conn) { CommandTimeout = 300 };
                await using var reader = await cmd.ExecuteReaderAsync();
                var bad = new List<string>();
                do
                {
                    if (reader.FieldCount == 0) continue;
                    while (await reader.ReadAsync())
                        if (bad.Count < 5)
                            bad.Add(string.Join(" ", Enumerable.Range(0, reader.FieldCount)
                                .Select(i => $"{reader.GetName(i)}={(reader.IsDBNull(i) ? "null" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))}")));
                        else
                            bad.Add("…");
                } while (await reader.NextResultAsync());
                results.Add(new CheckResult(name, bad.Count == 0, bad.Count == 0 ? description : $"{bad.Count} wrong: " + string.Join(" | ", bad.Take(5))));
            }
            catch (PostgresException e)
            {
                results.Add(new CheckResult(name, false, "could not run: " + e.MessageText));
                await using var rb = new NpgsqlCommand("ROLLBACK", conn);
                try { await rb.ExecuteNonQueryAsync(); } catch (PostgresException) { }
            }
        }
        if (booksIntegrity)
            results.AddRange(await BooksIntegrityAsync(connectionString, companyId));
        return results;
    }

    /// <summary>Books Integrity (every check of the Executive page) for the company, on the same database.</summary>
    private static async Task<IEnumerable<CheckResult>> BooksIntegrityAsync(string connectionString, int companyId)
    {
        var company = new CompanyContext();
        company.Set(companyId);
        await using var db = new AccountixDbContext(new DbContextOptionsBuilder<AccountixDbContext>()
            .UseNpgsql(connectionString, o => o.CommandTimeout(300)).Options, company);
        try
        {
            return (await new BooksIntegrity(db).RunAllAsync()).Select(c => new CheckResult(
                "integrity:" + c.Key, c.Ok,
                c.Ok ? c.Label : string.Create(CultureInfo.InvariantCulture, $"expected {c.Expected} actual {c.Actual} {c.Detail}"))).ToList();
        }
        catch (Exception e) when (e is PostgresException or InvalidOperationException or DbUpdateException)
        {
            return [new CheckResult("books_integrity", false, "could not run: " + e.Message)];
        }
    }

    /// <summary>pg_stat_database.deadlocks for this database (cumulative).</summary>
    public static async Task<long> DeadlocksAsync(NpgsqlDataSource ds)
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT pg_stat_clear_snapshot(); SELECT deadlocks FROM pg_stat_database WHERE datname = current_database()", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.NextResultAsync();
        await r.ReadAsync();
        return r.GetInt64(0);
    }
}
