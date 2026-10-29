namespace Accountix.E2E.Harness;

/// <summary>
/// Which module serves a URL (the Python blueprints' prefixes, split as the .NET
/// projects are). In production both apps share one domain, so a page that calls a
/// route of a module not moved yet is answered by the Python app; in this suite's
/// host that call is a 404. Such a 404 is noted, not failed — unless the module that
/// owns the route is in this build, where a 404 is a real bug.
/// </summary>
public static class RouteOwners
{
    // Longest prefix first.
    private static readonly (string Prefix, string Module)[] Map =
    [
        ("/inventory/transfer-to-fa", "FixedAssets"),
        ("/inventory/invoices", "Invoicing"), ("/inventory/purchase-invoice", "Invoicing"),
        ("/inventory/customers", "Parties"), ("/inventory/suppliers", "Parties"),
        ("/inventory/purchase-return", "Returns"), ("/invoicing/sales-return", "Returns"),
        ("/inventory/sales", "Orders"), ("/inventory/purchases", "Orders"),
        ("/invoicing/tracking", "Parties"), ("/invoicing/registers", "Parties"),
        ("/inventory", "Inventory"), ("/invoicing", "Invoicing"),
        ("/accounting", "Accounting"), ("/finance", "Finance"), ("/executive", "Executive"),
        ("/fixed-assets", "FixedAssets"), ("/fbr", "Fbr"),
        ("/attendance", "Hr"), ("/leaves", "Hr"), ("/timesheets", "Hr"), ("/ess", "Hr"), ("/mss", "Hr"),
        ("/auth/users", "Hr"), ("/auth/members", "Hr"), ("/auth/profile", "Hr"), ("/auth/change-password", "Hr"),
        ("/auth/account-settings", "Hr"),
        ("/communications", "HrRecords"), ("/digital-files", "HrRecords"), ("/workplace", "HrRecords"), ("/reports", "HrRecords"),
        ("/compensation", "Payroll"), ("/pf", "Payroll"),
        ("/superadmin", "SuperAdmin"),
        ("/auth", "Platform"), ("/portal", "Platform"), ("/settings", "Platform"), ("/company", "Platform"),
        ("/dashboard/", "Platform"), ("/hub", "Platform"), ("/signup", "Platform"),
    ];

    public static string? OwnerOf(string path)
    {
        if (path == "/dashboard")
            return "Hr";
        foreach (var (prefix, module) in Map)
            if (path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal) || path.StartsWith(prefix + "?", StringComparison.Ordinal)
                || prefix.EndsWith('/') && path.StartsWith(prefix, StringComparison.Ordinal))
                return module;
        return path is "/" or "" ? "Platform" : null;
    }

    /// <summary>True when the route belongs to a module this run does not serve (Python answers it in production).</summary>
    public static bool ServedElsewhere(string path, IReadOnlyList<string> modulesServed)
    {
        string? owner = OwnerOf(path);
        return owner is not null && !modulesServed.Contains(owner);
    }
}
