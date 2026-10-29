// Builds the reference database the .NET integration tests run against, and
// one copy per test project, from tests/fixtures/reference.sql.gz; then writes
// tools/reference.json (connection strings + the facts the tests assert on).
//
//   dotnet run --project tools/Accountix.RefDb                    # everything
//   dotnet run --project tools/Accountix.RefDb -- --only hr       # one module copy again
//   dotnet run --project tools/Accountix.RefDb -- --prefix t_ --out /tmp/t_reference.json
//   dotnet run --project tools/Accountix.RefDb -- --prefix t_ --drop      # remove that set again
//
// After the fixture loads, this build's migrations (src/Accountix.Migrations) run on
// it, so the copies have every migration added since the fixture was captured
// (--no-migrate to skip). Nothing to do while the fixture is at the migrations' head.
//
// The server: --server, else ACCOUNTIX_TEST_PG (a connection string such as
// "Host=localhost;Username=postgres;Password=…" or a postgresql:// URL), else
// the server named in the existing reference.json. Any Postgres 16+ will do.
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Accountix.RefDb;

public static class Cli
{
    public static async Task<int> Main(string[] args)
    {
        string? server = null, prefix = "", only = null, outPath = null, fixture = null, facts = null;
        bool migrate = true, drop = false;
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--server": server = Next(); break;
                case "--prefix": prefix = Next(); break;
                case "--only": only = Next(); break;
                case "--out": outPath = Next(); break;
                case "--fixture": fixture = Next(); break;
                case "--facts": facts = Next(); break;
                case "--no-migrate": migrate = false; break;
                case "--drop": drop = true; break;
                case "-h" or "--help":
                    Console.WriteLine("Accountix.RefDb [--server <conn>] [--prefix <p>] [--only <key>|base] [--out reference.json] " +
                                      "[--fixture reference.sql.gz] [--facts reference.facts.json] [--no-migrate] [--drop]\n" +
                                      "Module keys: " + string.Join(", ", ReferenceDatabase.ModuleCopies));
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown option {args[i]} (try --help)");
                    return 2;
            }
        }

        string root = DotnetRoot();
        outPath ??= Path.Combine(root, "tools", "reference.json");
        fixture ??= Path.Combine(root, "tests", "fixtures", "reference.sql.gz");
        facts ??= Path.Combine(root, "tests", "fixtures", "reference.facts.json");
        JsonObject? previous = File.Exists(outPath) ? JsonNode.Parse(File.ReadAllText(outPath)) as JsonObject : null;
        server ??= Environment.GetEnvironmentVariable("ACCOUNTIX_TEST_PG") is { Length: > 0 } env ? env
            : previous?["connection_string"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(server))
        {
            Console.Error.WriteLine("No Postgres server: set ACCOUNTIX_TEST_PG (e.g. \"Host=localhost;Username=postgres;Password=…\") or pass --server.");
            return 2;
        }
        if (only is not null && only != "base" && !ReferenceDatabase.ModuleCopies.Contains(only))
        {
            Console.Error.WriteLine($"Unknown module copy '{only}'. Keys: base, {string.Join(", ", ReferenceDatabase.ModuleCopies)}");
            return 2;
        }

        if (drop && prefix.Length == 0)
        {
            Console.Error.WriteLine("--drop needs --prefix (the live accountix_ref databases are rebuilt, never just dropped).");
            return 2;
        }
        if (drop)
        {
            // Only the named set (prefix + accountix_ref[_<key>]); reference.json is left alone.
            var names = (only is null ? [null, .. ReferenceDatabase.ModuleCopies] : new[] { only == "base" ? null : only })
                .Select(k => ReferenceDatabase.DatabaseName(prefix, k));
            foreach (string name in names)
            {
                await ReferenceDatabase.DropAsync(server, name);
                Console.WriteLine($"  dropped {name}");
            }
            return 0;
        }

        var factsJson = (JsonObject)JsonNode.Parse(File.ReadAllText(facts))!;
        string expected = factsJson["alembic_version"]!.GetValue<string>();
        var srv = Connection.Server(server);
        string baseName = ReferenceDatabase.DatabaseName(prefix, null);
        Console.WriteLine($"Server {srv.Host}{(srv.Port != 5432 ? ":" + srv.Port : "")}, fixture {Path.GetFileName(fixture)} " +
                          $"({Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fixture)))[..12]})");

        // The fixture, checked, then this build's migrations on top.
        async Task<bool> LoadAsync(string name)
        {
            await ReferenceDatabase.CreateFromFixtureAsync(server, name, fixture, Console.Out);
            string cs = Connection.ForDatabase(server, name);
            string? at = await ReferenceDatabase.AlembicVersionAsync(cs);
            if (at != expected)
            {
                Console.Error.WriteLine($"{name} is at {at ?? "(no alembic_version)"} after loading the fixture; the facts say {expected}");
                return false;
            }
            if (migrate)
            {
                var report = await new Accountix.Migrations.Migrator(cs).MigrateAsync();
                Console.WriteLine(report.Applied.Count == 0
                    ? $"  {name}: schema up to date ({report.StartedFrom})"
                    : $"  {name}: migrated {string.Join(", ", report.Applied)}");
                foreach (string w in report.Warnings)
                    Console.WriteLine($"  {name}: warning: {w}");
            }
            return true;
        }

        var built = new List<(string? Key, string Name)>();
        if (only is null)
        {
            if (!await LoadAsync(baseName))
                return 1;
            built.Add((null, baseName));
            foreach (string key in ReferenceDatabase.ModuleCopies)
            {
                string name = ReferenceDatabase.DatabaseName(prefix, key);
                if (!await ReferenceDatabase.TryCopyAsync(server, baseName, name, Console.Out) && !await LoadAsync(name))
                    return 1;
                built.Add((key, name));
            }
        }
        else
        {
            string? key = only == "base" ? null : only;
            string name = ReferenceDatabase.DatabaseName(prefix, key);
            if (!await LoadAsync(name))
                return 1;
            built.Add((key, name));
        }

        // reference.json: the same keys the tests have always read.
        var databases = only is not null && previous?["databases"] is JsonObject had ? (JsonObject)had.DeepClone() : new JsonObject();
        foreach (var (key, name) in built)
            if (key is not null)
                databases[key] = Connection.Format(srv, name);
        var output = new JsonObject
        {
            ["generated_at"] = only is not null && previous?["generated_at"] is { } g ? g.DeepClone()
                : DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["connection_string"] = Connection.Format(srv, baseName),
            ["database_url"] = Connection.Url(srv, baseName),
            ["alembic_version"] = expected,
        };
        foreach (string k in new[] { "secret_key", "password", "companies", "users", "emails", "hashes", "access" })
            output[k] = factsJson[k]!.DeepClone();
        output["databases"] = databases;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, output.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Console.WriteLine($"Wrote {outPath} ({built.Count} database{(built.Count == 1 ? "" : "s")} at {expected})");
        return 0;
    }

    private static string DotnetRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory, Path.GetDirectoryName(source) ?? "" })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Accountix.slnx")))
                    return dir.FullName;
                if (File.Exists(Path.Combine(dir.FullName, "dotnet", "Accountix.slnx")))
                    return Path.Combine(dir.FullName, "dotnet");
            }
        }
        throw new InvalidOperationException("dotnet/Accountix.slnx not found above the current folder");
    }
}
