using Accountix.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Accountix.Migrations;

/// <summary>Who to create as the first super admin of a database that has none (Accountix:FirstAdmin:*).</summary>
public sealed class MigratorOptions
{
    public string? FirstAdminEmail { get; init; }
    public string? FirstAdminPassword { get; init; }
    public string? FirstAdminName { get; init; }

    /// <summary>
    /// Apply forward migrations up to and including this one, and no further
    /// (comparisons with a database Python upgraded to 0018, after 0019+ exist).
    /// Null: everything.
    /// </summary>
    public string? StopAfter { get; init; }

    /// <summary>Tests: runs once the lock is held, before anything is read.</summary>
    internal Func<CancellationToken, Task>? AfterLock { get; init; }
}

/// <summary>What a run found and did.</summary>
public sealed record MigrationReport(
    string StartedFrom,
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Pending,
    string? FirstAdminCreated,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Nothing was written.</summary>
    public bool NothingDone => Applied.Count == 0 && FirstAdminCreated is null;
}

/// <summary>The database is not one this build may migrate; the message says what to do.</summary>
public sealed class MigrationRefusedException(string message) : Exception(message);

/// <summary>
/// Brings a database to the schema this build expects. Safe to run on every
/// start, on every instance at once:
/// <list type="bullet">
/// <item>One transaction, holding <c>pg_advisory_xact_lock(815204117)</c> (the Python
/// app's key, so even a Python instance from the backup queues behind it), with
/// <c>SET LOCAL lock_timeout = 0</c>: an instance queued behind the one
/// migrating waits, then finds nothing to do. The lock is transaction-scoped,
/// so it works through Neon's pooled endpoint.</item>
/// <item>Any failure rolls the whole run back.</item>
/// <item><c>schema_migrations</c> records what ran (version, how, checksum, time).</item>
/// <item>A database the Python app built is adopted: <c>alembic_version</c> says how
/// far Alembic got, and everything up to there counts as applied. Below
/// <see cref="MigrationCatalog.MinimumAlembic"/> it is refused. Adoption is
/// written down only together with the first change, so starting on a
/// database that is already at head writes nothing at all.</item>
/// <item>An empty database gets the baseline (the schema at Alembic 0018 and the rows
/// a new installation needs) and, when configured, its first super admin.</item>
/// <item><c>alembic_version</c> follows the ported revisions (0016–0018), so the Python app
/// from the backup still reads the database correctly if it is ever needed.</item>
/// </list>
/// </summary>
public sealed class Migrator(string connectionString, MigratorOptions? options = null, ILogger? log = null)
{
    /// <summary>shared/db_migrate.py ADVISORY_LOCK_KEY.</summary>
    public const long AdvisoryLockKey = 815_204_117;

    public const string RecordTable = "schema_migrations";

    private readonly MigratorOptions _options = options ?? new MigratorOptions();
    private readonly ILogger _log = log ?? NullLogger.Instance;

    /// <summary>Applies what is pending.</summary>
    public Task<MigrationReport> MigrateAsync(CancellationToken ct = default) => RunAsync(apply: true, ct);

    /// <summary>What would be applied; changes nothing.</summary>
    public Task<MigrationReport> StatusAsync(CancellationToken ct = default) => RunAsync(apply: false, ct);

    private async Task<MigrationReport> RunAsync(bool apply, CancellationToken ct)
    {
        var firstAdmin = FirstAdmin.From(_options);
        var csb = new NpgsqlConnectionStringBuilder(connectionString)
        {
            // Its own connection, closed at the end; nothing kept in a pool.
            Pooling = false,
            CommandTimeout = 0,
        };
        if (string.IsNullOrEmpty(csb.ApplicationName))
            csb.ApplicationName = "accountix-migrate";

        await using var conn = new NpgsqlConnection(csb.ConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var warnings = new List<string>();
        var db = new MigrationContext(conn, tx, _log, warnings);
        try
        {
            // As migrations/env.py: wait as long as it takes for the instance migrating,
            // or for requests in flight that hold a table an ALTER needs.
            await db.ExecuteAsync("SET LOCAL lock_timeout = 0", ct);
            await db.ExecuteAsync($"SELECT pg_advisory_xact_lock({AdvisoryLockKey})", ct);
            if (_options.AfterLock is { } hook)
                await hook(ct);

            var state = await InspectAsync(db, ct);
            var pending = Upto(_options.StopAfter)
                .Where(m => state.Fresh ? !MigrationCatalog.AlembicChain.Contains(m.Id) : !state.Applied.Contains(m.Id))
                .ToList();
            var pendingNames = new List<string>();
            if (state.Fresh)
                pendingNames.Add($"baseline ({MigrationCatalog.BaselineVersion})");
            pendingNames.AddRange(pending.Select(m => m.Id));

            if (!apply)
            {
                await tx.RollbackAsync(ct);
                return new MigrationReport(state.From, [], pendingNames, null, warnings);
            }

            var applied = new List<string>();
            if (state.Fresh)
            {
                _log.LogInformation("Empty database: creating the schema at {Version}", MigrationCatalog.BaselineVersion);
                string schema = MigrationCatalog.Resource("Sql.baseline_schema.sql");
                string seed = MigrationCatalog.Resource("Sql.baseline_seed.sql");
                await db.ExecuteAsync(schema, ct);
                await db.ExecuteAsync(seed, ct);
                await CreateRecordTableAsync(db, ct);
                string checksum = MigrationCatalog.Sha256(schema + seed);
                foreach (string v in MigrationCatalog.AlembicChain)
                    await RecordAsync(db, v, "baseline", v == MigrationCatalog.BaselineVersion ? checksum : null, ct);
                applied.Add(pendingNames[0]);
            }
            else if (!state.HasRecord && pending.Count > 0)
            {
                // Adopted from Alembic; written down now, with the first change.
                await CreateRecordTableAsync(db, ct);
                foreach (string v in state.Applied.OrderBy(v => v, StringComparer.Ordinal))
                    await RecordAsync(db, v, "alembic", null, ct);
            }

            foreach (var m in pending)
            {
                _log.LogInformation("Applying {Migration}: {Description}", m.Id, m.Description);
                await m.ApplyAsync(db, ct);
                await RecordAsync(db, m.Id, "dotnet", m.Checksum, ct);
                if (MigrationCatalog.AlembicChain.Contains(m.Id) && await db.TableExistsAsync("alembic_version", ct))
                    await db.ExecuteAsync("UPDATE public.alembic_version SET version_num = @v", ct, ("v", m.Id));
                applied.Add(m.Id);
            }

            string? created = await firstAdmin.EnsureAsync(db, ct);
            await tx.CommitAsync(ct);
            if (applied.Count > 0)
                _log.LogInformation("Database migrated: {Applied}", string.Join(", ", applied));
            else
                _log.LogInformation("Database schema is up to date ({From})", state.From);
            return new MigrationReport(state.From, applied, [], created, warnings);
        }
        catch
        {
            // Everything goes back; a broken connection must not hide the reason.
            try
            {
                if (conn.State == System.Data.ConnectionState.Open)
                    await tx.RollbackAsync(CancellationToken.None);
            }
            catch (Exception rollback)
            {
                _log.LogWarning(rollback, "Rolling back the migration failed (the server discards it with the connection)");
            }
            throw;
        }
    }

    private static IEnumerable<Migration> Upto(string? stopAfter)
    {
        if (stopAfter is null)
            return MigrationCatalog.Forward;
        int at = MigrationCatalog.Forward.ToList().FindIndex(m => m.Id == stopAfter);
        if (at < 0)
            throw new MigrationRefusedException($"StopAfter '{stopAfter}' is not a migration this build knows.");
        return MigrationCatalog.Forward.Take(at + 1);
    }

    private sealed record State(string From, bool Fresh, bool HasRecord, HashSet<string> Applied);

    private async Task<State> InspectAsync(MigrationContext db, CancellationToken ct)
    {
        if (await db.TableExistsAsync(RecordTable, ct))
        {
            var rows = await db.QueryStringsAsync($"SELECT version FROM public.{RecordTable}", ct);
            var applied = rows.ToHashSet(StringComparer.Ordinal);
            var known = MigrationCatalog.AlembicChain.Concat(MigrationCatalog.Forward.Select(m => m.Id)).ToHashSet();
            var unknown = applied.Where(v => !known.Contains(v)).OrderBy(v => v, StringComparer.Ordinal).ToList();
            if (unknown.Count > 0)
                db.Warn("The database has migrations this build does not know (" + string.Join(", ", unknown) +
                        "): a newer build ran them. Carrying on with what this build knows.");
            await CheckChecksumsAsync(db, ct);
            string last = rows.OrderBy(v => v, StringComparer.Ordinal).LastOrDefault() ?? "none";
            return new State($"{RecordTable} (latest {last})", false, true, applied);
        }

        if (await db.TableExistsAsync("alembic_version", ct))
        {
            var versions = await db.QueryStringsAsync("SELECT version_num FROM public.alembic_version", ct);
            if (versions.Count != 1)
                throw new MigrationRefusedException(
                    $"alembic_version holds {versions.Count} rows; expected one. " + UpgradeWithPython);
            string v = versions[0];
            int at = IndexOf(v);
            if (at < 0)
                throw new MigrationRefusedException(
                    $"The database is at Alembic revision '{v}', which this build does not know " +
                    $"(it knows {MigrationCatalog.AlembicChain[0]} to {MigrationCatalog.AlembicChain[^1]}).");
            if (at < IndexOf(MigrationCatalog.MinimumAlembic))
                throw new MigrationRefusedException(
                    $"The database is at Alembic revision {v}, older than {MigrationCatalog.MinimumAlembic}, " +
                    "the oldest this build takes over. " + UpgradeWithPython);
            return new State($"Alembic {v}", false, false,
                MigrationCatalog.AlembicChain.Take(at + 1).ToHashSet(StringComparer.Ordinal));
        }

        long relations = Convert.ToInt64(await db.ScalarAsync("""
            SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f')
            """, ct));
        if (relations > 0)
            throw new MigrationRefusedException(
                $"The database has {relations} tables or sequences but no migration record (neither " +
                $"{RecordTable} nor alembic_version). Point the app at an empty database, or at one the " +
                "Python app or this migrator built.");
        return new State("empty database", true, false, []);
    }

    private const string UpgradeWithPython =
        "Upgrade it with the Python app from the backup first (accountix_python_backup_2026-10-29.zip: " +
        "`python tools/db_migrate.py upgrade` with DATABASE_URL set), then run this again.";

    private static int IndexOf(string version)
    {
        for (int i = 0; i < MigrationCatalog.AlembicChain.Count; i++)
            if (MigrationCatalog.AlembicChain[i] == version)
                return i;
        return -1;
    }

    private static async Task CheckChecksumsAsync(MigrationContext db, CancellationToken ct)
    {
        var recorded = await db.QueryStringsAsync(
            $"SELECT version || ' ' || checksum FROM public.{RecordTable} WHERE how = 'dotnet' AND checksum IS NOT NULL", ct);
        foreach (string row in recorded)
        {
            string[] parts = row.Split(' ', 2);
            var m = MigrationCatalog.Forward.FirstOrDefault(x => x.Id == parts[0]);
            if (m is not null && m.Checksum != parts[1])
                db.Warn($"{m.Id} has changed since it ran on this database (checksum differs). " +
                        "A migration must not be edited once released; add a new one instead.");
        }
    }

    private static Task CreateRecordTableAsync(MigrationContext db, CancellationToken ct) =>
        db.ExecuteAsync($"""
            CREATE TABLE IF NOT EXISTS public.{RecordTable} (
                version VARCHAR(100) NOT NULL PRIMARY KEY,
                how VARCHAR(10) NOT NULL,
                checksum VARCHAR(64),
                applied_at TIMESTAMP WITHOUT TIME ZONE NOT NULL
            );
            """, ct);

    private static Task RecordAsync(MigrationContext db, string version, string how, string? checksum, CancellationToken ct) =>
        db.ExecuteAsync($"""
            INSERT INTO public.{RecordTable} (version, how, checksum, applied_at)
            VALUES (@v, @h, @c, (now() AT TIME ZONE 'utc'))
            """, ct, ("v", version), ("h", how), ("c", checksum));
}

/// <summary>
/// The first super admin, from configuration (Accountix:FirstAdmin:Email /
/// Password / Name), made only while the database has no super admin at all.
/// There is no built-in account and no default password: the Python app's
/// seeded admin@gmail.com / admin123 is not recreated.
/// </summary>
internal sealed class FirstAdmin
{
    private string? Email { get; init; }
    private string? PasswordHash { get; init; }
    private string Name { get; init; } = "Administrator";

    public static FirstAdmin From(MigratorOptions o)
    {
        string email = (o.FirstAdminEmail ?? "").Trim().ToLowerInvariant();
        string password = o.FirstAdminPassword ?? "";
        if (email.Length == 0)
        {
            if (password.Length > 0)
                throw new MigrationRefusedException("Accountix:FirstAdmin:Password is set but Accountix:FirstAdmin:Email is not.");
            return new FirstAdmin();
        }
        if (email.Length > 120 || email.IndexOf('@') <= 0 || email.IndexOf('@') == email.Length - 1 || email.Any(char.IsWhiteSpace))
            throw new MigrationRefusedException($"Accountix:FirstAdmin:Email '{email}' is not an email address.");
        if (PasswordPolicy.Problem(password) is { } problem)
            throw new MigrationRefusedException("Accountix:FirstAdmin:Password: " + problem);
        string name = (o.FirstAdminName ?? "").Trim();
        // The hash is made before the lock is taken (scrypt takes a moment).
        return new FirstAdmin
        {
            Email = email,
            PasswordHash = PasswordHasher.Hash(password),
            Name = name.Length == 0 ? "Administrator" : name.Length > 100 ? name[..100] : name,
        };
    }

    /// <summary>Creates the account when configured and no super admin exists; returns its email.</summary>
    public async Task<string?> EnsureAsync(MigrationContext db, CancellationToken ct)
    {
        bool anySuperAdmin = await db.ScalarAsync("SELECT EXISTS (SELECT 1 FROM public.users WHERE is_super_admin = true)", ct) is true;
        if (anySuperAdmin)
        {
            if (Email is not null)
                db.Log.LogInformation("A super admin exists; Accountix:FirstAdmin is not used");
            return null;
        }
        if (Email is null)
        {
            db.Warn("The database has no super admin. Set Accountix:FirstAdmin:Email and Accountix:FirstAdmin:Password " +
                    "(environment: Accountix__FirstAdmin__Email / Accountix__FirstAdmin__Password) and run the migration again.");
            return null;
        }
        if (await db.ScalarAsync("SELECT 1 FROM public.users WHERE lower(email) = @e OR lower(login_id) = @e", ct, ("e", Email)) is not null)
            throw new MigrationRefusedException(
                $"An account with the address {Email} exists but is not a super admin. Choose another Accountix:FirstAdmin:Email.");
        object? roleId = await db.ScalarAsync("SELECT id FROM public.roles WHERE name = 'admin' ORDER BY id LIMIT 1", ct)
                         ?? throw new MigrationRefusedException("The roles table has no 'admin' role.");

        // The Python app's built-in administrator was employee_code SYSADMIN; the
        // console's accounts are SA0001, SA0002... (both globally unique).
        string code = "SYSADMIN";
        if (await db.ScalarAsync("SELECT 1 FROM public.users WHERE employee_code = 'SYSADMIN'", ct) is not null)
        {
            long n = Convert.ToInt64(await db.ScalarAsync("SELECT count(*) FROM public.users", ct)) + 1;
            while (await db.ScalarAsync("SELECT 1 FROM public.users WHERE employee_code = @c", ct, ("c", $"SA{n:D4}")) is not null)
                n++;
            code = $"SA{n:D4}";
        }

        // Every column the model defaults client-side, as the console sets them for
        // a company's admin: active, every module switch on, joined today.
        await db.ExecuteAsync("""
            INSERT INTO public.users (
                employee_code, email, login_id, password_hash, full_name, role_id, designation,
                date_of_joining, is_active, is_super_admin,
                has_hr_access, has_inventory_access, has_invoicing_access, has_finance_access,
                has_accounting_access, has_fbr_access, has_fixed_assets_access, has_executive_access,
                created_at, updated_at)
            VALUES (@code, @e, @e, @h, @n, @r, 'System Administrator',
                    @today, true, true,
                    true, true, true, true, true, true, true, true,
                    (now() AT TIME ZONE 'utc'), (now() AT TIME ZONE 'utc'))
            """, ct, ("code", code), ("e", Email), ("h", PasswordHash), ("n", Name), ("r", roleId),
            ("today", DateOnly.FromDateTime(DateTime.Now)));
        db.Log.LogInformation("First super admin {Email} created", Email);
        return Email;
    }
}
