using System.Net;
using Accountix.Platform.Provisioning;

namespace Accountix.Admin.Tests;

/// <summary>
/// E2E finding F6: a new company had no leave types, so Leave → Apply offered nothing.
/// Provisioning now adds hr_app/seed.py's three; admin add-default-leave-types gives them to
/// existing companies that have none.
/// </summary>
public class LeaveTypesTests
{
    /// <summary>hr_app/seed.py, with the model's defaults (requires_approval True, max_carry_forward 0).</summary>
    private static readonly string[] Seed =
    [
        "Casual Leave|CL|12|True|True|False|0",
        "Sick Leave|SL|10|True|True|True|5",
        "Annual Leave|AL|20|True|True|True|10",
    ];

    private static Task<List<string>> TypesAsync(AdminApp app, int cid) => app.InAsync(cid, async (_, db) =>
        (await db.Set<LeaveType>().OrderBy(t => t.Id).ToListAsync())
        .Select(t => $"{t.Name}|{t.Code}|{t.DefaultQuota}|{t.IsPaid}|{t.RequiresApproval}|{t.CarryForward}|{t.MaxCarryForward}").ToList());

    [ReferenceFact]
    public async Task A_new_company_gets_the_seed_leave_types_once_and_leave_apply_offers_them()
    {
        await using var scratch = await ScratchDb.CreateAsync();
        await using var app = await AdminApp.StartAsync(scratch);
        int cid = await app.CompanyAsync("Leave Types Co");
        Assert.Equal(Seed, await TypesAsync(app, cid));

        // Provisioning again (idempotent) adds nothing.
        await app.InAsync(cid, (sp, _) => sp.GetRequiredService<CompanySetup>().ProvisionAsync(cid));
        Assert.Equal(Seed, await TypesAsync(app, cid));

        var r = await app.Host.ClientFor(AdminApp.OwnerB, cid).GetAsync("/leaves/apply");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        string html = await r.Content.ReadAsStringAsync();
        foreach (string name in new[] { "Casual Leave", "Sick Leave", "Annual Leave" })
            Assert.Contains(name, html);
    }

    [ReferenceFact]
    public async Task A_company_with_its_own_leave_types_keeps_them()
    {
        await using var scratch = await ScratchDb.CreateAsync();
        await using var app = await AdminApp.StartAsync(scratch);
        int cid = await app.CompanyAsync("Own Leave Co");
        await app.InAsync(cid, async (sp, db) =>
        {
            await db.Set<LeaveType>().ExecuteDeleteAsync();
            db.Add(new LeaveType { Name = "Leave without pay", Code = "LWP", DefaultQuota = 0, IsPaid = false, RequiresApproval = true, CarryForward = false, MaxCarryForward = 0 });
            await db.SaveChangesAsync();
            Assert.Equal(0, await sp.GetRequiredService<CompanySetup>().EnsureLeaveTypesAsync());
        });
        Assert.Equal(["Leave without pay|LWP|0|False|True|False|0"], await TypesAsync(app, cid));
    }

    [ReferenceFact]
    public async Task The_backfill_adds_them_where_there_are_none_and_leaves_the_rest()
    {
        await using var scratch = await ScratchDb.CreateAsync();
        await using var app = await AdminApp.StartAsync(scratch);
        int provisioned = await app.CompanyAsync("Backfill New");
        int own = await app.CompanyAsync("Backfill Own");
        await app.InAsync(own, async (_, db) =>
        {
            await db.Set<LeaveType>().Where(t => t.Code != "AL").ExecuteDeleteAsync();
        });
        // The reference database's companies (made before provisioning added them) have none.
        int[] old = [1, Reference.Company("a"), Reference.Company("b")];
        foreach (int c in old)
            Assert.Empty(await TypesAsync(app, c));

        var (exit, text) = await app.RunAsync("add-default-leave-types");
        Assert.Equal(0, exit);
        Assert.Contains("Default Company (company 1): added Casual Leave (CL), Sick Leave (SL), Annual Leave (AL)", text);
        Assert.Contains($"Backfill New (company {provisioned}): has 3 leave type(s); left as they are", text);
        Assert.Contains($"Backfill Own (company {own}): has 1 leave type(s); left as they are", text);
        Assert.Contains("Dry run — rolled back", text);
        Assert.Empty(await TypesAsync(app, 1));

        (exit, text) = await app.RunAsync("add-default-leave-types", "--apply");
        Assert.Equal(0, exit);
        Assert.Contains("COMMITTED", text);
        foreach (int c in old)
            Assert.Equal(Seed, await TypesAsync(app, c));
        Assert.Equal(["Annual Leave|AL|20|True|True|True|10"], await TypesAsync(app, own));

        (exit, text) = await app.RunAsync("add-default-leave-types", "--apply");
        Assert.Contains("Nothing to add.", text);

        // One company only.
        await app.InAsync(1, (_, db) => db.Set<LeaveType>().ExecuteDeleteAsync());
        await app.InAsync(old[1], (_, db) => db.Set<LeaveType>().ExecuteDeleteAsync());
        (exit, text) = await app.RunAsync("add-default-leave-types", "--apply", "1");
        Assert.Equal(Seed, await TypesAsync(app, 1));
        Assert.Empty(await TypesAsync(app, old[1]));
    }
}
