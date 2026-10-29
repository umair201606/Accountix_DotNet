using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.Core.Shared;
using Accountix.TestSupport;
using Accountix.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Accountix.Inventory.Tests;

/// <summary>
/// A stand-in for Finance's IReportExport: keeps the last table it was asked
/// to render, so a test can check the export's headers, rows and kinds without
/// the Finance project. Registered by booting the test assembly with the module
/// (modules register in name order, so this one comes after the stub).
/// </summary>
public sealed class CapturingReportExport : IReportExport
{
    public static volatile ReportTable? Last;
    public static volatile string? LastFormat;

    public Task<ExportFile> ExportTableAsync(string format, ReportTable table, CancellationToken ct = default)
    {
        string fmt = (format ?? "").ToLowerInvariant();
        if (fmt is not ("excel" or "xlsx" or "pdf" or "csv"))
            throw new UnknownExportFormatException(format ?? "");
        Last = table;
        LastFormat = fmt;
        return Task.FromResult(new ExportFile($"{table.Title}.{fmt}", "application/octet-stream", [1, 2, 3]));
    }
}

public sealed class ReportsFakeExportModule : IAccountixModule
{
    public string Key => "inventory-tests-reports";
    public void Register(IServiceCollection services, IConfiguration config) =>
        services.AddScoped<IReportExport, CapturingReportExport>();
    public ModuleNav? Navigation => null;
}

/// <summary>Shared helpers of the report-slice tests.</summary>
public static class ReportKit
{
    public static int CompanyB => Reference.Company("b");
    public static int OwnerB => Reference.UserId(Reference.Email("owner_b"));
    public static int Restricted => Reference.UserId(Reference.Email("restricted"));

    public static int DefaultCompany
    {
        get
        {
            using var db = Reference.NewDb();
            return db.Companies.Single(c => c.Slug == "default").Id;
        }
    }

    public static int Admin
    {
        get
        {
            using var db = Reference.NewDb();
            return db.Users.Single(u => u.Email == "admin@gmail.com").Id;
        }
    }

    public static AccountixDbContext Db(int companyId)
    {
        var company = new CompanyContext();
        company.Set(companyId);
        return Reference.NewDb(company);
    }

    public static Task<ModuleHost> HostAsync() =>
        ModuleHost.StartAsync(typeof(InventoryModule).Assembly, typeof(Accountix.Accounting.AccountingModule).Assembly,
                              typeof(ReportsFakeExportModule).Assembly);

    public static async Task<string> PageAsync(PageClient c, string url)
    {
        var r = await c.GetAsync(url);
        string body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url}: {(int)r.StatusCode} {body[..Math.Min(body.Length, 2000)]}");
        return body;
    }

    /// <summary>The visible text of the first element matching <paramref name="tag"/> (tags stripped, spaces collapsed).</summary>
    public static string Text(string html, string startMarker, string endMarker)
    {
        int i = html.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{startMarker}' not found");
        int j = html.IndexOf(endMarker, i, StringComparison.Ordinal);
        string part = html[i..(j < 0 ? html.Length : j)];
        part = Regex.Replace(part, "<[^>]+>", " ");
        part = System.Net.WebUtility.HtmlDecode(part);
        return Regex.Replace(part, @"\s+", " ").Trim();
    }

    /// <summary>The visible text of each &lt;tr&gt; between the markers.</summary>
    public static List<string> Rows(string html, string startMarker, string endMarker)
    {
        int i = html.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{startMarker}' not found");
        int j = html.IndexOf(endMarker, i, StringComparison.Ordinal);
        string part = html[i..(j < 0 ? html.Length : j)];
        return part.Split("<tr", StringSplitOptions.None).Skip(1)
            .Select(r => Regex.Replace(System.Net.WebUtility.HtmlDecode(Regex.Replace("<tr" + r, "<[^>]+>", " ")), @"\s+", " ").Trim())
            .ToList();
    }
}

/// <summary>
/// Report data of its own in company B: products with dated receipts and
/// issues (one back-dated), a re-costing, movements and one document of each
/// previewable type. Rows are committed (the host reads them on its own
/// connection) and removed again when the class is done.
/// </summary>
public sealed class ReportFixture : IAsyncLifetime
{
    public string Tag { get; } = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
    public int CategoryId, P1, P2, P3, SupplierId, ConsId, ScrapId, AdjId, PiId, PrId;
    public int Company => ReportKit.CompanyB;
    public int User => ReportKit.OwnerB;
    private readonly List<int> _ledger = [];

    public async Task InitializeAsync()
    {
        if (!Reference.Available) return;
        await using var db = ReportKit.Db(Company);
        var now = DateTime.UtcNow;
        var cat = new InvCategory { Name = $"RPT Cat {Tag}", IsActive = true, CreatedAt = now };
        db.Add(cat);
        await db.SaveChangesAsync();
        CategoryId = cat.Id;
        InvProduct Product(string suffix, decimal stock, int reorder, bool active) => new()
        {
            Sku = $"RPT-{Tag}-{suffix}", Name = $"Rpt {Tag} {suffix}", CategoryId = cat.Id, UnitPrice = 150, CostPrice = 100,
            ReorderLevel = reorder, CurrentStock = stock, Unit = "pcs", HsCode = "", Weight = 0, IsActive = active,
            CreatedAt = now, UpdatedAt = now,
        };
        // P2 is shown 7 on hand while its ledger says 0: the dashboard brings it back.
        var p1 = Product("A", 8.5m, 10, true);
        var p2 = Product("B", 7m, 0, true);
        var p3 = Product("C", 0m, 0, false);
        db.AddRange(p1, p2, p3);
        var sup = new InvSupplier { Name = $"Rpt Supplier {Tag}" };
        db.Add(sup);
        await db.SaveChangesAsync();
        (P1, P2, P3, SupplierId) = (p1.Id, p2.Id, p3.Id, sup.Id);

        var cons = new ConsumptionVoucher
        {
            VoucherNumber = $"RPTC-{Tag}", Date = new DateTime(2026, 2, 5), Department = "Workshop", Reason = "Repairs",
            Status = "approved", CreatedBy = User, ApprovedBy = User, ApprovedAt = new DateTime(2026, 2, 5, 10, 30, 0), CreatedAt = now,
        };
        cons.ConsumptionItems.Add(new ConsumptionItem { ProductId = P1, ProductName = p1.Name, Quantity = 4, UnitCost = 100, TotalCost = 400 });
        var scrap = new ScrapVoucher
        {
            VoucherNumber = $"RPTS-{Tag}", Date = new DateTime(2026, 3, 15), Reason = null, Status = "unapproved",
            CreatedBy = User, CreatedAt = now,
        };
        scrap.ScrapItems.Add(new ScrapItem { ProductId = P1, ProductName = p1.Name, Quantity = 2.5m, UnitCost = 109.0909m, TotalCost = 272.73m });
        var adj = new StockAdjustmentVoucher
        {
            VoucherNumber = $"RPTA-{Tag}", Date = new DateTime(2026, 3, 1), Reason = "Count", Status = "approved",
            CreatedBy = User, ApprovedBy = User, ApprovedAt = new DateTime(2026, 3, 1, 9, 0, 0), CreatedAt = now,
        };
        adj.StockAdjustmentItems.Add(new StockAdjustmentItem
        {
            ProductId = P1, ProductName = p1.Name, SystemQty = 6, PhysicalQty = 11, Difference = 5, UnitCost = 120, TotalCost = 600,
        });
        var pi = new InvPurchaseInvoice
        {
            InvoiceNumber = $"RPT-PI-{Tag}", VoucherNumber = $"RPTPI-{Tag}", InvoiceDate = new DateTime(2026, 1, 10),
            SupplierId = SupplierId, NetPayable = 1000.5m, Status = "approved", CreatedBy = User, CreatedAt = now,
        };
        pi.InvPurchaseInvoiceItems.Add(new InvPurchaseInvoiceItem { ProductId = P1, Description = "Panels", Quantity = 10, UnitPrice = 100, TotalAfterDiscount = 0 });
        pi.InvPurchaseInvoiceItems.Add(new InvPurchaseInvoiceItem { ProductId = null, Description = "Freight line", Quantity = 1, UnitPrice = 0.5m, TotalAfterDiscount = 0.5m });
        db.AddRange(cons, scrap, adj, pi);
        await db.SaveChangesAsync();
        (ConsId, ScrapId, AdjId, PiId) = (cons.Id, scrap.Id, adj.Id, pi.Id);
        var pr = new InvPurchaseReturn
        {
            ReturnNumber = $"RPT-DN-{Tag}", OriginalInvoiceId = PiId, SupplierId = SupplierId, ReturnDate = new DateTime(2026, 1, 20),
            NetReturnAmount = 200, Status = "new", CreatedBy = User, CreatedAt = now,
        };
        pr.InvPurchaseReturnItems.Add(new InvPurchaseReturnItem { ProductId = P1, Description = "Panels", CurrentReturnQty = 2, UnitPrice = 100, NetReturnValue = 0 });
        db.Add(pr);
        await db.SaveChangesAsync();
        PrId = pr.Id;

        // The stock ledger: P1 receives 10 @ 100, issues 4, receives 5 @ 120, scraps 2.5; one row keyed late
        // but dated early (the back-dated ADJ is keyed last). P2: 3 in and 3 out.
        StockLedger Row(int pid, string vt, int vid, string vn, string tt, decimal q, decimal unit, decimal total,
                        decimal rq, decimal rc, decimal ra, DateOnly d, string notes) => new()
        {
            ProductId = pid, VoucherType = vt, VoucherId = vid, VoucherNumber = vn, TransactionType = tt, Quantity = q,
            UnitCost = unit, TotalCost = total, RunningQty = rq, RunningCost = rc, RunningAvg = ra, ValuationMethod = "weighted_average",
            TxnDate = d, CreatedAt = now, CreatedBy = User, Notes = notes,
        };
        var rows = new[]
        {
            Row(P1, "PI", PiId, pi.VoucherNumber, "IN", 10, 100, 1000, 10, 1000, 100, new(2026, 1, 10), "Purchase"),
            Row(P1, "CONS", ConsId, cons.VoucherNumber, "OUT", 4, 100, 400, 6, 600, 100, new(2026, 2, 5), "Consumption: Repairs"),
            Row(P1, "SCRAP", ScrapId, scrap.VoucherNumber, "OUT", 2.5m, 109.0909m, 272.73m, 8.5m, 927.27m, 109.0906m, new(2026, 3, 15), ""),
            Row(P1, "ADJ", AdjId, adj.VoucherNumber, "IN", 5, 120, 600, 11, 1200, 109.0909m, new(2026, 3, 1), "Adjustment: Count"),
            Row(P2, "OPENING", P2, $"OPEN-RPT-{Tag}-B", "IN", 3, 50, 150, 3, 150, 50, new(2026, 4, 1), "Opening stock"),
            Row(P2, "SI", 999_000 + Random.Shared.Next(999), $"SI-RPT-{Tag}", "OUT", 3, 50, 150, 0, 0, 0, new(2026, 4, 2), ""),
        };
        db.AddRange(rows);
        await db.SaveChangesAsync();
        _ledger.AddRange(rows.Select(r => r.Id));
        db.Add(new StockCostAdjustment
        {
            ProductId = P1, LedgerId = rows[1].Id, VoucherType = "CONS", VoucherId = ConsId, VoucherNumber = cons.VoucherNumber,
            OldCost = 380, NewCost = 400, Delta = 20, Trigger = "reversal of RPTX", EntryDate = new DateOnly(2026, 2, 5), CreatedAt = now,
        });
        db.AddRange(
            new InvStockMovement { ProductId = P1, Type = "adjustment_in", Quantity = 5, ReferenceType = "stock_adjustment", ReferenceId = AdjId, Notes = "Count", CreatedBy = User, CreatedAt = new DateTime(2026, 3, 1, 9, 5, 0) },
            new InvStockMovement { ProductId = P1, Type = "adjustment_out", Quantity = 2.5m, ReferenceType = null, ReferenceId = null, Notes = null, CreatedBy = User, CreatedAt = new DateTime(2026, 3, 15, 11, 0, 0) });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (!Reference.Available) return;
        await using var db = ReportKit.Db(Company);
        var products = new[] { P1, P2, P3 };
        await db.Set<StockCostAdjustment>().Where(a => products.Contains(a.ProductId)).ExecuteDeleteAsync();
        await db.Set<InvStockMovement>().Where(m => products.Contains(m.ProductId)).ExecuteDeleteAsync();
        await db.Set<StockLedger>().Where(r => products.Contains(r.ProductId)).ExecuteDeleteAsync();
        await db.Set<InvPurchaseReturnItem>().Where(i => i.ReturnId == PrId).ExecuteDeleteAsync();
        await db.Set<InvPurchaseReturn>().Where(i => i.Id == PrId).ExecuteDeleteAsync();
        await db.Set<InvPurchaseInvoiceItem>().Where(i => i.InvoiceId == PiId).ExecuteDeleteAsync();
        await db.Set<InvPurchaseInvoice>().Where(i => i.Id == PiId).ExecuteDeleteAsync();
        await db.Set<ConsumptionItem>().Where(i => i.VoucherId == ConsId).ExecuteDeleteAsync();
        await db.Set<ConsumptionVoucher>().Where(i => i.Id == ConsId).ExecuteDeleteAsync();
        await db.Set<ScrapItem>().Where(i => i.VoucherId == ScrapId).ExecuteDeleteAsync();
        await db.Set<ScrapVoucher>().Where(i => i.Id == ScrapId).ExecuteDeleteAsync();
        await db.Set<StockAdjustmentItem>().Where(i => i.VoucherId == AdjId).ExecuteDeleteAsync();
        await db.Set<StockAdjustmentVoucher>().Where(i => i.Id == AdjId).ExecuteDeleteAsync();
        await db.Set<InvSupplier>().Where(s => s.Id == SupplierId).ExecuteDeleteAsync();
        await db.Set<InvProduct>().Where(p => products.Contains(p.Id)).ExecuteDeleteAsync();
        await db.Set<InvCategory>().Where(c => c.Id == CategoryId).ExecuteDeleteAsync();
    }
}
