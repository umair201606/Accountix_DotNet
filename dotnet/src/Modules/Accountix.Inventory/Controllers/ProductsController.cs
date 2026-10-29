using System.Globalization;
using Accountix.Inventory.Services;

namespace Accountix.Inventory.Controllers;

public sealed record ProductListModel(
    IReadOnlyList<InvProduct> Products, IReadOnlyDictionary<int, string> CategoryNames,
    IReadOnlyList<InvCategory> Categories, string Q, string? CategoryId);

public sealed record UnitOption(string Name, string Abbreviation);

/// <summary>The product form (products/form_inv.html): the product, or what was typed when it was refused.</summary>
public sealed class ProductFormModel
{
    public InvProduct? Product { get; init; }
    public Row? Draft { get; init; }
    public IReadOnlyList<InvCategory> Categories { get; init; } = [];
    public IReadOnlyList<UnitOption> Units { get; init; } = [];
    public bool CostLocked { get; init; }

    /// <summary>{% set f = draft or product %}: true when there is something to show.</summary>
    public bool HasF => Draft is not null || Product is not null;

    /// <summary>f.&lt;field&gt; with Python's type (draft text, or the product's column as Python reads it).</summary>
    public object? F(string field)
    {
        if (Draft is not null)
            return Draft.TryGetValue(field, out var v) ? v : null;
        var p = Product;
        if (p is null) return null;
        return field switch
        {
            "sku" => p.Sku, "name" => p.Name, "description" => p.Description, "hs_code" => p.HsCode,
            "unit" => p.Unit, "category_id" => p.CategoryId,
            "unit_price" => p.UnitPrice is decimal u ? (double)u : null,
            "cost_price" => p.CostPrice is decimal c ? (double)c : null,
            "weight" => p.Weight is decimal w ? (double)w : null,
            "reorder_level" => p.ReorderLevel,
            "is_active" => p.IsActive,
            _ => null,
        };
    }

    /// <summary>{{ f.x or '' if f else '' }}</summary>
    public string Or(string field) => HasF && MasterData.Truthy(F(field)) ? MasterData.Str(F(field)) : "";

    /// <summary>{{ f.x if f and f.x not in (none, '') else fallback }}</summary>
    public string NotBlank(string field, string fallback)
    {
        var v = HasF ? F(field) : null;
        return v is null || v is string { Length: 0 } ? fallback : MasterData.Str(v);
    }

    /// <summary>|string of a value as Jinja prints it (None → "None").</summary>
    public string AsString(string field) => MasterData.Str(F(field));
}

public sealed class ProductBatchModel
{
    public IReadOnlyList<Row> Rows { get; init; } = [];
    public IReadOnlyList<UnitOption> Units { get; init; } = [];
    public int Flagged { get; init; }
    public int Created { get; init; }
    public string? DateError { get; init; }
    public string OpeningDate { get; init; } = "";
}

/// <summary>Products (inventory_app/routes/products.py).</summary>
[Area("Inventory")]
[Module(Modules.Inventory)]
[Route("inventory/products")]
public sealed class ProductsController(AccountixDbContext db, ILedger ledger, IPeriods periods, StockEntry stock,
                                       CompanyClock clock) : Controller
{
    private const string ListUrl = "/inventory/products/";

    private static readonly string[] FormFields =
        ["sku", "name", "description", "category_id", "unit_price", "cost_price", "reorder_level", "current_stock", "unit",
         "hs_code", "weight", "opening_date"];

    private int UserId => HttpContext.Access()!.User.Id;

    [HttpGet("")]
    [Right("products", Rights.View)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        string q = Request.Query["q"].FirstOrDefault() ?? "";
        string? catArg = Request.Query["category_id"].FirstOrDefault();
        var query = db.Set<InvProduct>().AsNoTracking();
        if (q.Length > 0)
            query = query.Where(p => EF.Functions.ILike(p.Name, "%" + q + "%") || EF.Functions.ILike(p.Sku, "%" + q + "%"));
        if (int.TryParse(catArg, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int catId) && catId != 0)
            query = query.Where(p => p.CategoryId == catId);
        var products = await query.OrderBy(p => p.Name).ToListAsync(ct);
        var allCats = await db.Set<InvCategory>().AsNoTracking().OrderBy(c => c.Id).ToListAsync(ct);
        return View(new ProductListModel(products, allCats.ToDictionary(c => c.Id, c => c.Name),
                                         allCats.Where(c => c.IsActive == true).ToList(), q, catArg));
    }

    /// <summary>_units_for: the active units, plus the product's own unit if it was switched off since.</summary>
    private async Task<List<UnitOption>> UnitsForAsync(InvProduct? product, CancellationToken ct)
    {
        var units = await db.Set<InvUnit>().AsNoTracking().Where(u => u.IsActive == true).OrderBy(u => u.Name)
            .Select(u => new UnitOption(u.Name, u.Abbreviation)).ToListAsync(ct);
        if (product is { Unit: { Length: > 0 } own } && units.All(u => u.Abbreviation != own))
        {
            var unit = await db.Set<InvUnit>().AsNoTracking().Where(u => u.Abbreviation == own)
                .Select(u => new UnitOption(u.Name, u.Abbreviation)).FirstOrDefaultAsync(ct);
            units.Add(unit ?? new UnitOption(own, own));
        }
        return units;
    }

    private async Task<IActionResult> FormPageAsync(InvProduct? product, Row? draft, bool costLocked, CancellationToken ct)
    {
        var categories = await db.Set<InvCategory>().AsNoTracking().Where(c => c.IsActive == true).OrderBy(c => c.Id).ToListAsync(ct);
        return View("Form", new ProductFormModel
        {
            Product = product, Draft = draft, Categories = categories, Units = await UnitsForAsync(product, ct),
            CostLocked = costLocked,
        });
    }

    private Row RawForm()
    {
        var raw = new Row();
        foreach (string f in FormFields)
            raw[f] = Request.Form[f].FirstOrDefault() ?? "";
        return raw;
    }

    [HttpGet("create")]
    [Right("products", Rights.Create)]
    public Task<IActionResult> Create(CancellationToken ct) => FormPageAsync(null, null, false, ct);

    [HttpPost("create")]
    [Right("products", Rights.Create)]
    public async Task<IActionResult> CreatePost(CancellationToken ct)
    {
        var raw = RawForm();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (values, errors) = await MasterData.CleanProductAsync(db, raw, await MasterData.UnitChoicesAsync(db, null, ct), ct: ct);
        DateTime? when = null;
        if (errors.Count == 0 && values["current_stock"] is decimal q0 && q0 > 0)
        {
            try
            {
                when = DocumentDates.Parse((string?)raw["opening_date"], null, clock);
                if (await MasterData.OpeningDateRefusalAsync(periods, when.Value, ct) is string refusal)
                    errors.Add(refusal);
            }
            catch (DocumentDateException e)
            {
                errors.Add(e.Message);
            }
        }
        if (errors.Count > 0)
        {
            // Shown again with what was typed. A duplicate SKU used to reach the database's unique key.
            await tx.RollbackAsync(ct);
            this.Flash(string.Join(" ", errors), "error");
            return await FormPageAsync(null, raw, false, ct);
        }
        decimal openingQty = (decimal)values["current_stock"]!;
        var prod = MasterData.NewProduct(values, clock.UtcNow);
        db.Add(prod);
        await db.SaveChangesAsync(ct);
        await ledger.EntityAccountAsync("product", prod.Id, $"{prod.Name} ({prod.Sku})", ct);
        // Opening stock is a real receipt: a cost layer at the opening cost and Dr Inventory /
        // Cr Opening Balance Equity on the opening date.
        if (openingQty > 0)
            await stock.PostOpeningStockAsync(prod, openingQty, prod.CostPrice ?? 0, when, UserId, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash($"Product created — ledger account '{prod.Name} ({prod.Sku})' added under Inventory", "success");
        return Redirect(ListUrl);
    }

    private Task<bool> HasStockHistoryAsync(int productId, CancellationToken ct) =>
        db.Set<StockLedger>().AnyAsync(r => r.ProductId == productId, ct);

    [HttpGet("edit/{id:int}")]
    [Right("products", Rights.Edit)]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var prod = await db.Set<InvProduct>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (prod is null) return NotFound();
        return await FormPageAsync(prod, null, await HasStockHistoryAsync(id, ct), ct);
    }

    [HttpPost("edit/{id:int}")]
    [Right("products", Rights.Edit)]
    public async Task<IActionResult> EditPost(int id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var prod = await db.LockAsync<InvProduct>(id, ct);
        if (prod is null) return NotFound();
        // Once stock has moved, the costing engine keeps the cost at the current valuation;
        // the form's copy (read when the page opened) would overwrite it with a stale one.
        bool costLocked = await HasStockHistoryAsync(prod.Id, ct);
        var raw = RawForm();
        raw["is_active"] = Request.Form["is_active"].FirstOrDefault() == "on";
        if (costLocked)
            raw["cost_price"] = prod.CostPrice is decimal cp && cp != 0 ? (double)cp : 0;   // prod.cost_price or 0
        var (values, errors) = await MasterData.CleanProductAsync(db, raw, await MasterData.UnitChoicesAsync(db, prod.Unit, ct),
                                                                  excludeId: prod.Id, opening: false, ct: ct);
        if (errors.Count > 0)
        {
            await tx.RollbackAsync(ct);
            this.Flash(string.Join(" ", errors), "error");
            return await FormPageAsync(prod, raw, costLocked, ct);
        }
        MasterData.Apply(prod, values, includeCost: !costLocked);
        prod.IsActive = (bool)raw["is_active"]!;
        await ledger.EntityAccountAsync("product", prod.Id, $"{prod.Name} ({prod.Sku})", ct);
        db.ChangeTracker.DetectChanges();
        if (db.Entry(prod).State == EntityState.Modified)
            prod.UpdatedAt = clock.UtcNow;          // onupdate=datetime.utcnow
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        this.Flash("Product updated", "success");
        return Redirect(ListUrl);
    }

    [HttpPost("delete/{id:int}")]
    [Right("products", Rights.Delete)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Locked: a sale of this product in flight finishes first, and its stock history then refuses the delete.
        var prod = await db.LockAsync<InvProduct>(id, ct);
        if (prod is null) return NotFound();
        if (await db.Set<InvPurchaseOrderItem>().AnyAsync(i => i.ProductId == id, ct)
            || await db.Set<InvSalesOrderItem>().AnyAsync(i => i.ProductId == id, ct))
            this.Flash("Cannot delete product with order history", "error");
        else if (await HasStockHistoryAsync(id, ct))
            this.Flash("Cannot delete a product with stock history — mark it inactive instead.", "error");
        else
        {
            db.Remove(prod);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            this.Flash("Product deleted", "success");
        }
        return Redirect(ListUrl);
    }

    [HttpGet("adjust-stock/{id:int}")]
    [Right("adjustment_vouchers", Rights.Approve)]
    public async Task<IActionResult> AdjustStock(int id, CancellationToken ct)
    {
        var prod = await db.Set<InvProduct>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        return prod is null ? NotFound() : View("AdjustStock", prod);
    }

    [HttpPost("adjust-stock/{id:int}")]
    [Right("adjustment_vouchers", Rights.Approve)]
    public async Task<IActionResult> AdjustStockPost(int id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var prod = await db.Set<InvProduct>().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (prod is null) return NotFound();
        string note = Request.Form["notes"].FirstOrDefault() ?? "";
        double? qty;
        try
        {
            qty = (double)MasterData.Number(Request.Form["quantity"].FirstOrDefault(), "Quantity", minimum: null);
        }
        catch (InvalidValueException e)
        {
            qty = null;
            this.Flash(e.Message, "error");
        }
        if (qty == 0)
            this.Flash("Quantity must be non-zero", "error");
        else if (qty is double q)
        {
            // An approved Stock Adjustment voucher (reviewable, reversible), not a bare change of the column.
            string reason = note.Length > 0 ? note : $"Manual adjustment of {Py.G(q)}";
            var when = DocumentDates.Parse(Request.Form["date"].FirstOrDefault(), null, clock);
            var v = await stock.QuickAdjustmentAsync(prod, MasterData.ToDecimal(q), reason, when, UserId, ct);
            db.Add(new InvStockMovement
            {
                ProductId = prod.Id, Type = q > 0 ? "adjustment_in" : "adjustment_out",
                Quantity = Math.Round(MasterData.ToDecimal(Math.Abs(q)), 4), ReferenceType = "stock_adjustment",
                ReferenceId = v!.Id, Notes = reason, CreatedBy = UserId, CreatedAt = clock.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            decimal? current = await db.Set<InvProduct>().AsNoTracking().Where(p => p.Id == id)
                .Select(p => p.CurrentStock).FirstOrDefaultAsync(ct);
            this.Flash($"Stock adjusted by {Py.G(q)} ({v.VoucherNumber}). New stock: {MasterData.Qty(current)}", "success");
            return Redirect(ListUrl);
        }
        await tx.RollbackAsync(ct);
        return View("AdjustStock", await db.Set<InvProduct>().AsNoTracking().FirstAsync(p => p.Id == id, ct));
    }

    [HttpGet("check")]
    [AnyMember]
    public async Task<IActionResult> CheckDuplicate(CancellationToken ct)
    {
        string? field = Request.Query["field"].FirstOrDefault();
        string value = (Request.Query["value"].FirstOrDefault() ?? "").Trim();
        int? exclude = int.TryParse(Request.Query["exclude"].FirstOrDefault(), out int x) ? x : null;
        if (field is not ("name" or "sku") || value.Length == 0)
            return Json(new { exists = false });
        // Same rule the save applies: case and spacing do not make a new one.
        bool exists = await MasterData.TakenAsync(db.Set<InvProduct>(), field == "sku" ? nameof(InvProduct.Sku) : nameof(InvProduct.Name),
                                                  value, exclude, ct);
        return Json(new { exists });
    }

    [HttpGet("template")]
    [Right("products", Rights.Create)]
    public IActionResult DownloadTemplate()
    {
        byte[] bytes = ExcelSheets.Template("Products",
            ["SKU", "Name", "Description", "Category", "Unit Price", "Cost Price", "Reorder Level", "Opening Stock", "Unit", "HS Code"],
            [
                ["SKU-001", "Sample Product A", "Description for product A", "Category Name", "100", "70", "10", "50", "pcs", "8541.4000"],
                ["SKU-002", "Sample Product B", "Description for product B", "Category Name", "250", "180", "5", "20.5", "kg", ""],
                ["SKU-003", "Sample Product C", "Description for product C", "Category Name", "50", "30", "20", "100", "m", ""],
            ],
            [14, 22, 28, 16, 12, 12, 14, 14, 10, 12]);
        return File(bytes, ExcelSheets.ContentType, "product_import_template.xlsx");
    }

    /// <summary>_batch_cleaner: one validator for a whole sheet or batch (units and categories read once).</summary>
    private async Task<Func<Row, Task<(Row, List<string>)>>> BatchCleanerAsync(CancellationToken ct)
    {
        var choices = await MasterData.UnitChoicesAsync(db, null, ct);
        var categories = await MasterData.CategoryIndexAsync(db, ct);
        return raw => MasterData.CleanProductAsync(db, raw, choices, categories, upperSku: true, ct: ct);
    }

    private async Task<IActionResult> ReviewAsync(List<Row> rows, int created = 0, string openingDate = "", string? dateError = null,
                                                  CancellationToken ct = default)
    {
        var units = await db.Set<InvUnit>().AsNoTracking().Where(u => u.IsActive == true).OrderBy(u => u.Name)
            .Select(u => new UnitOption(u.Name, u.Abbreviation)).ToListAsync(ct);
        return View("BatchEditor", new ProductBatchModel
        {
            Rows = rows, Units = units, Flagged = rows.Count(r => r.Errors.Count > 0), Created = created,
            DateError = dateError,
            OpeningDate = openingDate.Length > 0 ? openingDate : clock.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        });
    }

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
            rows = await MasterData.ImportRowsAsync(MasterData.ReadSheet(s), MasterData.ProductColumns, await BatchCleanerAsync(ct),
                                                    ["sku", "name"]);
        }
        catch (InvalidValueException e)
        {
            this.Flash(e.Message, "error");
            return Redirect(ListUrl);
        }
        if (rows.Count == 0)
        {
            this.Flash("The sheet has no products under its heading row.", "error");
            return Redirect(ListUrl);
        }
        // The review screen is this response — the rows never ride in the session cookie.
        return await ReviewAsync(rows, ct: ct);
    }

    [HttpGet("batch")]
    [Right("products", Rights.Create)]
    public Task<IActionResult> BatchEditor(CancellationToken ct) => ReviewAsync([], ct: ct);

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
        string openingDate = Request.Form["opening_date"].FirstOrDefault() ?? "";
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Opening stock is dated once for the whole import (the go-live date); a closed period
        // refuses it here, with the rows kept, rather than half-way through the save.
        DateTime? when = null;
        if (rows.Any(HasStock))
        {
            try
            {
                when = DocumentDates.Parse(openingDate, null, clock);
            }
            catch (DocumentDateException e)
            {
                return await ReviewAsync(rows, openingDate: openingDate, dateError: e.Message, ct: ct);
            }
            if (await MasterData.OpeningDateRefusalAsync(periods, when.Value, ct) is string refusal)
            {
                await tx.RollbackAsync(ct);
                return await ReviewAsync(rows, openingDate: openingDate, dateError: refusal, ct: ct);
            }
        }

        async Task Create(Row values)
        {
            decimal qty = (decimal)values["current_stock"]!;
            var prod = MasterData.NewProduct(values, clock.UtcNow);
            db.Add(prod);
            await db.SaveChangesAsync(ct);
            await ledger.EntityAccountAsync("product", prod.Id, $"{prod.Name} ({prod.Sku})", ct);
            if (qty > 0)
                await stock.PostOpeningStockAsync(prod, qty, prod.CostPrice ?? 0, when, UserId, ct);
            await db.SaveChangesAsync(ct);
        }

        var (created, failed) = await MasterData.SaveRowsAsync(rows, await BatchCleanerAsync(ct), Create, ["sku", "name"]);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        if (failed.Count > 0)
            return await ReviewAsync(failed, created, openingDate, ct: ct);
        this.Flash($"{created} product(s) created.", "success");
        return Redirect(ListUrl);
    }

    private static bool HasStock(Row raw)
    {
        try
        {
            return MasterData.Number(raw.Get("current_stock"), "Opening stock") > 0;
        }
        catch (InvalidValueException)
        {
            return false;
        }
    }
}
