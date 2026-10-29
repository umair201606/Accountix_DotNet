using Accountix.Platform.Provisioning;

namespace Accountix.Admin.Commands;

/// <summary>
/// E2E finding F6, for companies made before provisioning added them: give every company that has
/// no leave types the three defaults (CompanySetup.LeaveTypes: Casual / Sick / Annual, as
/// hr_app/seed.py writes them). A company with leave types of its own is left as it is.
/// </summary>
public sealed class AddDefaultLeaveTypes : IAdminCommand
{
    public string Name => "add-default-leave-types";
    public string Arguments => "[--apply] [company id ...]";
    public string Summary => "Give companies with no leave types Casual / Sick / Annual Leave (dry run unless --apply).";

    public async Task<int> RunAsync(IServiceProvider sp, AdminArgs args, TextWriter output, CancellationToken ct)
    {
        var db = sp.GetRequiredService<AccountixDbContext>();
        var setup = sp.GetRequiredService<CompanySetup>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        int changed = 0;
        foreach (var (cid, name) in await AdminSql.CompaniesAsync(db, ct))
        {
            if (!args.Includes(cid))
                continue;
            db.Company.Set(cid);
            int have = await db.Set<LeaveType>().CountAsync(ct);
            if (have > 0)
            {
                output.WriteLine($"{name} (company {cid}): has {have} leave type(s); left as they are");
                continue;
            }
            await setup.EnsureLeaveTypesAsync(ct);
            changed++;
            output.WriteLine($"{name} (company {cid}): added " +
                             string.Join(", ", CompanySetup.LeaveTypes.Select(t => $"{t.Name} ({t.Code})")));
        }
        if (changed == 0)
            output.WriteLine("Nothing to add.");
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
}
