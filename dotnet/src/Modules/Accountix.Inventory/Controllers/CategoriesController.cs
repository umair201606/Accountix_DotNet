using System.Globalization;

namespace Accountix.Inventory.Controllers;

public sealed record CategoryRow(InvCategory Category, string? ParentName, int Products);

public sealed record CategoryFormModel(InvCategory? Category, IReadOnlyList<InvCategory> Parents);

/// <summary>Product categories (inventory_app/routes/categories.py).</summary>
[Area("Inventory")]
[Module(Modules.Inventory)]
[Route("inventory/categories")]
public sealed class CategoriesController(AccountixDbContext db, CompanyClock clock) : Controller
{
    private const string ListUrl = "/inventory/categories/";

    [HttpGet("")]
    [Right("categories", Rights.View)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var cats = await db.Set<InvCategory>().AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
        var names = cats.ToDictionary(c => c.Id, c => c.Name);
        // One grouped count, not one COUNT per row.
        var counts = await db.Set<InvProduct>().AsNoTracking().Where(p => p.CategoryId != null)
            .GroupBy(p => p.CategoryId!.Value).Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N, ct);
        return View(cats.Select(c => new CategoryRow(c, c.ParentId is int p ? names.GetValueOrDefault(p) : null,
                                                     counts.GetValueOrDefault(c.Id))).ToList());
    }

    /// <summary>request.form.get("parent_id", type=int) or None</summary>
    private int? ParentId() =>
        int.TryParse(Request.Form["parent_id"].FirstOrDefault(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int p) && p != 0
            ? p : null;

    [HttpGet("create")]
    [Right("categories", Rights.Create)]
    public async Task<IActionResult> Create(CancellationToken ct) =>
        View("Form", new CategoryFormModel(null,
            await db.Set<InvCategory>().AsNoTracking().Where(c => c.IsActive == true).OrderBy(c => c.Id).ToListAsync(ct)));

    [HttpPost("create")]
    [Right("categories", Rights.Create)]
    public async Task<IActionResult> CreatePost(CancellationToken ct)
    {
        if (!Request.Form.TryGetValue("name", out var name))
            return BadRequest();          // request.form["name"]: Flask answers 400
        db.Add(new InvCategory
        {
            Name = name.ToString(), Description = Request.Form["description"].FirstOrDefault() ?? "", ParentId = ParentId(),
            IsActive = true, CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        this.Flash("Category created", "success");
        return Redirect(ListUrl);
    }

    [HttpGet("edit/{id:int}")]
    [Right("categories", Rights.Edit)]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var cat = await db.Set<InvCategory>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (cat is null) return NotFound();
        var parents = await db.Set<InvCategory>().AsNoTracking().Where(c => c.Id != id && c.IsActive == true)
            .OrderBy(c => c.Id).ToListAsync(ct);
        return View("Form", new CategoryFormModel(cat, parents));
    }

    [HttpPost("edit/{id:int}")]
    [Right("categories", Rights.Edit)]
    public async Task<IActionResult> EditPost(int id, CancellationToken ct)
    {
        var cat = await db.Set<InvCategory>().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (cat is null) return NotFound();
        if (!Request.Form.TryGetValue("name", out var name))
            return BadRequest();
        cat.Name = name.ToString();
        cat.Description = Request.Form["description"].FirstOrDefault() ?? "";
        cat.ParentId = ParentId();
        cat.IsActive = Request.Form["is_active"].FirstOrDefault() == "on";
        await db.SaveChangesAsync(ct);
        this.Flash("Category updated", "success");
        return Redirect(ListUrl);
    }

    [HttpPost("delete/{id:int}")]
    [Right("categories", Rights.Delete)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var cat = await db.Set<InvCategory>().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (cat is null) return NotFound();
        if (await db.Set<InvProduct>().AnyAsync(p => p.CategoryId == id, ct))
            this.Flash("Cannot delete category with products", "error");
        else
        {
            // SQLAlchemy nulls the children's parent_id before deleting (the "children" backref).
            foreach (var child in await db.Set<InvCategory>().Where(c => c.ParentId == id).ToListAsync(ct))
                child.ParentId = null;
            db.Remove(cat);
            await db.SaveChangesAsync(ct);
            this.Flash("Category deleted", "success");
        }
        return Redirect(ListUrl);
    }
}
