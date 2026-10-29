using Accountix.Inventory.Services;

namespace Accountix.Inventory.Controllers;

/// <summary>Inventory reports (inventory_app/routes/reports.py): stock ledger, valuation, low stock.</summary>
[Area("Inventory")]
[Module(Modules.Inventory)]
[Route("inventory/reports")]
public sealed class ReportsController(AccountixDbContext db, IReportExport export, ILedger ledger, CompanyClock clock) : Controller
{
    public sealed record LedgerModel(IReadOnlyList<InventoryReports.ProductOption> Products, InventoryReports.ProductHead? Product,
                                     IReadOnlyList<StockLedger> Entries, int? SelectedId);

    public sealed record ValuationModel(bool Loaded, InventoryReports.Valuation? Data, string AsOf, string Start);

    private static int? IntArg(string? raw) => int.TryParse(raw, out int v) ? v : null;

    private FileContentResult Send(ExportFile f) => File(f.Content, f.ContentType, f.FileName);

    [HttpGet("stock-ledger")]
    [Right("inventory_reports", Rights.View)]
    public async Task<IActionResult> StockLedger(CancellationToken ct)
    {
        var reports = new InventoryReports(db);
        int? productId = IntArg(Request.Query["product_id"]);
        var products = await reports.ActiveProductsAsync(byName: true, ct);
        InventoryReports.ProductHead? product = null;
        List<StockLedger> entries = [];
        if (productId is int pid && pid != 0)
        {
            // Document-date order: a back-dated document sits where it belongs.
            entries = await reports.LedgerAsync(pid, ct);
            product = await reports.ProductAsync(pid, ct);
        }
        string? fmt = Request.Query["format"].FirstOrDefault();
        if (!string.IsNullOrEmpty(fmt) && product is not null)
            return Send(await export.ExportTableAsync(fmt, InventoryReports.LedgerTable(fmt, product, entries), ct));
        return View(new LedgerModel(products, product, entries, productId is 0 ? null : productId));
    }

    [HttpGet("valuation")]
    [Right("inventory_reports", Rights.View)]
    public async Task<IActionResult> Valuation(CancellationToken ct)
    {
        var q = Request.Query;
        var today = clock.Today;
        if (!new[] { "as_of", "from", "format", "run" }.Any(q.ContainsKey))
        {
            // Nothing is calculated until the report is asked for (Apply / export).
            return View(new ValuationModel(false, null, Iso(today), Iso(new DateOnly(today.Year, today.Month, 1))));
        }
        var asOf = DateArg("as_of", today);
        var start = DateArg("from", new DateOnly(asOf.Year, asOf.Month, 1));
        var data = await new InventoryReports(db).ValuationAsync(asOf, start, ledger, ct);
        string? fmt = q["format"].FirstOrDefault();
        if (!string.IsNullOrEmpty(fmt))
            return Send(await export.ExportTableAsync(fmt, InventoryReports.ValuationTable(data), ct));
        return View(new ValuationModel(true, data, Iso(data.AsOf), Iso(data.Start)));

        DateOnly DateArg(string name, DateOnly fallback)
        {
            try
            {
                return DateOnly.FromDateTime(DocumentDates.Parse(q[name].FirstOrDefault(), fallback.ToDateTime(TimeOnly.MinValue), clock));
            }
            catch (DocumentDateException)
            {
                return fallback;
            }
        }
    }

    [HttpGet("low-stock")]
    [Right("inventory_reports", Rights.View)]
    public async Task<IActionResult> LowStock(CancellationToken ct)
    {
        var products = await new InventoryReports(db).LowStockAsync(ct);
        string? fmt = Request.Query["format"].FirstOrDefault();
        if (!string.IsNullOrEmpty(fmt))
            return Send(await export.ExportTableAsync(fmt, InventoryReports.LowStockTable(products, clock.Today), ct));
        return View(products);
    }

    [HttpGet("api/stock-ledger-json")]
    [Right("inventory_reports", Rights.View)]
    public async Task<IActionResult> StockLedgerJson(CancellationToken ct)
    {
        int? productId = IntArg(Request.Query["product_id"]);
        if (productId is not int pid || pid == 0)
            return Json(Array.Empty<object>());
        var entries = await new InventoryReports(db).LedgerAsync(pid, ct);
        return Json(entries.Select(e => new Dictionary<string, object?>
        {
            ["id"] = e.Id,
            ["voucher_type"] = e.VoucherType,
            ["voucher_number"] = e.VoucherNumber,
            ["transaction_type"] = e.TransactionType,
            ["quantity"] = (double)e.Quantity,
            ["unit_cost"] = (double)e.UnitCost,
            ["running_qty"] = (double)e.RunningQty,
            ["running_value"] = (double)e.RunningCost,
            ["running_avg_cost"] = (double)e.RunningAvg,
            ["date"] = e.TxnDate is DateOnly d ? Iso(d) : e.CreatedAt is DateTime c ? c.ToString("yyyy-MM-dd") : "",
            ["notes"] = e.Notes ?? "",
        }));
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
