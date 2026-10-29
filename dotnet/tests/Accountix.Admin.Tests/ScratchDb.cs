using Npgsql;

namespace Accountix.Admin.Tests;

/// <summary>
/// A throwaway database on the reference server: a copy of the template st_admin_tpl (itself a
/// copy of the reference database, re-made when that one's Alembic revision changes). The
/// commands write to it freely; it is dropped on dispose. The reference databases are only read.
/// </summary>
internal sealed class ScratchDb : IAsyncDisposable
{
    public const string Prefix = "st_admin_";
    public const string Template = "st_admin_tpl";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _templateChecked;

    private ScratchDb(string name)
    {
        Name = name;
        ConnectionString = On(name);
    }

    public string Name { get; }
    public string ConnectionString { get; }

    /// <summary>postgresql://... for the Python app (SQLAlchemy), on the same server.</summary>
    public string DatabaseUrl
    {
        get
        {
            var b = new NpgsqlConnectionStringBuilder(ConnectionString);
            string auth = Uri.EscapeDataString(b.Username ?? "postgres") + ":" + Uri.EscapeDataString(b.Password ?? "");
            string host = b.Host ?? "localhost";
            return host.StartsWith('/')
                ? $"postgresql://{auth}@/{Name}?host={host}&port={b.Port}"
                : $"postgresql://{auth}@{host}:{b.Port}/{Name}";
        }
    }

    public AccountixDbContext NewDb(int? companyId = null)
    {
        var company = new CompanyContext();
        company.Set(companyId);
        return new AccountixDbContext(new DbContextOptionsBuilder<AccountixDbContext>().UseNpgsql(ConnectionString).Options, company);
    }

    private static string On(string database) =>
        new NpgsqlConnectionStringBuilder(Reference.ConnectionString) { Database = database }.ConnectionString;

    private static async Task ExecAsync(string database, string sql)
    {
        await using var c = new NpgsqlConnection(On(database));
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(string database, string sql)
    {
        await using var c = new NpgsqlConnection(On(database));
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        object? v = await cmd.ExecuteScalarAsync();
        return v is DBNull ? null : v;
    }

    public static async Task<ScratchDb> CreateAsync()
    {
        await EnsureTemplateAsync();
        return await CopyAsync(Template);
    }

    /// <summary>A second database with this one's rows, ids and sequences (to run two tools on the same input).</summary>
    public Task<ScratchDb> CloneAsync() => CopyAsync(Name);

    private static async Task<ScratchDb> CopyAsync(string source)
    {
        string name = Prefix + Guid.NewGuid().ToString("N")[..12];
        // A template must have no open connections: give back the idle ones this process holds.
        NpgsqlConnection.ClearAllPools();
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await ExecAsync("postgres", $"CREATE DATABASE \"{name}\" TEMPLATE \"{source}\"");
                return new ScratchDb(name);
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ObjectInUse && attempt < 30)
            {
                await Task.Delay(1000);
            }
        }
    }

    /// <summary>
    /// st_admin_tpl at the reference database's revision: made once per machine (and again when the
    /// reference database moves on). Copying needs the reference database to be idle for a moment;
    /// leftovers of crashed runs (st_admin_* nobody is connected to) are dropped first.
    /// </summary>
    private static async Task EnsureTemplateAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_templateChecked)
                return;
            string reference = new NpgsqlConnectionStringBuilder(Reference.ConnectionString).Database!;
            await using (var c = new NpgsqlConnection(On("postgres")))
            {
                await c.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    "SELECT d.datname FROM pg_database d WHERE d.datname LIKE 'st\\_admin\\_%' AND d.datname <> @t " +
                    "AND NOT EXISTS (SELECT 1 FROM pg_stat_activity a WHERE a.datname = d.datname)", c);
                cmd.Parameters.AddWithValue("t", Template);
                var stale = new List<string>();
                await using (var r = await cmd.ExecuteReaderAsync())
                    while (await r.ReadAsync())
                        stale.Add(r.GetString(0));
                foreach (string db in stale)
                    await ExecAsync("postgres", $"DROP DATABASE IF EXISTS \"{db}\" WITH (FORCE)");
            }
            string? want = (string?)await ScalarAsync(reference, "SELECT version_num FROM alembic_version");
            bool exists = await ScalarAsync("postgres", $"SELECT 1 FROM pg_database WHERE datname = '{Template}'") is not null;
            string? have = exists ? (string?)await ScalarAsync(Template, "SELECT version_num FROM alembic_version") : null;
            if (!exists || have != want)
            {
                if (exists)
                    await ExecAsync("postgres", $"DROP DATABASE \"{Template}\" WITH (FORCE)");
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        await ExecAsync("postgres", $"CREATE DATABASE \"{Template}\" TEMPLATE \"{reference}\"");
                        break;
                    }
                    catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ObjectInUse && attempt < 60)
                    {
                        await Task.Delay(1000);     // someone else is in the reference database: wait
                    }
                }
            }
            _templateChecked = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await ExecAsync("postgres", $"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)");
    }
}
