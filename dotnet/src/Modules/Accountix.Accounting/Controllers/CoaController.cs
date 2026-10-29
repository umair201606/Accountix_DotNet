using Accountix.Accounting.Services;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Accountix.Accounting.Controllers;

/// <summary>Chart of accounts (finance_app/routes/coa.py).</summary>
[Area("Accounting")]
[Module(Modules.Accounting)]
[Route("accounting/coa")]
public sealed class CoaController(AccountixDbContext db, CompanyClock clock) : Controller
{
    public static readonly IReadOnlyDictionary<int, string> LevelLabels = new Dictionary<int, string>
    {
        [1] = "Level 1 — Account Class", [2] = "Level 2 — Sub-Group", [3] = "Level 3 — Parent Head",
        [4] = "Level 4 — Child Account", [5] = "Level 5 — Operational Account",
    };

    public static readonly string[] CfActivities = ["operating", "investing", "financing", "cash"];

    public static readonly string[] PlSections =
        ["sales", "sales_returns", "other_income", "cost_of_sales", "admin", "selling_distribution",
         "other_operating", "finance_cost", "income_tax"];

    /// <summary>Plain names for the records that can point at an account, for the refusal.</summary>
    private static readonly IReadOnlyDictionary<string, string> UsedByLabels = new Dictionary<string, string>
    {
        ["fa_categories"] = "fixed asset categories", ["fixed_assets"] = "fixed assets",
        ["additional_charges"] = "invoice charges", ["inv_invoices"] = "sales invoices",
        ["inv_purchase_invoices"] = "purchase invoices", ["inv_purchase_orders"] = "purchase orders",
        ["inv_sales_orders"] = "sales orders", ["inv_sales_returns"] = "sales returns",
        ["accounting_vouchers"] = "accounting vouchers", ["accounting_voucher_lines"] = "accounting vouchers",
        ["consumption_vouchers"] = "consumption vouchers", ["scrap_vouchers"] = "scrap vouchers",
        ["bank_reconciliations"] = "bank reconciliations", ["exec_account_selections"] = "executive report selections",
        ["inventory_settings"] = "inventory settings", ["invoice_settings"] = "invoice settings",
        ["charge_ledger_defaults"] = "charge posting settings", ["category_revenue_accounts"] = "category revenue settings",
        ["tax_rate_accounts"] = "tax rate settings",
    };

    public sealed record TreeRow(ChartOfAccount Acct, int Depth, bool HasChildren, string Path);

    [HttpGet("")]
    [Right("chart_of_accounts", Rights.View)]
    public async Task<IActionResult> List(CancellationToken ct) => View(await TreeAsync(ct));

    private async Task<List<TreeRow>> TreeAsync(CancellationToken ct)
    {
        var all = await db.Set<ChartOfAccount>().AsNoTracking().OrderBy(a => a.Code).ToListAsync(ct);
        var byId = all.ToDictionary(a => a.Id);
        var children = all.GroupBy(a => a.ParentId ?? 0).ToDictionary(g => g.Key, g => g.OrderBy(a => a.Code, StringComparer.Ordinal).ToList());
        var rows = new List<TreeRow>();
        void Walk(ChartOfAccount node, int depth)
        {
            var kids = children.GetValueOrDefault(node.Id) ?? [];
            rows.Add(new TreeRow(node, depth, kids.Count > 0, PathOf(node, byId)));
            foreach (var k in kids) Walk(k, depth + 1);
        }
        foreach (var root in children.GetValueOrDefault(0) ?? []) Walk(root, 0);
        return rows;
    }

    /// <summary>display_path: "Assets > Current Assets > ...".</summary>
    public static string PathOf(ChartOfAccount a, IReadOnlyDictionary<int, ChartOfAccount> byId)
    {
        var parts = new List<string>();
        for (ChartOfAccount? x = a; x is not null; x = x.ParentId is int p ? byId.GetValueOrDefault(p) : null)
            parts.Add(x.Name);
        parts.Reverse();
        return string.Join(" > ", parts);
    }

    /// <summary>effective_cash_flow_activity / effective_pl_section: the nearest tagged ancestor's tag.</summary>
    public static string? Inherited(ChartOfAccount a, IReadOnlyDictionary<int, ChartOfAccount> byId, Func<ChartOfAccount, string?> tag)
    {
        for (ChartOfAccount? x = a; x is not null; x = x.ParentId is int p ? byId.GetValueOrDefault(p) : null)
            if (!string.IsNullOrEmpty(tag(x))) return tag(x);
        return null;
    }

    [HttpGet("add")]
    [Right("chart_of_accounts", Rights.Create)]
    public async Task<IActionResult> Add([FromQuery(Name = "parent_id")] int? parentId, CancellationToken ct)
    {
        var l1 = await db.Set<ChartOfAccount>().AsNoTracking().Where(a => a.Level == 1).OrderBy(a => a.Code).ToListAsync(ct);
        return View("Form", new CoaFormModel { L1 = l1, ParentId = parentId });
    }

    [HttpPost("add")]
    [Right("chart_of_accounts", Rights.Create)]
    public async Task<IActionResult> AddPost(CancellationToken ct)
    {
        var f = Request.Form;
        string name = (f["name"].FirstOrDefault() ?? "").Trim();
        int? parentId = int.TryParse(f["parent_id"], out int pid) ? pid : null;
        string desc = f["description"].FirstOrDefault() ?? "";
        string? cf = f["cash_flow_activity"].FirstOrDefault() is { Length: > 0 } c ? c : null;
        string? pl = f["pl_section"].FirstOrDefault() is { Length: > 0 } p ? p : null;
        if (name.Length == 0)
        {
            this.Flash("Account name is required.", "error");
            return Redirect("/accounting/coa/add");
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var parent = parentId is int id ? await db.Set<ChartOfAccount>().FirstOrDefaultAsync(a => a.Id == id, ct) : null;
        if (parent is null)
        {
            this.Flash("Parent account is required.", "error");
            return Redirect("/accounting/coa/add");
        }
        if (parent.Level >= 5)
        {
            this.Flash("Level 5 is the operational level — it cannot have children.", "error");
            return Redirect("/accounting/coa/add");
        }
        if (cf is not null && !CfActivities.Contains(cf)) cf = null;
        if (pl is not null && !PlSections.Contains(pl)) pl = null;
        int level = parent.Level + 1;
        // Two people adding under the same parent drew the same next code.
        await db.SerializeAsync("coa-child", parent.Id);
        var acct = new ChartOfAccount
        {
            Code = await Coa.NextChildCodeAsync(db, parent, ct), Name = name, Type = parent.Type, ParentId = parent.Id,
            Level = level, Description = desc, CashFlowActivity = cf, PlSection = pl,
            IsFixed = false, IsActive = true, CreatedAt = clock.UtcNow,
        };
        db.Add(acct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"{LevelLabels.GetValueOrDefault(level, "Account")} \"{name}\" created as {acct.Code}.", "success");
        return Redirect("/accounting/coa/");
    }

    [HttpGet("{id:int}/edit")]
    [Right("chart_of_accounts", Rights.Edit)]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var all = await db.Set<ChartOfAccount>().AsNoTracking().ToDictionaryAsync(a => a.Id, ct);
        if (!all.TryGetValue(id, out var acct)) return NotFound();
        return View("Form", new CoaFormModel
        {
            Account = acct, ParentId = acct.ParentId, Path = PathOf(acct, all),
            EffectiveCf = Inherited(acct, all, a => a.CashFlowActivity),
            EffectivePl = Inherited(acct, all, a => a.PlSection),
        });
    }

    [HttpPost("{id:int}/edit")]
    [Right("chart_of_accounts", Rights.Edit)]
    public async Task<IActionResult> EditPost(int id, CancellationToken ct)
    {
        var acct = await db.Set<ChartOfAccount>().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (acct is null) return NotFound();
        var f = Request.Form;
        // Fixed structural accounts keep name/code; tags stay editable.
        if (acct.IsFixed != true)
        {
            string name = (f["name"].FirstOrDefault() ?? acct.Name).Trim();
            acct.Name = name.Length > 0 ? name : acct.Name;
            acct.Description = f["description"].FirstOrDefault() ?? "";
            acct.IsActive = f["is_active"] == "1";
        }
        string? cf = f["cash_flow_activity"].FirstOrDefault() is { Length: > 0 } c ? c : null;
        string? pl = f["pl_section"].FirstOrDefault() is { Length: > 0 } p ? p : null;
        acct.CashFlowActivity = cf is not null && CfActivities.Contains(cf) ? cf : null;
        acct.PlSection = pl is not null && PlSections.Contains(pl) ? pl : null;
        await db.SaveChangesAsync(ct);
        this.Flash($"Account \"{acct.Name}\" updated.", "success");
        return Redirect("/accounting/coa/");
    }

    [HttpPost("{id:int}/delete")]
    [Right("chart_of_accounts", Rights.Delete)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var acct = await db.Set<ChartOfAccount>().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (acct is null) return NotFound();
        bool isLeaf = !await db.Set<ChartOfAccount>().AnyAsync(a => a.ParentId == acct.Id, ct);
        if (acct.IsFixed == true || !isLeaf)
        {
            this.Flash(acct.IsFixed == true
                ? $"\"{acct.Name}\" is a fixed account and cannot be deleted."
                : $"\"{acct.Name}\" has child accounts. Delete children first.", "error");
            return Redirect("/accounting/coa/");
        }
        var uses = await UsedByAsync(acct.Id, ct);
        if (uses.Count > 0)
        {
            this.Flash($"\"{acct.Name}\" is still used by {string.Join(", ", uses)}. Choose another account there first, " +
                       "or untick Active to retire it.", "error");
            return Redirect("/accounting/coa/");
        }
        if (await db.Set<JournalLine>().AnyAsync(l => l.AccountId == acct.Id, ct))
        {
            // Never delete history — deactivate instead.
            acct.IsActive = false;
            await db.SaveChangesAsync(ct);
            this.Flash($"\"{acct.Name}\" has entries, so it was deactivated.", "warning");
            return Redirect("/accounting/coa/");
        }
        db.Remove(acct);
        await db.SaveChangesAsync(ct);
        this.Flash($"Account \"{acct.Name}\" deleted.", "success");
        return Redirect("/accounting/coa/");
    }

    /// <summary>_used_by: every foreign key to chart_of_accounts.id (besides the journal tables) that still points here.</summary>
    private async Task<List<string>> UsedByAsync(int accountId, CancellationToken ct)
    {
        var uses = new List<string>();
        var target = db.Model.FindEntityType(typeof(ChartOfAccount))!;
        foreach (var fk in db.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys())
                     .Where(fk => fk.PrincipalEntityType == target))
        {
            string table = fk.DeclaringEntityType.GetTableName()!;
            if (table is "chart_of_accounts" or "journal_lines" or "journal_entries")
                continue;
            string column = fk.Properties[0].GetColumnName(StoreObjectIdentifier.Table(table, null))!;
            // Table and column come from the EF model and are checked; the account id is a parameter.
            string sql = $"SELECT COUNT(*)::int AS \"Value\" FROM {DbLocks.QuoteIdentifier(table)} " +
                         $"WHERE {DbLocks.QuoteIdentifier(column)} = {{0}}";
            int hit = await db.Database.SqlQueryRaw<int>(sql, accountId).SingleAsync(ct);
            if (hit > 0)
            {
                string label = UsedByLabels.GetValueOrDefault(table, table.Replace('_', ' '));
                if (!uses.Contains(label)) uses.Add(label);
            }
        }
        return uses;
    }

    [HttpGet("api/children")]
    [AnyRight("chart_of_accounts:view", "chart_of_accounts:create", "chart_of_accounts:edit")]
    public async Task<IActionResult> ApiChildren([FromQuery(Name = "parent_id")] int? parentId, CancellationToken ct)
    {
        if (parentId is not int pid || pid == 0) return Json(Array.Empty<object>());
        var kids = await db.Set<ChartOfAccount>().Where(a => a.ParentId == pid).OrderBy(a => a.Code)
            .Select(a => new { id = a.Id, code = a.Code, name = a.Name, level = a.Level, is_fixed = a.IsFixed,
                               has_children = db.Set<ChartOfAccount>().Any(c => c.ParentId == a.Id) })
            .ToListAsync(ct);
        return Json(kids);
    }

    [HttpGet("api/next-code")]
    [AnyRight("chart_of_accounts:view", "chart_of_accounts:create", "chart_of_accounts:edit")]
    public async Task<IActionResult> ApiNextCode([FromQuery(Name = "parent_id")] int? parentId, CancellationToken ct)
    {
        var parent = parentId is int pid ? await db.Set<ChartOfAccount>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == pid, ct) : null;
        if (parent is null || parent.Level >= 5) return Json(new { code = "" });
        return Json(new { code = await Coa.NextChildCodeAsync(db, parent, ct), level = parent.Level + 1,
                          level_label = LevelLabels.GetValueOrDefault(parent.Level + 1, "") });
    }

    [HttpGet("api/level-accounts")]
    [AnyRight("chart_of_accounts:view", "chart_of_accounts:create", "chart_of_accounts:edit")]
    public async Task<IActionResult> ApiLevelAccounts([FromQuery] int? level, [FromQuery(Name = "parent_id")] int? parentId, CancellationToken ct)
    {
        var q = db.Set<ChartOfAccount>().Where(a => a.Level == level);
        if (parentId is int pid && pid != 0) q = q.Where(a => a.ParentId == pid);
        return Json(await q.OrderBy(a => a.Code).Select(a => new { id = a.Id, code = a.Code, name = a.Name }).ToListAsync(ct));
    }
}

public sealed class CoaFormModel
{
    public ChartOfAccount? Account { get; init; }
    public IReadOnlyList<ChartOfAccount> L1 { get; init; } = [];
    public int? ParentId { get; init; }
    public string Path { get; init; } = "";
    public string? EffectiveCf { get; init; }
    public string? EffectivePl { get; init; }
}
