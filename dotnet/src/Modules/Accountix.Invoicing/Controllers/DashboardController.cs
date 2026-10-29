using Accountix.Invoicing.Models;
using Accountix.Invoicing.Services;

namespace Accountix.Invoicing.Controllers;

/// <summary>The invoicing dashboard (invoicing_app/routes/dashboard.py): the performance band.</summary>
[Area("Invoicing")]
[Module(Modules.Invoicing)]
[Route("invoicing")]
public sealed class DashboardController(AccountixDbContext db, PerformanceService perf) : Controller
{
    /// <summary>A query-string integer or null: a dashboard filter is not worth a 400.</summary>
    private static int? IntArg(string? raw)
    {
        raw = (raw ?? "").Trim();
        return int.TryParse(raw, out int v) && v != 0 ? v : null;
    }

    [HttpGet("")]
    [AnyRight("sales_invoices:view", "purchase_invoices:view")]
    public async Task<IActionResult> Index([FromQuery] string? year, [FromQuery] string? label, CancellationToken ct)
    {
        var years = await perf.AvailableYearsAsync(ct);
        int? y = IntArg(year);
        if ((y is null || !years.Contains(y.Value)) && years.Count > 0)
            y = years[0];
        int? labelId = IntArg(label);
        // Archived labels stay selectable: documents already carry them.
        var labels = await db.Set<ProjectLabel>().AsNoTracking().OrderBy(l => l.Name).ToListAsync(ct);
        if (labelId is int lid && labels.All(l => l.Id != lid))
            labelId = null;
        var performance = await perf.PerformanceAsync(y, labelId, ct);
        var geometry = PerformanceService.Chart(performance);
        string? nf = HttpContext.Access()?.Profile?.NumberFormat ?? "en";
        return View("Index", new DashboardModel
        {
            Perf = performance,
            Chart = geometry,
            ChartJson = PyJson.JsonDumps(PerformanceService.ChartJson(performance, geometry, nf)),
            Years = years,
            Labels = labels,
            SelYear = performance.Year,
            SelLabel = labelId,
        });
    }
}
