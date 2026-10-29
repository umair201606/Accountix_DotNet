using Accountix.Accounting.Services;
using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.Core.Shared;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Accountix.Accounting.Tests;

/// <summary>
/// The posting services on the accounting copy (ACCOUNTIX_TEST_DB=accounting). Each test uses
/// its own dates and numbers, so the suite can run again on the same copy; the account
/// resolution is checked against the Python app's answer captured in Golden/LedgerAccounts.json.
/// </summary>
public class LedgerTests
{
    private static (AccountixDbContext Db, CompanyContext Company) Db(int companyId)
    {
        var company = new CompanyContext();
        company.Set(companyId);
        var options = new DbContextOptionsBuilder<AccountixDbContext>()
            .UseNpgsql(Reference.ConnectionString)
            .AddInterceptors(new GlSummaryInterceptor())
            .Options;
        return (new AccountixDbContext(options, company), company);
    }

    private static int DefaultCompany()
    {
        using var db = Reference.NewDb();
        return db.Companies.Single(c => c.Slug == "default").Id;
    }

    private static int AdminId()
    {
        using var db = Reference.NewDb();
        return db.Users.Single(u => u.Email == "admin@gmail.com").Id;
    }

    /// <summary>gl_daily_balances against a fresh sum of the posted journal lines, for one company.</summary>
    private static async Task<List<string>> SummaryDifferences(AccountixDbContext db, int cid)
    {
        const string sql = """
            WITH truth AS (
              SELECT COALESCE(jl.company_id, e.company_id) c, jl.account_id a, COALESCE(jl.label_id, 0) l,
                     CAST(e.entry_date AS DATE) d, SUM(jl.debit) dr, SUM(jl.credit) cr, COUNT(*) n
              FROM journal_lines jl JOIN journal_entries e ON e.id = jl.journal_entry_id
              WHERE e.is_posted AND COALESCE(jl.company_id, e.company_id) = {0}
              GROUP BY 1, 2, 3, 4)
            SELECT concat_ws(' ', coalesce(t.a, g.account_id), coalesce(t.d, g.day), t.dr, g.debit, t.cr, g.credit, t.n, g.line_count) AS "Value"
            FROM truth t FULL JOIN (SELECT * FROM gl_daily_balances WHERE company_id = {0}) g
              ON g.account_id = t.a AND g.label_id = t.l AND g.day = t.d
            WHERE t.dr IS DISTINCT FROM g.debit OR t.cr IS DISTINCT FROM g.credit OR t.n IS DISTINCT FROM g.line_count
            """;
        return await db.Database.SqlQueryRaw<string>(sql, cid).ToListAsync();
    }

    /// <summary>The get_or_create_account calls the Python app's modules make (code, name, type, parent code).</summary>
    private static readonly (string Code, string Name, string Type, string? Parent)[] Legacy =
    [
        ("5710", "Impairment Loss on Fixed Assets", "expense", null),
        ("2125", "EOBI Payable", "liability", "212"),
        ("5123", "EOBI Employer Expense", "expense", "512"),
        ("5124", "Gratuity Expense", "expense", null),
        ("5125", "Leave Encashment Expense", "expense", null),
        ("5126", "PF Profit Credited to Members", "expense", null),
        ("5121", "Salary Expense", "expense", "512"),
        ("5122", "PF Employer Expense", "expense", "512"),
        ("2121", "Salary Payable", "liability", "212"),
        ("2122", "Income Tax Payable", "liability", "212"),
        ("2123", "PF Payable", "liability", "212"),
        ("2124", "Loan Deductions Clearing", "liability", "212"),
        ("5700", "Consumption Expense", "expense", null),
        ("5800", "Scrap/Write-off", "expense", null),
        ("5900", "Inventory Adjustment", "expense", null),
        ("1200", "Inventory", "asset", null),
        ("2-01-02-02-0004", "Other Payroll Deductions Payable", "liability", null),
        ("4-03-01-01-0099", "Golden Other Income", "revenue", null),
    ];

    private static readonly string[] EntityKinds = ["supplier", "customer", "product", "employee", "loan", "fixed_asset", "accum_dep"];

    private static async Task<Dictionary<string, object?>> ShapeAsync(AccountixDbContext db, ChartOfAccount a) => new()
    {
        ["code"] = a.Code, ["name"] = a.Name, ["type"] = a.Type, ["level"] = a.Level,
        ["parent"] = a.ParentId is int p ? await db.Set<ChartOfAccount>().Where(x => x.Id == p).Select(x => x.Code).SingleAsync() : null,
        ["is_fixed"] = a.IsFixed == true, ["is_active"] = a.IsActive == true,
    };

    /// <summary>
    /// Golden (Golden/LedgerAccounts.json, capture/ledger_accounts.py): on a freshly provisioned company
    /// every posting role, every legacy code the modules ask for (a code with no slot in the fixed chart is
    /// parked once and then found again by name — 5710, 2125, 5123–5126 used to open a new account on every
    /// call) and entity accounts of every kind (made, found again, renamed) resolve to the accounts Python
    /// made: code, name, type, level, parent.
    /// </summary>
    [ReferenceFact]
    public async Task Accounts_resolve_as_python_resolves_them()
    {
        var golden = Golden.Load("LedgerAccounts");
        int cid = await World.CompanyAsync("golden-ledger", "Golden Ledger");
        var (db, _) = Db(cid);
        await using var _db = db;
        var ledger = new Ledger(db, new Periods(db));

        var roles = new Dictionary<string, object?>();
        foreach (var (role, _) in golden["roles"]!.AsObject())
            roles[role] = await ShapeAsync(db, await ledger.PostingAccountAsync(role));
        var legacy = new List<object>();
        foreach (var (code, name, type, parent) in Legacy)
        {
            var a = await ledger.GetOrCreateAccountAsync(code, name, type, parent);
            var b = await ledger.GetOrCreateAccountAsync(code, name, type, parent);
            var shape = await ShapeAsync(db, a);
            legacy.Add(new Dictionary<string, object?> { ["ask"] = code, ["same_again"] = a.Id == b.Id && a.Id > 0 }
                .Concat(shape).ToDictionary());
        }
        var entities = new List<object>();
        foreach (string kind in EntityKinds)
            foreach (int entityId in new[] { 1, 57 })
            {
                var a = await ledger.EntityAccountAsync(kind, entityId, $"Golden {kind} {entityId}");
                var again = await ledger.EntityAccountAsync(kind, entityId, $"Golden {kind} {entityId}");
                var renamed = await ledger.EntityAccountAsync(kind, entityId, $"Golden {kind} {entityId} renamed");
                await db.SaveChangesAsync();
                entities.Add(new Dictionary<string, object?>
                {
                    ["kind"] = kind, ["entity_id"] = entityId, ["same_again"] = a.Id == again.Id && again.Id == renamed.Id && a.Id > 0,
                }.Concat(await ShapeAsync(db, renamed)).ToDictionary());
            }
        await db.SaveChangesAsync();
        Golden.Equal(golden, new { roles, legacy, entities }, "LedgerAccounts");
        // Parked legacy accounts are found by name: one account each, however often asked.
        Assert.Single(db.Set<ChartOfAccount>().Where(a => a.Name == "Impairment Loss on Fixed Assets"));
        Assert.Single(db.Set<ChartOfAccount>().Where(a => a.Name == "EOBI Payable"));
    }

    [ReferenceFact]
    public async Task Posting_and_unposting_keep_the_daily_balances_equal_to_the_lines()
    {
        int cid = DefaultCompany();
        var (db, _) = Db(cid);
        await using var _db = db;
        var ledger = new Ledger(db, new Periods(db));
        var cash = await ledger.PostingAccountAsync("cash");
        var revenue = await ledger.PostingAccountAsync("revenue");
        int vid = Random.Shared.Next(900_000, 999_999);
        var when = new DateTime(2026, 3, 5);

        var entry = await ledger.PostAsync("TEST", vid, $"TEST-{vid}", "parity",
        [
            new(cash.Id, 1000.505m, 0, "in"),
            new(revenue.Id, 0, 1000.505m, "out"),
        ], when, AdminId());
        Assert.True(entry.Id > 0);
        Assert.Equal(cid, entry.CompanyId);
        Assert.All(await db.Set<JournalLine>().Where(l => l.JournalEntryId == entry.Id).ToListAsync(),
                   l => Assert.Equal(cid, l.CompanyId));
        Assert.Empty(await SummaryDifferences(db, cid));

        await ledger.ReverseAsync("TEST", vid);
        Assert.False((await db.Set<JournalEntry>().AsNoTracking().SingleAsync(e => e.Id == entry.Id)).IsPosted);
        Assert.Empty(await SummaryDifferences(db, cid));
    }

    /// <summary>Two postings to one (account, label, day) key add up in gl_daily_balances (the upsert adds its difference).</summary>
    [ReferenceFact]
    public async Task Two_postings_to_one_account_and_day_add_up_in_the_reporting_table()
    {
        int cid = DefaultCompany();
        var (db, _) = Db(cid);
        await using var _db = db;
        var ledger = new Ledger(db, new Periods(db));
        var cash = await ledger.PostingAccountAsync("cash");
        var revenue = await ledger.PostingAccountAsync("revenue");
        var day = new DateOnly(2026, 3, 6);
        async Task<(decimal Dr, int N)> RowAsync() =>
            await db.Set<GlDailyBalance>().AsNoTracking().Where(g => g.AccountId == cash.Id && g.LabelId == 0 && g.Day == day)
                .Select(g => new ValueTuple<decimal, int>(g.Debit, g.LineCount)).SingleOrDefaultAsync();
        var before = await RowAsync();
        int vid = Random.Shared.Next(800_000, 899_999);
        await ledger.PostAsync("TEST", vid, $"TEST-{vid}", "first", [new(cash.Id, 250m, 0), new(revenue.Id, 0, 250m)],
                               new DateTime(2026, 3, 6, 9, 0, 0), AdminId());
        await ledger.PostAsync("TEST", vid + 1, $"TEST-{vid + 1}", "second", [new(cash.Id, 125.25m, 0), new(revenue.Id, 0, 125.25m)],
                               new DateTime(2026, 3, 6, 16, 0, 0), AdminId());
        var after = await RowAsync();
        Assert.Equal(before.Dr + 375.25m, after.Dr);
        Assert.Equal(before.N + 2, after.N);
        Assert.Empty(await SummaryDifferences(db, cid));
        await ledger.ReverseAsync("TEST", vid);
        await ledger.ReverseAsync("TEST", vid + 1);
        Assert.Equal(before, await RowAsync());
        Assert.Empty(await SummaryDifferences(db, cid));
    }

    /// <summary>VoucherNumber.next's series: PREFIX-00001, -00002, ... (Python's format), one number per call.</summary>
    [ReferenceFact]
    public async Task Voucher_numbers_are_one_gapless_series_in_pythons_format()
    {
        int cid = DefaultCompany();
        string prefix = "T" + Random.Shared.Next(1000, 9999) + Random.Shared.Next(10, 99);
        var (db, _) = Db(cid);
        await using var _db = db;
        var numbers = new VoucherNumbers(db);
        Assert.Equal([$"{prefix}-00001", $"{prefix}-00002", $"{prefix}-00003"],
                     new[] { await numbers.NextAsync(prefix), await numbers.NextAsync(prefix), await numbers.NextAsync(prefix) });
        Assert.Equal(4, await db.Set<VoucherNumber>().Where(v => v.Prefix == prefix).Select(v => v.NextNumber).SingleAsync());
    }

    [ReferenceFact]
    public async Task A_closed_period_refuses_posting_in_the_python_apps_words()
    {
        int cid = DefaultCompany();
        var (db, _) = Db(cid);
        await using var _db = db;
        await using var tx = await db.Database.BeginTransactionAsync();
        var period = new AccountingPeriod
        {
            FiscalYear = "T1990", PeriodName = "Test 1990", StartDate = new DateOnly(1990, 1, 1),
            EndDate = new DateOnly(1990, 12, 31), IsOpen = false, IsClosed = true, IsActive = true,
            CreatedAt = DateTime.UtcNow, ClosedAt = new DateTime(1991, 1, 15),
        };
        db.Add(period);
        await db.SaveChangesAsync();
        var ledger = new Ledger(db, new Periods(db));
        var cash = await ledger.PostingAccountAsync("cash");
        var revenue = await ledger.PostingAccountAsync("revenue");
        var e = await Assert.ThrowsAsync<ClosedPeriodException>(() => ledger.PostAsync("TEST", 1, "TEST-1990", "x",
            [new(cash.Id, 1m, 0), new(revenue.Id, 0, 1m)], new DateTime(1990, 6, 1)));
        Assert.Equal(
            "Cannot post TEST-1990 into 01 Jun 1990: the period 'Test 1990' (01 Jan 1990 – 31 Dec 1990) was closed on " +
            "15 Jan 1991 and its figures are final. Post an adjusting entry in the current open period instead, or " +
            "reopen 'Test 1990' in Settings → Periods if closing it was a mistake.", e.Message);
        await tx.RollbackAsync();
    }

    [ReferenceFact]
    public async Task Unbalanced_and_aggregating_journals_are_refused()
    {
        int cid = DefaultCompany();
        var (db, _) = Db(cid);
        await using var _db = db;
        var ledger = new Ledger(db, new Periods(db));
        var cash = await ledger.PostingAccountAsync("cash");
        var head = await db.Set<ChartOfAccount>().SingleAsync(a => a.Code == "1-01-01");
        var e1 = await Assert.ThrowsAsync<LedgerException>(() => ledger.PostAsync("TEST", 1, "T-U", "x",
            [new(cash.Id, 10m, 0), new(cash.Id, 0, 9.98m)]));
        Assert.StartsWith("Unbalanced journal for T-U: debits 10", e1.Message);
        var e2 = await Assert.ThrowsAsync<LedgerException>(() => ledger.PostAsync("TEST", 1, "T-A", "x",
            [new(head.Id, 1m, 0), new(cash.Id, 0, 1m)]));
        Assert.Contains("Cannot post to aggregating account 1-01-01", e2.Message);
    }
}
