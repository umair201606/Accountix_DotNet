using Accountix.Core.Entities;

namespace Accountix.Inventory.Tests;

/// <summary>Bare documents the engine looks up during a replay (a sales return's invoice, a purchase return's invoice).</summary>
internal static class ReturnDocs
{
    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    public static async Task<int> CustomerAsync(CostingWorld w)
    {
        var c = new InvCustomer { Name = "Costing Customer " + Tag(), IsActive = true, CreatedAt = DateTime.UtcNow };
        w.Db.Add(c);
        await w.Db.SaveChangesAsync();
        return c.Id;
    }

    public static async Task<int> SupplierAsync(CostingWorld w)
    {
        var s = new InvSupplier { Name = "Costing Supplier " + Tag(), IsActive = true, CreatedAt = DateTime.UtcNow };
        w.Db.Add(s);
        await w.Db.SaveChangesAsync();
        return s.Id;
    }

    public static async Task<int> SalesInvoiceAsync(CostingWorld w)
    {
        var inv = new InvInvoice
        {
            InvoiceNumber = "T-SI-" + Tag(), VoucherNumber = "T-SI-" + Tag(), CustomerId = await CustomerAsync(w),
            VoucherStatus = "approved", CreatedAt = DateTime.UtcNow,
        };
        w.Db.Add(inv);
        await w.Db.SaveChangesAsync();
        return inv.Id;
    }

    public static async Task<int> SalesReturnAsync(CostingWorld w, int invoiceId)
    {
        int customer = w.Db.Set<InvInvoice>().Single(i => i.Id == invoiceId).CustomerId;
        var r = new InvSalesReturn
        {
            ReturnNumber = "T-SR-" + Tag(), OriginalInvoiceId = invoiceId, CustomerId = customer, Status = "approved",
            CreatedAt = DateTime.UtcNow,
        };
        w.Db.Add(r);
        await w.Db.SaveChangesAsync();
        return r.Id;
    }

    public static async Task<int> PurchaseInvoiceAsync(CostingWorld w)
    {
        var pi = new InvPurchaseInvoice
        {
            InvoiceNumber = "T-PI-" + Tag(), VoucherNumber = "T-PI-" + Tag(), SupplierId = await SupplierAsync(w),
            Status = "approved", CreatedAt = DateTime.UtcNow,
        };
        w.Db.Add(pi);
        await w.Db.SaveChangesAsync();
        return pi.Id;
    }

    public static async Task<int> PurchaseReturnAsync(CostingWorld w, int invoiceId)
    {
        int supplier = w.Db.Set<InvPurchaseInvoice>().Single(i => i.Id == invoiceId).SupplierId;
        var r = new InvPurchaseReturn
        {
            ReturnNumber = "T-PR-" + Tag(), OriginalInvoiceId = invoiceId, SupplierId = supplier, Status = "approved",
            CreatedAt = DateTime.UtcNow,
        };
        w.Db.Add(r);
        await w.Db.SaveChangesAsync();
        return r.Id;
    }
}
