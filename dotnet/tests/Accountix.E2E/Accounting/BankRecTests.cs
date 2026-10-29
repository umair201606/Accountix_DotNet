using System.Globalization;
using Accountix.Core.Entities;
using Accountix.E2E.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Accountix.E2E.Accounting;

/// <summary>
/// Bank reconciliation in the browser (tests/e2e/test_bank_reconciliation.py): start a
/// statement for the E2E Bank account, tick the lines that appear on it (the cleared
/// balance and difference follow live), see Complete refused while a difference is
/// left, complete at zero, then reopen and delete. The two vouchers it reconciles are
/// posted first and taken off again at the end, so the run can repeat.
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class BankRecTests(AccountingApp app) : IClassFixture<AccountingApp>
{
    private const string Note = "E2E bank rec";

    [E2ETheory]
    [InlineData("desktop")]
    [InlineData("360")]
    public async Task A_statement_is_reconciled_completed_reopened_and_deleted(string viewport)
    {
        var d = await AccountingSeed.EnsureAsync(app);
        var c = await app.HttpAsync(E2EEnv.OwnerB, E2EEnv.CompanyB);
        await TidyAsync(c, d.Bank);

        // Yesterday: a receipt of 5,000 (on the statement) and a payment of 1,200 (not yet presented).
        var yesterday = AccountingSeed.CompanyNow.Date.AddDays(-1).AddHours(10);
        int receipt = await PostApprovedAsync(c, "BRV", d.Bank, d.Revenue, 5000m, yesterday);
        int payment = await PostApprovedAsync(c, "BPV", d.Bank, d.Expense, 1200m, yesterday);

        await using var s = await app.SessionAsync(Viewport.Named(viewport), E2EEnv.OwnerB, E2EEnv.CompanyB, test: "bankrec");
        s.AcceptDialogs = true;   // Delete asks with confirm(), as in Python
        var p = s.Page;
        await s.GoAsync("/accounting/bank-reconciliation");
        await p.Locator("select[name=account_id]").SelectOptionAsync(d.Bank.ToString());
        await p.Locator("input[name=statement_date]").FillAsync(AccountingSeed.CompanyNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        await p.Locator("input[name=statement_balance]").FillAsync("5000");
        await VoucherUi.SubmitAndWaitAsync(s, p.GetByRole(AriaRole.Button, new() { Name = "Start" }), @"/accounting/bank-reconciliation/\d+$");
        int rec = int.Parse(p.Url.Split('/').Last());

        var ticks = p.Locator(".br-tick");
        var receiptTick = p.Locator(".br-tick[data-net='5000.00']");
        var paymentTick = p.Locator(".br-tick[data-net='-1200.00']");
        await Expect(receiptTick).ToHaveCountAsync(1);
        await Expect(paymentTick).ToHaveCountAsync(1);
        Assert.Equal(2, await ticks.CountAsync());
        await Expect(p.Locator("#br-diff")).ToHaveTextAsync("5,000.00");

        // Tick the payment by mistake: the figures follow at once.
        await paymentTick.CheckAsync();
        await Expect(p.Locator("#br-cleared")).ToHaveTextAsync("-1,200.00");
        await Expect(p.Locator("#br-diff")).ToHaveTextAsync("6,200.00");
        await Expect(p.Locator("#br-diff")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("br-bad"));
        // Complete is refused while a difference is left.
        await VoucherUi.SubmitAndWaitAsync(s, p.GetByRole(AriaRole.Button, new() { Name = "Complete" }), $@"/accounting/bank-reconciliation/{rec}$");
        await Expect(p.Locator("body")).ToContainTextAsync("Not complete");
        Assert.Equal("draft", Status(rec));

        // The right line: zero difference, then Complete.
        await paymentTick.UncheckAsync();
        await receiptTick.CheckAsync();
        await Expect(p.Locator("#br-diff")).ToHaveTextAsync("0.00");
        await Expect(p.Locator("#br-diff")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("br-ok"));
        await VoucherUi.SubmitAndWaitAsync(s, p.GetByRole(AriaRole.Button, new() { Name = "Complete" }), $@"/accounting/bank-reconciliation/{rec}$");
        Assert.Equal("completed", Status(rec));
        await Expect(p.Locator(".badge")).ToHaveTextAsync("completed");
        // The statement: books 3,800 + unpresented 1,200 − in transit 0 = bank 5,000.
        var stmt = p.Locator(".br-stmt");
        await Expect(stmt.Locator("tr", new() { HasTextString = "payments not yet presented" })).ToContainTextAsync("1,200.00");
        await Expect(stmt.Locator("tr.br-total")).ToContainTextAsync("5,000.00");
        await Expect(p.Locator(".br-tick").First).ToBeDisabledAsync();

        // Reopen (the latest completed one), then delete the draft.
        await VoucherUi.SubmitAndWaitAsync(s, p.GetByRole(AriaRole.Button, new() { Name = "Reopen" }), $@"/accounting/bank-reconciliation/{rec}$");
        Assert.Equal("draft", Status(rec));
        await VoucherUi.SubmitAndWaitAsync(s, p.GetByRole(AriaRole.Button, new() { Name = "Delete" }), @"/accounting/bank-reconciliation$");
        Assert.Contains(s.Dialogs, x => x.StartsWith("confirm"));
        Assert.Null(Status(rec));
        await s.AssertCleanAsync();

        await UnpostAsync(c, receipt);
        await UnpostAsync(c, payment);
    }

    private static string? Status(int rec)
    {
        using var db = Db.For(E2EEnv.CompanyB);
        return db.Set<BankReconciliation>().Where(r => r.Id == rec).Select(r => r.Status).FirstOrDefault();
    }

    private static async Task<int> PostApprovedAsync(TestSupport.PageClient c, string type, int bank, int account, decimal amount, DateTime at)
    {
        bool receipt = type is "BRV" or "CRV";
        var r = await c.PostFormAsync("/accounting/vouchers",
            [new("action", "approve"), new("voucher_type", type), new("voucher_date", AccountingSeed.FormDate(at)),
             new("cash_bank_account_id", bank.ToString()), new("label_id", ""), new("notes", Note),
             new("account_id[]", account.ToString()), new("description[]", Note),
             new(receipt ? "credit[]" : "debit[]", amount.ToString(CultureInfo.InvariantCulture)),
             new(receipt ? "debit[]" : "credit[]", ""), new("label_id[]", "")], $"/accounting/vouchers?type={type}");
        Assert.Equal(System.Net.HttpStatusCode.Redirect, r.StatusCode);
        int id = VoucherUi.IdFrom(r.Headers.Location!.OriginalString);
        Assert.Equal("approved", AccountingSeed.Voucher(id)!.Status);
        return id;
    }

    private static async Task UnpostAsync(TestSupport.PageClient c, int voucher)
    {
        if (AccountingSeed.Voucher(voucher)?.Status == "approved")
            await c.PostFormAsync($"/accounting/vouchers/{voucher}/unapprove", [], "/accounting/vouchers/list");
        await c.PostFormAsync($"/accounting/vouchers/{voucher}/delete", [], "/accounting/vouchers/list");
        Assert.Null(AccountingSeed.Voucher(voucher));
    }

    /// <summary>Leftovers of an interrupted run on E2E Bank: reconciliations and the vouchers they matched.</summary>
    private static async Task TidyAsync(TestSupport.PageClient c, int bank)
    {
        for (int guard = 0; guard < 10; guard++)
        {
            BankReconciliation? left;
            using (var db = Db.For(E2EEnv.CompanyB))
                left = db.Set<BankReconciliation>().AsNoTracking().Where(r => r.AccountId == bank)
                    .OrderByDescending(r => r.Status == "draft").ThenByDescending(r => r.StatementDate).ThenByDescending(r => r.Id).FirstOrDefault();
            if (left is null)
                break;
            if (left.Status == "completed")
                await c.PostFormAsync($"/accounting/bank-reconciliation/{left.Id}/reopen", [], "/accounting/bank-reconciliation");
            await c.PostFormAsync($"/accounting/bank-reconciliation/{left.Id}/delete", [], "/accounting/bank-reconciliation");
        }
        List<int> vouchers;
        using (var db = Db.For(E2EEnv.CompanyB))
            vouchers = db.Set<AccountingVoucher>().Where(v => v.Notes == Note).Select(v => v.Id).ToList();
        foreach (int v in vouchers)
            await UnpostAsync(c, v);
    }
}
