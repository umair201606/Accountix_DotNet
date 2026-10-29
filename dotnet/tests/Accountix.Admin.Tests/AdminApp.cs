using System.Reflection;
using Accountix.Accounting.Services;
using Accountix.Inventory.Services;
using Accountix.Platform.Provisioning;
using Accountix.Platform.Services;

namespace Accountix.Admin.Tests;

/// <summary>
/// The app with every module (as AccountixHost loads them) on a scratch database, to run the
/// admin commands in-process and to build their inputs with the app's own services.
/// </summary>
internal sealed class AdminApp : IAsyncDisposable
{
    private AdminApp(ModuleHost host, ScratchDb db)
    {
        Host = host;
        Db = db;
    }

    public ModuleHost Host { get; }
    public ScratchDb Db { get; }

    /// <summary>Every Accountix.* assembly next to the tests, minus tests and tooling (AccountixHost's rule).</summary>
    public static Assembly[] Modules { get; } = Directory.GetFiles(AppContext.BaseDirectory, "Accountix.*.dll")
        .Where(p => !Path.GetFileName(p).Contains(".Tests", StringComparison.Ordinal)
                    && Path.GetFileName(p) is not ("Accountix.TestSupport.dll" or "Accountix.Load.dll"))
        .Select(p => Assembly.Load(AssemblyName.GetAssemblyName(p)))
        .OrderBy(a => a.GetName().Name == "Accountix.Admin" ? 0 : 1)
        .ThenBy(a => a.GetName().Name, StringComparer.Ordinal)
        .ToArray();

    public static async Task<AdminApp> StartAsync(ScratchDb db) => new(await ModuleHost.StartOnAsync(db.ConnectionString, Modules), db);

    public async Task<(int Exit, string Output)> RunAsync(string command, params string[] args)
    {
        var cmd = Host.Services.GetServices<IAdminCommand>().Single(c => c.Name == command);
        var parsed = AdminArgs.Parse(args, out string? error);
        Assert.Null(error);
        await using var scope = Host.Services.CreateAsyncScope();
        var output = new StringWriter();
        int exit = await cmd.RunAsync(scope.ServiceProvider, parsed, output, default);
        return (exit, output.ToString());
    }

    /// <summary>Run <paramref name="work"/> in a fresh scope in this company.</summary>
    public async Task<T> InAsync<T>(int companyId, Func<IServiceProvider, AccountixDbContext, Task<T>> work)
    {
        await using var scope = Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountixDbContext>();
        db.Company.Set(companyId);
        return await work(scope.ServiceProvider, db);
    }

    public Task InAsync(int companyId, Func<IServiceProvider, AccountixDbContext, Task> work) =>
        InAsync(companyId, async (sp, db) => { await work(sp, db); return 0; });

    /// <summary>A company made as the portal makes one: owner_b its admin, then provisioned.</summary>
    public async Task<int> CompanyAsync(string name)
    {
        await using var scope = Host.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AccountixDbContext>();
        var now = DateTime.UtcNow;
        string slug = "adm-" + Guid.NewGuid().ToString("N")[..10];
        var company = new Company
        {
            Name = name, Slug = slug, IsActive = true, PlanName = "free", CreatedBy = OwnerB,
            Country = "Pakistan", FiscalYearStartMonth = 1, Currency = "PKR", CurrencySymbol = "Rs.",
            DateFormat = "Y-m-d", NumberFormat = "en", Timezone = "Asia/Karachi", CreatedAt = now, UpdatedAt = now,
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        int admin = await db.Roles.Where(r => r.Name == Role.Admin).Select(r => r.Id).FirstAsync();
        await sp.GetRequiredService<Memberships>().AddAsync(company.Id, OwnerB, admin);
        await sp.GetRequiredService<CompanySetup>().ProvisionAsync(company.Id);
        return company.Id;
    }

    public static int OwnerB => Reference.UserId(Reference.Email("owner_b"));

    /// <summary>Failing Books Integrity checks of a company (keys).</summary>
    public Task<List<string>> FailingAsync(int companyId) => InAsync(companyId, async (sp, db) =>
        (await ActivatorUtilities.CreateInstance<Accountix.Executive.Services.BooksIntegrity>(sp).FailuresAsync()).Select(f => f.Key).ToList());

    public ValueTask DisposeAsync() => Host.DisposeAsync();

    // ── inputs ───────────────────────────────────────────────────────────────

    /// <summary>The six names get_or_create_account parked (tests/e2e/test_legacy_accounts.py LEGACY).</summary>
    public static readonly (string Code, string Name, string Type)[] Legacy =
    [
        ("5710", "Impairment Loss on Fixed Assets", "expense"),
        ("2125", "EOBI Payable", "liability"),
        ("5123", "EOBI Employer Expense", "expense"),
        ("5124", "Gratuity Expense", "expense"),
        ("5125", "Leave Encashment Expense", "expense"),
        ("5126", "PF Profit Credited to Members", "expense"),
    ];

    /// <summary>
    /// _park_duplicates: <paramref name="copies"/> accounts of each legacy name under its type's
    /// catch-all head (as the old get_or_create_account made them), and a journal posted to each
    /// account of that name. Returns name → account ids, lowest first.
    /// </summary>
    public Task<Dictionary<string, List<int>>> ParkDuplicatesAsync(int companyId, int copies, string tag) => InAsync(companyId, async (sp, db) =>
    {
        var ledger = sp.GetRequiredService<ILedger>();
        var cash = await ledger.PostingAccountAsync("cash");
        var made = new Dictionary<string, List<int>>();
        foreach (var (_, name, type) in Legacy)
        {
            var parent = await db.Set<ChartOfAccount>().SingleAsync(a => a.Code == Coa.CatchallParentCodes[type]);
            for (int i = 0; i < copies; i++)
            {
                db.Add(new ChartOfAccount
                {
                    Code = await NextChildCodeAsync(db, parent), Name = name, Type = type, ParentId = parent.Id, Level = 5,
                    IsFixed = false, IsActive = true, CreatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }
            var ids = await db.Set<ChartOfAccount>().Where(a => a.Name == name).OrderBy(a => a.Id).Select(a => a.Id).ToListAsync();
            for (int n = 0; n < ids.Count; n++)
            {
                int aid = ids[n];
                decimal amt = 100 * (n + 1) + 0.25m;
                var (dr, cr) = type == "expense" ? (aid, cash.Id) : (cash.Id, aid);
                await ledger.PostAsync("LEGACY-TEST", aid, $"LEGACY-{tag}-{aid}", $"Parked {name}",
                    [new(dr, amt, 0, name), new(cr, 0, amt, name)], new DateTime(2025, 3, 1), 1);
            }
            made[name] = ids;
        }
        return made;
    });

    /// <summary>coa.next_child_code for a level-4 parent: the lowest free 4-digit suffix.</summary>
    private static async Task<string> NextChildCodeAsync(AccountixDbContext db, ChartOfAccount parent)
    {
        var used = (await db.Set<ChartOfAccount>().Where(a => a.Code.StartsWith(parent.Code + "-")).Select(a => a.Code).ToListAsync())
            .Select(c => c[(parent.Code.Length + 1)..]).Where(r => r.All(char.IsAsciiDigit)).Select(int.Parse).ToHashSet();
        int n = 1;
        while (used.Contains(n)) n++;
        return $"{parent.Code}-{n:D4}";
    }

    /// <summary>A product with OPENING stock in the stock ledger and no journal (the boot routine's legacy rows).</summary>
    public Task<int> UnjournaledOpeningAsync(int companyId, string sku, decimal qty, decimal unitCost, DateTime when) => InAsync(companyId, async (sp, db) =>
    {
        var p = new InvProduct { Name = $"Legacy {sku}", Sku = sku, CurrentStock = 0, CostPrice = 0, UnitPrice = 0, IsActive = true };
        db.Add(p);
        await db.SaveChangesAsync();
        await sp.GetRequiredService<StockCosting>().ReceiveAsync(p.Id, "OPENING", p.Id, $"OPEN-{p.Id:D5}", qty, unitCost, "legacy", 1, when);
        await db.SaveChangesAsync();
        return p.Id;
    });
}
