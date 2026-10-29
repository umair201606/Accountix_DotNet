using System.Globalization;
using System.Text.Json;
using Accountix.Platform.Audit;
using Accountix.Platform.Services;

namespace Accountix.Platform.Controllers;

// Company & Finance tabs (company profile, financial periods, report structure, cash flow, project labels) and Inventory.
public sealed partial class SettingsController
{
    public static readonly string[] Months =
        ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    public sealed record PlRow(string? Section, string? Subtotal, string Label, bool Negate);
    public sealed record PeriodsTab(FiscalYearRule Rule, DateOnly ExStart, DateOnly ExEnd, IReadOnlyList<AccountingPeriod> Periods, CloseReview? Review);
    public sealed record CloseReview(int Id, IReadOnlyList<string> Issues);
    public sealed record ReportsTab(ReportSetting Settings, IReadOnlyList<PlRow> Rows);
    public sealed record LabelsTab(IReadOnlyList<ProjectLabel> Labels, bool PerLineVoucher, bool PerLineInvoice,
                                   int? DefaultVoucher, int? DefaultInvoice, IReadOnlySet<int> InUse);
    public sealed record InventoryTab(InventorySetting Settings, IReadOnlyList<ChartOfAccount> Accounts);

    /// <summary>DEFAULT_PL_STRUCTURE.</summary>
    public static readonly IReadOnlyList<PlRow> DefaultPlStructure =
    [
        new("sales", null, "Sales", false),
        new("sales_returns", null, "Less: Sales Returns & Discounts", true),
        new(null, "net_sales", "Net Sales", false),
        new("cost_of_sales", null, "Cost of Sales", true),
        new(null, "gross_profit", "Gross Profit", false),
        new("admin", null, "Administrative Expenses", true),
        new("selling_distribution", null, "Selling & Distribution Expenses", true),
        new("other_operating", null, "Other Operating Expenses", true),
        new(null, "operating_profit", "Operating Profit", false),
        new("other_income", null, "Other Income", false),
        new("finance_cost", null, "Finance Costs", true),
        new(null, "profit_before_tax", "Profit Before Tax", false),
        new("income_tax", null, "Taxation", true),
        new(null, "net_profit", "Net Profit", false),
    ];

    private const string ReviewKey = "period_close_review";

    /// <summary>ReportSettings.pl_structure: the stored rows, or the default layout.</summary>
    public static IReadOnlyList<PlRow> PlStructure(ReportSetting s)
    {
        if (!string.IsNullOrEmpty(s.PlStructureJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(s.PlStructureJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                    return doc.RootElement.EnumerateArray().Select(r => new PlRow(
                        r.TryGetProperty("section", out var sec) && sec.ValueKind == JsonValueKind.String ? sec.GetString() : null,
                        r.TryGetProperty("subtotal", out var sub) && sub.ValueKind == JsonValueKind.String ? sub.GetString() : null,
                        r.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString()! : "",
                        r.TryGetProperty("negate", out var n) && n.ValueKind == JsonValueKind.True)).ToList();
            }
            catch (JsonException) { }
        }
        return DefaultPlStructure;
    }

    private DateTime UtcNowMicro => FirstUse.Micro(time.GetUtcNow().UtcDateTime);

    /// <summary>A save in one transaction (settings first-use rows and the change commit together).</summary>
    private async Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var result = await work();
        TouchUpdated();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }

    /// <summary>onupdate=datetime.utcnow: rows changed by this save get a new updated_at.</summary>
    private void TouchUpdated()
    {
        db.ChangeTracker.DetectChanges();
        foreach (var e in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Modified))
            if (e.Metadata.FindProperty("UpdatedAt") is { } p && !e.Property(p.Name).IsModified)
                e.Property(p.Name).CurrentValue = UtcNowMicro;
    }

    // ── tabs ─────────────────────────────────────────────────────────────

    private async Task<object?> CompanyTabAsync(CancellationToken ct) =>
        await InTransactionAsync(() => firstUse.CompanyInfoAsync(ct), ct);

    private async Task<object?> PeriodsTabAsync(CancellationToken ct)
    {
        var rule = await InTransactionAsync(() => firstUse.FiscalYearRuleAsync(ct), ct);
        // A worked example: the year that contains today (server date, as Python's date.today()).
        int m = rule.StartMonth is > 0 ? rule.StartMonth : 1, d = rule.StartDay is > 0 ? rule.StartDay : 1;
        var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        DateOnly Start(int y) => new(y, m, Math.Min(d, DateTime.DaysInMonth(y, m)));
        int year = today >= Start(today.Year) ? today.Year : today.Year - 1;
        var periods = await db.Set<AccountingPeriod>().AsNoTracking().OrderByDescending(p => p.StartDate).ToListAsync(ct);
        CloseReview? review = null;
        if (TempData.Peek(ReviewKey) is string json)
            review = JsonSerializer.Deserialize<CloseReview>(json);
        return new PeriodsTab(rule, Start(year), Start(year + 1).AddDays(-1), periods, review);
    }

    private async Task<object?> ReportsTabAsync(CancellationToken ct)
    {
        var rs = await InTransactionAsync(() => firstUse.ReportSettingsAsync(ct), ct);
        return new ReportsTab(rs, PlStructure(rs));
    }

    private async Task<object?> CashFlowTabAsync(CancellationToken ct) =>
        await InTransactionAsync(() => firstUse.ReportSettingsAsync(ct), ct);

    private async Task<object?> LabelsTabAsync(CancellationToken ct)
    {
        var s = await InTransactionAsync(() => firstUse.InventorySettingsAsync(ct), ct);
        var labels = await db.Set<ProjectLabel>().AsNoTracking().OrderBy(l => l.Name).ToListAsync(ct);
        // _label_in_use_ids: label ids referenced by any posting surface (one query).
        var inUse = await db.Database.SqlQueryRaw<int>(
            "SELECT label_id AS \"Value\" FROM journal_lines WHERE company_id = {0} AND label_id IS NOT NULL " +
            "UNION SELECT label_id FROM accounting_voucher_lines WHERE company_id = {0} AND label_id IS NOT NULL " +
            "UNION SELECT label_id FROM accounting_vouchers WHERE company_id = {0} AND label_id IS NOT NULL " +
            "UNION SELECT label_id FROM inv_invoices WHERE company_id = {0} AND label_id IS NOT NULL " +
            "UNION SELECT label_id FROM inv_purchase_invoices WHERE company_id = {0} AND label_id IS NOT NULL " +
            "UNION SELECT label_id FROM inv_invoice_items WHERE company_id = {0} AND label_id IS NOT NULL " +
            "UNION SELECT label_id FROM inv_purchase_invoice_items WHERE company_id = {0} AND label_id IS NOT NULL",
            Me.CompanyId!.Value).ToListAsync(ct);
        return new LabelsTab(labels, s.PerLineLabelingVoucher == true, s.PerLineLabelingInvoice == true,
                             s.DefaultVoucherLabelId, s.DefaultInvoiceLabelId, inUse.ToHashSet());
    }

    private Task<List<ChartOfAccount>> PostableAccountsAsync(CancellationToken ct) =>
        db.Set<ChartOfAccount>().AsNoTracking().Where(a => a.Level >= 5 && a.IsActive == true).OrderBy(a => a.Code).ToListAsync(ct);

    private async Task<object?> InventoryTabAsync(CancellationToken ct)
    {
        var s = await InTransactionAsync(() => firstUse.InventorySettingsAsync(ct), ct);
        return new InventoryTab(s, await PostableAccountsAsync(ct));
    }

    // ── Company profile ──────────────────────────────────────────────────

    [HttpPost("company")]
    public async Task<IActionResult> SaveCompany(CancellationToken ct)
    {
        if (Require("company") is { } denied) return denied;
        var f = Request.Form;
        string Get(string k, string d) => f.ContainsKey(k) ? f[k].ToString() : d;
        await InTransactionAsync(async () =>
        {
            var c = await firstUse.CompanyInfoAsync(ct);
            c.CompanyName = f.ContainsKey("company_name") ? f["company_name"].ToString() : c.CompanyName;
            c.Address = Get("address", ""); c.City = Get("city", ""); c.State = Get("state", "");
            c.Country = Get("country", "Pakistan"); c.Phone = Get("phone", ""); c.Email = Get("email", "");
            c.Website = Get("website", ""); c.TaxId = Get("tax_id", ""); c.RegistrationNumber = Get("registration_number", "");
            c.BankName = Get("bank_name", ""); c.BankAccountTitle = Get("bank_account_title", "");
            c.BankAccountNumber = Get("bank_account_number", ""); c.LogoUrl = Get("logo_url", "");
            // request.form.get(..., 1, type=int): an unreadable value falls back to the default.
            c.FiscalYearStartMonth = int.TryParse(f["fiscal_year_start_month"], out int fm) ? fm : 1;
            c.Currency = Get("currency", "PKR"); c.CurrencySymbol = Get("currency_symbol", "Rs.");
            c.DateFormat = Get("date_format", "Y-m-d"); c.NumberFormat = Get("number_format", "en");
            c.Timezone = Get("timezone", "Asia/Karachi");
            return 0;
        }, ct);
        this.Flash("Company profile updated.", "success");
        return ToTab("company");
    }

    // ── Financial periods ────────────────────────────────────────────────

    [HttpPost("periods/rule")]
    public async Task<IActionResult> SaveFiscalYearRule(CancellationToken ct)
    {
        if (Require("periods") is { } denied) return denied;
        var back = ToTab("periods");
        if (!int.TryParse(Request.Form["start_month"], out int month) || !int.TryParse(Request.Form["start_day"], out int day))
        {
            this.Flash("Start month and day are required.", "error");
            return back;
        }
        if (month is < 1 or > 12)
        {
            this.Flash("Start month must be between 1 and 12.", "error");
            return back;
        }
        if (day is < 1 or > 28)
        {
            this.Flash("Start day must be between 1 and 28.", "error");
            return back;
        }
        await InTransactionAsync(async () =>
        {
            var rule = await firstUse.FiscalYearRuleAsync(ct);
            rule.StartMonth = month;
            rule.StartDay = day;
            TouchUpdated();
            await db.SaveChangesAsync(ct);
            await setup.GeneratePeriodsAsync(rule, null, null, ct);
            return 0;
        }, ct);
        this.Flash($"Fiscal year rule updated to {day} {Months[month - 1]}. Periods regenerated.", "success");
        return back;
    }

    [HttpPost("periods/generate")]
    public async Task<IActionResult> GeneratePeriods(CancellationToken ct)
    {
        if (Require("periods") is { } denied) return denied;
        var back = ToTab("periods");
        if (!int.TryParse(Request.Form["from_year"], out int from) || !int.TryParse(Request.Form["to_year"], out int to))
        {
            this.Flash("From year and to year are required.", "error");
            return back;
        }
        if (from > to)
        {
            this.Flash("From year must not be after to year.", "error");
            return back;
        }
        await InTransactionAsync(async () =>
        {
            var rule = await firstUse.FiscalYearRuleAsync(ct);
            await setup.GeneratePeriodsAsync(rule, from, to, ct);
            return 0;
        }, ct);
        this.Flash($"Periods generated from {from} to {to}.", "success");
        return back;
    }

    [HttpPost("periods/{id:int}/close")]
    public async Task<IActionResult> ClosePeriod(int id, CancellationToken ct)
    {
        if (Require("periods") is { } denied) return denied;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Exclusive until commit: waits for postings into the period in flight (they hold FOR SHARE), then holds new ones off.
        var p = await db.LockAsync<AccountingPeriod>(id, ct);
        if (p is null) return NotFound();
        var issues = await PeriodClose.ChecklistAsync(db, p, HttpContext.RequestServices, ct);
        if (issues.Count > 0 && Request.Form["force"] != "1")
        {
            TempData[ReviewKey] = JsonSerializer.Serialize(new CloseReview(p.Id, issues));
            this.Flash($"{p.PeriodName} still has open items — review them below, then close anyway or finish them first.", "warning");
            await tx.RollbackAsync(ct);
            return ToTab("periods");
        }
        TempData.Remove(ReviewKey);
        p.IsOpen = false;
        p.IsClosed = true;
        p.ClosedAt = UtcNowMicro;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"Period {p.PeriodName} closed.", "success");
        return ToTab("periods");
    }

    [HttpPost("periods/{id:int}/reopen")]
    public async Task<IActionResult> ReopenPeriod(int id, CancellationToken ct)
    {
        if (Require("periods") is { } denied) return denied;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var p = await db.LockAsync<AccountingPeriod>(id, ct);
        if (p is null) return NotFound();
        p.IsOpen = true;
        p.IsClosed = false;
        p.ClosedAt = null;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"Period {p.PeriodName} reopened.", "success");
        return ToTab("periods");
    }

    // ── Report structure, cash flow ──────────────────────────────────────

    [HttpPost("reports")]
    public async Task<IActionResult> SaveReports(CancellationToken ct)
    {
        if (Require("reports") is { } denied) return denied;
        var f = Request.Form;
        await InTransactionAsync(async () =>
        {
            var s = await firstUse.ReportSettingsAsync(ct);
            s.PlDetailRows = int.TryParse(f["pl_detail_rows"], out int rows) && rows != 0 ? rows : 10;
            if (f["reset_pl"] == "1")
                s.PlStructureJson = null;
            else
            {
                // Each row carries its fixed identity (section:<key> / subtotal:<key>): reorder and rename, never re-feed.
                var posted = new List<(int Order, int Idx, PyDict Entry)>();
                for (int idx = 0; f.ContainsKey($"row_ident_{idx}"); idx++)
                {
                    string ident = f[$"row_ident_{idx}"].ToString();
                    string label = f[$"row_label_{idx}"].ToString().Trim();
                    int order = int.TryParse(f[$"row_order_{idx}"], out int o) ? o : idx;
                    int colon = ident.IndexOf(':');
                    string kind = ident[..colon], key = ident[(colon + 1)..];
                    var entry = new PyDict { { "label", label.Length > 0 ? label : PyTitle(key.Replace('_', ' ')) } };
                    if (kind == "section")
                    {
                        entry.Add("section", key);
                        if (f[$"row_negate_{idx}"] == "1") entry.Add("negate", true);
                    }
                    else
                        entry.Add("subtotal", key);
                    posted.Add((order, idx, entry));
                }
                if (posted.Count > 0)
                    s.PlStructureJson = PyJson.Dumps(posted.OrderBy(t => t.Order).ThenBy(t => t.Idx).Select(t => t.Entry).ToList());
            }
            return 0;
        }, ct);
        this.Flash("Report structure updated.", "success");
        return ToTab("reports");
    }

    /// <summary>Python's str.title(): every word's first letter up, the rest down.</summary>
    private static string PyTitle(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant(), @"(?<![A-Za-z])[a-z]", m => m.Value.ToUpperInvariant());

    [HttpPost("cash-flow")]
    public async Task<IActionResult> SaveCashFlow(CancellationToken ct)
    {
        if (Require("cash_flow") is { } denied) return denied;
        string method = Request.Form.ContainsKey("cash_flow_method") ? Request.Form["cash_flow_method"].ToString() : "indirect";
        string floorRaw = Request.Form["twcf_cash_floor"].ToString().Trim();
        decimal? floor = null;
        if (floorRaw.Length > 0)
        {
            if (!decimal.TryParse(floorRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                this.Flash("Cash floor must be a number.", "danger");
                return ToTab("cash_flow");
            }
            floor = v;
        }
        await InTransactionAsync(async () =>
        {
            var s = await firstUse.ReportSettingsAsync(ct);
            s.CashFlowMethod = method;
            s.TwcfCashFloor = floor;
            return 0;
        }, ct);
        this.Flash("Cash flow settings updated.", "success");
        return ToTab("cash_flow");
    }

    // ── Inventory ────────────────────────────────────────────────────────

    [HttpPost("inventory")]
    public async Task<IActionResult> SaveInventory(CancellationToken ct)
    {
        if (Require("inventory") is { } denied) return denied;
        var f = Request.Form;
        string method = f.ContainsKey("valuation_method") ? f["valuation_method"].ToString() : "weighted_average";
        if (method is not ("weighted_average" or "fifo")) method = "weighted_average";
        await InTransactionAsync(async () =>
        {
            var s = await firstUse.InventorySettingsAsync(ct);
            if (method != s.ValuationMethod)
            {
                // A REVALUATION, prospective only: stock on hand carries forward at book value.
                await Revaluation.ForMethodChangeAsync(db, method, ct);
                this.Flash($"Valuation method changed to {(method == "fifo" ? "FIFO" : "weighted average")}. Old costs unchanged.", "success");
            }
            s.ValuationMethod = method;
            s.AllowNegativeStock = f["allow_negative_stock"] == "on";
            s.AutoGenerateVouchers = f["auto_generate_vouchers"] == "on";
            s.DecimalPlaces = Math.Min(Math.Max(int.TryParse(f["decimal_places"], out int dp) ? dp : 4, 0), 6);
            string pf = f.ContainsKey("purchase_flow") ? f["purchase_flow"].ToString() : "with_po";
            s.PurchaseFlow = pf is "with_po" or "direct_invoice" ? pf : "with_po";
            string sf = f.ContainsKey("sales_flow") ? f["sales_flow"].ToString() : "with_so";
            s.SalesFlow = sf is "with_so" or "direct_invoice" ? sf : "with_so";
            int? Acct(string k) => int.TryParse(f[k], out int v) && v != 0 ? v : null;
            s.DefaultCogsAccountId = Acct("default_cogs_account_id");
            s.DefaultInventoryAccountId = Acct("default_inventory_account_id");
            s.DefaultReturnAccountId = Acct("default_return_account_id");
            return 0;
        }, ct);
        this.Flash("Inventory settings updated.", "success");
        return ToTab("inventory");
    }

    // ── Project labels ───────────────────────────────────────────────────

    /// <summary>GET /settings/labels renders the settings page on the labels tab.</summary>
    [HttpGet("labels")]
    public async Task<IActionResult> LabelsPage(CancellationToken ct) => Require("labels") ?? await Index("labels", ct);

    [HttpPost("labels")]
    public async Task<IActionResult> CreateLabel(CancellationToken ct)
    {
        if (Require("labels") is { } denied) return denied;
        string name = Request.Form["name"].ToString().Trim();
        if (name.Length == 0)
            this.Flash("Label name is required.", "error");
        else if (await db.Set<ProjectLabel>().AnyAsync(l => l.Name.ToLower() == name.ToLower(), ct))
            this.Flash($"A label named \"{name}\" already exists.", "error");
        else
        {
            db.Add(new ProjectLabel { Name = name, IsActive = true, CreatedAt = UtcNowMicro });
            await db.SaveChangesAsync(ct);
            this.Flash($"Label \"{name}\" created.", "success");
        }
        return ToTab("labels");
    }

    [HttpPost("labels/per-line")]
    public async Task<IActionResult> LabelsPerLine(CancellationToken ct)
    {
        if (Require("labels") is { } denied) return denied;
        bool on = Request.Form["enabled"] == "on";
        bool invoice = Request.Form["scope"] == "invoice";
        await InTransactionAsync(async () =>
        {
            var s = await firstUse.InventorySettingsAsync(ct);
            if (invoice) s.PerLineLabelingInvoice = on; else s.PerLineLabelingVoucher = on;
            return 0;
        }, ct);
        this.Flash($"Per-line labeling for {(invoice ? "invoice" : "voucher")}s " + (on ? "enabled" : "disabled") + ".", "success");
        return ToTab("labels");
    }

    [HttpPost("labels/{id:int}/rename")]
    public async Task<IActionResult> LabelRename(int id, CancellationToken ct)
    {
        if (Require("labels") is { } denied) return denied;
        var label = await db.Set<ProjectLabel>().FirstOrDefaultAsync(l => l.Id == id, ct);
        if (label is null) return NotFound();
        string name = Request.Form["name"].ToString().Trim();
        if (name.Length == 0)
            this.Flash("Label name is required.", "error");
        else if (await db.Set<ProjectLabel>().AnyAsync(l => l.Id != id && l.Name.ToLower() == name.ToLower(), ct))
            this.Flash($"A label named \"{name}\" already exists.", "error");
        else
        {
            label.Name = name;
            await db.SaveChangesAsync(ct);
            this.Flash("Label renamed.", "success");
        }
        return ToTab("labels");
    }

    [HttpPost("labels/{id:int}/toggle")]
    public async Task<IActionResult> LabelToggle(int id, CancellationToken ct)
    {
        if (Require("labels") is { } denied) return denied;
        var label = await db.Set<ProjectLabel>().FirstOrDefaultAsync(l => l.Id == id, ct);
        if (label is null) return NotFound();
        await InTransactionAsync(async () =>
        {
            label.IsActive = label.IsActive != true;
            // An archived label never stays a default.
            if (label.IsActive != true)
            {
                var s = await firstUse.InventorySettingsAsync(ct);
                if (s.DefaultVoucherLabelId == label.Id) s.DefaultVoucherLabelId = null;
                if (s.DefaultInvoiceLabelId == label.Id) s.DefaultInvoiceLabelId = null;
            }
            return 0;
        }, ct);
        this.Flash($"Label \"{label.Name}\" {(label.IsActive != true ? "deactivated" : "activated")}.", "success");
        return ToTab("labels");
    }

    [HttpPost("labels/defaults")]
    public async Task<IActionResult> LabelsDefaults(CancellationToken ct)
    {
        if (Require("labels") is { } denied) return denied;
        string dv = Request.Form["default_voucher_label_id"].ToString().Trim();
        string di = Request.Form["default_invoice_label_id"].ToString().Trim();
        await InTransactionAsync(async () =>
        {
            var s = await firstUse.InventorySettingsAsync(ct);
            s.DefaultVoucherLabelId = dv.Length > 0 && dv.All(char.IsAsciiDigit) ? int.Parse(dv) : null;
            s.DefaultInvoiceLabelId = di.Length > 0 && di.All(char.IsAsciiDigit) ? int.Parse(di) : null;
            return 0;
        }, ct);
        this.Flash("Default labels saved.", "success");
        return ToTab("labels");
    }
}
