using Accountix.Inventory.Services;

namespace Accountix.Admin.Commands;

/// <summary>
/// tools/repair_opening_stock.py: post the missing journals for opening stock that only the
/// stock ledger holds. For each OPENING stock-ledger row whose product has no posted OPENING
/// journal it posts, on the row's own date, Dr the product's stock account / Cr Opening Balance
/// Equity (StockEntry.JournalOpeningRowAsync, created_by 1 as Python's).
///
/// Only needed when Books Integrity reports "Inventory account = stock valuation" failing with
/// "opening-stock rows ... have no journal". A closed period refuses (reopen it, repair, close
/// again). One transaction per company. A dry run posts and rolls back, so it shows what Books
/// Integrity would say afterwards. A company is rolled back, and the command stops, when a check
/// that passed before would fail after. A second run finds nothing.
/// </summary>
public sealed class RepairOpeningStock : IAdminCommand
{
    public string Name => "repair-opening-stock";
    public string Arguments => "[--apply] [company id ...]";
    public string Summary => "Post the missing journals of opening stock rows that have none (dry run unless --apply).";

    public async Task<int> RunAsync(IServiceProvider sp, AdminArgs args, TextWriter output, CancellationToken ct)
    {
        var db = sp.GetRequiredService<AccountixDbContext>();
        var entry = sp.GetRequiredService<StockEntry>();
        foreach (var (cid, name) in await AdminSql.CompaniesAsync(db, ct))
        {
            if (!args.Includes(cid))
                continue;
            db.Company.Set(cid);
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var rows = await entry.UnjournaledOpeningsAsync(ct);
            output.WriteLine($"{name}: {rows.Count} opening rows without a journal, {AdminSql.Money(rows.Sum(r => r.TotalCost))}");
            if (rows.Count == 0)
            {
                await tx.RollbackAsync(ct);
                continue;
            }
            var before = (await AdminSql.FailuresAsync(sp, cid, ct)).Select(f => f.Key).ToHashSet();
            try
            {
                db.Company.Set(cid);
                foreach (var r in rows)
                    await entry.JournalOpeningRowAsync(r, 1, ct);
                await db.SaveChangesAsync(ct);
            }
            catch (BusinessRuleException e)
            {
                await tx.RollbackAsync(ct);
                output.WriteLine($"REFUSED: {name} (company {cid}): {e.Message} Nothing was posted for this company.");
                return AdminExit.Refused;
            }
            var after = await AdminSql.FailuresAsync(sp, cid, ct);
            var worse = after.Where(f => !before.Contains(f.Key)).ToList();
            if (worse.Count > 0)
            {
                await tx.RollbackAsync(ct);
                output.WriteLine($"REFUSED: {name} (company {cid}): Books Integrity would fail after the repair — "
                                 + AdminSql.Describe(worse) + ". Rolled back; nothing was posted for this company.");
                return AdminExit.Refused;
            }
            output.WriteLine(after.Count == 0
                ? "  Books Integrity passes afterwards"
                : "  Books Integrity still fails afterwards: " + AdminSql.Describe(after));
            if (args.Apply)
            {
                await tx.CommitAsync(ct);
                output.WriteLine($"  posted {rows.Count} journals");
            }
            else
            {
                await tx.RollbackAsync(ct);
                output.WriteLine($"  would post {rows.Count} journals");
            }
        }
        if (!args.Apply)
            output.WriteLine("Dry run — nothing posted. Re-run with --apply.");
        return AdminExit.Ok;
    }
}
