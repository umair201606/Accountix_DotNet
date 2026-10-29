using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Accountix.Invoicing.Services;

/// <summary>
/// Edit versions (shared/concurrency.stale_edit / bump_edit): a save carrying
/// an older edit_version than the document's is refused, naming who changed
/// it and when (from the audit trail). Call with the document locked.
/// </summary>
public sealed class EditVersions(AccountixDbContext db, CompanyClock clock)
{
    /// <summary>The audit-trail entity type per table (shared/audit.TRACKED).</summary>
    public static readonly IReadOnlyDictionary<string, string> Tracked = new Dictionary<string, string>
    {
        ["inv_invoices"] = "Sales invoice",
        ["inv_purchase_invoices"] = "Purchase invoice",
        ["inv_sales_returns"] = "Sales return",
        ["inv_purchase_returns"] = "Purchase return",
        ["inv_sales_orders"] = "Sales order",
        ["inv_purchase_orders"] = "Purchase order",
    };

    /// <summary>stale_edit: the refusal text, or null after bumping the version.</summary>
    public async Task<string?> StaleAsync(string table, int docId, int? companyId, int? current, JsonElement? sent,
                                          string label, Action<int> setVersion, CancellationToken ct)
    {
        int? s = ParseSent(sent);
        int cur = current ?? 0;
        if (s is int v && v != cur)
        {
            string who = await LastChangeAsync(table, docId, companyId, ct);
            return $"{label} was changed{who} after you opened it, so your changes were not saved. Reload it to see the " +
                   "latest version, then make your changes again.";
        }
        setVersion(cur + 1);
        return null;
    }

    /// <summary>int(sent) for a version the form sent; None for "", None or anything unreadable.</summary>
    public static int? ParseSent(JsonElement? sent)
    {
        if (sent is not { } e) return null;
        switch (e.ValueKind)
        {
            case JsonValueKind.Number:
                if (e.TryGetInt64(out long l)) return (int)l;
                return (int)Math.Truncate(e.GetDouble());
            case JsonValueKind.String:
                string str = e.GetString()!;
                if (str.Length == 0) return null;
                return int.TryParse(str.Trim().Replace("_", ""), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int i) ? i : null;
            case JsonValueKind.True: return 1;
            case JsonValueKind.False: return 0;
            default: return null;
        }
    }

    public static int Bump(int? current) => (current ?? 0) + 1;

    private async Task<string> LastChangeAsync(string table, int docId, int? companyId, CancellationToken ct)
    {
        try
        {
            if (!Tracked.TryGetValue(table, out string? type)) return "";
            var row = await db.AuditLogs.AsNoTracking()
                .Where(a => a.EntityType == type && a.EntityId == docId && a.CompanyId == companyId)
                .OrderByDescending(a => a.CreatedAt).FirstOrDefaultAsync(ct);
            if (row is null) return "";
            var when = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc), clock.Zone);
            return $" by {(string.IsNullOrEmpty(row.UserName) ? "another user" : row.UserName)} at " +
                   when.ToString("HH:mm 'on' dd MMM", CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is InvalidOperationException or DbUpdateException or TimeZoneNotFoundException)
        {
            return "";
        }
    }
}

/// <summary>The settings rows the documents read (InvoiceSettings, ReportSettings, InventorySettings .get()).</summary>
public sealed class DocSettings(AccountixDbContext db)
{
    private InvoiceSetting? _invoice;
    private ReportSetting? _report;
    private InventorySetting? _inventory;
    private bool _loadedInvoice, _loadedReport, _loadedInventory;

    /// <summary>InvoiceSettings.get(): the company's row, or the defaults when it has none yet.</summary>
    public async Task<InvoiceSetting> InvoiceAsync(CancellationToken ct)
    {
        if (!_loadedInvoice)
        {
            _invoice = await db.Set<InvoiceSetting>().AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
            _loadedInvoice = true;
        }
        return _invoice ?? new InvoiceSetting
        {
            DefaultSalesTaxPct = 0, DefaultFurtherTaxPct = 0, DefaultWithholdingTaxPct = 0, DefaultDiscountPct = 0,
            DefaultChargesMode = "general", DefaultDiscountMode = "general", DefaultTaxMode = "general",
            ShowDiscountColumn = true, ShowChargesColumn = true, ShowTaxColumn = true, AutoAddLine = true,
            RequireApproval = true, AllowPartialPayment = true, DefaultPartyMode = "relevant", OverInvoiceTolerancePct = 0,
            WithholdingBase = "taxable", ShowFurtherTax = true, ShowWithholdingTax = true, ShowTransportBlock = true,
            CreateFromOrdersEnabled = true, PerLineDiscountEnabled = true, PerLineTaxEnabled = true,
        };
    }

    public async Task<ReportSetting> ReportAsync(CancellationToken ct)
    {
        if (!_loadedReport)
        {
            _report = await db.Set<ReportSetting>().AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
            _loadedReport = true;
        }
        return _report ?? new ReportSetting();
    }

    public async Task<InventorySetting> InventoryAsync(CancellationToken ct)
    {
        if (!_loadedInventory)
        {
            _inventory = await db.Set<InventorySetting>().AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
            _loadedInventory = true;
        }
        return _inventory ?? new InventorySetting
        {
            ValuationMethod = "weighted_average", PurchaseFlow = "with_po", SalesFlow = "with_so",
            PerLineLabelingVoucher = false, PerLineLabelingInvoice = false,
        };
    }

    /// <summary>ReportSettings.party_mode(doc).</summary>
    public static string PartyMode(ReportSetting rs, string doc)
    {
        string? v = doc == "purchase" ? rs.PurchasePartyMode : rs.SalesPartyMode;
        if (!string.IsNullOrEmpty(v)) return v;
        return string.IsNullOrEmpty(rs.InvoicePartyMode) ? "relevant" : rs.InvoicePartyMode;
    }

    /// <summary>InventorySettings.default_invoice_label(): the default label while it is active.</summary>
    public async Task<ProjectLabel?> DefaultInvoiceLabelAsync(CancellationToken ct)
    {
        var s = await InventoryAsync(ct);
        if (s.DefaultInvoiceLabelId is not int lid) return null;
        return await db.Set<ProjectLabel>().AsNoTracking().FirstOrDefaultAsync(l => l.Id == lid && l.IsActive == true, ct);
    }

    public Task<List<ProjectLabel>> ActiveLabelsAsync(CancellationToken ct) =>
        db.Set<ProjectLabel>().AsNoTracking().Where(l => l.IsActive == true).OrderBy(l => l.Name).ToListAsync(ct);
}

/// <summary>
/// Invoice tracking caches after a document changes (payment_tracking.sync_invoice /
/// drop_allocations_for_doc). Owned by Parties; failures are non-fatal, as in Python.
/// </summary>
public sealed class TrackingSync(AccountixDbContext db, IPaymentTracking tracking, ILogger<TrackingSync> log)
{
    public const string SalesDoc = "SI", PurchaseDoc = "PI";

    /// <summary>sync_invoice: re-derive paid_amount / payment_status (after commit; never fails the save).</summary>
    public async Task SyncInvoiceAsync(string docType, int docId, CancellationToken ct)
    {
        try
        {
            await tracking.SyncInvoiceAsync(docType, docId, ct);
        }
        catch (Exception e) when (e is NotYetPortedException or InvalidOperationException or DbUpdateException)
        {
            log.LogWarning(e, "Invoice tracking not refreshed for {Type} {Id}", docType, docId);
        }
    }

    /// <summary>drop_allocations_for_doc: assignments pointing at an invoice about to be deleted.</summary>
    public async Task DropAllocationsAsync(string docType, int docId, CancellationToken ct)
    {
        try
        {
            await tracking.DropForDocAsync(docType, docId, ct);
            return;
        }
        catch (NotYetPortedException)
        {
            // Until Parties registers it: the same delete Python's helper makes.
        }
        var rows = await db.Set<PaymentAllocation>().Where(a => a.DocType == docType && a.DocId == docId).ToListAsync(ct);
        if (rows.Count == 0) return;
        db.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
    }
}
