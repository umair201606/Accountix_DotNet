using Accountix.Web.Infrastructure;

namespace Accountix.Web.Hosting;

/// <summary>
/// <c>dotnet Accountix.Web.dll admin &lt;command&gt; [--apply] [company id ...]</c>: the maintenance
/// commands (the IAdminCommand services, Accountix.Admin) instead of the web server, on the
/// database the app is configured with (DATABASE_URL or ConnectionStrings__Accountix). The app is
/// built as the server builds it (modules, company filter, interceptors) but never started.
/// Logs go to stderr at Warning and above, so stdout is the command's report. docs/DEPLOY.md.
/// </summary>
public static class AdminHost
{
    public static bool Matches(string[] args) => args.Length > 0 && args[0] == "admin";

    public static async Task<int> RunAsync(string[] args, TextWriter? output = null, CancellationToken ct = default)
    {
        output ??= Console.Out;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],                                  // "--apply 3" is not configuration
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Warning",
            ["Logging:Console:LogToStandardErrorThreshold"] = "Trace",
        });
        // The commands never sign or read a sign-in cookie: a key is not needed to run them.
        if ((builder.Configuration["SECRET_KEY"] ?? builder.Configuration["Accountix:SecretKey"] ?? "").Length < 16)
            builder.Configuration["SECRET_KEY"] = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));

        await using var app = AccountixHost.Build(builder);
        var commands = app.Services.GetServices<IAdminCommand>().OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
        string? name = args.Length > 1 ? args[1] : null;
        if (name is null or "help" or "-h" or "--help")
        {
            Usage(output, commands);
            return name is null ? AdminExit.Usage : AdminExit.Ok;
        }
        var command = commands.FirstOrDefault(c => c.Name == name);
        if (command is null)
        {
            output.WriteLine($"Unknown admin command '{name}'.");
            Usage(output, commands);
            return AdminExit.Usage;
        }
        var parsed = AdminArgs.Parse(args.Skip(2), out string? error);
        if (error is not null)
        {
            output.WriteLine($"{command.Name}: {error}. Usage: admin {command.Name} {command.Arguments}");
            return AdminExit.Usage;
        }
        await using var scope = app.Services.CreateAsyncScope();
        return await command.RunAsync(scope.ServiceProvider, parsed, output, ct);
    }

    private static void Usage(TextWriter output, IReadOnlyList<IAdminCommand> commands)
    {
        output.WriteLine("Usage: dotnet Accountix.Web.dll admin <command> [arguments]");
        output.WriteLine("Uses DATABASE_URL (or ConnectionStrings__Accountix) like the app. Dry run unless --apply.");
        foreach (var c in commands)
            output.WriteLine($"  {c.Name} {c.Arguments}\n      {c.Summary}");
    }
}
