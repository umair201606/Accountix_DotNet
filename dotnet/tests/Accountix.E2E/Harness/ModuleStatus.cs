using System.Reflection;
using System.Text.RegularExpressions;

namespace Accountix.E2E.Harness;

/// <summary>
/// Whether a module can be tested in the browser yet: its assembly is in this
/// build (E2EModules in the .csproj) AND its owner marked it done, in
/// docs/modules/&lt;key&gt;.md or the PROGRESS.md table. E2E_FORCE_MODULES=Inventory,Hr
/// runs a module's tests anyway (to try one that is nearly there).
/// </summary>
public static partial class ModuleStatus
{
    /// <summary>Project name (Accountix.&lt;Name&gt;) → docs/modules/&lt;key&gt;.md.</summary>
    public static readonly IReadOnlyDictionary<string, string> DocKeys = new Dictionary<string, string>
    {
        ["Accounting"] = "accounting", ["Finance"] = "finance", ["Executive"] = "executive",
        ["Inventory"] = "inventory", ["Invoicing"] = "invoicing", ["Parties"] = "parties",
        ["Returns"] = "returns", ["Orders"] = "orders", ["Hr"] = "hr", ["HrRecords"] = "hr_records",
        ["Payroll"] = "payroll", ["FixedAssets"] = "fixed_assets", ["Fbr"] = "fbr",
        ["Platform"] = "platform", ["SuperAdmin"] = "superadmin",
    };

    /// <summary>Modules compiled into this test build (E2EModules).</summary>
    public static IReadOnlyList<string> InBuild { get; } =
        (typeof(ModuleStatus).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "E2EModules")?.Value ?? "")
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static readonly HashSet<string> Forced =
        new((Environment.GetEnvironmentVariable("E2E_FORCE_MODULES") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);

    public static bool AssemblyPresent(string name) =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, $"Accountix.{name}.dll"));

    public static Assembly Load(string name) => Assembly.Load(new AssemblyName($"Accountix.{name}"));

    /// <summary>Null when every named module is testable, else why not (the skip reason).</summary>
    public static string? WhyNot(params string[] names)
    {
        foreach (string name in names)
        {
            if (!AssemblyPresent(name) || !InBuild.Contains(name, StringComparer.OrdinalIgnoreCase))
                return $"Module {name} is not in this build: add it to E2EModules in Accountix.E2E.csproj " +
                       $"(or -p:E2EModules=\"...;{name}\") once it compiles.";
            if (Forced.Contains(name))
                continue;
            if (!MarkedDone(name, out string where))
                return $"Module {name} is not marked done yet ({where}). Its browser tests run once it is " +
                       "(or set E2E_FORCE_MODULES).";
        }
        return null;
    }

    /// <summary>
    /// "Done" as the owners write it: a status line saying done/complete at the top of
    /// docs/modules/&lt;key&gt;.md ("**Status**: ✅ done", "Status: complete",
    /// "**Ported**: all ...", a heading with ✅/done/complete), or "✅ done" in the module's row of PROGRESS.md.
    /// </summary>
    public static bool MarkedDone(string name, out string where)
    {
        string key = DocKeys.TryGetValue(name, out var k) ? k : name.ToLowerInvariant();
        string doc = Path.Combine(E2EEnv.DocsDirectory, "modules", key + ".md");
        string progress = Path.Combine(E2EEnv.DocsDirectory, "PROGRESS.md");
        if (File.Exists(progress))
        {
            foreach (string line in File.ReadLines(progress))
            {
                if (!line.StartsWith('|')) continue;
                string first = line.Split('|', StringSplitOptions.TrimEntries).ElementAtOrDefault(1) ?? "";
                bool mine = Regex.IsMatch(first, $@"^(Accountix\.)?{Regex.Escape(name)}\b", RegexOptions.IgnoreCase)
                            || Regex.IsMatch(first, $@"^{Regex.Escape(key.Replace('_', ' '))}\b", RegexOptions.IgnoreCase);
                string status = line.Split('|', StringSplitOptions.TrimEntries).ElementAtOrDefault(2) ?? "";
                if (mine && (DoneInTable().IsMatch(line) || status.StartsWith('✅')))
                {
                    where = "PROGRESS.md";
                    return true;
                }
            }
        }
        if (!File.Exists(doc))
        {
            where = $"no docs/modules/{key}.md";
            return false;
        }
        var lines = File.ReadLines(doc).ToList();
        string head = string.Join('\n', lines.Take(25));
        if (DoneInDoc().IsMatch(head))
        {
            where = $"docs/modules/{key}.md";
            return true;
        }
        // A route table whose rows are all ✅ (❌ marks a route deliberately not moved yet, which the
        // tests leave alone); nothing marked in progress.
        var rows = lines.Where(l => l.StartsWith('|') && (l.Contains('✅') || l.Contains('❌') || InProgress().IsMatch(l))).ToList();
        int done = rows.Count(l => l.Contains('✅'));
        if (done >= 3 && !rows.Any(l => InProgress().IsMatch(l)))
        {
            where = $"docs/modules/{key}.md route table";
            return true;
        }
        where = $"docs/modules/{key}.md has no done status line or finished route table";
        return false;
    }

    [GeneratedRegex(@"⏳|🚧|\bin progress\b|\bWIP\b|\bTODO\b|\bnot started\b", RegexOptions.IgnoreCase)]
    private static partial Regex InProgress();

    [GeneratedRegex(@"✅\s*(done|complete)", RegexOptions.IgnoreCase)]
    private static partial Regex DoneInTable();

    [GeneratedRegex(@"(?im)(^[^|\w]*status\W*[:：]?\W*(✅\s*)?(done|complete|completed|finished)\b)|(^[^|\w]*ported\W*[:：]?\W*(all|every)\b)|(\ball\s+✅)|(^#.*(✅|\b(done|complete)\b))")]
    private static partial Regex DoneInDoc();
}
