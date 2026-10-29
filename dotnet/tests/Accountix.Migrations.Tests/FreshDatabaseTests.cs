using System.Text.Json.Nodes;
using Accountix.Core.Security;

namespace Accountix.Migrations.Tests;

/// <summary>An empty database: the baseline, the rows a new installation needs, the first super admin.</summary>
public class FreshDatabaseTests
{
    private static MigratorOptions Admin(string email = "Owner@Example.com", string password = "Long-enough-pass-7") =>
        new() { FirstAdminEmail = email, FirstAdminPassword = password, FirstAdminName = "Platform Owner" };

    [PostgresFact]
    public async Task An_empty_database_gets_exactly_the_schema_the_python_app_builds()
    {
        await using var db = await TempDb.CreateAsync();
        var report = await db.Migrator().MigrateAsync();

        Assert.Equal("empty database", report.StartedFrom);
        // The baseline, then whatever .NET migrations came after 0018 (none yet).
        Assert.Equal(new[] { "baseline (0018_report_indexes)" }
            .Concat(MigrationCatalog.Forward.Select(m => m.Id).Where(id => !MigrationCatalog.AlembicChain.Contains(id))), report.Applied);
        // Tables, columns (type, default, nullability, order), constraints, indexes,
        // sequences: as a Python-built 0018 database (captured in expected_schema.json).
        AssertSchema.Same(SchemaDescription.Expected(), await Pg.SchemaAsync(db.Name), "fresh .NET database vs Python 0018");

        // Every Alembic revision counts as built by the baseline.
        var record = await db.ColumnAsync("SELECT version || ':' || how FROM schema_migrations WHERE how = 'baseline' ORDER BY version");
        Assert.Equal(MigrationCatalog.AlembicChain.Select(v => v + ":baseline"), record);
        Assert.Equal("0018_report_indexes", await db.ScalarAsync("SELECT version_num FROM alembic_version"));
        // The key ring's table is there before the app starts (DataProtectionStore).
        Assert.Equal(true, await db.ScalarAsync("SELECT to_regclass('dotnet_data_protection_keys') IS NOT NULL"));
    }

    [PostgresFact]
    public async Task The_rows_a_new_installation_needs_are_the_python_apps()
    {
        await using var db = await TempDb.CreateAsync();
        await db.Migrator().MigrateAsync();
        var golden = Fixtures.Json("fresh_seed.json");

        var rows = JsonNode.Parse((string)(await db.ScalarAsync("""
            SELECT jsonb_build_object(
              'roles', (SELECT jsonb_agg(to_jsonb(r) ORDER BY r.id) FROM roles r),
              'permissions', (SELECT jsonb_agg(to_jsonb(p) ORDER BY p.id) FROM permissions p),
              'global_limits', (SELECT jsonb_agg(to_jsonb(g) - 'updated_at' ORDER BY g.id) FROM global_limits g),
              'alembic_version', (SELECT jsonb_agg(v.version_num) FROM alembic_version v))::text
            """))!)!;
        foreach (string key in new[] { "roles", "permissions", "global_limits", "alembic_version" })
            Assert.True(JsonNode.DeepEquals(golden[key], rows[key]), $"{key}: {rows[key]?.ToJsonString()}");
        Assert.Equal(54L, await db.ScalarAsync("SELECT count(*) FROM permissions"));
        Assert.Equal(true, await db.ScalarAsync("SELECT updated_at IS NOT NULL FROM global_limits"));

        // Nothing else: no company, no account, no demo data.
        foreach (string table in new[] { "users", "companies", "company_memberships", "chart_of_accounts", "inv_products" })
            Assert.Equal(0L, await db.ScalarAsync($"SELECT count(*) FROM {table}"));
        // Sequences continue after the seeded ids.
        await db.ExecAsync("INSERT INTO roles (name, description) VALUES ('auditor', 'x')");
        Assert.Equal(4, await db.ScalarAsync("SELECT id FROM roles WHERE name = 'auditor'"));
    }

    [PostgresFact]
    public async Task The_first_super_admin_comes_from_configuration_never_from_a_default_password()
    {
        await using var db = await TempDb.CreateAsync();
        var report = await db.Migrator(Admin()).MigrateAsync();
        Assert.Equal("owner@example.com", report.FirstAdminCreated);

        var u = JsonNode.Parse((string)(await db.ScalarAsync(
            "SELECT (to_jsonb(u) - 'id' - 'created_at' - 'updated_at' - 'date_of_joining')::text FROM users u"))!)!;
        Assert.True(PasswordHasher.Verify(u["password_hash"]!.GetValue<string>(), "Long-enough-pass-7"));
        Assert.StartsWith("scrypt:32768:8:1$", u["password_hash"]!.GetValue<string>());   // Werkzeug's format

        // As the Python app's built-in administrator (SYSADMIN), except: the address and
        // name configured, and every module switch on (as the console sets for an admin).
        var expected = Fixtures.Json("fresh_seed.json")["sysadmin"]!.AsObject().DeepClone().AsObject();
        expected["email"] = "owner@example.com";
        expected["login_id"] = "owner@example.com";
        expected["full_name"] = "Platform Owner";
        foreach (var (_, column) in AccessPerCompany.ModuleFlags)
            expected[column] = true;
        u.AsObject().Remove("password_hash");
        Assert.True(JsonNode.DeepEquals(expected, u), u.ToJsonString());
        Assert.Equal(true, await db.ScalarAsync("SELECT created_at IS NOT NULL AND date_of_joining IS NOT NULL FROM users"));

        // Made once: a later start with other settings changes nothing.
        var again = await db.Migrator(Admin("someone.else@example.com")).MigrateAsync();
        Assert.True(again.NothingDone);
        Assert.Equal(1L, await db.ScalarAsync("SELECT count(*) FROM users"));
    }

    [PostgresFact]
    public async Task Without_configuration_there_is_no_account_and_the_report_says_how_to_make_one()
    {
        await using var db = await TempDb.CreateAsync();
        var report = await db.Migrator().MigrateAsync();
        Assert.Null(report.FirstAdminCreated);
        Assert.Contains(report.Warnings, w => w.Contains("Accountix:FirstAdmin:Email", StringComparison.Ordinal));
        Assert.Equal(0L, await db.ScalarAsync("SELECT count(*) FROM users"));

        // Configured later, the next run makes the account (the schema is not touched again).
        var later = await db.Migrator(Admin()).MigrateAsync();
        Assert.Empty(later.Applied);
        Assert.Equal("owner@example.com", later.FirstAdminCreated);
    }

    [PostgresFact]
    public async Task A_weak_first_admin_password_is_refused_before_anything_is_written()
    {
        await using var db = await TempDb.CreateAsync();
        var e = await Assert.ThrowsAsync<MigrationRefusedException>(() => db.Migrator(Admin(password: "admin123")).MigrateAsync());
        Assert.Contains("Accountix:FirstAdmin:Password", e.Message);
        Assert.Equal(0L, await db.ScalarAsync("SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public'"));

        await Assert.ThrowsAsync<MigrationRefusedException>(() => db.Migrator(Admin(email: "not-an-address")).MigrateAsync());
        await Assert.ThrowsAsync<MigrationRefusedException>(() =>
            db.Migrator(new MigratorOptions { FirstAdminPassword = "Long-enough-pass-7" }).MigrateAsync());
    }

    [PostgresFact]
    public async Task Status_reports_the_baseline_and_changes_nothing()
    {
        await using var db = await TempDb.CreateAsync();
        var status = await db.Migrator(Admin()).StatusAsync();
        Assert.Equal("baseline (0018_report_indexes)", status.Pending[0]);
        Assert.Empty(status.Applied);
        Assert.Equal(0L, await db.ScalarAsync("SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public'"));
    }
}
