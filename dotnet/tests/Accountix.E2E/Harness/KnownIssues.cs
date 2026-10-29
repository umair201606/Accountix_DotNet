namespace Accountix.E2E.Harness;

/// <summary>
/// Cases that fail because of an open finding in docs/E2E_FINDINGS.md (someone else's
/// module). They are skipped with the finding's id, never passed silently;
/// E2E_KNOWN=run runs them anyway to check a fix. Remove an entry once its case passes.
/// </summary>
public static class KnownIssues
{
    public static bool Run => Environment.GetEnvironmentVariable("E2E_KNOWN") == "run";

    /// <summary>Case key ("page:viewport" or a test name) → finding.</summary>
    private static readonly Dictionary<string, string> Open = new(StringComparer.Ordinal)
    {
        // F1–F5 fixed 29 Oct (coordinator); their cases run in the normal tests again.
    };

    public static string? For(string key)
    {
        if (Open.TryGetValue(key, out var f))
            return f;
        int colon = key.LastIndexOf(':');
        return colon > 0 && Open.TryGetValue(key[..colon] + ":*", out f) ? f : null;
    }

    public static string Reason(string finding) =>
        $"Known issue {finding} (docs/E2E_FINDINGS.md). Set E2E_KNOWN=run to check whether it is fixed.";
}

/// <summary>A browser theory over cases blocked by open findings: skipped unless E2E_KNOWN=run.</summary>
public sealed class KnownIssueTheoryAttribute : TheoryAttribute
{
    public KnownIssueTheoryAttribute(params string[] modules)
    {
        string[] m = modules.Length == 0 ? ["Accounting"] : modules;
        if (Prerequisites.WhyNot(m) is { } why)
            Skip = why;
        else if (!KnownIssues.Run)
            Skip = "Cases blocked by open findings (docs/E2E_FINDINGS.md, ids in the case names). Set E2E_KNOWN=run to check them.";
    }
}

/// <summary>A browser test blocked by an open finding: skipped (with its id) unless E2E_KNOWN=run.</summary>
public sealed class BlockedFactAttribute : FactAttribute
{
    public BlockedFactAttribute(string finding, params string[] modules)
    {
        string[] m = modules.Length == 0 ? ["Accounting"] : modules;
        if (Prerequisites.WhyNot(m) is { } why)
            Skip = why;
        else if (!KnownIssues.Run)
            Skip = KnownIssues.Reason(finding);
    }
}
