using Accountix.Parties.Services;

namespace Accountix.Parties.Controllers;

/// <summary>Customers (invoicing_app/routes/customers.py, blueprint inv_customers).</summary>
[Area("Parties")]
[Module(Modules.Invoicing)]
[Route("inventory/customers")]
public sealed class CustomersController(PartyPages pages) : Controller
{
    private static PartyKind Kind => PartyKind.Customer;

    [HttpGet("")]
    [Right("customers", Rights.View)]
    public Task<IActionResult> List([FromQuery] string? q, CancellationToken ct) => pages.ListAsync(this, Kind, q, ct);

    [HttpGet("create")]
    [Right("customers", Rights.Create)]
    public IActionResult Create() => pages.NewForm(this, Kind);

    [HttpPost("create")]
    [Right("customers", Rights.Create)]
    public Task<IActionResult> CreatePost(CancellationToken ct) => pages.CreateAsync(this, Kind, ct);

    [HttpGet("edit/{id:int}")]
    [Right("customers", Rights.Edit)]
    public Task<IActionResult> Edit(int id, CancellationToken ct) => pages.EditFormAsync(this, Kind, id, ct);

    [HttpPost("edit/{id:int}")]
    [Right("customers", Rights.Edit)]
    public Task<IActionResult> EditPost(int id, CancellationToken ct) => pages.EditAsync(this, Kind, id, ct);

    [HttpPost("delete/{id:int}")]
    [Right("customers", Rights.Delete)]
    public Task<IActionResult> Delete(int id, CancellationToken ct) => pages.DeleteAsync(this, Kind, id, ct);

    [HttpGet("check")]
    [AnyMember]   // OPEN in the rights map: "is this name taken?" while typing
    public Task<IActionResult> Check([FromQuery] string? value, [FromQuery] string? exclude, CancellationToken ct) =>
        pages.CheckAsync(this, Kind, value, int.TryParse(exclude, out int ex) ? ex : null, ct);

    [HttpGet("template")]
    [Right("customers", Rights.Create)]
    public IActionResult Template() => pages.Template(this, Kind);

    [HttpPost("upload-excel")]
    [Right("customers", Rights.Create)]
    public Task<IActionResult> Upload(CancellationToken ct) => pages.UploadAsync(this, Kind, ct);

    [HttpGet("batch")]
    [Right("customers", Rights.Create)]
    public IActionResult Batch() => pages.BatchForm(this, Kind);

    [HttpPost("batch")]
    [Right("customers", Rights.Create)]
    public Task<IActionResult> BatchPost(CancellationToken ct) => pages.BatchSaveAsync(this, Kind, ct);
}

/// <summary>Suppliers (invoicing_app/routes/suppliers.py, blueprint inv_suppliers).</summary>
[Area("Parties")]
[Module(Modules.Invoicing)]
[Route("inventory/suppliers")]
public sealed class SuppliersController(PartyPages pages) : Controller
{
    private static PartyKind Kind => PartyKind.Supplier;

    [HttpGet("")]
    [Right("suppliers", Rights.View)]
    public Task<IActionResult> List([FromQuery] string? q, CancellationToken ct) => pages.ListAsync(this, Kind, q, ct);

    [HttpGet("create")]
    [Right("suppliers", Rights.Create)]
    public IActionResult Create() => pages.NewForm(this, Kind);

    [HttpPost("create")]
    [Right("suppliers", Rights.Create)]
    public Task<IActionResult> CreatePost(CancellationToken ct) => pages.CreateAsync(this, Kind, ct);

    [HttpGet("edit/{id:int}")]
    [Right("suppliers", Rights.Edit)]
    public Task<IActionResult> Edit(int id, CancellationToken ct) => pages.EditFormAsync(this, Kind, id, ct);

    [HttpPost("edit/{id:int}")]
    [Right("suppliers", Rights.Edit)]
    public Task<IActionResult> EditPost(int id, CancellationToken ct) => pages.EditAsync(this, Kind, id, ct);

    [HttpPost("delete/{id:int}")]
    [Right("suppliers", Rights.Delete)]
    public Task<IActionResult> Delete(int id, CancellationToken ct) => pages.DeleteAsync(this, Kind, id, ct);

    [HttpGet("check")]
    [AnyMember]   // OPEN in the rights map
    public Task<IActionResult> Check([FromQuery] string? value, [FromQuery] string? exclude, CancellationToken ct) =>
        pages.CheckAsync(this, Kind, value, int.TryParse(exclude, out int ex) ? ex : null, ct);

    [HttpGet("template")]
    [Right("suppliers", Rights.Create)]
    public IActionResult Template() => pages.Template(this, Kind);

    [HttpPost("upload-excel")]
    [Right("suppliers", Rights.Create)]
    public Task<IActionResult> Upload(CancellationToken ct) => pages.UploadAsync(this, Kind, ct);

    [HttpGet("batch")]
    [Right("suppliers", Rights.Create)]
    public IActionResult Batch() => pages.BatchForm(this, Kind);

    [HttpPost("batch")]
    [Right("suppliers", Rights.Create)]
    public Task<IActionResult> BatchPost(CancellationToken ct) => pages.BatchSaveAsync(this, Kind, ct);
}
