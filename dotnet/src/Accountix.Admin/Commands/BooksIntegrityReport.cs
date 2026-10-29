namespace Accountix.Admin.Commands;

/// <summary>
/// Executive → Books Integrity from the command line, read-only: every check for each company
/// (or those named). Run it before and after the other commands. Exit code 1 when a check fails.
/// </summary>
public sealed class BooksIntegrityReport : IAdminCommand
{
    public string Name => "books-integrity";
    public string Arguments => "[company id ...]";
    public string Summary => "Run every Books Integrity check for each company (read-only).";

    public async Task<int> RunAsync(IServiceProvider sp, AdminArgs args, TextWriter output, CancellationToken ct)
    {
        if (args.Apply)
        {
            output.WriteLine($"{Name} only reads; it takes no --apply.");
            return AdminExit.Usage;
        }
        var db = sp.GetRequiredService<AccountixDbContext>();
        bool allOk = true;
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        foreach (var (cid, name) in await AdminSql.CompaniesAsync(db, ct))
        {
            if (!args.Includes(cid))
                continue;
            db.Company.Set(cid);
            db.ChangeTracker.Clear();
            var checks = await ActivatorUtilities.CreateInstance<Executive.Services.BooksIntegrity>(sp).RunAllAsync(ct);
            int passed = checks.Count(c => c.Ok);
            allOk &= passed == checks.Count;
            output.WriteLine($"{name} (company {cid}): {passed}/{checks.Count} pass");
            foreach (var c in checks.Where(c => !c.Ok))
                output.WriteLine($"  FAIL {c.Label}: expected {AdminSql.Num(c.Expected)} got {AdminSql.Num(c.Actual)} ({c.Detail})");
        }
        await tx.RollbackAsync(ct);
        return allOk ? AdminExit.Ok : AdminExit.Refused;
    }
}
