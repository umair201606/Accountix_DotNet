namespace Accountix.Web.Infrastructure;

/// <summary>
/// A maintenance command of the app, run instead of the web server against the configured
/// database: <c>dotnet Accountix.Web.dll admin &lt;name&gt; [--apply] [company id ...]</c>
/// (Accountix.Web/Hosting/AdminHost). A module registers each as a singleton
/// <see cref="IAdminCommand"/>. Every command is a dry run unless <c>--apply</c> is given.
/// </summary>
public interface IAdminCommand
{
    /// <summary>The word after "admin", e.g. "merge-legacy-accounts".</summary>
    string Name { get; }

    /// <summary>Arguments after the name, e.g. "[--apply] [company id ...]".</summary>
    string Arguments { get; }

    /// <summary>One line: what it does.</summary>
    string Summary { get; }

    /// <summary>Runs in <paramref name="services"/> (one scope: one DbContext). Returns the process exit code.</summary>
    Task<int> RunAsync(IServiceProvider services, AdminArgs args, TextWriter output, CancellationToken ct);
}

/// <summary>Exit codes of admin commands.</summary>
public static class AdminExit
{
    public const int Ok = 0;
    /// <summary>Refused or aborted: nothing was written.</summary>
    public const int Refused = 1;
    public const int Usage = 2;
}

/// <summary>"--apply" and company ids (bare numbers), as the Python tools read them; anything else is refused.</summary>
public sealed record AdminArgs(bool Apply, IReadOnlyList<int> Companies)
{
    public static AdminArgs Parse(IEnumerable<string> args, out string? error)
    {
        bool apply = false;
        var companies = new List<int>();
        error = null;
        foreach (string a in args)
        {
            if (a == "--apply")
                apply = true;
            else if (a.Length > 0 && a.All(char.IsAsciiDigit) && int.TryParse(a, out int id))
                companies.Add(id);
            else
            {
                error = $"unknown argument '{a}'";
                break;
            }
        }
        return new AdminArgs(apply, companies);
    }

    /// <summary>No ids given = every company.</summary>
    public bool Includes(int companyId) => Companies.Count == 0 || Companies.Contains(companyId);
}
