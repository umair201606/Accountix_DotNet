using Accountix.Invoicing.Services;

namespace Accountix.Orders.Services;

/// <summary>What the order editor (sales/form_inv.html, purchases/form_inv.html) prints.</summary>
public sealed class OrderFormModel
{
    /// <summary>The order, or null for a new one (Jinja's `order`).</summary>
    public int? Id { get; init; }
    /// <summary>order.status (vstatus); "unapproved" for a new order; null prints "None".</summary>
    public string? Status { get; init; }
    public string? Number { get; init; }
    public int? PartyId { get; init; }
    public string PartyName { get; init; } = "";
    /// <summary>Sales: order.customer.address (the hidden billing address).</summary>
    public string PartyAddress { get; init; } = "";
    public int? PartyAccountId { get; init; }
    public string PartyMode { get; init; } = "relevant";
    public DateOnly? OrderDate { get; init; }
    public DateOnly? ExpectedDate { get; init; }
    public string? TaxMode { get; init; }
    public decimal? GlobalSalesTaxPct { get; init; }
    public string? Notes { get; init; }
    public string? DriverName { get; init; }
    public string? DriverContact { get; init; }
    public string? VehicleNumber { get; init; }
    public string? GatePass { get; init; }
    public bool HasApprover { get; init; }
    public string? ApproverName { get; init; }
    public DateTime? ApprovedAt { get; init; }
    public int? EditVersion { get; init; }
    public string ProductsJson { get; init; } = "[]";
    public string OrderItemsJson { get; init; } = "[]";

    public bool Exists => Id is not null;
    public string VStatus => Exists ? Jinja.Str(Status) : "unapproved";
    public bool IsApproved => Exists ? Status == "approved" : false;
    /// <param name="closed">"delivered" (sales) / "received" (purchase).</param>
    public bool EditableUnless(string closed) => !IsApproved && VStatus != closed && VStatus != "cancelled";

    // {{ x if order else '' }} and friends.
    public string IdText => Id?.ToString() ?? "";
    public string PartyIdText => Exists ? PartyId?.ToString() ?? "None" : "";
    public string PartyAccountIdText => Exists ? PartyAccountId?.ToString() ?? "" : "";
    public string OrderDateText => Exists && OrderDate is { } d ? d.ToString("yyyy-MM-dd") : "";
    public string ExpectedDateText => Exists && ExpectedDate is { } d ? d.ToString("yyyy-MM-dd") : "";
    public string NotesText => Exists ? Jinja.Str(Notes) : "";
    public string SalesTaxPctText => Exists ? Jinja.Float(GlobalSalesTaxPct) : "0";
    /// <summary>A column the order model no longer has (discount, further / withholding tax): Jinja prints "" for it.</summary>
    public string DroppedOrZero => Exists ? "" : "0";
    public string ApproverText => HasApprover ? Jinja.Str(ApproverName) : "";
    public string ApprovedAtText => ApprovedAt is { } a ? a.ToString("yyyy-MM-dd HH:mm") : "";
    public string EditVersionValue => Exists ? (EditVersion ?? 0).ToString() : "0";
    /// <summary>{{ 'display:none;' if not order or order.tax_mode != 'individual' else '' }}.</summary>
    public string TaxColumnStyle => Exists && TaxMode == "individual" ? "" : "display:none;";
    /// <summary>Discount pills: the order model has no discount_mode, so a saved order lights neither.</summary>
    public string DiscountGeneralActive => Exists ? "" : "active";
    public string TaxGeneralActive => !Exists || TaxMode == "general" ? "active" : "";
    public string TaxIndividualActive => Exists && TaxMode == "individual" ? "active" : "";
    public string Text(string? v) => Exists ? Jinja.Str(v) : "";
}

/// <summary>What an order line shows of its product.</summary>
public sealed record ProductBrief(int Id, string Name, string Sku, decimal? Weight);

/// <summary>One row of the order list.</summary>
public sealed record OrderListRow(int Id, string Number, string PartyName, DateOnly? OrderDate, DateOnly? ExpectedDate,
                                  decimal? TotalAmount, string? Status);

public sealed record OrderListModel(IReadOnlyList<OrderListRow> Orders, string? StatusFilter, string NumberFormat);

/// <summary>The read side both order pages share: products, accounts, form JSON.</summary>
public sealed class OrderReads(AccountixDbContext db, DocSettings settings)
{
    /// <summary>The form's preloaded catalog: every active product, by name (no weight here; the API adds it).</summary>
    public async Task<string> ProductsJsonAsync(CancellationToken ct)
    {
        var rows = await db.Set<InvProduct>().AsNoTracking().Where(p => p.IsActive == true).OrderBy(p => p.Name)
            .Select(p => new { p.Id, p.Name, p.Sku, p.UnitPrice, p.CurrentStock, p.Unit }).ToListAsync(ct);
        return PyJson.Dumps(rows.Select(p => new Dictionary<string, object?>
        {
            ["id"] = p.Id, ["name"] = p.Name, ["sku"] = p.Sku, ["unit_price"] = Py.FN(p.UnitPrice),
            ["current_stock"] = Py.QtyJson(p.CurrentStock), ["unit"] = p.Unit,
        }).ToList(), htmlSafe: true);
    }

    /// <summary>The editor's existing lines (order.items.all()), each with its product's code and weight.</summary>
    public static string ItemsJson(IEnumerable<(int ProductId, string? Sku, decimal? Weight, bool HasProduct, string? Description,
                                                 decimal? Quantity, string? Unit, decimal? UnitPrice, decimal? SalesTaxPct,
                                                 decimal? TotalBeforeDiscount, decimal? TotalAfterDiscount)> items) =>
        PyJson.Dumps(items.Select(it => new Dictionary<string, object?>
        {
            ["product_id"] = it.ProductId,
            ["product"] = new Dictionary<string, object?> { ["sku"] = it.HasProduct ? it.Sku : "" },
            ["weight"] = it.HasProduct ? WeightOr0(it.Weight) : 0,
            ["description"] = it.Description,
            ["quantity"] = Py.FN(it.Quantity),
            ["unit"] = it.Unit,
            ["unit_price"] = Py.FN(it.UnitPrice),
            ["sales_tax_pct"] = Py.FN(it.SalesTaxPct),
            ["total_before_discount"] = Py.FN(it.TotalBeforeDiscount),
            ["total_after_discount"] = Py.FN(it.TotalAfterDiscount),
        }).ToList(), htmlSafe: true);

    /// <summary>The products of these lines (company-filtered, as the lazy `it.product` load).</summary>
    public async Task<Dictionary<int, ProductBrief>> ProductsByIdAsync(IEnumerable<int> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return [];
        return await db.Set<InvProduct>().AsNoTracking().Where(p => list.Contains(p.Id))
            .Select(p => new ProductBrief(p.Id, p.Name, p.Sku, p.Weight)).ToDictionaryAsync(p => p.Id, ct);
    }

    /// <summary>`p.weight or 0`: 0.0 and None both become the int 0.</summary>
    public static object WeightOr0(decimal? w) => w is decimal v && v != 0 ? (object)(double)v : 0;

    /// <summary>ReportSettings.party_mode(doc).</summary>
    public async Task<string> PartyModeAsync(string doc, CancellationToken ct) =>
        DocSettings.PartyMode(await settings.ReportAsync(ct), doc);

    /// <summary>/api/products?q=: active products by name or SKU, first 20 by name.</summary>
    public async Task<List<Dictionary<string, object?>>> ProductsAsync(string q, CancellationToken ct)
    {
        var query = db.Set<InvProduct>().AsNoTracking().Where(p => p.IsActive == true);
        if (q.Length > 0)
        {
            string like = $"%{q}%";
            query = query.Where(p => EF.Functions.ILike(p.Name, like) || EF.Functions.ILike(p.Sku, like));
        }
        var rows = await query.OrderBy(p => p.Name).Take(20)
            .Select(p => new { p.Id, p.Name, p.Sku, p.UnitPrice, p.CurrentStock, p.Unit, p.Weight }).ToListAsync(ct);
        return rows.Select(p => new Dictionary<string, object?>
        {
            ["id"] = p.Id, ["name"] = p.Name, ["sku"] = p.Sku, ["unit_price"] = Py.FN(p.UnitPrice),
            ["current_stock"] = Py.QtyJson(p.CurrentStock), ["unit"] = p.Unit, ["weight"] = WeightOr0(p.Weight),
        }).ToList();
    }

    /// <summary>/api/accounts?q=: active level-5 accounts by name or code, first 30 by code.</summary>
    public async Task<List<Dictionary<string, object?>>> AccountsAsync(string q, CancellationToken ct)
    {
        var query = db.Set<ChartOfAccount>().AsNoTracking().Where(a => a.IsActive == true && a.Level == 5);
        if (q.Length > 0)
        {
            string like = $"%{q}%";
            query = query.Where(a => EF.Functions.ILike(a.Name, like) || EF.Functions.ILike(a.Code, like));
        }
        var rows = await query.OrderBy(a => a.Code).Take(30).Select(a => new { a.Id, a.Code, a.Name, a.Type }).ToListAsync(ct);
        return rows.Select(a => new Dictionary<string, object?>
        {
            ["id"] = a.Id, ["code"] = a.Code, ["name"] = a.Name, ["type"] = a.Type,
        }).ToList();
    }

    /// <summary>
    /// /api/customers and /api/suppliers rows: {id, name, city, phone, address}
    /// (None printed as "").
    /// </summary>
    public static Dictionary<string, object?> Party(int id, string name, string? city, string? phone, string? address) => new()
    {
        ["id"] = id, ["name"] = name, ["city"] = city ?? "", ["phone"] = phone ?? "", ["address"] = address ?? "",
    };

    /// <summary>/api/orders/&lt;party&gt; items: what the "From Orders" picker reads.</summary>
    public static Dictionary<string, object?> PickerItem(int id, int productId, bool hasProduct, string? name, string? sku,
                                                         decimal? quantity, decimal? unitPrice) => new()
    {
        ["id"] = id, ["product_id"] = productId,
        ["product_name"] = hasProduct ? name : "", ["product_sku"] = hasProduct ? sku : "",
        ["ordered_qty"] = Py.FN(quantity), ["unit_price"] = Py.FN(unitPrice),
    };
}
