using System.Globalization;

namespace Accountix.Platform.Controllers;

// Invoicing tabs: procurement / sales documents, invoice defaults, invoice templates.
public sealed partial class SettingsController
{
    public sealed record DocumentsTab(ReportSetting Settings, IReadOnlyList<InvoiceTemplate> Templates);
    public sealed record TemplatesTab(IReadOnlyList<InvoiceTemplate> Sales, IReadOnlyList<InvoiceTemplate> Purchase);
    public sealed record InvoicingTab(
        InvoiceSetting Settings, IReadOnlyList<ChartOfAccount> Accounts,
        IReadOnlyList<(ChargeLedgerDefault D, ChartOfAccount? A)> ChargeDefaults, IReadOnlyList<ChartOfAccount> ChargeLedgerChoices,
        IReadOnlyList<(CategoryRevenueAccount M, InvCategory? C, ChartOfAccount? A)> CategoryMaps,
        IReadOnlyList<InvCategory> Categories, IReadOnlyList<InvCategory> UnmappedCategories, IReadOnlyList<ChartOfAccount> RevenueAccounts,
        IReadOnlyList<(TaxRateAccount M, ChartOfAccount? A)> TaxRateMaps, IReadOnlyList<ChartOfAccount> TaxAccounts);

    /// <summary>ReportSettings.party_mode(doc): the per-document mode, else the deprecated single flag, else "relevant".</summary>
    public static string PartyMode(ReportSetting s, string doc)
    {
        string? v = doc == "purchase" ? s.PurchasePartyMode : s.SalesPartyMode;
        return J.Or(v, J.Or(s.InvoicePartyMode, "relevant"));
    }

    private async Task<object?> DocumentsTabAsync(string doc, CancellationToken ct)
    {
        var rs = await InTransactionAsync(() => firstUse.ReportSettingsAsync(ct), ct);
        var templates = await db.Set<InvoiceTemplate>().AsNoTracking().Where(t => t.Type == doc).OrderBy(t => t.Name).ToListAsync(ct);
        return new DocumentsTab(rs, templates);
    }

    private async Task<object?> TemplatesTabAsync(CancellationToken ct)
    {
        var all = await db.Set<InvoiceTemplate>().AsNoTracking().OrderBy(t => t.Name)
            .Select(t => new InvoiceTemplate { Id = t.Id, Name = t.Name, Type = t.Type, Design = t.Design, AccentColor = t.AccentColor, IsDefault = t.IsDefault, BodyHtml = "" })
            .ToListAsync(ct);
        return new TemplatesTab(all.Where(t => t.Type == "sales").ToList(), all.Where(t => t.Type == "purchase").ToList());
    }

    private async Task<object?> InvoicingTabAsync(CancellationToken ct)
    {
        var s = await InTransactionAsync(() => firstUse.InvoiceSettingsAsync(ct), ct);
        var accounts = await PostableAccountsAsync(ct);
        var byId = await db.Set<ChartOfAccount>().AsNoTracking().ToDictionaryAsync(a => a.Id, ct);
        var defaults = (await db.Set<ChargeLedgerDefault>().AsNoTracking().ToListAsync(ct))
            .Select(d => (d, byId.GetValueOrDefault(d.AccountId))).Where(x => x.Item2 is not null)
            .OrderBy(x => x.Item2!.Code, StringComparer.Ordinal).ToList();
        var taken = defaults.Select(x => x.d.AccountId).ToHashSet();
        var categories = await db.Set<InvCategory>().AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
        var catById = categories.ToDictionary(c => c.Id);
        var catMaps = (await db.Set<CategoryRevenueAccount>().AsNoTracking().ToListAsync(ct))
            .Select(m => (m, catById.GetValueOrDefault(m.CategoryId), byId.GetValueOrDefault(m.AccountId)))
            .OrderBy(x => x.Item3?.Code ?? "", StringComparer.Ordinal).ToList();
        var mapped = catMaps.Select(x => x.m.CategoryId).ToHashSet();
        var taxMaps = (await db.Set<TaxRateAccount>().AsNoTracking().OrderBy(m => m.RatePct).ToListAsync(ct))
            .Select(m => (m, byId.GetValueOrDefault(m.AccountId))).ToList();
        return new InvoicingTab(s, accounts,
            defaults.Select(x => (x.d, x.Item2)).ToList(),
            accounts.Where(a => !taken.Contains(a.Id) && a.Type is "expense" or "revenue").ToList(),
            catMaps, categories, categories.Where(c => !mapped.Contains(c.Id)).ToList(),
            accounts.Where(a => a.Type == "revenue").ToList(), taxMaps, accounts.Where(a => a.Type == "liability").ToList());
    }

    private static bool IsDocType(string doc) => doc is "purchase" or "sales";

    [HttpPost("documents/{doc}")]
    public async Task<IActionResult> SaveDocumentSettings(string doc, CancellationToken ct)
    {
        if (!IsDocType(doc)) return NotFound();
        if (Require(doc) is { } denied) return denied;
        string mode = Request.Form.ContainsKey("party_mode") ? Request.Form["party_mode"].ToString() : "relevant";
        if (mode is not ("relevant" or "all")) mode = "relevant";
        await InTransactionAsync(async () =>
        {
            var s = await firstUse.ReportSettingsAsync(ct);
            if (doc == "purchase") s.PurchasePartyMode = mode; else s.SalesPartyMode = mode;
            return 0;
        }, ct);
        this.Flash($"{(doc == "purchase" ? "Procurement" : "Sales")} settings updated.", "success");
        return ToTab(doc);
    }

    [HttpPost("documents/{doc}/template")]
    public async Task<IActionResult> SaveDocumentTemplate(string doc, CancellationToken ct)
    {
        if (!IsDocType(doc)) return NotFound();
        if (Require(doc) is { } denied) return denied;
        string text = Request.Form["template_text"].ToString();
        await InTransactionAsync(async () =>
        {
            var s = await firstUse.ReportSettingsAsync(ct);
            if (doc == "purchase") s.PurchaseTemplateText = text; else s.SalesTemplateText = text;
            return 0;
        }, ct);
        this.Flash($"{(doc == "purchase" ? "Procurement" : "Sales")} template saved.", "success");
        return ToTab(doc);
    }

    [HttpPost("documents/{doc}/select-template")]
    public async Task<IActionResult> SelectDocumentTemplate(string doc, CancellationToken ct)
    {
        if (!IsDocType(doc)) return NotFound();
        if (Require(doc) is { } denied) return denied;
        int? templateId = int.TryParse(Request.Form["template_id"], out int tid) && tid != 0 ? tid : null;
        if (templateId is int id && !await db.Set<InvoiceTemplate>().AnyAsync(t => t.Id == id && t.Type == doc, ct))
        {
            this.Flash("Invalid template.", "error");
            return ToTab(doc);
        }
        await InTransactionAsync(async () =>
        {
            var s = await firstUse.ReportSettingsAsync(ct);
            if (doc == "purchase") s.PurchaseTemplateId = templateId; else s.SalesTemplateId = templateId;
            return 0;
        }, ct);
        this.Flash($"{(doc == "purchase" ? "Procurement" : "Sales")} print template updated.", "success");
        return ToTab(doc);
    }

    [HttpPost("templates/delete/{id:int}")]
    public async Task<IActionResult> DeleteTemplate(int id, CancellationToken ct)
    {
        if (Require("templates") is { } denied) return denied;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var t = await db.Set<InvoiceTemplate>().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return NotFound();
        bool wasDefault = t.IsDefault == true;
        db.Remove(t);
        await db.SaveChangesAsync(ct);
        // Never leave a type with templates but no default.
        if (wasDefault && await db.Set<InvoiceTemplate>().Where(x => x.Type == t.Type).OrderBy(x => x.Id).FirstOrDefaultAsync(ct) is { } next)
        {
            next.IsDefault = true;
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        this.Flash($"Template '{t.Name}' deleted.", "success");
        return ToTab("templates");
    }

    /// <summary>request.form.get(k, 0, type=float) or 0 (a Money column).</summary>
    private decimal FormFloat(string key)
    {
        string v = Request.Form[key].ToString();
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && !double.IsNaN(d) && d != 0
            ? Math.Round((decimal)d, 6) : 0m;
    }

    private int? FormInt(string key) => int.TryParse(Request.Form[key], out int v) ? v : null;

    /// <summary>_mapping_account: a postable, active ledger of one of these types in THIS company, or null.</summary>
    private async Task<ChartOfAccount?> MappingAccountAsync(int? id, string type, CancellationToken ct) =>
        id is int i && i != 0
            ? await db.Set<ChartOfAccount>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == i && a.IsActive == true && a.Level >= 5 && a.Type == type, ct)
            : null;

    [HttpPost("invoicing")]
    public async Task<IActionResult> SaveInvoicing(CancellationToken ct)
    {
        if (Require("invoicing") is { } denied) return denied;
        var f = Request.Form;
        bool On(string k) => f[k] == "on";
        string Get(string k, string d) => f.ContainsKey(k) ? f[k].ToString() : d;
        var ignored = new List<string>();
        await InTransactionAsync(async () =>
        {
            var s = await firstUse.InvoiceSettingsAsync(ct);
            s.DefaultSalesTaxPct = FormFloat("default_sales_tax_pct");
            s.DefaultFurtherTaxPct = FormFloat("default_further_tax_pct");
            s.DefaultWithholdingTaxPct = FormFloat("default_withholding_tax_pct");
            s.DefaultDiscountPct = FormFloat("default_discount_pct");
            s.DefaultChargesMode = Get("default_charges_mode", "general");
            s.DefaultDiscountMode = Get("default_discount_mode", "general");
            s.DefaultTaxMode = Get("default_tax_mode", "general");
            s.ShowDiscountColumn = On("show_discount_column");
            s.ShowChargesColumn = On("show_charges_column");
            s.ShowTaxColumn = On("show_tax_column");
            s.AutoAddLine = On("auto_add_line");
            s.RequireApproval = On("require_approval");
            s.AllowPartialPayment = On("allow_partial_payment");
            s.DefaultPartyMode = Get("default_party_mode", "relevant");
            s.OverInvoiceTolerancePct = FormFloat("over_invoice_tolerance_pct");
            string wh = Get("withholding_base", "taxable");
            s.WithholdingBase = wh is "taxable" or "gross" ? wh : "taxable";
            s.CreateFromOrdersEnabled = On("create_from_orders_enabled");
            s.PerLineDiscountEnabled = On("per_line_discount_enabled");
            s.PerLineTaxEnabled = On("per_line_tax_enabled");
            s.ShowWithholdingTax = On("show_withholding_tax");
            s.ShowFurtherTax = On("show_further_tax");
            s.ShowTransportBlock = On("show_transport_block");

            // Per-ledger charge defaults: tax-base switches only mean anything on a billed charge.
            foreach (var d in await db.Set<ChargeLedgerDefault>().ToListAsync(ct))
            {
                if (On($"cld_delete_{d.Id}")) { db.Remove(d); continue; }
                string treatment = Get($"cld_treatment_{d.Id}", d.Treatment ?? "");
                d.Treatment = treatment is "bill" or "absorb" or "expense" ? treatment : "bill";
                bool billed = d.Treatment == "bill";
                d.StTaxable = billed && On($"cld_st_{d.Id}");
                d.WhtTaxable = billed && On($"cld_wht_{d.Id}");
                d.ExtraTaxable = billed && On($"cld_extra_{d.Id}");
                string applies = Get($"cld_applies_{d.Id}", d.AppliesTo ?? "");
                d.AppliesTo = applies is "both" or "sales" or "purchase" ? applies : "both";
            }
            if (FormInt("cld_new_account") is int newAcct && newAcct != 0
                && !await db.Set<ChargeLedgerDefault>().AnyAsync(d => d.AccountId == newAcct, ct))
            {
                string treatment = Get("cld_new_treatment", "bill");
                bool billed = treatment == "bill";
                db.Add(new ChargeLedgerDefault
                {
                    AccountId = newAcct, Treatment = treatment is "bill" or "absorb" or "expense" ? treatment : "bill",
                    StTaxable = billed && On("cld_new_st"), WhtTaxable = billed && On("cld_new_wht"), ExtraTaxable = billed && On("cld_new_extra"),
                    AppliesTo = "both",
                });
            }

            // Category → revenue account (an unmapped category posts to the global revenue account).
            foreach (var m in await db.Set<CategoryRevenueAccount>().ToListAsync(ct))
            {
                if (On($"cra_delete_{m.Id}")) { db.Remove(m); continue; }
                if (FormInt($"cra_account_{m.Id}") is int acct && acct != 0 && acct != m.AccountId)
                {
                    if (await MappingAccountAsync(acct, "revenue", ct) is { } valid) m.AccountId = valid.Id;
                    else ignored.Add($"category {m.CategoryId}");
                }
            }
            int? newCat = FormInt("cra_new_category"), newCatAcct = FormInt("cra_new_account");
            if (newCat is int nc && nc != 0 && newCatAcct is int nca && nca != 0)
            {
                var category = await db.Set<InvCategory>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == nc, ct);
                var account = await MappingAccountAsync(nca, "revenue", ct);
                if (category is not null && account is not null && !await db.Set<CategoryRevenueAccount>().AnyAsync(x => x.CategoryId == nc, ct))
                    db.Add(new CategoryRevenueAccount { CategoryId = category.Id, AccountId = account.Id });
                else if (category is null || account is null)
                    ignored.Add("the new category");
            }

            // Sales-tax rate → Output Sales Tax sub-account.
            foreach (var m in await db.Set<TaxRateAccount>().ToListAsync(ct))
            {
                if (On($"tra_delete_{m.Id}")) { db.Remove(m); continue; }
                if (FormInt($"tra_account_{m.Id}") is int acct && acct != 0 && acct != m.AccountId)
                {
                    if (await MappingAccountAsync(acct, "liability", ct) is { } valid) m.AccountId = valid.Id;
                    else ignored.Add($"rate {Audit.PyJson.FloatRepr((double)m.RatePct)}%");
                }
            }
            bool rateOk = double.TryParse(f["tra_new_rate"], NumberStyles.Float, CultureInfo.InvariantCulture, out double newRate);
            if (rateOk && FormInt("tra_new_account") is int nra && nra != 0)
            {
                var account = await MappingAccountAsync(nra, "liability", ct);
                decimal rate = (decimal)newRate;
                if (newRate is >= 0 and <= 100 && account is not null && !await db.Set<TaxRateAccount>().AnyAsync(x => x.RatePct == rate, ct))
                    db.Add(new TaxRateAccount { RatePct = rate, AccountId = account.Id });
                else if (newRate is < 0 or > 100 || account is null)
                    ignored.Add($"the new rate {newRate.ToString("G6", CultureInfo.InvariantCulture)}%");
            }
            return 0;
        }, ct);
        if (ignored.Count > 0)
            this.Flash("Some posting-account mappings were ignored — the ledger must be an active postable revenue/liability account of this company: "
                       + string.Join(", ", ignored) + ".", "error");
        this.Flash("Invoice settings updated.", "success");
        return ToTab("invoicing");
    }

    // ── Invoice template designer (design + toggles compiled to HTML; custom HTML kept as is) ──

    public sealed record TemplateFormModel(InvoiceTemplate? Template, string DocType, IReadOnlyDictionary<string, object> Options,
                                           string DefaultBody, bool IsCustom);

    private static bool IsCustomTemplate(InvoiceTemplate t) => t.Design is null or "" or "custom";

    private ViewResult TemplateForm(InvoiceTemplate? t, string docType) =>
        View("TemplateForm", new TemplateFormModel(t, docType,
            t is null ? Accountix.Invoicing.Services.InvoiceTemplates.DefaultOptions(docType)
                      : Accountix.Invoicing.Services.InvoiceTemplates.Options(t.Type, t.OptionsJson),
            Accountix.Invoicing.Services.InvoiceDesigns.BuildBody("classic", docType, "#0f766e",
                Accountix.Invoicing.Services.InvoiceTemplates.DefaultOptions(docType)),
            t is not null && IsCustomTemplate(t)));

    /// <summary>_read_template_form: (name, design, accent, options in option-group order, custom body, is_default).</summary>
    private (string Name, string Design, string Accent, Audit.PyDict Options, string Body, bool IsDefault) ReadTemplateForm(string docType)
    {
        var f = Request.Form;
        string design = f.ContainsKey("design") ? f["design"].ToString() : "classic";
        if (!Accountix.Invoicing.Services.InvoiceDesigns.DesignKeys.Contains(design) && design != "custom") design = "classic";
        string accent = f.ContainsKey("accent_color") ? f["accent_color"].ToString() : "#0f766e";
        if (!System.Text.RegularExpressions.Regex.IsMatch(accent, "^#[0-9a-fA-F]{6}$")) accent = "#0f766e";
        var opts = new Audit.PyDict();
        foreach (string key in Accountix.Invoicing.Services.InvoiceDesigns.OptionKeys(docType))
            opts.Add(key, Accountix.Invoicing.Services.InvoiceDesigns.StringOptions.Contains(key)
                ? (f.ContainsKey(key) ? f[key].ToString() : "combined")
                : f[key] == "on");
        return (f["name"].ToString().Trim(), design, accent, opts, f["body_html"].ToString().Trim(), f["is_default"] == "on");
    }

    private static Dictionary<string, object> AsDict(Audit.PyDict d)
    {
        var o = new Dictionary<string, object>();
        foreach (var (k, v) in d) o[k] = v!;
        return o;
    }

    [HttpPost("templates/preview")]
    public async Task<IActionResult> PreviewTemplate(CancellationToken ct)
    {
        if (Require("templates") is { } denied) return denied;
        string docType = Request.Form["type"] == "sales" ? "sales" : "purchase";
        var (_, design, accent, opts, custom, _) = ReadTemplateForm(docType);
        var dict = AsDict(opts);
        string body = design == "custom" ? custom : Accountix.Invoicing.Services.InvoiceDesigns.BuildBody(design, docType, accent, dict);
        var info = await InTransactionAsync(() => firstUse.CompanyInfoAsync(ct), ct);
        var ctx = Accountix.Invoicing.Services.InvoiceDesigns.SampleContext(docType, info, dict, time.GetUtcNow().UtcDateTime, info.NumberFormat);
        return Content(Accountix.Invoicing.Services.InvoiceTemplates.Render(body, ctx), "text/html; charset=utf-8");
    }

    [HttpGet("templates/create")]
    public IActionResult CreateTemplate(string? type) =>
        Require("templates") ?? TemplateForm(null, type == "sales" ? "sales" : "purchase");

    [HttpPost("templates/create")]
    public async Task<IActionResult> CreateTemplatePost(CancellationToken ct)
    {
        if (Require("templates") is { } denied) return denied;
        string docType = (Request.Form.ContainsKey("type") ? Request.Form["type"].ToString() : Request.Query["type"].ToString()) == "sales" ? "sales" : "purchase";
        var (name, design, accent, opts, custom, isDefault) = ReadTemplateForm(docType);
        if (name.Length == 0) { this.Flash("Give the template a name.", "error"); return TemplateForm(null, docType); }
        if (design == "custom" && custom.Length == 0) { this.Flash("A custom template needs some HTML.", "error"); return TemplateForm(null, docType); }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = UtcNowMicro;
        var t = new InvoiceTemplate
        {
            Name = name, Type = docType, Design = design, AccentColor = accent, IsDefault = isDefault, BodyHtml = custom,
            OptionsJson = Audit.PyJson.Dumps(opts), CreatedAt = now, UpdatedAt = now,
        };
        if (!IsCustomTemplate(t))       // recompile: a design's body is generated, a custom one kept
            t.BodyHtml = Accountix.Invoicing.Services.InvoiceDesigns.BuildBody(t.Design, t.Type, t.AccentColor, Accountix.Invoicing.Services.InvoiceTemplates.Options(t.Type, t.OptionsJson));
        // The first template of its type becomes the default (otherwise nothing prints with it).
        if (isDefault || !await db.Set<InvoiceTemplate>().AnyAsync(x => x.Type == docType, ct))
        {
            await db.Set<InvoiceTemplate>().Where(x => x.Type == docType && x.IsDefault == true)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.IsDefault, false), ct);
            t.IsDefault = true;
        }
        db.Add(t);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"Template '{name}' created.", "success");
        return ToTab("templates");
    }

    [HttpGet("templates/edit/{id:int}")]
    public async Task<IActionResult> EditTemplate(int id, CancellationToken ct)
    {
        if (Require("templates") is { } denied) return denied;
        var t = await db.Set<InvoiceTemplate>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return t is null ? NotFound() : TemplateForm(t, t.Type);
    }

    [HttpPost("templates/edit/{id:int}")]
    public async Task<IActionResult> EditTemplatePost(int id, CancellationToken ct)
    {
        if (Require("templates") is { } denied) return denied;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var t = await db.LockAsync<InvoiceTemplate>(id, ct);
        if (t is null) return NotFound();
        var (name, design, accent, opts, custom, isDefault) = ReadTemplateForm(t.Type);
        if (name.Length == 0) { this.Flash("Give the template a name.", "error"); return TemplateForm(t, t.Type); }
        if (design == "custom" && custom.Length == 0) { this.Flash("A custom template needs some HTML.", "error"); return TemplateForm(t, t.Type); }
        if (isDefault && t.IsDefault != true)
            await db.Set<InvoiceTemplate>().Where(x => x.Type == t.Type && x.IsDefault == true)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.IsDefault, false), ct);
        t.Name = name;
        t.Design = design;
        t.AccentColor = accent;
        t.OptionsJson = Audit.PyJson.Dumps(opts);
        t.BodyHtml = design == "custom" ? custom
            : Accountix.Invoicing.Services.InvoiceDesigns.BuildBody(design, t.Type, accent, Accountix.Invoicing.Services.InvoiceTemplates.Options(t.Type, t.OptionsJson));
        t.IsDefault = isDefault || t.IsDefault == true;
        t.UpdatedAt = UtcNowMicro;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"Template '{name}' updated.", "success");
        return ToTab("templates");
    }
}
