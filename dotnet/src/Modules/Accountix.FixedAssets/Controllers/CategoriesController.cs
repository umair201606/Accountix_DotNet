namespace Accountix.FixedAssets.Controllers;

/// <summary>Asset categories (fixed_assets_app/routes/categories.py).</summary>
[Area("FixedAssets")]
[Module(Modules.FixedAssets)]
[Route("fixed-assets/categories")]
public sealed class CategoriesController(AccountixDbContext db, CompanyClock clock) : Controller
{
    private const string Section = "fixed_assets_categories";

    [HttpGet("")]
    [Right(Section, Rights.View)]
    public async Task<IActionResult> List(CancellationToken ct) =>
        View(await db.Set<FaCategory>().AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct));

    [HttpGet("create")]
    [Right(Section, Rights.Create)]
    public IActionResult Create() => View("Form", (FaCategory?)null);

    [HttpPost("create")]
    [Right(Section, Rights.Create)]
    public async Task<IActionResult> CreatePost(CancellationToken ct)
    {
        var form = Request.Form;
        string name = (form["name"].FirstOrDefault() ?? "").Trim();
        if (name.Length == 0)
            return Refuse("Category name is required.", null);
        if (await db.Set<FaCategory>().AnyAsync(c => c.Name == name, ct))
            return Refuse($"Category '{name}' already exists.", null);
        db.Add(new FaCategory
        {
            Name = name,
            Description = form["description"].FirstOrDefault() ?? "",
            DefaultUsefulLife = FormInput.Int(form, "default_useful_life", 5),
            DefaultDepreciationMethod = form.ContainsKey("default_depreciation_method")
                ? form["default_depreciation_method"].FirstOrDefault() ?? "" : "straight_line",
            DefaultSalvageValuePct = Services.Py.Dec(FormInput.Float(form, "default_salvage_value_pct", 0)),
            IsActive = true,
            CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        this.Flash($"Category '{name}' created.", "success");
        return Redirect("/fixed-assets/categories/");
    }

    [HttpGet("{category_id:int}/edit")]
    [Right(Section, Rights.Edit)]
    public async Task<IActionResult> Edit([FromRoute(Name = "category_id")] int categoryId, CancellationToken ct)
    {
        var category = await db.Set<FaCategory>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == categoryId, ct);
        return category is null ? NotFound() : View("Form", category);
    }

    [HttpPost("{category_id:int}/edit")]
    [Right(Section, Rights.Edit)]
    public async Task<IActionResult> EditPost([FromRoute(Name = "category_id")] int categoryId, CancellationToken ct)
    {
        var category = await db.Set<FaCategory>().FirstOrDefaultAsync(c => c.Id == categoryId, ct);
        if (category is null) return NotFound();
        var form = Request.Form;
        string name = (form["name"].FirstOrDefault() ?? "").Trim();
        if (name.Length == 0)
            return Refuse("Category name is required.", category);
        if (await db.Set<FaCategory>().AnyAsync(c => c.Name == name && c.Id != category.Id, ct))
            return Refuse($"Category '{name}' already exists.", category);
        category.Name = name;
        category.Description = form["description"].FirstOrDefault() ?? "";
        category.DefaultUsefulLife = FormInput.Int(form, "default_useful_life", 5);
        category.DefaultDepreciationMethod = form.ContainsKey("default_depreciation_method")
            ? form["default_depreciation_method"].FirstOrDefault() ?? "" : "straight_line";
        category.DefaultSalvageValuePct = Services.Py.Dec(FormInput.Float(form, "default_salvage_value_pct", 0));
        category.IsActive = form["is_active"].FirstOrDefault() == "1";
        await db.SaveChangesAsync(ct);
        this.Flash("Category updated.", "success");
        return Redirect("/fixed-assets/categories/");
    }

    private ViewResult Refuse(string message, FaCategory? category)
    {
        this.Flash(message, "error");
        return View("Form", category);
    }
}
