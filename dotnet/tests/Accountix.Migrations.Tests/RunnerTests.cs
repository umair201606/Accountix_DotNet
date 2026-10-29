using System.Text.Json.Nodes;

namespace Accountix.Migrations.Tests;

/// <summary>The runner: once is enough, many instances at once, and what it refuses.</summary>
public class RunnerTests
{
    private static async Task<(JsonNode Schema, JsonNode Data, List<string> Record)> SnapshotAsync(TempDb db) =>
        (await Pg.SchemaAsync(db.Name), await Pg.DigestsAsync(db.Name),
         await db.ColumnAsync("SELECT version || ' ' || how || ' ' || coalesce(checksum, '-') || ' ' || applied_at FROM schema_migrations ORDER BY version"));

    private static async Task AssertUnchangedAsync(TempDb db, (JsonNode Schema, JsonNode Data, List<string> Record) before)
    {
        var after = await SnapshotAsync(db);
        AssertSchema.Same(before.Schema, after.Schema, "second run");
        Assert.True(JsonNode.DeepEquals(before.Data, after.Data), "data changed on the second run");
        Assert.Equal(before.Record, after.Record);
    }

    [PostgresFact]
    public async Task Running_twice_is_a_no_op_on_a_new_database()
    {
        await using var db = await TempDb.CreateAsync();
        await db.Migrator(new() { FirstAdminEmail = "a@b.example", FirstAdminPassword = "Long-enough-pass-7" }).MigrateAsync();
        var before = await SnapshotAsync(db);

        var second = await db.Migrator(new() { FirstAdminEmail = "a@b.example", FirstAdminPassword = "Long-enough-pass-7" }).MigrateAsync();
        Assert.True(second.NothingDone);
        Assert.StartsWith("schema_migrations (latest ", second.StartedFrom);
        Assert.Empty((await db.Migrator().StatusAsync()).Pending);
        await AssertUnchangedAsync(db, before);
    }

    [PostgresFact]
    public async Task Running_twice_is_a_no_op_on_an_upgraded_database()
    {
        await using var db = await TempDb.From0015Async();
        await db.Migrator().MigrateAsync();
        var before = await SnapshotAsync(db);
        Assert.True((await db.Migrator().MigrateAsync()).NothingDone);
        await AssertUnchangedAsync(db, before);
    }

    [PostgresFact]
    public async Task A_database_python_already_brought_to_head_is_adopted_without_writing_anything()
    {
        // As the reference databases at 0018: Alembic did everything, there is no record yet.
        await using var db = await TempDb.From0015Async();
        await db.Migrator().MigrateAsync();
        await db.ExecAsync("DROP TABLE schema_migrations");
        var schema = await Pg.SchemaAsync(db.Name);
        var data = await Pg.DigestsAsync(db.Name);

        var report = await db.Migrator().MigrateAsync();
        Assert.Equal("Alembic 0018_report_indexes", report.StartedFrom);
        Assert.True(report.NothingDone);
        Assert.Equal(false, await db.ScalarAsync("SELECT to_regclass('schema_migrations') IS NOT NULL"));
        AssertSchema.Same(schema, await Pg.SchemaAsync(db.Name), "adopted at head");
        Assert.True(JsonNode.DeepEquals(data, await Pg.DigestsAsync(db.Name)));
    }

    [PostgresFact]
    public async Task Two_instances_at_once_one_migrates_and_the_other_waits_then_finds_nothing_to_do()
    {
        await using var db = await TempDb.CreateAsync();
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(() => db.Migrator(new()
        {
            AfterLock = async ct => { holding.SetResult(); await release.Task.WaitAsync(ct); },
        }).MigrateAsync());
        await holding.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var second = Task.Run(() => db.Migrator().MigrateAsync());
        // The second is queued on the migration lock (not failing on a lock timeout).
        bool waiting = false;
        for (int i = 0; i < 200 && !waiting; i++)
        {
            waiting = (long)(await Pg.ScalarAsync("postgres", $"""
                SELECT count(*) FROM pg_stat_activity
                 WHERE datname = '{db.Name}' AND wait_event_type = 'Lock' AND wait_event = 'advisory'
                """))! == 1;
            if (!waiting)
                await Task.Delay(50);
        }
        Assert.True(waiting, "the second instance never waited on the advisory lock");
        Assert.False(second.IsCompleted);
        // Nothing is visible to others while the first holds the lock... and it has done nothing yet.
        Assert.Equal(false, await db.ScalarAsync("SELECT to_regclass('users') IS NOT NULL"));

        release.SetResult();
        var a = await first;
        var b = await second;
        Assert.Equal("baseline (0018_report_indexes)", a.Applied[0]);
        Assert.True(b.NothingDone);
        Assert.StartsWith("schema_migrations", b.StartedFrom);
        AssertSchema.Same(SchemaDescription.Expected(), await Pg.SchemaAsync(db.Name), "after two instances");
    }

    [PostgresFact]
    public async Task Many_instances_starting_together_on_a_production_database_upgrade_it_once()
    {
        await using var db = await TempDb.From0015Async();
        var runs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => db.Migrator().MigrateAsync())));
        var did = Assert.Single(runs, r => !r.NothingDone);
        Assert.Equal(MigrationCatalog.Forward.Select(m => m.Id), did.Applied);
        Assert.All(runs.Where(r => r != did), r => Assert.StartsWith("schema_migrations", r.StartedFrom));
        AssertSchema.Same(SchemaDescription.Expected(), await Pg.SchemaAsync(db.Name), "after four instances");
    }

    [PostgresFact]
    public async Task A_database_below_0015_is_refused_with_the_way_forward()
    {
        await using var db = await TempDb.From0015Async();
        await db.ExecAsync("UPDATE alembic_version SET version_num = '0014_membership_module_flags'");
        var before = await Pg.DigestsAsync(db.Name);

        var e = await Assert.ThrowsAsync<MigrationRefusedException>(() => db.Migrator().MigrateAsync());
        Assert.Contains("0014_membership_module_flags", e.Message);
        Assert.Contains("older than 0015_membership_employment", e.Message);
        Assert.Contains("Upgrade it with the Python app from the backup first", e.Message);
        // Nothing written.
        Assert.Equal(false, await db.ScalarAsync("SELECT to_regclass('schema_migrations') IS NOT NULL"));
        Assert.Equal(false, await db.ScalarAsync("SELECT to_regclass('user_sessions') IS NOT NULL"));
        Assert.True(JsonNode.DeepEquals(before, await Pg.DigestsAsync(db.Name)));
        // Status refuses the same way.
        await Assert.ThrowsAsync<MigrationRefusedException>(() => db.Migrator().StatusAsync());
    }

    [PostgresFact]
    public async Task Unknown_revisions_and_unrecorded_tables_are_refused()
    {
        await using var db = await TempDb.From0015Async();
        await db.ExecAsync("UPDATE alembic_version SET version_num = '0019_something_newer'");
        var e = await Assert.ThrowsAsync<MigrationRefusedException>(() => db.Migrator().MigrateAsync());
        Assert.Contains("'0019_something_newer', which this build does not know", e.Message);

        await db.ExecAsync("DELETE FROM alembic_version");
        e = await Assert.ThrowsAsync<MigrationRefusedException>(() => db.Migrator().MigrateAsync());
        Assert.Contains("holds 0 rows", e.Message);

        await db.ExecAsync("DROP TABLE alembic_version");
        e = await Assert.ThrowsAsync<MigrationRefusedException>(() => db.Migrator().MigrateAsync());
        Assert.Contains("no migration record", e.Message);
    }

    [PostgresFact]
    public async Task A_failure_rolls_the_whole_run_back()
    {
        await using var db = await TempDb.From0015Async();
        // 0018 cannot index a view: put one where a table it indexes was.
        await db.ExecAsync("ALTER TABLE inv_invoice_items RENAME TO inv_invoice_items_real; " +
                           "CREATE VIEW inv_invoice_items AS SELECT * FROM inv_invoice_items_real;");
        var before = await Pg.DigestsAsync(db.Name);
        var e = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Migrator().MigrateAsync());
        Assert.Contains("inv_invoice_items", e.MessageText);
        // 0016 and 0017 had run in the same transaction: gone with it.
        Assert.Equal(false, await db.ScalarAsync("SELECT to_regclass('user_sessions') IS NOT NULL"));
        Assert.Equal(false, await db.ScalarAsync("SELECT to_regclass('dotnet_data_protection_keys') IS NOT NULL"));
        Assert.Equal(false, await db.ScalarAsync("SELECT to_regclass('schema_migrations') IS NOT NULL"));
        Assert.Equal("0015_membership_employment", await db.ScalarAsync("SELECT version_num FROM alembic_version"));
        Assert.True(JsonNode.DeepEquals(before, await Pg.DigestsAsync(db.Name)));
    }

    [PostgresFact]
    public async Task Migrations_a_newer_build_ran_are_reported_not_fatal()
    {
        // Swapping back to the previous release after a newer one migrated must still start.
        await using var db = await TempDb.CreateAsync();
        await db.Migrator().MigrateAsync();
        await db.ExecAsync("INSERT INTO schema_migrations (version, how, applied_at) VALUES ('0019_from_the_future', 'dotnet', now())");
        var report = await db.Migrator().MigrateAsync();
        Assert.True(report.NothingDone);
        Assert.Contains(report.Warnings, w => w.Contains("0019_from_the_future"));
    }

    [PostgresFact]
    public async Task Stopping_after_a_migration_applies_up_to_it_and_the_next_run_continues()
    {
        await using var db = await TempDb.From0015Async();
        var first = await db.Migrator(new() { StopAfter = "0016_access_per_company" }).MigrateAsync();
        Assert.Equal(["0016_access_per_company"], first.Applied);
        Assert.Equal("0016_access_per_company", await db.ScalarAsync("SELECT version_num FROM alembic_version"));
        Assert.Equal(false, await db.ScalarAsync("SELECT to_regclass('dotnet_data_protection_keys') IS NOT NULL"));
        var rest = await db.Migrator().MigrateAsync();
        Assert.Equal(MigrationCatalog.Forward.Skip(1).Select(m => m.Id), rest.Applied);
        await Assert.ThrowsAsync<MigrationRefusedException>(() => db.Migrator(new() { StopAfter = "0099_nope" }).MigrateAsync());
    }

    [Fact]
    public void Every_forward_migration_has_its_sql_and_a_unique_id_in_order()
    {
        var ids = MigrationCatalog.Forward.Select(m => m.Id).ToList();
        Assert.Equal(ids.Distinct().Count(), ids.Count);
        Assert.Equal(ids.OrderBy(i => i, StringComparer.Ordinal), ids);
        Assert.All(MigrationCatalog.Forward, m => Assert.Equal(64, m.Checksum.Length));
        // The ported revisions continue the Alembic chain from the minimum.
        int from = MigrationCatalog.AlembicChain.ToList().IndexOf(MigrationCatalog.MinimumAlembic);
        Assert.Equal(MigrationCatalog.AlembicChain.Skip(from + 1), ids.Take(MigrationCatalog.AlembicChain.Count - from - 1));
        Assert.Equal(MigrationCatalog.BaselineVersion, MigrationCatalog.AlembicChain[^1]);
        Assert.Contains("CREATE TABLE public.users", MigrationCatalog.Resource("Sql.baseline_schema.sql"));
        Assert.NotNull(SchemaDescription.Expected()["tables"]!["users"]);
    }
}
