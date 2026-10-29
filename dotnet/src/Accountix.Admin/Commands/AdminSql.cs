using System.Data.Common;
using Accountix.Executive.Services;
using Microsoft.EntityFrameworkCore.Storage;

namespace Accountix.Admin.Commands;

/// <summary>Raw SQL on the command's DbContext connection and transaction, and the other helpers the commands share.</summary>
internal static class AdminSql
{
    private static DbCommand Command(AccountixDbContext db, string sql, object?[] args)
    {
        var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        for (int i = 0; i < args.Length; i++)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = "p" + i;
            p.Value = args[i] ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    private static async Task OpenAsync(AccountixDbContext db, CancellationToken ct)
    {
        if (db.Database.GetDbConnection().State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);
    }

    /// <summary>Rows affected. Parameters are @p0, @p1, ...</summary>
    public static async Task<int> ExecAsync(this AccountixDbContext db, string sql, CancellationToken ct, params object?[] args)
    {
        await OpenAsync(db, ct);
        await using var cmd = Command(db, sql, args);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Every row, each as its column values (DBNull → null).</summary>
    public static async Task<List<object?[]>> RowsAsync(this AccountixDbContext db, string sql, CancellationToken ct, params object?[] args)
    {
        await OpenAsync(db, ct);
        await using var cmd = Command(db, sql, args);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<object?[]>();
        while (await r.ReadAsync(ct))
        {
            var row = new object?[r.FieldCount];
            for (int i = 0; i < r.FieldCount; i++)
                row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    public static async Task<object?> ScalarAsync(this AccountixDbContext db, string sql, CancellationToken ct, params object?[] args)
    {
        await OpenAsync(db, ct);
        await using var cmd = Command(db, sql, args);
        object? v = await cmd.ExecuteScalarAsync(ct);
        return v is DBNull ? null : v;
    }

    /// <summary>
    /// integrity.failures() for one company: a fresh BooksIntegrity each time (it keeps the
    /// ledger it read), in the command's transaction, so it sees the writes made so far.
    /// </summary>
    public static async Task<List<IntegrityCheck>> FailuresAsync(IServiceProvider sp, int companyId, CancellationToken ct)
    {
        var db = sp.GetRequiredService<AccountixDbContext>();
        db.Company.Set(companyId);
        db.ChangeTracker.Clear();
        return await ActivatorUtilities.CreateInstance<BooksIntegrity>(sp).FailuresAsync(ct);
    }

    /// <summary>"label: expected 1 got 2 (detail)", as the Python tools print a failing check.</summary>
    public static string Describe(IEnumerable<IntegrityCheck> failures) =>
        string.Join("; ", failures.Select(f => $"{f.Label}: expected {Num(f.Expected)} got {Num(f.Actual)} ({f.Detail})"));

    /// <summary>Python's {x:,.2f}.</summary>
    public static string Money(decimal v) => v.ToString("#,##0.00", CultureInfo.InvariantCulture);

    public static string Num(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Python's list repr of ints: [1, 2, 3].</summary>
    public static string List(IEnumerable<int> ids) => "[" + string.Join(", ", ids) + "]";

    /// <summary>Companies in id order (companies is a platform table: no company filter).</summary>
    public static async Task<List<(int Id, string Name)>> CompaniesAsync(AccountixDbContext db, CancellationToken ct) =>
        (await db.Companies.AsNoTracking().OrderBy(c => c.Id).Select(c => new { c.Id, c.Name }).ToListAsync(ct))
        .Select(c => (c.Id, c.Name)).ToList();

    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public static decimal Dec(object? v) => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}
