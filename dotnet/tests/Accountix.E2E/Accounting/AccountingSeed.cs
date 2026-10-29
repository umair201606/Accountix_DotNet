using System.Globalization;
using System.Net;
using Accountix.Core.Entities;
using Accountix.E2E.Harness;
using Microsoft.EntityFrameworkCore;

namespace Accountix.E2E.Accounting;

/// <summary>
/// What the accounting pages need to exist in company B, made once through the
/// .NET app's own routes and found again on later runs (the e2e copy keeps its
/// rows): a draft cash payment, an approved journal, a draft reconciliation of
/// Main Cash and an "E2E Bank" account for the reconciliation flow.
/// </summary>
public static class AccountingSeed
{
    public const string DraftNote = "E2E fixture: draft cash payment";
    public const string ApprovedNote = "E2E fixture: approved journal";
    public const string BankName = "E2E Bank";

    public sealed record Data(int Cash, int Expense, int Revenue, int Bank, int DraftId, int ApprovedId, int RecId);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Data? _data;

    public static int Company => E2EEnv.CompanyB;

    /// <summary>The company's "now" (Asia/Karachi, as CompanyClock): vouchers are dated with it.</summary>
    public static DateTime CompanyNow =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi"));

    public static string FormDate(DateTime at) => at.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);

    public static async Task<Data> EnsureAsync(AppFixture app)
    {
        if (_data is not null)
            return _data;
        await Gate.WaitAsync();
        try
        {
            if (_data is not null)
                return _data;
            int cash = Db.AccountId(Company, "1-01-01-01-0001");
            int expense = Db.AccountId(Company, "5-04-01-01-0001");
            int revenue = Db.AccountId(Company, "4-01-01-01-0001");
            var c = await app.HttpAsync(E2EEnv.OwnerB, Company);

            int bank = BankId() ?? 0;
            if (bank == 0)
            {
                int parent = Db.AccountId(Company, "1-01-01-02");
                var r = await c.PostFormAsync("/accounting/coa/add",
                    [new("name", BankName), new("parent_id", parent.ToString()), new("description", "Bank account for the browser tests"),
                     new("cash_flow_activity", ""), new("pl_section", "")], "/accounting/coa/add");
                Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
                bank = BankId() ?? throw new InvalidOperationException("E2E Bank was not created");
            }

            int draft = VoucherId(DraftNote) ?? 0;
            if (draft == 0)
            {
                var r = await c.PostFormAsync("/accounting/vouchers",
                    [new("action", "save"), new("voucher_type", "CPV"), new("voucher_date", FormDate(CompanyNow)),
                     new("cash_bank_account_id", cash.ToString()), new("label_id", ""), new("notes", DraftNote),
                     new("account_id[]", expense.ToString()), new("description[]", "Stationery"),
                     new("debit[]", "250"), new("credit[]", ""), new("label_id[]", "")], "/accounting/vouchers?type=CPV");
                Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
                draft = VoucherId(DraftNote) ?? throw new InvalidOperationException("draft voucher not saved");
            }

            int approved = VoucherId(ApprovedNote) ?? 0;
            if (approved == 0)
            {
                var r = await c.PostFormAsync("/accounting/vouchers",
                    [new("action", "approve"), new("voucher_type", "JV"), new("voucher_date", FormDate(CompanyNow)),
                     new("notes", ApprovedNote),
                     new("account_id[]", expense.ToString()), new("description[]", "Accrued rent"),
                     new("debit[]", "1000"), new("credit[]", ""), new("label_id[]", ""),
                     new("account_id[]", revenue.ToString()), new("description[]", "Recharged to tenant"),
                     new("debit[]", ""), new("credit[]", "1000"), new("label_id[]", "")], "/accounting/vouchers?type=JV");
                Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
                approved = VoucherId(ApprovedNote) ?? throw new InvalidOperationException("approved voucher not saved");
            }

            int rec = DraftRecId(cash) ?? 0;
            if (rec == 0)
            {
                var r = await c.PostFormAsync("/accounting/bank-reconciliation/new",
                    [new("account_id", cash.ToString()), new("statement_date", CompanyNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                     new("statement_balance", "0")], "/accounting/bank-reconciliation");
                Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
                rec = DraftRecId(cash) ?? throw new InvalidOperationException("reconciliation not started");
            }
            return _data = new Data(cash, expense, revenue, bank, draft, approved, rec);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static int? BankId()
    {
        using var db = Db.For(Company);
        return db.Set<ChartOfAccount>().Where(a => a.Name == BankName && a.Level == 5).Select(a => (int?)a.Id).FirstOrDefault();
    }

    public static int? VoucherId(string notes)
    {
        using var db = Db.For(Company);
        return db.Set<AccountingVoucher>().Where(v => v.Notes == notes).OrderBy(v => v.Id).Select(v => (int?)v.Id).FirstOrDefault();
    }

    public static int? DraftRecId(int account)
    {
        using var db = Db.For(Company);
        return db.Set<BankReconciliation>().Where(r => r.AccountId == account && r.Status == "draft")
            .OrderBy(r => r.Id).Select(r => (int?)r.Id).FirstOrDefault();
    }

    public static AccountingVoucher? Voucher(int id)
    {
        using var db = Db.For(Company);
        return db.Set<AccountingVoucher>().AsNoTracking().FirstOrDefault(v => v.Id == id);
    }

    /// <summary>(debit, credit) lines of the posted journal of a voucher; empty when nothing is posted.</summary>
    public static List<(int Account, decimal Dr, decimal Cr)> PostedJournal(string type, int id)
    {
        using var db = Db.For(Company);
        return db.Set<JournalLine>()
            .Where(l => l.JournalEntry.VoucherType == type && l.JournalEntry.VoucherId == id && l.JournalEntry.IsPosted == true)
            .OrderBy(l => l.Id)
            .Select(l => new ValueTuple<int, decimal, decimal>(l.AccountId, l.Debit ?? 0, l.Credit ?? 0))
            .ToList();
    }
}
