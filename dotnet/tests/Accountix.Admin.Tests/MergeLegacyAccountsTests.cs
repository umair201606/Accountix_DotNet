namespace Accountix.Admin.Tests;

/// <summary>admin merge-legacy-accounts: tests/e2e/test_legacy_accounts.py's merge-tool tests, and parity with the Python tool.</summary>
public class MergeLegacyAccountsTests
{
    private static readonly string[] Names = AdminApp.Legacy.Select(l => l.Name).ToArray();

    /// <summary>A company with 3 parked accounts of each name, each with a journal; selections and an asset pointing at some.</summary>
    private static async Task<(int Company, Dictionary<string, List<int>> Made)> WorldAsync(AdminApp app)
    {
        int cid = await app.CompanyAsync("Legacy Accounts Co");
        var made = await app.ParkDuplicatesAsync(cid, copies: 3, tag: "A");
        await app.InAsync(cid, async (sp, db) =>
        {
            // References outside the journal: a dashboard selection of the kept account and of a duplicate
            // (one key), a selection of a duplicate only, an asset's expense account on a duplicate.
            var g = made["Gratuity Expense"];
            var e = made["EOBI Payable"];
            foreach (int aid in new[] { g[0], g[2], e[1] })
                db.Add(new ExecAccountSelection { AccountId = aid, IncludeChildren = false });
            int cat = await db.Set<FaCategory>().OrderBy(c => c.Id).Select(c => c.Id).FirstAsync();
            db.Add(new FixedAsset
            {
                AssetCode = "FA-LEG-1", Name = "Press Machine", CategoryId = cat, PurchaseDate = new DateOnly(2025, 1, 1),
                PurchaseCost = 1000, SalvageValue = 0, UsefulLife = 5, DepreciationMethod = "straight_line", CurrentBookValue = 1000,
                Status = "draft", DepExpenseAccountId = made["Impairment Loss on Fixed Assets"][2],
            });
            await db.SaveChangesAsync();
        });
        return (cid, made);
    }

    private static Task<List<int>> AccountsNamedAsync(AdminApp app, int cid, string name) => app.InAsync(cid, (_, db) =>
        db.Set<ChartOfAccount>().Where(a => a.Name == name).OrderBy(a => a.Id).Select(a => a.Id).ToListAsync());

    /// <summary>_by_name: posted (debit, credit) per account name, and the company's totals.</summary>
    private static Task<(Dictionary<string, (decimal, decimal)> Per, (decimal, decimal) Total)> ByNameAsync(AdminApp app, int cid) =>
        app.InAsync(cid, async (_, db) =>
        {
            var rows = await (from l in db.Set<JournalLine>()
                              join e in db.Set<JournalEntry>() on l.JournalEntryId equals e.Id
                              join a in db.Set<ChartOfAccount>() on l.AccountId equals a.Id
                              where e.IsPosted == true
                              group l by a.Name into g
                              select new { Name = g.Key, Dr = g.Sum(x => x.Debit ?? 0), Cr = g.Sum(x => x.Credit ?? 0) }).ToListAsync();
            var per = rows.ToDictionary(r => r.Name, r => (Math.Round(r.Dr, 2), Math.Round(r.Cr, 2)));
            return (Names.ToDictionary(n => n, n => per.GetValueOrDefault(n)), (per.Values.Sum(v => v.Item1), per.Values.Sum(v => v.Item2)));
        });

    [ReferenceFact]
    public async Task Folds_parked_duplicates_into_one_account_dry_run_first_and_a_second_run_finds_nothing()
    {
        await using var scratch = await ScratchDb.CreateAsync();
        await using var app = await AdminApp.StartAsync(scratch);
        var (cid, made) = await WorldAsync(app);
        foreach (string n in Names)
            Assert.Equal(3, (await AccountsNamedAsync(app, cid, n)).Count);
        var (before, tbBefore) = await ByNameAsync(app, cid);
        Assert.Empty(await app.FailingAsync(cid));

        // Dry run: reports, rolls back.
        var (exit, text) = await app.RunAsync("merge-legacy-accounts", cid.ToString());
        Assert.Equal(0, exit);
        Assert.Contains("Dry run", text);
        Assert.Contains("Books Integrity passes", text);
        Assert.Contains("EOBI Payable [2125]: keep", text);
        Assert.Equal(Names.Length, text.Split('\n').Count(l => l.StartsWith("  ") && l.Contains("]: keep ")));
        foreach (string n in Names)
            Assert.Equal(3, (await AccountsNamedAsync(app, cid, n)).Count);

        // Apply.
        (exit, text) = await app.RunAsync("merge-legacy-accounts", "--apply", cid.ToString());
        Assert.Equal(0, exit);
        Assert.Contains("COMMITTED", text);
        Assert.Contains("repointed 2 row(s) of journal_lines.account_id", text);
        Assert.Contains("dropped 1 row(s) of exec_account_selections.account_id", text);
        Assert.Contains("repointed 1 row(s) of fixed_assets.dep_expense_account_id", text);
        foreach (string n in Names)
            Assert.Equal([made[n][0]], await AccountsNamedAsync(app, cid, n));
        var (after, tbAfter) = await ByNameAsync(app, cid);
        Assert.Equal(before, after);           // each name's postings summed on the one account
        Assert.Equal(tbBefore, tbAfter);       // trial balance unchanged
        await app.InAsync(cid, async (_, db) =>
        {
            var g = made["Gratuity Expense"];
            var e = made["EOBI Payable"];
            var sel = await db.Set<ExecAccountSelection>().Select(s => s.AccountId).ToListAsync();
            Assert.Contains(g[0], sel);
            Assert.Contains(e[0], sel);
            Assert.Empty(sel.Intersect(g.Skip(1).Concat(e.Skip(1))));
            Assert.Equal(1, sel.Count(a => a == g[0]));
            Assert.Equal(made["Impairment Loss on Fixed Assets"][0],
                         await db.Set<FixedAsset>().Where(a => a.Name == "Press Machine").Select(a => a.DepExpenseAccountId).SingleAsync());
            var gone = made.Values.SelectMany(ids => ids.Skip(1)).ToList();
            Assert.False(await db.Set<ChartOfAccount>().AnyAsync(a => gone.Contains(a.Id)));
        });
        Assert.Empty(await app.FailingAsync(cid));

        // A second run finds nothing.
        (exit, text) = await app.RunAsync("merge-legacy-accounts", "--apply", cid.ToString());
        Assert.Equal(0, exit);
        Assert.Contains("Nothing to merge.", text);
    }

    [ReferenceFact]
    public async Task Refuses_books_that_do_not_tie_and_writes_nothing()
    {
        await using var scratch = await ScratchDb.CreateAsync();
        await using var app = await AdminApp.StartAsync(scratch);
        int cid = await app.CompanyAsync("Legacy Refusal Co");
        var made = await app.ParkDuplicatesAsync(cid, copies: 2, tag: "B");
        // Knock the reporting table off the ledger: Books Integrity fails.
        await app.InAsync(cid, (_, db) => db.Database.ExecuteSqlAsync(
            $"UPDATE gl_daily_balances SET debit = debit + 1 WHERE company_id = {cid} AND account_id = {made["Gratuity Expense"][0]}"));

        var (exit, text) = await app.RunAsync("merge-legacy-accounts", "--apply", cid.ToString());
        Assert.Equal(1, exit);
        Assert.Contains("REFUSED:", text);
        Assert.Contains("fails before the merge", text);
        Assert.Contains("Reporting table matches the ledger", text);
        Assert.Equal(2, (await AccountsNamedAsync(app, cid, "Gratuity Expense")).Count);     // nothing written

        // rebuild-gl-summary puts the reporting table right; then the merge goes through.
        (exit, text) = await app.RunAsync("rebuild-gl-summary", "--apply", cid.ToString());
        Assert.Equal(0, exit);
        Assert.Contains("COMMITTED", text);
        (exit, text) = await app.RunAsync("merge-legacy-accounts", "--apply", cid.ToString());
        Assert.Equal(0, exit);
        Assert.Contains("COMMITTED", text);
        Assert.Single(await AccountsNamedAsync(app, cid, "Gratuity Expense"));
    }

    [ReferenceFact]
    public async Task Only_the_companies_named_are_touched()
    {
        await using var scratch = await ScratchDb.CreateAsync();
        await using var app = await AdminApp.StartAsync(scratch);
        int one = await app.CompanyAsync("Legacy One");
        int two = await app.CompanyAsync("Legacy Two");
        await app.ParkDuplicatesAsync(one, 2, "O");
        await app.ParkDuplicatesAsync(two, 2, "T");
        var (exit, text) = await app.RunAsync("merge-legacy-accounts", "--apply", one.ToString());
        Assert.Equal(0, exit);
        Assert.Contains($"Legacy One (company {one}):", text);
        Assert.DoesNotContain("Legacy Two", text);
        Assert.Single(await AccountsNamedAsync(app, one, "EOBI Payable"));
        Assert.Equal(2, (await AccountsNamedAsync(app, two, "EOBI Payable")).Count);
    }

}
