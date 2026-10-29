using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Fbr.Tests;

/// <summary>Invoices, customers and the FBR settings row the tests need, in company B (owner_b is its admin).</summary>
public static class FbrData
{
    public static int OwnerB => Reference.UserId(Reference.Email("owner_b"));
    public static int CompanyB => Reference.Company("b");
    /// <summary>An employee in B (owner_a): not an admin there.</summary>
    public static int EmployeeInB => Reference.UserId(Reference.Email("owner_a"));
    public static int Restricted => Reference.UserId(Reference.Email("restricted"));

    public static AccountixDbContext Db()
    {
        var company = new CompanyContext();
        company.Set(CompanyB);
        return Reference.NewDb(company);
    }

    /// <summary>FBR switched on with a token and a seller profile (or off).</summary>
    public static async Task ConfigureAsync(bool active = true, string token = "test-token", string environment = "sandbox")
    {
        await using var db = Db();
        var c = await db.Set<FbrConfig>().OrderBy(x => x.Id).FirstOrDefaultAsync();
        if (c is null)
        {
            c = new FbrConfig { CreatedAt = DateTime.UtcNow, TokenExpiry = null };
            db.Add(c);
        }
        c.Environment = environment;
        c.AccessToken = token;
        c.SellerNtn = "1234567-8";
        c.SellerStrn = "32-77-8761-234-55";
        c.SellerCnic = "3520212345671";
        c.SellerName = "Owner B";
        c.SellerBusinessName = "Réf B (Pvt) Ltd — “Lahore”";
        c.SellerAddress = "12 Mall Road,\nLahore";
        c.SellerPhone = "0300-1234567";
        c.SellerEmail = "fbr@ref-b.test";
        c.IsActive = active;
        c.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public static async Task RemoveConfigAsync()
    {
        await using var db = Db();
        await db.Set<FbrConfig>().ExecuteDeleteAsync();
    }

    public sealed record Line(decimal? Qty, decimal? Price, decimal? DiscAmount = null, decimal? TaxPct = null,
                              string? Description = null, string? Unit = "pcs", decimal? Delivery = null, decimal? Installation = null,
                              bool WithProduct = true, string? HsCode = "8541.4000", decimal? DiscPct = null);

    public sealed record Charge(decimal? Amount, string? Treatment = "bill", bool? StTaxable = true, bool? WhtTaxable = false,
                                string? Description = "Freight");

    /// <summary>
    /// An invoice written straight to the tables, with every field Python's
    /// model would set. Header totals are given as the invoice form would
    /// have computed them; the mapper only reads them.
    /// </summary>
    public static async Task<int> InvoiceAsync(string number, IReadOnlyList<Line> lines, IReadOnlyList<Charge>? charges = null,
        string status = "approved", string taxMode = "general", decimal? globalTaxPct = 18m,
        decimal? subtotal = null, decimal? totalDiscount = null, decimal? totalTax = null,
        bool furtherTax = false, decimal? furtherPct = 4m, bool wht = false, decimal? whtPct = 1m,
        decimal? delivery = 0m, decimal? installation = 0m, string? paymentStatus = "unpaid",
        DateTime? date = null, string customerName = "FBR Buyer", string? taxId = "NTN-778899")
    {
        await using var db = Db();
        var customer = new InvCustomer
        {
            Name = customerName, TaxId = taxId, Address = "Shop 4, Hall Road — Lahore", Phone = "042-111", Email = "buyer@ref.test",
        };
        db.Add(customer);
        await db.SaveChangesAsync();
        decimal sub = subtotal ?? lines.Sum(l => (l.Qty ?? 0) * (l.Price ?? 0));
        var inv = new InvInvoice
        {
            InvoiceNumber = number,
            VoucherNumber = "SV-" + number,
            CustomerId = customer.Id,
            InvoiceDate = date ?? new DateTime(2026, 5, 12, 10, 30, 0),
            DueDate = (date ?? new DateTime(2026, 5, 12)).AddDays(30),
            VoucherStatus = status,
            PaymentStatus = paymentStatus,
            DiscountMode = "general", ChargesMode = "general", TaxMode = taxMode,
            GlobalDiscountPct = 0, GlobalDiscountValue = 0, GlobalDelivery = delivery, GlobalInstallation = installation,
            GlobalSalesTaxPct = globalTaxPct, FurtherTaxPct = furtherPct, ApplyFurtherTax = furtherTax,
            WithholdingTaxPct = whtPct, ApplyWithholdingTax = wht,
            Subtotal = sub, TotalDiscount = totalDiscount ?? lines.Sum(l => l.DiscAmount ?? 0),
            TotalCharges = charges?.Sum(c => c.Amount ?? 0) ?? 0, TotalTax = totalTax ?? 0,
            TotalFurtherTax = 0, TotalWithholdingTax = 0, TotalAmount = sub, PaidAmount = 0,
            Notes = "", EditVersion = 0, CreatedAt = DateTime.UtcNow, CreatedBy = OwnerB,
        };
        db.Add(inv);
        await db.SaveChangesAsync();
        int n = 0;
        foreach (var l in lines)
        {
            int? productId = null;
            if (l.WithProduct)
            {
                var p = new InvProduct
                {
                    // The SKU is FBR's itemCode: built from the (unique) invoice number so payloads are reproducible.
                    Sku = $"FBR-{number}-{++n}", Name = $"Solar part {n}", HsCode = l.HsCode,
                    Weight = 2.5m, CurrentStock = 0, CreatedAt = DateTime.UtcNow,
                };
                db.Add(p);
                await db.SaveChangesAsync();
                productId = p.Id;
            }
            decimal value = (l.Qty ?? 0) * (l.Price ?? 0);
            db.Add(new InvInvoiceItem
            {
                InvoiceId = inv.Id, ProductId = productId, Description = l.Description, Quantity = l.Qty, Unit = l.Unit,
                UnitPrice = l.Price, DiscountPct = l.DiscPct ?? 0, DiscountAmount = l.DiscAmount, Delivery = l.Delivery,
                Installation = l.Installation, SalesTaxPct = l.TaxPct, TotalBeforeDiscount = value,
                TotalAfterDiscount = value - (l.DiscAmount ?? 0), SourceOrderNumber = "", Comments = "",
            });
        }
        if (charges is { Count: > 0 })
        {
            int account = await db.Set<ChartOfAccount>().OrderBy(a => a.Id).Select(a => a.Id).FirstAsync();
            foreach (var c in charges)
                db.Add(new AdditionalCharge
                {
                    DocType = "SI", DocId = inv.Id, ChargeAccountId = account, Description = c.Description, Amount = c.Amount,
                    Scope = "general", Distribution = "pro_rata_value", ManualAllocations = "", Treatment = c.Treatment,
                    StTaxable = c.StTaxable, WhtTaxable = c.WhtTaxable, ExtraTaxable = false, Taxable = true,
                    TaxBase = "after_discount", CreatedAt = DateTime.UtcNow,
                });
        }
        await db.SaveChangesAsync();
        return inv.Id;
    }

    /// <summary>A plain approved invoice for the Send / Retry tests.</summary>
    public static Task<int> SimpleInvoiceAsync(string tag) =>
        InvoiceAsync($"SI-FBR-{tag}-{Guid.NewGuid().ToString("N")[..5]}",
            [new(2m, 1500m, TaxPct: 18m), new(1m, 333.33m, DiscAmount: 10m, TaxPct: 18m)]);

    public static async Task<List<FbrInvoiceRecord>> RecordsAsync(int invoiceId)
    {
        await using var db = Db();
        return await db.Set<FbrInvoiceRecord>().AsNoTracking().Where(r => r.InvInvoiceId == invoiceId).OrderBy(r => r.Id).ToListAsync();
    }

    public static async Task DeleteRecordsAsync(int invoiceId)
    {
        await using var db = Db();
        await db.Set<FbrInvoiceRecord>().Where(r => r.InvInvoiceId == invoiceId).ExecuteDeleteAsync();
    }

    public static async Task SetAsync(int invoiceId, Action<InvInvoice> change)
    {
        await using var db = Db();
        var inv = await db.Set<InvInvoice>().SingleAsync(i => i.Id == invoiceId);
        change(inv);
        await db.SaveChangesAsync();
    }
}
