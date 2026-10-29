using System.Text.Json;
using Accountix.Core.Entities;
using Accountix.Platform.Services;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Platform.Tests;

/// <summary>The period-close checklist (shared/periods.py close_checklist) gives Python's lines, depreciation included
/// (golden: Golden/CloseChecklist.json, captured 29 Oct 2026).</summary>
public class PeriodChecklistTests
{
    /// <summary>A fresh company with last year's period holding five assets (two due) and one draft voucher: (company, period).</summary>
    private static async Task<(int Company, int Period)> CompanyWithAssetsAsync(ModuleHost host)
    {
        var (cid, admin) = await P.FreshCompanyAsync(host);
        int pid;
        await using (var db = P.Db(cid))
        {
            var period = await db.Set<AccountingPeriod>().Where(p => p.StartDate.Year == DateTime.UtcNow.Year - 1).SingleAsync();
            pid = period.Id;
            var cat = await db.Set<FaCategory>().OrderBy(c => c.Id).FirstAsync();
            DateOnly start = period.StartDate;
            // Due: bought at the start of the year, nothing charged. Not due: fully depreciated already; bought after the end; disposed.
            db.AddRange(
                Asset("DUE-1", cat.Id, start.AddDays(10), 120000, 5, "straight_line", "active"),
                Asset("DUE-2", cat.Id, start.AddMonths(-20), 90000, 3, "declining_balance", "active"),
                Asset("LATE", cat.Id, period.EndDate.AddDays(5), 50000, 5, "straight_line", "active"),
                Asset("GONE", cat.Id, start, 50000, 5, "straight_line", "disposed"),
                Asset("NEW", cat.Id, period.EndDate.AddDays(-10), 40000, 4, "straight_line", "active"));
            db.Add(new AccountingVoucher
            {
                VoucherType = "CPV", VoucherNumber = "CPV-CL-" + P.Tag(), VoucherDate = start.AddDays(40).ToDateTime(new TimeOnly(9, 0)),
                Status = "unapproved", CreatedBy = admin, CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        return (cid, pid);
    }

    [ReferenceFact]
    public async Task The_close_checklist_lists_what_python_listed_on_a_company_with_assets()
    {
        await using var host = await P.HostAsync();
        var (cid, pid) = await CompanyWithAssetsAsync(host);
        var (scope, sdb) = P.Scope(host, cid);
        using (scope)
        {
            var period = await sdb.Set<AccountingPeriod>().SingleAsync(p => p.Id == pid);
            var net = await PeriodClose.ChecklistAsync(sdb, period, scope.ServiceProvider);
            Golden.Match("CloseChecklist", net.Select(l => P.Norm(l, years: true)).ToList());
            Assert.Contains(net, l => l.StartsWith("depreciation not yet posted to ") && l.EndsWith(" on 2 asset(s)"));
            Assert.Contains("1 unapproved accounting voucher(s)", net);
        }
    }

    private static FixedAsset Asset(string code, int category, DateOnly bought, decimal cost, int life, string method, string status) => new()
    {
        AssetCode = code + "-" + P.Tag(), Name = code, CategoryId = category, PurchaseDate = bought, PurchaseCost = cost, UsefulLife = life,
        DepreciationMethod = method, SalvageValue = 0, AccumulatedDepreciation = 0, CurrentBookValue = cost, Status = status, IsActive = true,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };
}
