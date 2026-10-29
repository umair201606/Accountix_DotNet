using System.Net;
using Accountix.Web.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Accountix.Migrations.Tests;

/// <summary>The host's side: Accountix:MigrateOnStart and `dotnet Accountix.Web.dll migrate`.</summary>
public class HostTests
{
    private const string Secret = "migration-host-tests-secret-0123456789";

    /// <summary>The deployed app (AccountixHost, Production) in-process on <paramref name="cs"/>, as Program.cs starts it.</summary>
    private static WebApplication Build(string cs, params (string Key, string? Value)[] settings)
    {
        var b = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
            ApplicationName = typeof(MigrateCommand).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
        });
        b.WebHost.UseTestServer();
        b.Configuration["SECRET_KEY"] = Secret;
        b.Configuration["DATABASE_URL"] = "";
        b.Configuration["ConnectionStrings:Accountix"] = cs;
        b.Configuration["LOG_FORMAT"] = "simple";
        b.Configuration["DB_MAX_POOL_SIZE"] = "3";
        foreach (var (k, v) in settings)
            b.Configuration[k] = v;
        b.Logging.SetMinimumLevel(LogLevel.Warning);
        return AccountixHost.Build(b);
    }

    [PostgresFact]
    public async Task In_production_an_empty_database_is_migrated_before_the_key_ring_loads_and_the_app_answers()
    {
        await using var db = await TempDb.CreateAsync();
        await using (var app = Build(db.ConnectionString,
                         ("Accountix:FirstAdmin:Email", "boss@example.com"), ("Accountix:FirstAdmin:Password", "Long-enough-pass-7")))
        {
            MigrateCommand.OnStart(app);           // Program.cs, before Run
            await app.StartAsync();                // the key ring warm-up needs 0017's table
            var health = await app.GetTestServer().CreateClient().GetAsync("/net/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            await app.StopAsync();
        }
        Assert.Equal(1L, await db.ScalarAsync("SELECT count(*) FROM dotnet_data_protection_keys"));
        Assert.Equal("boss@example.com", await db.ScalarAsync("SELECT email FROM users WHERE is_super_admin"));
        AssertSchema.Same(SchemaDescription.Expected(), await Pg.SchemaAsync(db.Name), "after the first start");
    }

    [PostgresFact]
    public async Task With_migrate_on_start_off_an_unmigrated_database_stops_the_start_with_the_fix_in_words()
    {
        await using var db = await TempDb.CreateAsync();
        await using var app = Build(db.ConnectionString, ("Accountix:MigrateOnStart", "false"));
        MigrateCommand.OnStart(app);
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
        Assert.Contains("dotnet_data_protection_keys is missing", e.Message);
        Assert.Contains("dotnet Accountix.Web.dll migrate", e.Message);
        Assert.Equal(0L, await db.ScalarAsync("SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public'"));
    }

    [PostgresFact]
    public async Task A_refused_database_stops_the_start()
    {
        await using var db = await TempDb.From0015Async();
        await db.ExecAsync("UPDATE alembic_version SET version_num = '0013_edit_version'");
        await using var app = Build(db.ConnectionString);
        var e = Assert.Throws<MigrationRefusedException>(() => MigrateCommand.OnStart(app));
        Assert.Contains("Upgrade it with the Python app from the backup first", e.Message);
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Accountix.Web";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [Fact]
    public void Migrate_on_start_is_on_in_production_and_off_elsewhere_unless_set()
    {
        Assert.True(MigrateCommand.ShouldMigrateOnStart(Config(), new Env("Production")));
        Assert.False(MigrateCommand.ShouldMigrateOnStart(Config(), new Env("Development")));
        Assert.False(MigrateCommand.ShouldMigrateOnStart(Config(), new Env("Staging")));
        Assert.False(MigrateCommand.ShouldMigrateOnStart(Config(("Accountix:MigrateOnStart", "false")), new Env("Production")));
        Assert.True(MigrateCommand.ShouldMigrateOnStart(Config(("Accountix:MigrateOnStart", "true")), new Env("Development")));
    }

    [Fact]
    public void Migrations_use_their_own_url_then_the_apps()
    {
        const string direct = "postgresql://app:pw@ep-x.eastus2.azure.neon.tech/neondb?sslmode=require";
        const string pooled = "postgresql://app:pw@ep-x-pooler.eastus2.azure.neon.tech/neondb?sslmode=require";
        Assert.Contains("Host=ep-x.eastus2", MigrateCommand.ConnectionString(Config(("MIGRATIONS_DATABASE_URL", direct), ("DATABASE_URL", pooled))));
        Assert.Contains("Host=ep-x-pooler", MigrateCommand.ConnectionString(Config(("DATABASE_URL", pooled))));
        Assert.Equal("Host=/tmp;Database=x", MigrateCommand.ConnectionString(Config(("ConnectionStrings:Accountix", "Host=/tmp;Database=x"))));
        Assert.Throws<InvalidOperationException>(() => MigrateCommand.ConnectionString(Config()));
        var o = MigrateCommand.Options(Config(("Accountix:FirstAdmin:Email", "a@b.c"), ("Accountix:FirstAdmin:Password", "p")));
        Assert.Equal(("a@b.c", "p"), (o.FirstAdminEmail, o.FirstAdminPassword));
    }

    [PostgresFact]
    public async Task The_command_line_reports_applies_and_checks()
    {
        await using var db = await TempDb.CreateAsync();
        string? saved = Environment.GetEnvironmentVariable("MIGRATIONS_DATABASE_URL");
        var stdout = Console.Out;
        var stderr = Console.Error;
        try
        {
            Environment.SetEnvironmentVariable("MIGRATIONS_DATABASE_URL", db.ConnectionString);
            async Task<(int Code, string Text)> Run(params string[] args)
            {
                var w = new StringWriter();
                Console.SetOut(w);
                Console.SetError(w);
                int code = await MigrateCommand.RunAsync(args);
                Console.SetOut(stdout);
                Console.SetError(stderr);
                return (code, w.ToString());
            }

            var status = await Run("--status");
            Assert.Equal(0, status.Code);
            Assert.Contains("Would apply: baseline (0018_report_indexes)", status.Text);
            Assert.Equal(0L, await db.ScalarAsync("SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public'"));

            var check = await Run("--check");
            Assert.Equal(2, check.Code);                         // empty: everything missing

            var migrate = await Run();
            Assert.Equal(0, migrate.Code);
            Assert.Contains("Applied: baseline (0018_report_indexes)", migrate.Text);
            Assert.Equal((0, "The schema is the one this build expects.\n"), await Run("--check"));
            Assert.Contains("Nothing to apply.", (await Run()).Text);

            var described = await Run("--describe");
            Assert.Equal(0, described.Code);
            AssertSchema.Same(SchemaDescription.Expected(), System.Text.Json.Nodes.JsonNode.Parse(described.Text)!, "--describe");

            Assert.Equal(64, (await Run("--bogus")).Code);
            await db.ExecAsync("UPDATE alembic_version SET version_num = 'x'; DROP TABLE schema_migrations;");
            var refused = await Run();
            Assert.Equal(1, refused.Code);
            Assert.Contains("Refused: The database is at Alembic revision 'x'", refused.Text);
        }
        finally
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            Environment.SetEnvironmentVariable("MIGRATIONS_DATABASE_URL", saved);
        }
    }
}
