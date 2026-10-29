using Accountix.Accounting.Services;
using Accountix.Executive.Services;

namespace Accountix.Admin.Commands;

/// <summary>
/// tools/rebuild_gl_summary.py: rebuild the reporting table (gl_daily_balances) from the posted
/// ledger, for the companies named or every company (GlSummary.RebuildAsync). Only needed when
/// Books Integrity reports "Reporting table matches the ledger" failing (e.g. after rows were
/// changed with raw SQL outside the app). Shows that check for each company before and after.
/// Unlike the Python script, a dry run unless --apply, like every admin command.
/// </summary>
public sealed class RebuildGlSummary : IAdminCommand
{
    public string Name => "rebuild-gl-summary";
    public string Arguments => "[--apply] [company id ...]";
    public string Summary => "Rebuild the reporting table gl_daily_balances from the posted ledger (dry run unless --apply).";

    public async Task<int> RunAsync(IServiceProvider sp, AdminArgs args, TextWriter output, CancellationToken ct)
    {
        var db = sp.GetRequiredService<AccountixDbContext>();
        var companies = (await AdminSql.CompaniesAsync(db, ct)).Where(c => args.Includes(c.Id)).ToList();
        foreach (int id in args.Companies.Where(id => companies.All(c => c.Id != id)))
        {
            output.WriteLine($"No company {id}.");
            return AdminExit.Usage;
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var before = new Dictionary<int, IntegrityCheck>();
        foreach (var (cid, _) in companies)
            before[cid] = await CheckAsync(sp, cid, ct);
        if (args.Companies.Count == 0)
            await GlSummary.RebuildAsync(db, null, ct);
        else
            foreach (int cid in args.Companies.Distinct())
                await GlSummary.RebuildAsync(db, cid, ct);
        foreach (var (cid, name) in companies)
        {
            var after = await CheckAsync(sp, cid, ct);
            output.WriteLine($"{name} (company {cid}): reporting table {State(before[cid])} before, {State(after)} after");
        }
        output.WriteLine("Rebuilt gl_daily_balances " + (args.Companies.Count > 0
            ? $"for company {string.Join(", ", args.Companies.Distinct())}" : "for every company"));
        if (args.Apply)
        {
            await tx.CommitAsync(ct);
            output.WriteLine("COMMITTED");
        }
        else
        {
            await tx.RollbackAsync(ct);
            output.WriteLine("Dry run — rolled back. Re-run with --apply to commit.");
        }
        return AdminExit.Ok;
    }

    private static Task<IntegrityCheck> CheckAsync(IServiceProvider sp, int companyId, CancellationToken ct)
    {
        var db = sp.GetRequiredService<AccountixDbContext>();
        db.Company.Set(companyId);
        db.ChangeTracker.Clear();
        return ActivatorUtilities.CreateInstance<BooksIntegrity>(sp).ReportingTableAsync(ct);
    }

    private static string State(IntegrityCheck c) => c.Ok ? "matches the ledger" : $"differs ({c.Detail})";
}
