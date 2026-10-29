using Accountix.Web.Hosting;

// Maintenance commands instead of the server: dotnet Accountix.Web.dll admin <command> (AdminHost).
if (AdminHost.Matches(args))
{
    Environment.ExitCode = await AdminHost.RunAsync(args);
    return;
}

// Schema migrations instead of the server: dotnet Accountix.Web.dll migrate [--status|--check|--describe].
if (MigrateCommand.Matches(args))
{
    Environment.ExitCode = await MigrateCommand.RunAsync(args[1..]);
    return;
}

// Everything is in AccountixHost.Build, so the tests boot the same app
// (shared key ring, compression, HSTS, Sentry...). docs/DEPLOY.md.
var app = AccountixHost.Build(WebApplication.CreateBuilder(args));
// Accountix:MigrateOnStart (on in Production): the schema first, then the key ring and requests.
MigrateCommand.OnStart(app);
app.Run();

public partial class Program;
