using System.Runtime.CompilerServices;
using Accountix.TestSupport;

namespace Accountix.E2E.Harness;

/// <summary>Where the suite runs: its database copy, ports, people and screenshot folder.</summary>
public static class E2EEnv
{
#pragma warning disable CA2255 // a test assembly: pick the e2e copy before anything reads Reference
    [ModuleInitializer]
    internal static void SelectDatabase()
    {
        // The suite writes vouchers, accounts and reconciliations: always its own copy
        // (accountix_ref_e2e), never another module's. ACCOUNTIX_TEST_DB may name another copy on purpose.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ACCOUNTIX_TEST_DB")))
            Environment.SetEnvironmentVariable("ACCOUNTIX_TEST_DB", "e2e");
    }
#pragma warning restore CA2255

    /// <summary>The app under test (reference e2e copy). 5315–5320 belong to this suite.</summary>
    public const int AppPort = 5315;
    /// <summary>The app on the full-year speed-test database (read-only).</summary>
    public const int PerfPort = 5316;

    public static string DbKey => Environment.GetEnvironmentVariable("ACCOUNTIX_TEST_DB") ?? "e2e";

    /// <summary>The e2e copy exists (reference.json lists it and Postgres answers).</summary>
    public static bool DatabaseAvailable =>
        Reference.Available
        && Reference.Json.TryGetProperty("databases", out var dbs) && dbs.TryGetProperty(DbKey, out _);

    public static string ConnectionString => Reference.ConnectionString;

    // ── people in the reference database (dotnet/tools/make_reference_db.py) ──
    /// <summary>Admin of company B (fresh, fully provisioned): the suite's writer.</summary>
    public static int OwnerB => Reference.UserId(Reference.Email("owner_b"));
    public static int CompanyB => Reference.Company("b");
    /// <summary>Admin of company A and an employee in B: two companies in the switcher.</summary>
    public static int OwnerA => Reference.UserId(Reference.Email("owner_a"));
    /// <summary>Company A: the suite never writes vouchers here, so its lists stay empty.</summary>
    public static int CompanyA => Reference.Company("a");
    /// <summary>A manager in B with only journal_vouchers view+create and chart_of_accounts view.</summary>
    public static int Restricted => Reference.UserId(Reference.Email("restricted"));
    /// <summary>Super admin, member of company 1 "Default" (the seeded demo data).</summary>
    public static int SuperAdmin => SuperAdminId.Value;
    public const int DefaultCompany = 1;

    private static readonly Lazy<int> SuperAdminId = new(() =>
    {
        using var db = Reference.NewDb();
        return db.Users.Where(u => u.Email == "admin@gmail.com").Select(u => u.Id).First();
    });

    /// <summary>Screenshots of failed tests and of findings.</summary>
    public static string Screenshots
    {
        get
        {
            string dir = Environment.GetEnvironmentVariable("E2E_SCREENSHOTS") is { Length: > 0 } s ? s
                : Path.Combine(ScratchRoot(), "e2e-screens");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string ScratchRoot()
    {
        // The session scratchpad when it exists (docs/E2E.md), else the temp folder.
        const string scratch = "/private/tmp/claude-501/-Users-apple-Downloads-accountix-erp-main/5a911460-b143-4a75-bfdd-3af0a155b1c4/scratchpad";
        return Directory.Exists(scratch) ? scratch : Path.GetTempPath();
    }

    /// <summary>docs/ folder of the .NET solution (module status files live in docs/modules), next to Accountix.slnx.</summary>
    public static string DocsDirectory => Path.Combine(Reference.DotnetRoot, "docs");
}
