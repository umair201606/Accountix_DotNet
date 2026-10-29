using Accountix.Migrations;
using Accountix.Web.Infrastructure;

namespace Accountix.Web.Hosting;

/// <summary>
/// Schema migrations from the host (src/Accountix.Migrations owns the schema):
/// <c>dotnet Accountix.Web.dll migrate</c> on the command line, and at every start
/// when <c>Accountix:MigrateOnStart</c> is on (the default in Production, as the
/// Python app migrated at every boot). Both run the same <see cref="Migrator"/>:
/// one transaction under the migration lock, so instances starting together
/// queue and the later ones find nothing to do. docs/DEPLOY.md, "Migrations".
/// </summary>
public static class MigrateCommand
{
    private const string Usage = """
        dotnet Accountix.Web.dll migrate [--status | --check | --describe]

          (no option)  bring the database to this build's schema; on an empty database,
                       also the first super admin from Accountix:FirstAdmin:Email / Password
          --status     what would run; changes nothing
          --check      compare the database's schema with the one this build expects
                       (exit 2 when they differ); changes nothing
          --describe   print the database's schema description (JSON), for re-capturing
                       Schema/expected_schema.json after a migration changes the schema

        Database: MIGRATIONS_DATABASE_URL (e.g. Neon's direct endpoint), else DATABASE_URL,
        else ConnectionStrings:Accountix.
        """;

    public static bool Matches(string[] args) => args.Length > 0 && args[0] == "migrate";

    /// <summary>Accountix:MigrateOnStart; unset means on in Production, off elsewhere.</summary>
    public static bool ShouldMigrateOnStart(IConfiguration config, IHostEnvironment env) =>
        config.GetValue<bool?>("Accountix:MigrateOnStart") ?? env.IsProduction();

    /// <summary>The database the migrations run on.</summary>
    public static string ConnectionString(IConfiguration config)
    {
        string? url = config["MIGRATIONS_DATABASE_URL"] is { Length: > 0 } m ? m : config["DATABASE_URL"];
        return !string.IsNullOrWhiteSpace(url)
            ? AppSettings.FromDatabaseUrl(url)
            : config.GetConnectionString("Accountix")
              ?? throw new InvalidOperationException("Set DATABASE_URL (or MIGRATIONS_DATABASE_URL, or ConnectionStrings:Accountix).");
    }

    public static MigratorOptions Options(IConfiguration config) => new()
    {
        FirstAdminEmail = config["Accountix:FirstAdmin:Email"],
        FirstAdminPassword = config["Accountix:FirstAdmin:Password"],
        FirstAdminName = config["Accountix:FirstAdmin:Name"],
    };

    /// <summary>
    /// Before the app takes requests (and before the key ring warm-up reads
    /// dotnet_data_protection_keys). A refusal or failure stops the start.
    /// </summary>
    public static void OnStart(WebApplication app)
    {
        if (!ShouldMigrateOnStart(app.Configuration, app.Environment))
            return;
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Accountix.Migrations");
        new Migrator(ConnectionString(app.Configuration), Options(app.Configuration), log)
            .MigrateAsync().GetAwaiter().GetResult();
    }

    /// <summary>The command line; returns the exit code.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        string mode = args.FirstOrDefault() ?? "";
        if (mode is "-h" or "--help" || args.Length > 1 || mode is not ("" or "--status" or "--check" or "--describe"))
        {
            Console.Error.WriteLine(Usage);
            return mode is "-h" or "--help" ? 0 : 64;
        }
        // The app's own configuration sources (appsettings, environment), nothing started.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        using var loggers = LoggerFactory.Create(l => l.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; }));
        var log = loggers.CreateLogger("Accountix.Migrations");
        try
        {
            string cs = ConnectionString(builder.Configuration);
            switch (mode)
            {
                case "--describe":
                    Console.Write(SchemaDescription.ToText(await SchemaDescription.DescribeAsync(cs)));
                    return 0;
                case "--check":
                {
                    var diffs = SchemaDescription.Differences(SchemaDescription.Expected(), await SchemaDescription.DescribeAsync(cs));
                    foreach (string d in diffs)
                        Console.WriteLine(d);
                    Console.WriteLine(diffs.Count == 0
                        ? "The schema is the one this build expects."
                        : $"{diffs.Count} difference(s) from the schema this build expects.");
                    return diffs.Count == 0 ? 0 : 2;
                }
            }
            var migrator = new Migrator(cs, Options(builder.Configuration), log);
            var report = mode == "--status" ? await migrator.StatusAsync() : await migrator.MigrateAsync();
            Console.WriteLine($"Database: {report.StartedFrom}");
            if (mode == "--status")
                Console.WriteLine(report.Pending.Count == 0 ? "Nothing to apply." : "Would apply: " + string.Join(", ", report.Pending));
            else
                Console.WriteLine(report.Applied.Count == 0 ? "Nothing to apply." : "Applied: " + string.Join(", ", report.Applied));
            if (report.FirstAdminCreated is { } email)
                Console.WriteLine($"First super admin: {email}");
            foreach (string w in report.Warnings)
                Console.WriteLine("Warning: " + w);
            return 0;
        }
        catch (MigrationRefusedException e)
        {
            Console.Error.WriteLine("Refused: " + e.Message);
            return 1;
        }
        catch (Npgsql.NpgsqlException e)
        {
            // Nothing was committed: the run is one transaction.
            Console.Error.WriteLine("Failed, nothing changed: " + e.Message);
            return 1;
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }
}
