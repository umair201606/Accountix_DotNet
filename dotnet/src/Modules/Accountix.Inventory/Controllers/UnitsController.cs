using Accountix.Inventory.Services;

namespace Accountix.Inventory.Controllers;

/// <summary>units/form_inv.html: a unit, or the values typed when it was refused ("name is defined").</summary>
public sealed class UnitFormModel
{
    public InvUnit? Unit { get; init; }
    public string? Name { get; init; }
    public string? Abbreviation { get; init; }
    public string? Explanation { get; init; }
}

public sealed class UnitBatchModel
{
    public IReadOnlyList<Row> Rows { get; init; } = [];
    public int Flagged { get; init; }
    public int Created { get; init; }
}

/// <summary>Units of measure (inventory_app/routes/units.py). Part of the product master: rights of "products".</summary>
[Area("Inventory")]
[Module(Modules.Inventory)]
[Route("inventory/units")]
public sealed class UnitsController(AccountixDbContext db, CompanyClock clock) : Controller
{
    private const string ListUrl = "/inventory/units/";

    [HttpGet("check")]
    [AnyMember]
    public async Task<IActionResult> CheckDuplicate(CancellationToken ct)
    {
        string? field = Request.Query["field"].FirstOrDefault();
        string value = (Request.Query["value"].FirstOrDefault() ?? "").Trim();
        int? exclude = int.TryParse(Request.Query["exclude"].FirstOrDefault(), out int x) ? x : null;
        if (field is not ("name" or "abbreviation") || value.Length == 0)
            return Json(new { exists = false });
        // Same rule the save applies: case and spacing do not make a new unit.
        bool exists = await MasterData.TakenAsync(db.Set<InvUnit>(),
            field == "name" ? nameof(InvUnit.Name) : nameof(InvUnit.Abbreviation), value, exclude, ct);
        return Json(new { exists });
    }

    [HttpGet("template")]
    [Right("products", Rights.Create)]
    public IActionResult DownloadTemplate()
    {
        byte[] bytes = ExcelSheets.Template("Units", ["Name", "Abbreviation", "Explanation"],
        [
            ["Kilogram", "kg", "Weight in kilograms"],
            ["Meter", "m", "Length in meters"],
            ["Piece", "pcs", "Individual unit count"],
        ], [24, 16, 30]);
        return File(bytes, ExcelSheets.ContentType, "unit_import_template.xlsx");
    }

    [HttpGet("")]
    [Right("products", Rights.View)]
    public async Task<IActionResult> List(CancellationToken ct) =>
        View(await db.Set<InvUnit>().AsNoTracking().OrderBy(u => u.Name).ToListAsync(ct));

    private Row FormValues()
    {
        var raw = new Row();
        foreach (string f in MasterData.UnitColumns)
            raw[f] = Request.Form[f].FirstOrDefault() ?? "";
        return raw;
    }

    private static UnitFormModel Typed(InvUnit? unit, Row values) => new()
    {
        Unit = unit, Name = (string?)values["name"], Abbreviation = (string?)values["abbreviation"],
        Explanation = (string?)values["explanation"],
    };

    [HttpGet("create")]
    [Right("products", Rights.Create)]
    public IActionResult Create() => View("Form", new UnitFormModel { Name = "", Abbreviation = "", Explanation = "" });

    [HttpPost("create")]
    [Right("products", Rights.Create)]
    public async Task<IActionResult> CreatePost(CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (values, errors) = await MasterData.CleanUnitAsync(db, FormValues(), ct: ct);
        if (errors.Count > 0)
        {
            this.Flash(string.Join(" ", errors), "error");
            return View("Form", Typed(null, values));
        }
        var unit = NewUnit(values);
        db.Add(unit);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"Unit '{unit.Name}' created.", "success");
        return Redirect(ListUrl);
    }

    private InvUnit NewUnit(Row values) => new()
    {
        Name = (string)values["name"]!, Abbreviation = (string)values["abbreviation"]!,
        Explanation = values["explanation"] is string { Length: > 0 } e ? e : null,
        IsActive = true, CreatedAt = clock.UtcNow,
    };

    [HttpGet("edit/{id:int}")]
    [Right("products", Rights.Edit)]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var unit = await db.Set<InvUnit>().AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
        return unit is null ? NotFound() : View("Form", new UnitFormModel { Unit = unit });
    }

    [HttpPost("edit/{id:int}")]
    [Right("products", Rights.Edit)]
    public async Task<IActionResult> EditPost(int id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var unit = await db.LockAsync<InvUnit>(id, ct);
        if (unit is null) return NotFound();
        var (values, errors) = await MasterData.CleanUnitAsync(db, FormValues(), unit.Id, ct);
        if (errors.Count > 0)
        {
            this.Flash(string.Join(" ", errors), "error");
            return View("Form", Typed(unit, values));
        }
        string old = unit.Abbreviation;
        unit.Name = (string)values["name"]!;
        unit.Abbreviation = (string)values["abbreviation"]!;
        unit.Explanation = values["explanation"] is string { Length: > 0 } e ? e : null;
        unit.IsActive = Request.Form["is_active"].FirstOrDefault() == "on";
        int moved = 0;
        if (old != unit.Abbreviation)
        {
            // Products record their unit by abbreviation: renaming "Kg" to "kg" used to leave every
            // product on the old spelling. Row by row, so each product's change is in the audit log.
            foreach (var product in await db.Set<InvProduct>().Where(p => p.Unit == old).ToListAsync(ct))
            {
                product.Unit = unit.Abbreviation;
                product.UpdatedAt = clock.UtcNow;
                moved++;
            }
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"Unit '{unit.Name}' updated." + (moved > 0 ? $" {moved} product(s) moved to '{unit.Abbreviation}'." : ""), "success");
        return Redirect(ListUrl);
    }

    [HttpPost("delete/{id:int}")]
    [Right("products", Rights.Delete)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var unit = await db.LockAsync<InvUnit>(id, ct);
        if (unit is null) return NotFound();
        string name = unit.Name;
        int inUse = await db.Set<InvProduct>().CountAsync(p => p.Unit == unit.Abbreviation, ct);
        if (inUse > 0)
        {
            this.Flash($"{inUse} product(s) use '{unit.Abbreviation}' — move them to another " +
                       "unit first, or switch this unit off instead of deleting it.", "error");
            return Redirect(ListUrl);
        }
        db.Remove(unit);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"Unit '{name}' deleted.", "success");
        return Redirect(ListUrl);
    }

    [HttpPost("quick-create")]
    [Right("products", Rights.Create)]
    public async Task<IActionResult> QuickCreate(CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (values, errors) = await MasterData.CleanUnitAsync(db, FormValues(), ct: ct);
        if (errors.Count > 0)
            return Json(new { ok = false, error = string.Join(" ", errors) });
        var unit = NewUnit(values);
        db.Add(unit);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Json(new { abbreviation = unit.Abbreviation, id = unit.Id, name = unit.Name, ok = true });
    }

    private IActionResult Review(List<Row> rows, int created = 0) =>
        View("BatchEditor", new UnitBatchModel { Rows = rows, Flagged = rows.Count(r => r.Errors.Count > 0), Created = created });

    private Func<Row, Task<(Row, List<string>)>> Cleaner(CancellationToken ct) => raw => MasterData.CleanUnitAsync(db, raw, null, ct);

    [HttpPost("upload-excel")]
    [Right("products", Rights.Create)]
    public async Task<IActionResult> UploadExcel(CancellationToken ct)
    {
        var file = Request.Form.Files["file"];
        if (file is null || string.IsNullOrEmpty(file.FileName))
        {
            this.Flash("No file selected.", "error");
            return Redirect(ListUrl);
        }
        List<Row> rows;
        try
        {
            await using var s = file.OpenReadStream();
            rows = await MasterData.ImportRowsAsync(MasterData.ReadSheet(s), MasterData.UnitColumns, Cleaner(ct), ["name", "abbreviation"]);
        }
        catch (InvalidValueException e)
        {
            this.Flash(e.Message, "error");
            return Redirect(ListUrl);
        }
        if (rows.Count == 0)
        {
            this.Flash("The sheet has no units under its heading row.", "error");
            return Redirect(ListUrl);
        }
        return Review(rows);
    }

    [HttpGet("batch")]
    [Right("products", Rights.Create)]
    public IActionResult BatchEditor() => Review([]);

    [HttpPost("batch")]
    [Right("products", Rights.Create)]
    public async Task<IActionResult> BatchEditorPost(CancellationToken ct)
    {
        List<Row> rows;
        try
        {
            rows = MasterData.SubmittedRows(Request.Form["data"].FirstOrDefault());
        }
        catch (InvalidValueException e)
        {
            this.Flash(e.Message, "error");
            return Redirect(ListUrl);
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        async Task Create(Row values)
        {
            db.Add(NewUnit(values));
            await db.SaveChangesAsync(ct);
        }

        var (created, failed) = await MasterData.SaveRowsAsync(rows, Cleaner(ct), Create, ["name", "abbreviation"]);
        await tx.CommitAsync(ct);
        if (failed.Count > 0)
            return Review(failed, created);
        this.Flash($"{created} unit(s) created.", "success");
        return Redirect(ListUrl);
    }
}
