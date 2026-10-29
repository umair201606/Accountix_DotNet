using System.Diagnostics;

namespace Accountix.Admin.Tests;

/// <summary>
/// `dotnet Accountix.Web.dll admin ...` as the user runs it on a server: a separate process, the
/// Production environment, the database from ConnectionStrings__Accountix, no SECRET_KEY, and
/// stdout holding only the command's report (logs go to stderr).
/// </summary>
public class CommandLineTests
{
    private static (int Exit, string Out, string Err) Run(ScratchDb db, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet", ["Accountix.Web.dll", .. args])
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        psi.Environment["ConnectionStrings__Accountix"] = db.ConnectionString;
        psi.Environment.Remove("DATABASE_URL");
        psi.Environment.Remove("SECRET_KEY");
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromMinutes(3)))
        {
            p.Kill(true);
            throw new TimeoutException(string.Join(' ', args));
        }
        return (p.ExitCode, stdout.Result, stderr.Result);
    }

    [ReferenceFact]
    public async Task The_commands_run_from_the_command_line()
    {
        await using var scratch = await ScratchDb.CreateAsync();
        int cid;
        await using (var app = await AdminApp.StartAsync(scratch))
        {
            cid = await app.CompanyAsync("Command Line Co");
            await app.ParkDuplicatesAsync(cid, 2, "C");
        }

        var usage = Run(scratch, "admin");
        Assert.Equal(2, usage.Exit);
        foreach (string name in new[] { "merge-legacy-accounts", "repair-opening-stock", "remove-faulty-demo-records",
                                        "rebuild-gl-summary", "add-default-leave-types", "books-integrity" })
            Assert.True(usage.Out.Contains($"  {name} "), $"{name}:\n{usage.Out}\n{usage.Err}");

        var dry = Run(scratch, "admin", "merge-legacy-accounts", cid.ToString());
        Assert.True(dry.Exit == 0, dry.Out + dry.Err);
        Assert.Contains("Dry run", dry.Out);
        Assert.Contains("EOBI Payable [2125]", dry.Out);
        Assert.DoesNotContain("\"LogLevel\"", dry.Out);              // no JSON log lines in the report

        var done = Run(scratch, "admin", "merge-legacy-accounts", "--apply", cid.ToString());
        Assert.True(done.Exit == 0 && done.Out.Contains("COMMITTED"), done.Out + done.Err);
        var again = Run(scratch, "admin", "merge-legacy-accounts", "--apply", cid.ToString());
        Assert.True(again.Exit == 0 && again.Out.Contains("Nothing to merge."), again.Out + again.Err);

        var check = Run(scratch, "admin", "books-integrity", cid.ToString());
        Assert.True(check.Exit == 0, check.Out + check.Err);
        Assert.Contains($"Command Line Co (company {cid}): 11/11 pass", check.Out);

        var unknown = Run(scratch, "admin", "merge-legacy-acounts");
        Assert.Equal(2, unknown.Exit);
        Assert.Contains("Unknown admin command 'merge-legacy-acounts'.", unknown.Out);
        var typo = Run(scratch, "admin", "repair-opening-stock", "--aply");
        Assert.Equal(2, typo.Exit);
        Assert.Contains("unknown argument '--aply'", typo.Out);

        await using var db = scratch.NewDb(cid);
        Assert.Equal(1, await db.Set<ChartOfAccount>().CountAsync(a => a.Name == "EOBI Payable"));
    }
}
