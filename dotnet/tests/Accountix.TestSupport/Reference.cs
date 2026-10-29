using System.Text.Json;
using Accountix.Core.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Accountix.TestSupport;

/// <summary>
/// The reference database and the facts about it, in tools/reference.json, both
/// written by tools/Accountix.RefDb from tests/fixtures/reference.sql.gz (+ its
/// reference.facts.json). Integration tests skip when it has not been built.
/// </summary>
public static class Reference
{
    private static readonly Lazy<JsonElement?> Data = new(Load);

    public static string? Path { get; } = Find();
    public static bool Available => Data.Value is not null;
    public static JsonElement Json => Data.Value ?? throw new InvalidOperationException("reference.json missing");

    /// <summary>
    /// The database tests use: the module's own copy when ACCOUNTIX_TEST_DB
    /// names one of reference.json's "databases" (so modules tested side by
    /// side never see each other's writes), else the shared reference database.
    /// </summary>
    public static string ConnectionString
    {
        get
        {
            string? module = Environment.GetEnvironmentVariable("ACCOUNTIX_TEST_DB");
            if (module is not null && Json.TryGetProperty("databases", out var dbs)
                && dbs.TryGetProperty(module, out var cs))
                return cs.GetString()!;
            return Json.GetProperty("connection_string").GetString()!;
        }
    }
    public static string SecretKey => Json.GetProperty("secret_key").GetString()!;
    public static int Company(string key) => Json.GetProperty("companies").GetProperty(key).GetInt32();
    public static int UserId(string email) => Json.GetProperty("users").GetProperty(email).GetInt32();
    public static string Email(string key) => Json.GetProperty("emails").GetProperty(key).GetString()!;

    public static AccountixDbContext NewDb(CompanyContext? company = null) =>
        new(new DbContextOptionsBuilder<AccountixDbContext>().UseNpgsql(ConnectionString).Options,
            company ?? new CompanyContext());

    /// <summary>
    /// tools/reference.json: ACCOUNTIX_REFERENCE if set, else above the test
    /// binaries, else above this source file (builds with --artifacts-path put
    /// the binaries outside the repo, where walking up finds nothing).
    /// </summary>
    private static string? Find()
    {
        if (Environment.GetEnvironmentVariable("ACCOUNTIX_REFERENCE") is { Length: > 0 } env && File.Exists(env))
            return env;
        foreach (string start in new[] { AppContext.BaseDirectory, SourceDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                string candidate = System.IO.Path.Combine(dir.FullName, "tools", "reference.json");
                if (File.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }
        }
        return null;
    }

    /// <summary>The dotnet/ folder of the checkout (Accountix.slnx): src/, tests/, tools/.</summary>
    public static string DotnetRoot
    {
        get
        {
            foreach (string start in new[] { SourceDirectory(), AppContext.BaseDirectory })
                for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                    if (File.Exists(System.IO.Path.Combine(dir.FullName, "Accountix.slnx")))
                        return dir.FullName;
            throw new InvalidOperationException("Accountix.slnx not found above the test source or binaries.");
        }
    }

    /// <summary>The folder of this source file, as compiled (the repo checkout).</summary>
    internal static string SourceDirectory([System.Runtime.CompilerServices.CallerFilePath] string file = "") =>
        System.IO.Path.GetDirectoryName(file) ?? AppContext.BaseDirectory;

    private static JsonElement? Load()
    {
        if (Path is null)
            return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(Path));
        var root = doc.RootElement.Clone();
        try
        {
            using var db = new AccountixDbContext(
                new DbContextOptionsBuilder<AccountixDbContext>()
                    .UseNpgsql(root.GetProperty("connection_string").GetString()).Options,
                new CompanyContext());
            return db.Database.CanConnect() ? root : null;
        }
        catch
        {
            return null;
        }
    }
}

public sealed class ReferenceFactAttribute : FactAttribute
{
    public ReferenceFactAttribute()
    {
        if (!Reference.Available)
            Skip = "Reference database not available: dotnet run --project dotnet/tools/Accountix.RefDb";
    }
}
