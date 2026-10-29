using System.Text.RegularExpressions;

namespace Accountix.Accounting.Services;

/// <summary>
/// Chart-of-accounts helpers (shared/coa.py): the five-level segmented chart
/// 1 / 1-01 / 1-01-01 / 1-01-01-01 / 1-01-01-01-0001. Levels 1-4 aggregate;
/// only level 5 takes journal lines.
/// </summary>
public static partial class Coa
{
    public const int PostingLevel = 5;

    [GeneratedRegex(@"^\d(-\d{2}){0,3}(-\d{4,})?$")]
    private static partial Regex SegmentedRe();

    public static int CodeLevel(string code) => code.Split('-').Length;

    public static string? ParentCodeOf(string code) => code.Contains('-') ? code[..code.LastIndexOf('-')] : null;

    public static bool IsSegmented(string? code) => SegmentedRe().IsMatch(code ?? "");

    public static bool IsPostable(this ChartOfAccount a) => a.Level >= PostingLevel;

    /// <summary>
    /// next_child_code: the next free segment under <paramref name="parent"/>
    /// (2 digits for levels 2-4, 4 for level 5), by code prefix.
    /// </summary>
    public static async Task<string> NextChildCodeAsync(AccountixDbContext db, ChartOfAccount parent, CancellationToken ct = default)
    {
        int width = parent.Level == 4 ? 4 : 2;
        string prefix = parent.Code + "-";
        var codes = await db.Set<ChartOfAccount>().Where(a => a.Code.StartsWith(prefix))
            .Select(a => a.Code).ToListAsync(ct);
        // Pending (not yet saved) accounts count too, as Python's autoflush would show them.
        codes.AddRange(db.ChangeTracker.Entries<ChartOfAccount>()
            .Where(e => e.State == EntityState.Added && e.Entity.Code.StartsWith(prefix))
            .Select(e => e.Entity.Code));
        var used = new HashSet<int>();
        foreach (string code in codes)
        {
            string rest = code[prefix.Length..];
            if (rest.Length > 0 && rest.All(char.IsAsciiDigit))
                used.Add(int.Parse(rest));
        }
        int n = 1;
        while (used.Contains(n)) n++;
        return $"{parent.Code}-{n.ToString().PadLeft(width, '0')}";
    }

    /// <summary>
    /// seed_fixed_tree: create the fixed chart idempotently (keyed on code).
    /// Existing rows get their level / fixed flag / tags trued up; names a user
    /// edited are kept.
    /// </summary>
    public static async Task<Dictionary<string, ChartOfAccount>> SeedFixedTreeAsync(
        AccountixDbContext db, IReadOnlySet<int>? levels = null, CancellationToken ct = default)
    {
        levels ??= new HashSet<int> { 1, 2, 3, 4, 5 };
        var wanted = FixedAccounts.Where(f => levels.Contains(CodeLevel(f.Code))).ToList();
        var codes = wanted.Select(f => f.Code).ToList();
        var existing = await db.Set<ChartOfAccount>().Where(a => codes.Contains(a.Code)).ToDictionaryAsync(a => a.Code, ct);
        var created = new Dictionary<string, ChartOfAccount>();
        foreach (var (code, name, type, cf, pl) in wanted)
        {
            int level = CodeLevel(code);
            if (!existing.TryGetValue(code, out var acct))
            {
                string? parentCode = ParentCodeOf(code);
                ChartOfAccount? parent = null;
                if (parentCode is not null)
                    parent = created.GetValueOrDefault(parentCode) ?? existing.GetValueOrDefault(parentCode)
                             ?? await db.Set<ChartOfAccount>().FirstOrDefaultAsync(a => a.Code == parentCode, ct);
                acct = new ChartOfAccount
                {
                    Code = code, Name = name, Type = type, Parent = parent, ParentId = parent?.Id,
                    Level = level, IsFixed = level <= 4, IsActive = true,
                    CashFlowActivity = cf, PlSection = pl, CreatedAt = DateTime.UtcNow,
                };
                db.Add(acct);
                await db.SaveChangesAsync(ct);
                existing[code] = acct;
            }
            else
            {
                acct.Level = level;
                if (level <= 4) acct.IsFixed = true;
                if (cf is not null && string.IsNullOrEmpty(acct.CashFlowActivity)) acct.CashFlowActivity = cf;
                if (pl is not null && string.IsNullOrEmpty(acct.PlSection)) acct.PlSection = pl;
            }
            created[code] = acct;
        }
        return created;
    }
}
