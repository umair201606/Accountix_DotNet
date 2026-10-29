using Accountix.TestSupport;

namespace Accountix.E2E.Harness;

/// <summary>Why a browser test cannot run here (null: it can).</summary>
internal static class Prerequisites
{
    public static string? WhyNot(string[] modules, bool perf = false)
    {
        if (!E2EEnv.DatabaseAvailable)
            return $"Reference database copy '{E2EEnv.DbKey}' not available (dotnet/tools/reference.json, pgserver running).";
        if (perf && !Perf.Available)
            return "Speed-test database not available (dotnet/tools/perf.json).";
        if (ModuleStatus.WhyNot(modules) is { } why)
            return why;
        return Browsers.Unavailable;
    }
}

/// <summary>
/// A browser test of the named modules (default: Accounting). Skipped, with the
/// reason, when the database, the browser or a module is not ready — never a silent pass.
/// </summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute(params string[] modules)
    {
        Modules = modules.Length == 0 ? ["Accounting"] : modules;
        if (Prerequisites.WhyNot(Modules) is { } why)
            Skip = why;
    }

    public string[] Modules { get; }
}

/// <inheritdoc cref="E2EFactAttribute"/>
public sealed class E2ETheoryAttribute : TheoryAttribute
{
    public E2ETheoryAttribute(params string[] modules)
    {
        Modules = modules.Length == 0 ? ["Accounting"] : modules;
        if (Prerequisites.WhyNot(Modules) is { } why)
            Skip = why;
    }

    public string[] Modules { get; }
}

/// <summary>A browser timing on the full-year speed-test database (read-only).</summary>
public sealed class PerfE2EFactAttribute : FactAttribute
{
    public PerfE2EFactAttribute(params string[] modules)
    {
        if (Prerequisites.WhyNot(modules, perf: true) is { } why)
            Skip = why;
    }
}

/// <summary>The four widths every page must work at.</summary>
public sealed record Viewport(string Name, int Width, int Height)
{
    public static readonly Viewport Desktop = new("desktop", 1366, 768);
    public static readonly Viewport Phone375 = new("375", 375, 812);
    public static readonly Viewport Phone360 = new("360", 360, 780);
    public static readonly Viewport Phone320 = new("320", 320, 640);

    public static readonly Viewport[] All = [Desktop, Phone375, Phone360, Phone320];

    public static Viewport Named(string name) => All.Single(v => v.Name == name);

    public bool IsPhone => Width <= 768;

    public override string ToString() => Name;
}
