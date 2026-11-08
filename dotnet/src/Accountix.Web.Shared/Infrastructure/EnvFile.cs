using System.Reflection;

namespace Accountix.Web.Infrastructure;

/// <summary>
/// Reads dotenv files into the process environment before configuration is built, so the
/// variables <c>neon deploy</c> pulls into <c>.env.local</c> (DATABASE_URL, the function
/// URLs, NEON_BRANCH) reach the app the same way App Settings do.
/// A real environment variable always wins: a deployment's settings are never overridden
/// by a file sitting next to the binaries. The files do beat appsettings/user-secrets,
/// because the environment wins over both — so a pulled DATABASE_URL is what a local run
/// uses; set <c>ACCOUNTIX_ENV_FILES=0</c> to ignore the files entirely.
/// </summary>
public static class EnvFile
{
    /// <summary>The opt-out: <c>ACCOUNTIX_ENV_FILES=0</c> makes every Load call a no-op.</summary>
    public const string OptOutVariable = "ACCOUNTIX_ENV_FILES";

    /// <summary>
    /// Loads every file found by walking up from the working directory, then from the app's
    /// own folder (<c>dotnet run --project …</c> starts in the project, not the repo root).
    /// The first hit for a name wins; a missing file is not an error.
    /// Only the app's own process loads the files: a test host runs <c>Program.cs</c>
    /// in-process (WebApplicationFactory), and the file's variables would land in the shared
    /// process environment and override the reference database the tests configure.
    /// </summary>
    public static IReadOnlyList<string> Load(string hostAssemblyName, params string[] fileNames) =>
        Load(hostAssemblyName, [Directory.GetCurrentDirectory(), AppContext.BaseDirectory], fileNames);

    /// <summary>The same, with the folders to search named (tests use this).</summary>
    public static IReadOnlyList<string> Load(string hostAssemblyName, IReadOnlyList<string> roots, params string[] fileNames)
    {
        if (Environment.GetEnvironmentVariable(OptOutVariable) == "0"
            || Assembly.GetEntryAssembly()?.GetName().Name != hostAssemblyName)
            return [];
        var set = new List<string>();
        foreach (string name in fileNames)
        {
            string? file = roots.Select(r => Find(r, name)).FirstOrDefault(f => f is not null);
            if (file is not null)
                Apply(file, set);
        }
        return set;
    }

    /// <summary>Loads the named files from one directory (tests use this; no upward walk).</summary>
    public static IReadOnlyList<string> LoadFrom(string directory, params string[] fileNames)
    {
        var set = new List<string>();
        foreach (string name in fileNames)
        {
            string file = Path.Combine(directory, name);
            if (File.Exists(file))
                Apply(file, set);
        }
        return set;
    }

    /// <summary>
    /// KEY=value lines, one per line: blank lines and # comments ignored, an optional
    /// "export " prefix, and an optional pair of surrounding single or double quotes.
    /// </summary>
    public static IEnumerable<(string Key, string Value)> Parse(string text)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            if (line.StartsWith("export ", StringComparison.Ordinal))
                line = line[7..].TrimStart();
            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
                value = value[1..^1];
            if (key.Length > 0)
                yield return (key, value);
        }
    }

    /// <summary>
    /// One file into the environment: the last occurrence of a key wins (dotenv's rule),
    /// a blank value sets nothing (in .NET it would delete the variable, so a blank can
    /// never hide a value), and an already-set variable is left alone.
    /// </summary>
    private static void Apply(string file, List<string> set)
    {
        var pairs = Parse(File.ReadAllText(file)).ToList();
        var done = new HashSet<string>(StringComparer.Ordinal);
        for (int i = pairs.Count - 1; i >= 0; i--)          // backwards: the last occurrence applies
        {
            var (key, value) = pairs[i];
            if (value.Length == 0 || !done.Add(key) || Environment.GetEnvironmentVariable(key) is not null)
                continue;
            Environment.SetEnvironmentVariable(key, value);
            set.Add(key);
        }
    }

    /// <summary>
    /// The nearest <paramref name="name"/> at or above <paramref name="startDirectory"/>, but
    /// never above the repository root: the walk stops (without reading anything further) at
    /// the first folder holding <c>.git</c>, so a stray <c>~/.env</c> or <c>/home/.env</c>
    /// outside the checkout is never loaded.
    /// </summary>
    public static string? Find(string startDirectory, string name)
    {
        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, name);
            if (File.Exists(candidate))
                return candidate;
            if (File.Exists(Path.Combine(dir.FullName, ".git")) || Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return null;
        }
        return null;
    }
}
