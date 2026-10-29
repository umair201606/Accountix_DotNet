using System.IO.Compression;
using System.Text;
using Npgsql;

namespace Accountix.RefDb;

/// <summary>
/// Builds the reference database the integration tests run on, and its
/// per-module copies, from the fixture <c>tests/fixtures/reference.sql.gz</c>
/// (a plain-INSERT pg_dump of the database the Python app once built and
/// seeded). Needs only a Postgres server: no Python, no Postgres client tools.
/// </summary>
public static class ReferenceDatabase
{
    public const string BaseName = "accountix_ref";

    /// <summary>One copy per test project (ACCOUNTIX_TEST_DB=&lt;key&gt;), so projects tested side by side never see each other's writes.</summary>
    public static readonly IReadOnlyList<string> ModuleCopies =
    [
        "accounting", "finance", "executive", "inventory", "invoicing", "parties", "orders", "returns",
        "hr", "hr_records", "payroll", "fixed_assets", "fbr", "platform", "superadmin", "e2e",
    ];

    /// <summary>accountix_ref, or accountix_ref_&lt;key&gt;, behind an optional prefix (t_accountix_ref_hr).</summary>
    public static string DatabaseName(string prefix, string? key) =>
        prefix + BaseName + (string.IsNullOrEmpty(key) ? "" : "_" + key);

    /// <summary>Drops <paramref name="name"/> (ending its connections), creates it empty and loads the fixture.</summary>
    public static async Task CreateFromFixtureAsync(string server, string name, string fixturePath, TextWriter? log = null,
                                                    CancellationToken ct = default)
    {
        await DropAsync(server, name, ct);
        await CreateEmptyAsync(server, name, ct);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int n = await LoadAsync(Connection.ForDatabase(server, name), fixturePath, ct);
        log?.WriteLine($"  {name}: {n} statements from the fixture in {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// A copy of <paramref name="source"/> (CREATE DATABASE … TEMPLATE, a file copy). False when the
    /// source is in use (a test connected to it meanwhile): build that one from the fixture instead.
    /// </summary>
    public static async Task<bool> TryCopyAsync(string server, string source, string name, TextWriter? log = null,
                                                CancellationToken ct = default)
    {
        await DropAsync(server, name, ct);
        try
        {
            await AdminAsync(server, $"CREATE DATABASE {Quote(name)} TEMPLATE {Quote(source)}", ct);
            log?.WriteLine($"  {name}: copy of {source}");
            return true;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ObjectInUse)
        {
            log?.WriteLine($"  {name}: {source} is in use, building it from the fixture");
            return false;
        }
    }

    public static Task DropAsync(string server, string name, CancellationToken ct = default) =>
        AdminAsync(server, $"DROP DATABASE IF EXISTS {Quote(name)} WITH (FORCE)", ct);

    private static async Task CreateEmptyAsync(string server, string name, CancellationToken ct)
    {
        try
        {
            await AdminAsync(server, $"CREATE DATABASE {Quote(name)} TEMPLATE template0 ENCODING 'UTF8'", ct);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InvalidParameterValue)
        {
            // A server whose locale does not allow UTF8 on template0: take its default.
            await AdminAsync(server, $"CREATE DATABASE {Quote(name)}", ct);
        }
    }

    /// <summary>Runs every statement of the (gzipped) fixture in one transaction. Returns the number of statements.</summary>
    public static async Task<int> LoadAsync(string connectionString, string fixturePath, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        using var reader = OpenFixture(fixturePath);
        int total = 0;
        var batch = new NpgsqlBatch(conn, tx);
        foreach (string sql in Statements(reader))
        {
            batch.BatchCommands.Add(new NpgsqlBatchCommand(sql));
            total++;
            if (batch.BatchCommands.Count == 500)
            {
                await batch.ExecuteNonQueryAsync(ct);
                await batch.DisposeAsync();
                batch = new NpgsqlBatch(conn, tx);
            }
        }
        if (batch.BatchCommands.Count > 0)
            await batch.ExecuteNonQueryAsync(ct);
        await batch.DisposeAsync();
        await tx.CommitAsync(ct);
        return total;
    }

    /// <summary>The Alembic revision a database is at (null when it has none).</summary>
    public static async Task<string?> AlembicVersionAsync(string connectionString, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT CASE WHEN to_regclass('public.alembic_version') IS NULL THEN NULL " +
            "ELSE (SELECT version_num FROM public.alembic_version LIMIT 1) END", conn);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public static TextReader OpenFixture(string path)
    {
        Stream s = File.OpenRead(path);
        if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            s = new GZipStream(s, CompressionMode.Decompress);
        return new StreamReader(s, Encoding.UTF8);
    }

    /// <summary>
    /// The SQL statements of a plain-format pg_dump, without their terminating semicolons.
    /// Knows single-quoted literals ('' inside; E'' with backslash escapes), quoted
    /// identifiers, dollar-quoted bodies, -- and /* */ comments, and skips psql
    /// meta-commands (lines starting with a backslash, e.g. \restrict in newer dumps).
    /// </summary>
    public static IEnumerable<string> Statements(TextReader reader)
    {
        string text = reader.ReadToEnd();
        var sb = new StringBuilder();
        int i = 0, n = text.Length;
        bool lineStart = true;
        while (i < n)
        {
            char c = text[i];
            if (lineStart && c == '\\' && IsBlank(sb))
            {
                while (i < n && text[i] != '\n') i++;
                continue;
            }
            lineStart = false;
            if (c == '\n')
            {
                sb.Append(c);
                i++;
                lineStart = true;
                continue;
            }
            if (c == '-' && i + 1 < n && text[i + 1] == '-')
            {
                while (i < n && text[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? n : end + 2;
                sb.Append(' ');
                continue;
            }
            if (c == '\'')
            {
                bool escapes = i > 0 && (text[i - 1] == 'E' || text[i - 1] == 'e') && (i < 2 || !IsIdent(text[i - 2]));
                int start = i++;
                while (i < n)
                {
                    if (escapes && text[i] == '\\') { i += 2; continue; }
                    if (text[i] == '\'')
                    {
                        if (i + 1 < n && text[i + 1] == '\'') { i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                sb.Append(text, start, Math.Min(i, n) - start);
                continue;
            }
            if (c == '"')
            {
                int start = i++;
                while (i < n)
                {
                    if (text[i] == '"')
                    {
                        if (i + 1 < n && text[i + 1] == '"') { i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                sb.Append(text, start, Math.Min(i, n) - start);
                continue;
            }
            if (c == '$' && (i == 0 || !IsIdent(text[i - 1])))
            {
                int j = i + 1;
                while (j < n && (char.IsLetterOrDigit(text[j]) || text[j] == '_')) j++;
                if (j < n && text[j] == '$' && (j == i + 1 || !char.IsDigit(text[i + 1])))
                {
                    string tag = text[i..(j + 1)];
                    int end = text.IndexOf(tag, j + 1, StringComparison.Ordinal);
                    int stop = end < 0 ? n : end + tag.Length;
                    sb.Append(text, i, stop - i);
                    i = stop;
                    continue;
                }
            }
            if (c == ';')
            {
                string stmt = sb.ToString().Trim();
                sb.Clear();
                i++;
                if (stmt.Length > 0)
                    yield return stmt;
                continue;
            }
            sb.Append(c);
            i++;
        }
        string last = sb.ToString().Trim();
        if (last.Length > 0)
            yield return last;
    }

    private static bool IsBlank(StringBuilder sb)
    {
        for (int k = 0; k < sb.Length; k++)
            if (!char.IsWhiteSpace(sb[k]))
                return false;
        return true;
    }

    private static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

    private static async Task AdminAsync(string server, string sql, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(Connection.ForDatabase(server, "postgres"));
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 300 };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Quote(string name)
    {
        if (name.Length == 0 || name.Any(ch => !(char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch == '_')))
            throw new ArgumentException($"Database names are lower-case letters, digits and _ only: '{name}'");
        return "\"" + name + "\"";
    }
}

/// <summary>Connection strings in the form tools/reference.json has always used.</summary>
public static class Connection
{
    /// <summary>
    /// The server to build on: a key/value connection string ("Host=…;Username=…") or a
    /// postgresql:// URL. The database named in it, if any, is ignored.
    /// </summary>
    public static NpgsqlConnectionStringBuilder Server(string server)
    {
        server = server.Trim();
        var b = server.Contains("://", StringComparison.Ordinal) ? FromUrl(server) : new NpgsqlConnectionStringBuilder(server);
        if (string.IsNullOrEmpty(b.Host))
            b.Host = "localhost";
        if (string.IsNullOrEmpty(b.Username))
            b.Username = "postgres";
        return b;
    }

    /// <summary>"Host=…;Database=…;Username=…[;Port=…][;Password=…]", unpooled (CREATE … TEMPLATE needs the source idle).</summary>
    public static string ForDatabase(string server, string database) => Format(Server(server), database) + ";Pooling=false";

    /// <summary>The string written to reference.json (pooled; what the tests use).</summary>
    public static string Format(NpgsqlConnectionStringBuilder s, string database)
    {
        var parts = new List<string> { $"Host={s.Host}", $"Database={database}", $"Username={s.Username}" };
        if (s.Port != 5432 && s.Port > 0)
            parts.Add($"Port={s.Port}");
        if (!string.IsNullOrEmpty(s.Password))
            parts.Add($"Password={s.Password}");
        return string.Join(";", parts);
    }

    /// <summary>postgresql://user:pass@host:port/db, or postgresql://user:@/db?host=/socket for a local socket.</summary>
    public static string Url(NpgsqlConnectionStringBuilder s, string database)
    {
        string user = Uri.EscapeDataString(s.Username ?? "postgres");
        string pass = Uri.EscapeDataString(s.Password ?? "");
        string host = s.Host ?? "localhost";
        if (host.StartsWith('/'))
            return $"postgresql://{user}:{pass}@/{database}?host={host}" + (s.Port != 5432 && s.Port > 0 ? $"&port={s.Port}" : "");
        return $"postgresql://{user}:{pass}@{host}:{(s.Port > 0 ? s.Port : 5432)}/{database}";
    }

    private static NpgsqlConnectionStringBuilder FromUrl(string url)
    {
        int scheme = url.IndexOf("://", StringComparison.Ordinal);
        string rest = url[(scheme + 3)..];
        string query = "";
        int q = rest.IndexOf('?');
        if (q >= 0)
        {
            query = rest[(q + 1)..];
            rest = rest[..q];
        }
        var b = new NpgsqlConnectionStringBuilder();
        int at = rest.LastIndexOf('@');
        if (at >= 0)
        {
            string[] up = rest[..at].Split(':', 2);
            b.Username = Uri.UnescapeDataString(up[0]);
            if (up.Length > 1 && up[1].Length > 0)
                b.Password = Uri.UnescapeDataString(up[1]);
            rest = rest[(at + 1)..];
        }
        int slash = rest.IndexOf('/');
        string hostPort = slash < 0 ? rest : rest[..slash];
        if (hostPort.Length > 0)
        {
            int colon = hostPort.LastIndexOf(':');
            if (colon > 0 && int.TryParse(hostPort[(colon + 1)..], out int port))
            {
                b.Host = hostPort[..colon];
                b.Port = port;
            }
            else
                b.Host = hostPort;
        }
        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] kv = pair.Split('=', 2);
            string v = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
            switch (kv[0].ToLowerInvariant())
            {
                case "host": b.Host = v; break;
                case "port" when int.TryParse(v, out int p): b.Port = p; break;
                case "user": b.Username = v; break;
                case "password": b.Password = v; break;
            }
        }
        return b;
    }
}
