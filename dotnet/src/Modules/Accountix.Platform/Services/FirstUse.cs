namespace Accountix.Platform.Services;

/// <summary>
/// The settings rows made on first use (CompanyInfo.get, ReportSettings.get,
/// FiscalYearRule.get, InventorySettings.get, InvoiceSettings.get,
/// GlobalLimits.get): read the company's row ordered by id; when there is
/// none, queue on serialize("first-use", table, company) and look again, so
/// two first requests at once make ONE row; then add it with Python's column
/// defaults and save only that row. Call inside a transaction for the queue
/// to hold (Postgres advisory locks are transaction-scoped).
/// </summary>
public sealed class FirstUse(AccountixDbContext db, TimeProvider time)
{
    private DateTime Now => Micro(time.GetUtcNow().UtcDateTime);

    /// <summary>datetime.utcnow() as Postgres keeps it (microseconds).</summary>
    internal static DateTime Micro(DateTime t) => new(t.Ticks - t.Ticks % 10, t.Kind);

    private int Company => db.Company.CompanyId ?? throw new NoActiveCompanyException("No active company for a company setting.");

    public Task<CompanyInfo> CompanyInfoAsync(CancellationToken ct = default) =>
        GetAsync("company_info", () => new CompanyInfo
        {
            CompanyId = Company, CompanyName = "SolarKon Energy Solutions", Country = "Pakistan",
            FiscalYearStartMonth = 1, Currency = "PKR", CurrencySymbol = "Rs.", DateFormat = "Y-m-d",
            NumberFormat = "en", Timezone = "Asia/Karachi", CreatedAt = Now, UpdatedAt = Now,
        }, ct);

    public Task<ReportSetting> ReportSettingsAsync(CancellationToken ct = default) =>
        GetAsync("report_settings", () => new ReportSetting
        {
            CompanyId = Company, PlDetailRows = 10, InvoicePartyMode = "relevant", PurchasePartyMode = "relevant",
            SalesPartyMode = "relevant", CashFlowMethod = "indirect", UpdatedAt = Now,
        }, ct);

    public Task<FiscalYearRule> FiscalYearRuleAsync(CancellationToken ct = default) =>
        GetAsync("fiscal_year_rule", () => new FiscalYearRule
        {
            CompanyId = Company, StartMonth = 1, StartDay = 1, CreatedAt = Now, UpdatedAt = Now,
        }, ct);

    public Task<InventorySetting> InventorySettingsAsync(CancellationToken ct = default) =>
        GetAsync("inventory_settings", () => new InventorySetting
        {
            CompanyId = Company, ValuationMethod = "weighted_average", AllowNegativeStock = false, DecimalPlaces = 4,
            AutoGenerateVouchers = true, PurchaseFlow = "with_po", SalesFlow = "with_so",
            PerLineLabelingVoucher = false, PerLineLabelingInvoice = false,
        }, ct);

    public Task<InvoiceSetting> InvoiceSettingsAsync(CancellationToken ct = default) =>
        GetAsync("invoice_settings", () => new InvoiceSetting
        {
            CompanyId = Company, DefaultSalesTaxPct = 0, DefaultFurtherTaxPct = 0, DefaultWithholdingTaxPct = 0,
            DefaultDiscountPct = 0, DefaultChargesMode = "general", DefaultDiscountMode = "general", DefaultTaxMode = "general",
            ShowDiscountColumn = true, ShowChargesColumn = true, ShowTaxColumn = true, AutoAddLine = true,
            RequireApproval = true, AllowPartialPayment = true, DefaultPartyMode = "relevant",
            OverInvoiceTolerancePct = 0, WithholdingBase = "taxable", ShowFurtherTax = true, ShowWithholdingTax = true,
            ShowTransportBlock = true, CreateFromOrdersEnabled = true, PerLineDiscountEnabled = true, PerLineTaxEnabled = true,
        }, ct);

    /// <summary>GlobalLimits.get (a platform table: one row, no company).</summary>
    public async Task<GlobalLimit> GlobalLimitsAsync(CancellationToken ct = default)
    {
        var r = await db.GlobalLimits.OrderBy(g => g.Id).FirstOrDefaultAsync(ct);
        if (r is not null) return r;
        await db.SerializeAsync("first-use", "global_limits");
        r = await db.GlobalLimits.OrderBy(g => g.Id).FirstOrDefaultAsync(ct);
        if (r is not null) return r;
        r = new GlobalLimit { MaxCompaniesPerUser = 3, DefaultMaxMembers = 25, UpdatedAt = Now };
        db.GlobalLimits.Add(r);
        await SaveOnlyAsync(r, ct);
        return r;
    }

    private async Task<T> GetAsync<T>(string table, Func<T> make, CancellationToken ct) where T : class
    {
        int cid = Company;
        var set = db.Set<T>();
        T? row = await set.OrderBy(e => EF.Property<int>(e, "Id")).FirstOrDefaultAsync(ct);
        if (row is not null) return row;
        await db.SerializeAsync("first-use", table, cid);
        row = await set.OrderBy(e => EF.Property<int>(e, "Id")).FirstOrDefaultAsync(ct);
        if (row is not null) return row;
        row = make();
        set.Add(row);
        await SaveOnlyAsync(row, ct);
        return row;
    }

    /// <summary>db.session.flush(objects=[row]): save this row, nothing else pending.</summary>
    private async Task SaveOnlyAsync(object row, CancellationToken ct)
    {
        // Half-built new rows (an invoice being saved) stay out of this save.
        var pendingNew = db.ChangeTracker.Entries()
            .Where(e => !ReferenceEquals(e.Entity, row) && e.State == EntityState.Added)
            .Select(e => e.Entity)
            .ToList();
        foreach (var o in pendingNew) db.Entry(o).State = EntityState.Detached;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            foreach (var o in pendingNew) db.Add(o);
        }
    }
}
