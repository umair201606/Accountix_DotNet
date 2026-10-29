using System.Globalization;

namespace Accountix.Invoicing.Services;

/// <summary>
/// The lookups the document editors make while open (api/products,
/// api/customers, api/suppliers, api/accounts, api/orders) and the posting
/// maps (category → revenue account, rate → output tax account). Shared by
/// invoices, orders and returns.
/// </summary>
public sealed class InvoiceLookups(AccountixDbContext db)
{
    private static string Like(string q) => $"%{q}%";

    /// <summary>api_products: active products matching name / SKU, 20 by name.</summary>
    public async Task<List<Dictionary<string, object?>>> ProductsAsync(string? q, bool withWeight, CancellationToken ct)
    {
        q = (q ?? "").Trim();
        var query = db.Set<InvProduct>().AsNoTracking().Where(p => p.IsActive == true);
        if (q.Length > 0)
            query = query.Where(p => EF.Functions.ILike(p.Name, Like(q)) || EF.Functions.ILike(p.Sku, Like(q)));
        var rows = await query.OrderBy(p => p.Name).Take(20).ToListAsync(ct);
        return rows.Select(p =>
        {
            var d = new Dictionary<string, object?>
            {
                ["id"] = p.Id, ["name"] = p.Name, ["sku"] = p.Sku, ["unit_price"] = Py.FN(p.UnitPrice),
                ["current_stock"] = p.CurrentStock is null ? null : Py.QtyJson(p.CurrentStock), ["unit"] = p.Unit,
            };
            if (withWeight) d["weight"] = p.Weight is decimal w && w != 0 ? Py.F(w) : 0;
            return d;
        }).ToList();
    }

    /// <summary>api_customers: active customers by name, 20.</summary>
    public async Task<List<Dictionary<string, object?>>> CustomersAsync(string? q, bool withTax, CancellationToken ct)
    {
        q = (q ?? "").Trim();
        var query = db.Set<InvCustomer>().AsNoTracking().Where(c => c.IsActive == true);
        if (q.Length > 0) query = query.Where(c => EF.Functions.ILike(c.Name, Like(q)));
        var rows = await query.OrderBy(c => c.Name).Take(20).ToListAsync(ct);
        return rows.Select(c =>
        {
            var d = new Dictionary<string, object?>
            {
                ["id"] = c.Id, ["name"] = c.Name, ["city"] = c.City ?? "", ["phone"] = c.Phone ?? "", ["address"] = c.Address ?? "",
            };
            if (withTax)
            {
                d["tax_id"] = c.TaxId ?? "";
                d["payment_terms"] = c.PaymentTerms ?? "";
            }
            return d;
        }).ToList();
    }

    /// <summary>api_suppliers: active suppliers by name, 20.</summary>
    public async Task<List<Dictionary<string, object?>>> SuppliersAsync(string? q, CancellationToken ct)
    {
        q = (q ?? "").Trim();
        var query = db.Set<InvSupplier>().AsNoTracking().Where(s => s.IsActive == true);
        if (q.Length > 0) query = query.Where(s => EF.Functions.ILike(s.Name, Like(q)));
        var rows = await query.OrderBy(s => s.Name).Take(20).ToListAsync(ct);
        return rows.Select(s => new Dictionary<string, object?>
        {
            ["id"] = s.Id, ["name"] = s.Name, ["city"] = s.City ?? "", ["phone"] = s.Phone ?? "", ["address"] = s.Address ?? "",
        }).ToList();
    }

    /// <summary>api_charge_accounts: active level-5 accounts by code, 30; on the sales side a ledger's admin defaults ride along.</summary>
    public async Task<List<Dictionary<string, object?>>> ChargeAccountsAsync(string? q, string? withDefaults, CancellationToken ct)
    {
        q = (q ?? "").Trim();
        var query = db.Set<ChartOfAccount>().AsNoTracking().Where(a => a.IsActive == true && a.Level == 5);
        if (q.Length > 0)
            query = query.Where(a => EF.Functions.ILike(a.Name, Like(q)) || EF.Functions.ILike(a.Code, Like(q)));
        var accts = await query.OrderBy(a => a.Code).Take(30).ToListAsync(ct);
        Dictionary<int, ChargeLedgerDefault> defaults = [];
        if (withDefaults is not null)
        {
            var ids = accts.Select(a => a.Id).ToList();
            if (ids.Count == 0) ids.Add(0);
            defaults = (await db.Set<ChargeLedgerDefault>().AsNoTracking().Where(d => ids.Contains(d.AccountId)).ToListAsync(ct))
                .GroupBy(d => d.AccountId).ToDictionary(g => g.Key, g => g.First());
        }
        return accts.Select(a =>
        {
            var row = new Dictionary<string, object?> { ["id"] = a.Id, ["code"] = a.Code, ["name"] = a.Name, ["type"] = a.Type };
            if (withDefaults is not null && defaults.TryGetValue(a.Id, out var d) && (d.AppliesTo == "both" || d.AppliesTo == withDefaults))
                row["defaults"] = new Dictionary<string, object?>
                {
                    ["treatment"] = d.Treatment, ["st_taxable"] = d.StTaxable, ["wht_taxable"] = d.WhtTaxable, ["extra_taxable"] = d.ExtraTaxable,
                };
            return row;
        }).ToList();
    }

    /// <summary>The invoice's order picker (api/orders/&lt;customer&gt;): approved sales orders, newest first, picker_payload each.</summary>
    public async Task<List<Dictionary<string, object?>>> SalesOrdersForPickerAsync(int customerId, CancellationToken ct)
    {
        var orders = await db.Set<InvSalesOrder>().AsNoTracking().Where(o => o.CustomerId == customerId && o.Status == "approved")
            .OrderByDescending(o => o.Id).ToListAsync(ct);
        var ids = orders.Select(o => o.Id).ToList();
        var items = await db.Set<InvSalesOrderItem>().AsNoTracking().Where(i => ids.Contains(i.SoId)).OrderBy(i => i.Id).ToListAsync(ct);
        var pids = items.Select(i => i.ProductId).Distinct().ToList();
        var products = await db.Set<InvProduct>().AsNoTracking().Where(p => pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var byOrder = items.GroupBy(i => i.SoId).ToDictionary(g => g.Key, g => g.Select(OrderLine.Of).ToList());
        return orders.Select(o => OrderLinkage.PickerPayload(o.SoNumber, o.OrderDate, o.TotalAmount, o.TaxMode, o.GlobalSalesTaxPct, o.Id,
            byOrder.GetValueOrDefault(o.Id, []), products)).ToList();
    }

    /// <summary>The purchase invoice's order picker (api/orders/&lt;supplier&gt;).</summary>
    public async Task<List<Dictionary<string, object?>>> PurchaseOrdersForPickerAsync(int supplierId, CancellationToken ct)
    {
        var orders = await db.Set<InvPurchaseOrder>().AsNoTracking().Where(o => o.SupplierId == supplierId && o.Status == "approved")
            .OrderByDescending(o => o.Id).ToListAsync(ct);
        var ids = orders.Select(o => o.Id).ToList();
        var items = await db.Set<InvPurchaseOrderItem>().AsNoTracking().Where(i => ids.Contains(i.PoId)).OrderBy(i => i.Id).ToListAsync(ct);
        var pids = items.Select(i => i.ProductId).Distinct().ToList();
        var products = await db.Set<InvProduct>().AsNoTracking().Where(p => pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var byOrder = items.GroupBy(i => i.PoId).ToDictionary(g => g.Key, g => g.Select(OrderLine.Of).ToList());
        return orders.Select(o => OrderLinkage.PickerPayload(o.PoNumber, o.OrderDate, o.TotalAmount, o.TaxMode, o.GlobalSalesTaxPct, o.Id,
            byOrder.GetValueOrDefault(o.Id, []), products)).ToList();
    }

    /// <summary>CategoryRevenueAccount: category id → revenue account id.</summary>
    public async Task<Dictionary<int, int>> CategoryRevenueAccountsAsync(CancellationToken ct) =>
        (await db.Set<CategoryRevenueAccount>().AsNoTracking().OrderBy(m => m.Id).ToListAsync(ct))
            .GroupBy(m => m.CategoryId).ToDictionary(g => g.Key, g => g.Last().AccountId);

    /// <summary>TaxRateAccount: round(rate, 4) → output tax account id.</summary>
    public async Task<Dictionary<double, int>> TaxRateAccountsAsync(CancellationToken ct) =>
        (await db.Set<TaxRateAccount>().AsNoTracking().OrderBy(m => m.Id).ToListAsync(ct))
            .GroupBy(m => Py.Round(Py.F(m.RatePct), 4)).ToDictionary(g => g.Key, g => g.Last().AccountId);

    /// <summary>A list's date filter: ?month=YYYY-MM, or ?from= / ?to= (inclusive days). [from, to) as midnights.</summary>
    public static (DateTime? From, DateTime? To) Range(string? from, string? to, string? month)
    {
        if (!string.IsNullOrWhiteSpace(month)
            && DateTime.TryParseExact(month.Trim() + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var m))
            return (m, m.AddMonths(1));
        DateTime? f = DateTime.TryParseExact((from ?? "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fd) ? fd : null;
        DateTime? t = DateTime.TryParseExact((to ?? "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var td) ? td.AddDays(1) : null;
        return (f, t);
    }
}
