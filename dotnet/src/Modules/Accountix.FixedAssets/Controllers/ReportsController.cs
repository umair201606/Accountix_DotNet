using System.Globalization;
using Accountix.FixedAssets.Services;

namespace Accountix.FixedAssets.Controllers;

/// <summary>The asset register and the movement schedule, with their exports (fixed_assets_app/routes/reports.py).</summary>
[Area("FixedAssets")]
[Module(Modules.FixedAssets)]
[Route("fixed-assets/reports")]
public sealed class ReportsController(AccountixDbContext db, AssetBooks books, Movement movement, IPeriods periods,
                                      IReportExport export, CompanyClock clock) : Controller
{
    private const string Section = "fixed_assets_reports";

    [HttpGet("")]
    [Right(Section, Rights.View)]
    public async Task<IActionResult> Index([FromQuery] string? format, CancellationToken ct)
    {
        // The register is the assets in service; depreciation is derived from the
        // posted charges. The status summary covers every asset ever booked.
        var booked = db.Set<FixedAsset>().Where(a => a.Status != null && a.Status != "draft" && a.Status != "cancelled");
        var everything = await booked.AsNoTracking().Include(a => a.Category)
            .OrderBy(a => a.Category.Name).ThenBy(a => a.Name).ToListAsync(ct);
        var live = await books.LiveByAssetAsync(booked.Where(a => a.Status == "active").Select(a => a.Id), ct);
        var assets = everything.Where(a => a.Status == "active")
            .Select(a => new AssetCalc(a, live.GetValueOrDefault(a.Id, LiveCharges.None))).ToList();
        double totalCost = Py.Sum(assets.Select(a => a.Cost));
        double totalDep = Py.Sum(assets.Select(a => a.PostedDepreciation));
        var byCategory = new List<CategorySummary>();
        foreach (var a in assets)
        {
            string cat = a.Asset.Category?.Name ?? "Uncategorized";
            var s = byCategory.FirstOrDefault(x => x.Name == cat);
            if (s is null) byCategory.Add(s = new CategorySummary { Name = cat });
            s.Count++;
            s.Cost += a.Cost;
            s.Depreciation += a.PostedDepreciation;
        }
        var byStatus = new List<StatusSummary>();
        foreach (var a in everything)
        {
            string st = string.IsNullOrEmpty(a.Status) ? "unknown" : a.Status;
            var s = byStatus.FirstOrDefault(x => x.Status == st);
            if (s is null) byStatus.Add(s = new StatusSummary { Status = st });
            s.Count++;
            s.Cost += Py.F(a.PurchaseCost);
        }
        if (!string.IsNullOrEmpty(format))
            return await ExportRegisterAsync(format, assets, byCategory, ct);
        return View(new RegisterModel
        {
            Assets = assets, TotalCost = totalCost, TotalDep = totalDep, NetBookValue = totalCost - totalDep,
            ByCategory = byCategory, ByStatus = byStatus,
        });
    }

    [HttpGet("movement")]
    [Right(Section, Rights.View)]
    public async Task<IActionResult> MovementSchedule([FromQuery] string? from, [FromQuery] string? to, [FromQuery] string? format,
                                                      CancellationToken ct)
    {
        var today = clock.Today;
        var period = await periods.PeriodForAsync(today.ToDateTime(TimeOnly.MinValue), ct);
        var defaultFrom = period?.StartDate ?? new DateOnly(today.Year, 1, 1);
        DateOnly dateFrom, dateTo;
        if (IsoDate(string.IsNullOrEmpty(from) ? Py.Date(defaultFrom) : from) is DateOnly f
            && IsoDate(string.IsNullOrEmpty(to) ? Py.Date(today) : to) is DateOnly t)
            (dateFrom, dateTo) = (f, t);
        else
            (dateFrom, dateTo) = (defaultFrom, today);
        if (dateTo < dateFrom)
            (dateFrom, dateTo) = (dateTo, dateFrom);
        var data = await movement.BuildAsync(dateFrom, dateTo, ct);
        if (!string.IsNullOrEmpty(format))
        {
            string[] headers = ["Category", "Cost b/f", "Additions", "Disposals", "Cost c/f", "Dep. b/f", "Charge for period",
                                "On disposals", "Dep. c/f", "NBV b/f", "NBV c/f"];
            var rows = data.Rows.Select(r => new ReportRow([r.Category, .. r.Values.Cast<object?>()], "plain")).ToList();
            rows.Add(new ReportRow(["Total", .. data.Total.Values.Cast<object?>()], "grand"));
            var file = await export.ExportTableAsync(format, new ReportTable
            {
                Title = "Fixed Asset Movement Schedule",
                Period = $"{Py.DateLong(dateFrom)} to {Py.DateLong(dateTo)}",
                FilePeriod = Py.Date(dateTo),
                Columns = headers.Select((h, i) => new ReportColumn(h, i == 0 ? ReportColumnKind.Text : ReportColumnKind.Money)).ToList(),
                Rows = rows,
                Landscape = true,
            }, ct);
            return File(file.Content, file.ContentType, file.FileName);
        }
        return View("Movement", new MovementModel(data, dateFrom, dateTo));
    }

    /// <summary>_export_register: every asset in service; Excel adds the category summary.</summary>
    private async Task<IActionResult> ExportRegisterAsync(string fmt, List<AssetCalc> assets, List<CategorySummary> byCategory,
                                                          CancellationToken ct)
    {
        string[] headers = ["Code", "Asset", "Category", "Purchase date", "Location", "Assigned to", "Serial no.", "Method",
                            "Life (yrs)", "Cost", "Salvage", "Acc. depreciation", "Net book value", "Status"];
        var kinds = new[]
        {
            ReportColumnKind.Text, ReportColumnKind.Text, ReportColumnKind.Text, ReportColumnKind.Date, ReportColumnKind.Text,
            ReportColumnKind.Text, ReportColumnKind.Text, ReportColumnKind.Text, ReportColumnKind.Integer, ReportColumnKind.Money,
            ReportColumnKind.Money, ReportColumnKind.Money, ReportColumnKind.Money, ReportColumnKind.Text,
        };
        var rows = assets.Select(a => new ReportRow(
        [
            a.Asset.AssetCode, a.Asset.Name, a.Asset.Category?.Name ?? "", a.Asset.PurchaseDate, a.Asset.Location ?? "",
            a.Asset.AssignedTo ?? "", a.Asset.SerialNumber ?? "", ViewHelpers.Title((a.Asset.DepreciationMethod ?? "").Replace('_', ' ')),
            a.Asset.UsefulLife, a.Cost, a.Salvage, a.PostedDepreciation, a.NetBookValue, ViewHelpers.Title(a.Asset.Status),
        ], "plain")).ToList();
        rows.Add(new ReportRow(
        [
            "", $"Total ({assets.Count} assets)", "", "", "", "", "", "", "",
            Py.Sum(assets.Select(a => a.Cost)), Py.Sum(assets.Select(a => a.Salvage)),
            Py.Sum(assets.Select(a => a.PostedDepreciation)), Py.Sum(assets.Select(a => a.NetBookValue)), "",
        ], "grand"));
        var today = clock.Today;
        bool excel = fmt is "excel" or "xlsx";
        var file = await export.ExportTableAsync(fmt, new ReportTable
        {
            Title = "Fixed Asset Register",
            Period = $"Assets in service as at {Py.DateLong(today)}",
            FilePeriod = Py.Date(today),
            Columns = headers.Select((h, i) => new ReportColumn(h, kinds[i])).ToList(),
            Rows = rows,
            ExtraSheets = excel
                ?
                [
                    ("By category", ["Category", "Assets", "Cost", "Acc. depreciation", "Net book value"],
                     byCategory.Select(c => (IReadOnlyList<object?>)[c.Name, c.Count, c.Cost, c.Depreciation, c.Cost - c.Depreciation]).ToList()),
                ]
                : [],
        }, ct);
        return File(file.Content, file.ContentType, file.FileName);
    }

    /// <summary>date.fromisoformat for the forms this page sends.</summary>
    private static DateOnly? IsoDate(string? s) =>
        DateOnly.TryParseExact((s ?? "").Trim(), ["yyyy-MM-dd", "yyyyMMdd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;
}

public sealed class CategorySummary
{
    public string Name { get; init; } = "";
    public int Count { get; set; }
    public double Cost { get; set; }
    public double Depreciation { get; set; }
}

public sealed class StatusSummary
{
    public string Status { get; init; } = "";
    public int Count { get; set; }
    public double Cost { get; set; }
}

public sealed class RegisterModel
{
    public IReadOnlyList<AssetCalc> Assets { get; init; } = [];
    public double TotalCost { get; init; }
    public double TotalDep { get; init; }
    public double NetBookValue { get; init; }
    public IReadOnlyList<CategorySummary> ByCategory { get; init; } = [];
    public IReadOnlyList<StatusSummary> ByStatus { get; init; } = [];
}

public sealed record MovementModel(MovementData Data, DateOnly From, DateOnly To);
