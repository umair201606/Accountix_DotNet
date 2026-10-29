using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.Core.Shared;
using Accountix.Parties.Services;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Parties.Tests;

/// <summary>
/// The test_payment_tracking.py scenario, made fresh for each test (in company B, or in a fresh company of its own for
/// the golden tests) so tests never see each other's money: one customer, invoice A (3,000, long overdue) and invoice B
/// (4,000, not yet due), and one approved 5,000 cash receipt (Dr Cash / Cr the customer's subledger account).
/// </summary>
public sealed record Scenario(int CompanyId, int CustomerId, string CustomerName, int AccountId, string AccountCode,
                              int InvA, int InvB, string NumA, string NumB, int VoucherId, int LineId, string VoucherNumber, string Tag)
{
    public static int OwnerB => Reference.UserId(Reference.Email("owner_b"));
    public static int CompanyB => Reference.Company("b");

    public static AccountixDbContext Db(int? companyId = null)
    {
        var company = new CompanyContext();
        company.Set(companyId ?? CompanyB);
        return Reference.NewDb(company);
    }

    /// <summary>A tracking engine on its own context (company B unless named).</summary>
    public static (AccountixDbContext Db, PaymentTracking Pt) Engine(int? companyId = null)
    {
        var company = new CompanyContext();
        company.Set(companyId ?? CompanyB);
        var db = Reference.NewDb(company);
        return (db, new PaymentTracking(db, new CompanyClock(db, company, TimeProvider.System), company));
    }

    public static string NewTag() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// A fresh company of its own, provisioned as the app provisions one (Platform's CompanySetup: chart, numbering,
    /// settings, periods, templates), with owner B as its admin. Golden tests build their data here, so company-wide
    /// figures (register totals, date ranges) hold only what the test made, whatever earlier runs left behind.
    /// </summary>
    public static async Task<int> FreshCompanyAsync(string name = "Golden Parties Co")
    {
        await using var db = Reference.NewDb(new CompanyContext());
        var c = new Company
        {
            Name = name, Slug = "golden-parties-" + NewTag(), IsActive = true, PlanName = "free", CreatedBy = OwnerB,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.Companies.Add(c);
        await db.SaveChangesAsync();
        int admin = await db.Roles.Where(r => r.Name == Role.Admin).Select(r => r.Id).FirstAsync();
        await new Accountix.Platform.Services.Memberships(db, TimeProvider.System).AddAsync(c.Id, OwnerB, admin);
        await new Accountix.Platform.Provisioning.CompanySetup(db, new Accountix.Platform.Services.FirstUse(db, TimeProvider.System),
                                                               TimeProvider.System).ProvisionAsync(c.Id);
        return c.Id;
    }

    public static async Task<int> CashAccountAsync(AccountixDbContext db) =>
        (await db.Set<ChartOfAccount>().SingleAsync(a => a.Code == "1-01-01-01-0001")).Id;

    /// <summary>A customer with its deterministic subledger account (what create_entity_account makes).</summary>
    public static async Task<(int Id, string Name, int AccountId, string Code)> CustomerAsync(AccountixDbContext db, string tag, string? terms = null)
    {
        var c = new InvCustomer
        {
            Name = $"Tracking Test Customer {tag}", IsActive = true, CreatedAt = DateTime.UtcNow, CreditLimit = 0, PaymentTerms = terms,
        };
        db.Add(c);
        await db.SaveChangesAsync();
        var parent = await db.Set<ChartOfAccount>().SingleAsync(a => a.Code == TrackingRules.CustomerParent);
        string code = TrackingRules.EntityCode("customer", c.Id);
        var acct = await db.Set<ChartOfAccount>().FirstOrDefaultAsync(a => a.Code == code);
        if (acct is null)
        {
            acct = new ChartOfAccount
            {
                Code = code, Name = c.Name, Type = parent.Type, ParentId = parent.Id, Level = 5, IsFixed = false, IsActive = true,
                CreatedAt = DateTime.UtcNow,
            };
            db.Add(acct);
            await db.SaveChangesAsync();
        }
        return (c.Id, c.Name, acct.Id, code);
    }

    public static async Task<int> InvoiceAsync(AccountixDbContext db, int customerId, string number, decimal total, DateTime date,
                                               DateTime? due, string status = "approved")
    {
        var inv = new InvInvoice
        {
            InvoiceNumber = number, VoucherNumber = number, CustomerId = customerId, TotalAmount = total, InvoiceDate = date,
            DueDate = due, VoucherStatus = status, PaymentStatus = "unpaid", PaidAmount = 0, CreatedBy = OwnerB, CreatedAt = DateTime.UtcNow,
            EditVersion = 1,
        };
        db.Add(inv);
        await db.SaveChangesAsync();
        return inv.Id;
    }

    /// <summary>An approved receipt voucher (Dr Cash / Cr the account); returns (voucher id, party line id).</summary>
    public static async Task<(int VoucherId, int LineId)> ReceiptAsync(AccountixDbContext db, int accountId, decimal amount, string number,
                                                                      DateTime? date = null, string type = "CRV")
    {
        int cash = await CashAccountAsync(db);
        var v = new AccountingVoucher
        {
            VoucherType = type, VoucherNumber = number, VoucherDate = date ?? DateTime.UtcNow.Date, CashBankAccountId = cash,
            Status = "approved", CreatedBy = OwnerB, ApprovedBy = OwnerB, CreatedAt = DateTime.UtcNow, Notes = "",
        };
        db.Add(v);
        await db.SaveChangesAsync();
        var cashLine = new AccountingVoucherLine { VoucherId = v.Id, LineNo = 1, AccountId = cash, Description = "Cash in", Debit = amount, Credit = 0 };
        var partyLine = new AccountingVoucherLine { VoucherId = v.Id, LineNo = 2, AccountId = accountId, Description = "From customer", Debit = 0, Credit = amount };
        db.AddRange(cashLine, partyLine);
        await db.SaveChangesAsync();
        return (v.Id, partyLine.Id);
    }

    /// <param name="companyId">Company B unless named (a fresh company for the golden tests).</param>
    /// <param name="tag">Random unless named (a fresh company can use fixed names).</param>
    /// <param name="today">The day the scenario is built around (today unless named).</param>
    public static async Task<Scenario> CreateAsync(decimal receipt = 5000m, decimal a = 3000m, decimal b = 4000m,
                                                   int? companyId = null, string? tag = null, DateTime? today = null)
    {
        tag ??= NewTag();
        int company = companyId ?? CompanyB;
        await using var db = Db(company);
        var (cid, name, acctId, code) = await CustomerAsync(db, tag);
        var day = today ?? DateTime.UtcNow.Date;
        int invA = await InvoiceAsync(db, cid, $"TRK-A-{tag}", a, day.AddDays(-90), day.AddDays(-60));
        int invB = await InvoiceAsync(db, cid, $"TRK-B-{tag}", b, day, day.AddDays(30));
        var (vid, lid) = await ReceiptAsync(db, acctId, receipt, $"TRK-CRV-{tag}", day);
        return new Scenario(company, cid, name, acctId, code, invA, invB, $"TRK-A-{tag}", $"TRK-B-{tag}", vid, lid, $"TRK-CRV-{tag}", tag);
    }

    /// <summary>An approved credit note against an invoice (company B unless named; dated now unless named).</summary>
    public static async Task<int> ReturnAsync(int invoiceId, int customerId, decimal amount, string number, int? companyId = null,
                                              DateTime? date = null)
    {
        await using var db = Db(companyId);
        var r = new InvSalesReturn
        {
            ReturnNumber = number, OriginalInvoiceId = invoiceId, CustomerId = customerId, ReturnDate = date ?? DateTime.UtcNow,
            NetReturnAmount = amount, Status = "approved", CreatedBy = OwnerB, CreatedAt = DateTime.UtcNow, EditVersion = 1,
        };
        db.Add(r);
        await db.SaveChangesAsync();
        return r.Id;
    }

    /// <summary>Everything the general ledger is made of, as one comparable value (_ledger_fingerprint).</summary>
    public static async Task<string> LedgerFingerprintAsync()
    {
        await using var db = Db();
        var lines = await db.Set<AccountingVoucherLine>().OrderBy(l => l.Id)
            .Select(l => $"{l.Id}|{l.VoucherId}|{l.AccountId}|{l.Debit}|{l.Credit}").ToListAsync();
        int vouchers = await db.Set<AccountingVoucher>().CountAsync();
        int journals = await db.Set<JournalEntry>().CountAsync();
        int jlines = await db.Set<JournalLine>().CountAsync();
        return $"{vouchers}/{journals}/{jlines}/{string.Join(";", lines)}";
    }
}
