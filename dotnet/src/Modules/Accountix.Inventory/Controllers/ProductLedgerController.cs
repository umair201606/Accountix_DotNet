using Accountix.Inventory.Services;

namespace Accountix.Inventory.Controllers;

/// <summary>
/// Product ledger, product sub-ledgers and the voucher print preview
/// (inventory_app/routes/vouchers.py product_ledger, product_ledger_list, voucher_preview).
/// </summary>
[Area("Inventory")]
[Module(Modules.Inventory)]
[Route("inventory/vouchers")]
public sealed class ProductLedgerController(AccountixDbContext db, IReportExport export, CompanyClock clock) : Controller
{
    public sealed record LedgerModel(IReadOnlyList<InventoryReports.ProductOption> Products, InventoryReports.ProductHead? Product,
                                     IReadOnlyList<StockLedger> Entries, int? SelectedId,
                                     IReadOnlyList<StockCostAdjustment> Adjustments);

    /// <summary>One printed line of the preview; the ADJ columns are present only for stock adjustments.</summary>
    public sealed record PreviewItem(string Product, string Qty, decimal UnitCost, decimal Total,
                                     string? SystemQty = null, string? PhysicalQty = null, string? Diff = null);

    public sealed class PreviewModel
    {
        public required string Title { get; init; }
        public required string Number { get; init; }
        public DateTime? Date { get; init; }
        public string? Status { get; init; }
        public IReadOnlyList<(string Label, string Value)> Extra { get; init; } = [];
        public IReadOnlyList<PreviewItem> Items { get; init; } = [];
        public bool Adjustment { get; init; }
        public int? ApprovedBy { get; init; }
        public DateTime? ApprovedAt { get; init; }
    }

    private static int? IntArg(string? raw) => int.TryParse(raw, out int v) ? v : null;

    private FileContentResult Send(ExportFile f) => File(f.Content, f.ContentType, f.FileName);

    [HttpGet("product-ledger")]
    [Right("inventory_reports", Rights.View)]
    public async Task<IActionResult> ProductLedger(CancellationToken ct)
    {
        var reports = new InventoryReports(db);
        int? productId = IntArg(Request.Query["product_id"]);
        var products = await reports.ActiveProductsAsync(byName: true, ct);
        InventoryReports.ProductHead? product = null;
        List<StockLedger> entries = [];
        List<StockCostAdjustment> adjustments = [];
        if (productId is int pid && pid != 0)
        {
            product = await reports.ProductAsync(pid, ct);
            entries = await reports.LedgerAsync(pid, ct);
            adjustments = await reports.AdjustmentsAsync(pid, ct);
        }
        string? fmt = Request.Query["format"].FirstOrDefault();
        if (!string.IsNullOrEmpty(fmt) && product is not null)
            return Send(await export.ExportTableAsync(fmt, InventoryReports.LedgerTable(fmt, product, entries, adjustments), ct));
        return View(new LedgerModel(products, product, entries, productId is 0 ? null : productId, adjustments));
    }

    [HttpGet("product-ledger/list")]
    [Right("inventory_reports", Rights.View)]
    public async Task<IActionResult> ProductLedgerList(CancellationToken ct)
    {
        var rows = await new InventoryReports(db).SubLedgersAsync(ct);
        string? fmt = Request.Query["format"].FirstOrDefault();
        if (!string.IsNullOrEmpty(fmt))
            return Send(await export.ExportTableAsync(fmt, InventoryReports.SubLedgerTable(rows, clock.Today), ct));
        return View(rows);
    }

    /// <summary>_inventory_preview: the section follows the voucher type; an unknown type needs none.</summary>
    private static readonly IReadOnlyDictionary<string, string> PreviewSections = new Dictionary<string, string>
    {
        ["CONS"] = "consumption_vouchers", ["SCRAP"] = "scrap_vouchers", ["ADJ"] = "adjustment_vouchers",
        ["PI"] = "purchase_invoices", ["PR"] = "purchase_returns",
    };

    [HttpGet("preview/{vtype}/{vid:int}")]
    [AnyMember]
    public async Task<IActionResult> Preview(string vtype, int vid, CancellationToken ct)
    {
        if (PreviewSections.TryGetValue(vtype.ToUpperInvariant(), out string? section) && this.Deny(section, Rights.View) is { } refused)
            return refused;
        PreviewModel? model = vtype switch
        {
            "CONS" => await ConsumptionAsync(vid, ct),
            "SCRAP" => await ScrapAsync(vid, ct),
            "ADJ" => await AdjustmentAsync(vid, ct),
            "PI" => await PurchaseInvoiceAsync(vid, ct),
            "PR" => await PurchaseReturnAsync(vid, ct),
            _ => null,
        };
        if (model is null && !new[] { "CONS", "SCRAP", "ADJ", "PI", "PR" }.Contains(vtype))
        {
            this.Flash("Unknown voucher type.", "error");
            return Redirect("/inventory/vouchers/consumption/list");
        }
        return model is null ? NotFound() : View(model);
    }

    private async Task<PreviewModel?> ConsumptionAsync(int id, CancellationToken ct)
    {
        var v = await db.Set<ConsumptionVoucher>().AsNoTracking().Include(x => x.ConsumptionItems).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (v is null) return null;
        return new PreviewModel
        {
            Title = "Consumption Voucher", Number = v.VoucherNumber, Date = v.Date, Status = v.Status,
            Items = v.ConsumptionItems.OrderBy(i => i.Id)
                .Select(i => new PreviewItem(PyText.Str(i.ProductName), PyText.Dec(i.Quantity), i.UnitCost ?? 0, i.TotalCost ?? 0)).ToList(),
            Extra = [("Department", PyText.Str(v.Department)), ("Reason", PyText.Str(v.Reason))],
            ApprovedBy = v.ApprovedBy, ApprovedAt = v.ApprovedAt,
        };
    }

    private async Task<PreviewModel?> ScrapAsync(int id, CancellationToken ct)
    {
        var v = await db.Set<ScrapVoucher>().AsNoTracking().Include(x => x.ScrapItems).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (v is null) return null;
        return new PreviewModel
        {
            Title = "Scrap Voucher", Number = v.VoucherNumber, Date = v.Date, Status = v.Status,
            Items = v.ScrapItems.OrderBy(i => i.Id)
                .Select(i => new PreviewItem(PyText.Str(i.ProductName), PyText.Dec(i.Quantity), i.UnitCost ?? 0, i.TotalCost ?? 0)).ToList(),
            Extra = [("Reason", PyText.Str(v.Reason))],
            ApprovedBy = v.ApprovedBy, ApprovedAt = v.ApprovedAt,
        };
    }

    private async Task<PreviewModel?> AdjustmentAsync(int id, CancellationToken ct)
    {
        var v = await db.Set<StockAdjustmentVoucher>().AsNoTracking().Include(x => x.StockAdjustmentItems).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (v is null) return null;
        return new PreviewModel
        {
            Title = "Stock Adjustment Voucher", Number = v.VoucherNumber, Date = v.Date, Status = v.Status, Adjustment = true,
            Items = v.StockAdjustmentItems.OrderBy(i => i.Id)
                .Select(i => new PreviewItem(PyText.Str(i.ProductName), PyText.Dec(i.Difference), i.UnitCost ?? 0, i.TotalCost ?? 0,
                                             PyText.Dec(i.SystemQty), PyText.Dec(i.PhysicalQty), PyText.Dec(i.Difference))).ToList(),
            Extra = [("Reason", PyText.Str(v.Reason))],
            ApprovedBy = v.ApprovedBy, ApprovedAt = v.ApprovedAt,
        };
    }

    private async Task<PreviewModel?> PurchaseInvoiceAsync(int id, CancellationToken ct)
    {
        var v = await db.Set<InvPurchaseInvoice>().AsNoTracking()
            .Include(x => x.InvPurchaseInvoiceItems).ThenInclude(i => i.Product)
            .Include(x => x.Supplier)
            .AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (v is null) return null;
        return new PreviewModel
        {
            Title = "Purchase Invoice",
            Number = !string.IsNullOrEmpty(v.VoucherNumber) ? v.VoucherNumber : v.InvoiceNumber,
            Date = v.InvoiceDate, Status = v.Status,
            Items = v.InvPurchaseInvoiceItems.OrderBy(i => i.Id).Select(i =>
            {
                double qty = (double)(i.Quantity ?? 0), price = (double)(i.UnitPrice ?? 0), after = (double)(i.TotalAfterDiscount ?? 0);
                return new PreviewItem(i.Product?.Name ?? PyText.Str(i.Description), PyText.Float(i.Quantity), i.UnitPrice ?? 0,
                                       (decimal)(after != 0 ? after : qty * price));
            }).ToList(),
            Extra = [("Supplier", v.Supplier?.Name ?? ""), ("Net Payable", PyText.Float(v.NetPayable))],
            ApprovedBy = v.ApprovedBy, ApprovedAt = v.ApprovedAt,
        };
    }

    private async Task<PreviewModel?> PurchaseReturnAsync(int id, CancellationToken ct)
    {
        var v = await db.Set<InvPurchaseReturn>().AsNoTracking()
            .Include(x => x.InvPurchaseReturnItems).ThenInclude(i => i.Product)
            .Include(x => x.Supplier)
            .AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (v is null) return null;
        return new PreviewModel
        {
            Title = "Purchase Return", Number = v.ReturnNumber, Date = v.ReturnDate, Status = v.Status,
            Items = v.InvPurchaseReturnItems.OrderBy(i => i.Id).Select(i =>
            {
                double qty = (double)(i.CurrentReturnQty ?? 0), price = (double)(i.UnitPrice ?? 0), net = (double)(i.NetReturnValue ?? 0);
                return new PreviewItem(i.Product?.Name ?? PyText.Str(i.Description), PyText.Float(i.CurrentReturnQty), i.UnitPrice ?? 0,
                                       (decimal)(net != 0 ? net : qty * price));
            }).ToList(),
            Extra = [("Supplier", v.Supplier?.Name ?? ""), ("Net Return", PyText.Float(v.NetReturnAmount))],
            ApprovedBy = v.ApprovedBy, ApprovedAt = v.ApprovedAt,
        };
    }
}
