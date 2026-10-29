using Accountix.Inventory.Services;

namespace Accountix.Admin.Tests;

/// <summary>
/// admin remove-faulty-demo-records: the three records tools/remove_faulty_demo_records.py was
/// written for (as they were on the live database: Default Company, July 2026), each of its
/// checks, the second run that aborts, and parity with the Python script.
/// </summary>
public class RemoveFaultyDemoRecordsTests
{
    private const int Default = RemoveFaultyDemoRecords.CompanyId;

    private sealed record Demo(int Pi, int Dn, int Brv, int P1, int P2);

    /// <summary>
    /// In company 1: BRV-00001 (unposted, two debit lines, its voucher gone); PINV-202607-0001
    /// approved with Inventory debited 105,600 while its two stock layers hold 92,250; and
    /// DN-202607-0001 against it, "approved" with no stock and no journal.
    /// </summary>
    private static Task<Demo> FaultyAsync(AdminApp app) => app.InAsync(Default, async (sp, db) =>
    {
        var ledger = sp.GetRequiredService<ILedger>();
        var costing = sp.GetRequiredService<StockCosting>();
        var when = new DateTime(2026, 7, 10);
        var supplier = new InvSupplier { Name = "July Demo Supplier" };
        var p1 = new InvProduct { Name = "Demo Panel A", Sku = "DEMO-A", CurrentStock = 0, CostPrice = 0, UnitPrice = 0, IsActive = true };
        var p2 = new InvProduct { Name = "Demo Panel B", Sku = "DEMO-B", CurrentStock = 0, CostPrice = 0, UnitPrice = 0, IsActive = true };
        db.AddRange(supplier, p1, p2);
        await db.SaveChangesAsync();

        var pi = new InvPurchaseInvoice
        {
            InvoiceNumber = "PINV-202607-0001", VoucherNumber = "PINV-202607-0001", SupplierId = supplier.Id, InvoiceDate = when,
            Status = "approved", TotalAmount = 105_600, ApprovedAt = when, ApprovedBy = 1, CreatedBy = 1, CreatedAt = when,
        };
        db.Add(pi);
        await db.SaveChangesAsync();
        db.AddRange(
            new InvPurchaseInvoiceItem { InvoiceId = pi.Id, ProductId = p1.Id, Description = p1.Name, Quantity = 15, UnitPrice = 3_550 },
            new InvPurchaseInvoiceItem { InvoiceId = pi.Id, ProductId = p2.Id, Description = p2.Name, Quantity = 12, UnitPrice = 3_250 },
            new InvStockMovement { ProductId = p1.Id, Type = "in", Quantity = 15, ReferenceType = "purchase_invoice", ReferenceId = pi.Id, CreatedBy = 1 },
            new InvStockMovement { ProductId = p2.Id, Type = "in", Quantity = 12, ReferenceType = "purchase_invoice", ReferenceId = pi.Id, CreatedBy = 1 });
        await db.SaveChangesAsync();
        await costing.ReceiveAsync(p1.Id, "PI", pi.Id, pi.InvoiceNumber, 15, 3_550, "", 1, when);     // 53,250
        await costing.ReceiveAsync(p2.Id, "PI", pi.Id, pi.InvoiceNumber, 12, 3_250, "", 1, when);     // 39,000
        var inventory = await ledger.PostingAccountAsync("inventory");
        var ap = await ledger.PostingAccountAsync("ap");
        await ledger.PostAsync("PI", pi.Id, pi.InvoiceNumber, "Purchase Invoice PINV-202607-0001",
            [new(inventory.Id, 105_600, 0, "Inventory"), new(ap.Id, 0, 105_600, "Supplier")], when, 1);

        var dn = new InvPurchaseReturn
        {
            ReturnNumber = "DN-202607-0001", OriginalInvoiceId = pi.Id, SupplierId = supplier.Id, ReturnDate = when.AddDays(5),
            Status = "approved", NetReturnAmount = 7_100, ApprovedBy = 1, ApprovedAt = when.AddDays(5), CreatedBy = 1,
        };
        db.Add(dn);
        await db.SaveChangesAsync();
        db.Add(new InvPurchaseReturnItem { ReturnId = dn.Id, ProductId = p1.Id, Description = p1.Name, CurrentReturnQty = 2, UnitPrice = 3_550, NetReturnValue = 7_100 });

        var cash = await ledger.PostingAccountAsync("cash");
        var ar = await ledger.PostingAccountAsync("ar");
        var brv = new JournalEntry
        {
            VoucherType = "BRV", VoucherId = 987_654, VoucherNumber = "BRV-00001", Description = "Bank receipt",
            EntryDate = when, CreatedBy = 1, CreatedAt = when, IsPosted = false,
        };
        brv.JournalLines.Add(new JournalLine { AccountId = cash.Id, Debit = 500, Credit = 0, Description = "" });
        brv.JournalLines.Add(new JournalLine { AccountId = ar.Id, Debit = 500, Credit = 0, Description = "" });
        db.Add(brv);
        await db.SaveChangesAsync();
        return new Demo(pi.Id, dn.Id, brv.Id, p1.Id, p2.Id);
    });

    private static Task<(bool Brv, bool Pi, bool Dn)> PresentAsync(AdminApp app, Demo d) => app.InAsync(Default, async (_, db) =>
        (await db.Set<JournalEntry>().AnyAsync(e => e.Id == d.Brv),
         await db.Set<InvPurchaseInvoice>().AnyAsync(p => p.Id == d.Pi),
         await db.Set<InvPurchaseReturn>().AnyAsync(r => r.Id == d.Dn)));

    [ReferenceFact]
    public async Task Removes_the_three_records_and_default_company_passes_afterwards()
    {
        await using var scratch = await ScratchDb.CreateAsync();
        await using var app = await AdminApp.StartAsync(scratch);
        var d = await FaultyAsync(app);
        var failing = await app.FailingAsync(Default);
        Assert.Contains("inventory_vs_gl", failing);       // 105,600 in the GL, 92,250 in the layers
        Assert.Contains("documents_posted", failing);      // the "approved" debit note has no journal

        // Dry run: finds them, deletes inside the transaction, rolls back.
        var (exit, text) = await app.RunAsync("remove-faulty-demo-records");
        Assert.Equal(0, exit);
        Assert.Contains($"found: BRV journal {d.Brv} | DN {d.Dn} | PI {d.Pi}", text);
        Assert.Contains("'journal_entries': 2", text);
        Assert.Contains("Books Integrity after: passes", text);
        Assert.Contains("dry run - rolled back (add --apply to commit)", text);
        Assert.Equal((true, true, true), await PresentAsync(app, d));

        (exit, text) = await app.RunAsync("remove-faulty-demo-records", "--apply");
        Assert.Equal(0, exit);
        Assert.Contains("COMMITTED", text);
        Assert.Equal((false, false, false), await PresentAsync(app, d));
        Assert.Empty(await app.FailingAsync(Default));
        await app.InAsync(Default, async (_, db) =>
        {
            var ids = new[] { d.P1, d.P2 };
            Assert.All(await db.Set<InvProduct>().Where(p => ids.Contains(p.Id)).Select(p => p.CurrentStock).ToListAsync(), q => Assert.Equal(0m, q));
            Assert.False(await db.Set<StockLedger>().AnyAsync(s => ids.Contains(s.ProductId)));
            Assert.False(await db.Set<InvStockMovement>().AnyAsync(m => ids.Contains(m.ProductId)));
        });

        // A second run finds nothing to remove and aborts, as Python's does.
        (exit, text) = await app.RunAsync("remove-faulty-demo-records", "--apply");
        Assert.Equal(1, exit);
        Assert.Contains("ABORT: BRV-00001 journal: []", text);
    }

    private static readonly string[] Surprises = ["renamed", "brv posted", "voucher back", "debit note posted", "stock issued", "other return"];

    [ReferenceFact]
    public async Task Any_record_that_is_not_as_found_aborts_and_nothing_is_deleted()
    {
        await using var scratch = await ScratchDb.CreateAsync();
        Demo d;
        await using (var app = await AdminApp.StartAsync(scratch))
            d = await FaultyAsync(app);
        foreach (string surprise in Surprises)
        {
            await using var copy = await scratch.CloneAsync();
            await using var app = await AdminApp.StartAsync(copy);
            string expect = await app.InAsync(Default, async (sp, db) =>
            {
                switch (surprise)
                {
                    case "renamed":
                        await db.Database.ExecuteSqlRawAsync("UPDATE companies SET name = 'Default Co' WHERE id = 1");
                        return "ABORT: company 1 is 'Default Co'";
                    case "brv posted":
                        await db.Database.ExecuteSqlAsync($"UPDATE journal_entries SET is_posted = TRUE WHERE id = {d.Brv}");
                        return "ABORT: BRV-00001 journal: [(";
                    case "voucher back":
                        var v = new AccountingVoucher { VoucherType = "BRV", VoucherNumber = "BRV-00001", VoucherDate = new DateTime(2026, 7, 10), CreatedBy = 1 };
                        db.Add(v);
                        await db.SaveChangesAsync();
                        await db.Database.ExecuteSqlAsync($"UPDATE journal_entries SET voucher_id = {v.Id} WHERE id = {d.Brv}");
                        return "ABORT: BRV-00001's voucher exists after all";
                    case "debit note posted":
                        var ledger = sp.GetRequiredService<ILedger>();
                        var inv = await ledger.PostingAccountAsync("inventory");
                        var ap = await ledger.PostingAccountAsync("ap");
                        await ledger.PostAsync("PR", d.Dn, "DN-202607-0001", "Debit note", [new(ap.Id, 7_100, 0), new(inv.Id, 0, 7_100)], new DateTime(2026, 7, 15), 1);
                        return "ABORT: DN-202607-0001 has a journal";
                    case "stock issued":
                        await sp.GetRequiredService<StockCosting>().IssueAsync(d.P1, "SCRAP", 1, "SCRAP-1", 1, "", 1, null, new DateTime(2026, 7, 20));
                        await db.SaveChangesAsync();
                        return "ABORT: stock from the PI was issued";
                    default:
                        var other = new InvPurchaseReturn { ReturnNumber = "DN-202607-0002", OriginalInvoiceId = d.Pi, SupplierId = (await db.Set<InvPurchaseInvoice>().FirstAsync(p => p.Id == d.Pi)).SupplierId, Status = "draft" };
                        db.Add(other);
                        await db.SaveChangesAsync();
                        return "ABORT: other returns against the PI: [('DN-202607-0002',)]";
                }
            });
            var (exit, text) = await app.RunAsync("remove-faulty-demo-records", "--apply");
            Assert.True(exit == 1 && text.Contains(expect), $"{surprise}: exit {exit}\n{text}");
            Assert.DoesNotContain("deleted:", text);
            Assert.Equal((true, true, true), await PresentAsync(app, d));
        }
    }

}
