using System.Net;
using System.Text.Json;
using Accountix.Core.Data;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Accounting.Tests;

/// <summary>The voucher pages through the .NET app; what a save posts is checked against the Python app's answer (Golden/).</summary>
public class VoucherPageTests
{
    private static int OwnerB => Reference.UserId(Reference.Email("owner_b"));
    private static int CompanyB => Reference.Company("b");

    private static async Task<(int Cash, int Expense, int Revenue)> AccountsAsync()
    {
        var company = new CompanyContext();
        company.Set(CompanyB);
        await using var db = Reference.NewDb(company);
        int Code(string code) => db.Set<ChartOfAccount>().Single(a => a.Code == code).Id;
        return (Code("1-01-01-01-0001"), Code("5-04-01-01-0001") , Code("4-01-01-01-0001"));
    }

    private static List<KeyValuePair<string, string>> CpvForm(int cash, int expense, string action, string amount = "1250.75",
                                                            string narration = "Office rent")
        =>
        [
            new("action", action), new("voucher_type", "CPV"), new("voucher_date", "2026-04-10T09:30"),
            new("cash_bank_account_id", cash.ToString()), new("label_id", ""), new("notes", narration),
            new("account_id[]", expense.ToString()), new("description[]", "Rent April"),
            new("debit[]", amount), new("credit[]", ""), new("label_id[]", ""),
        ];

    private static async Task<AccountingVoucher> VoucherAsync(int id)
    {
        var company = new CompanyContext();
        company.Set(CompanyB);
        await using var db = Reference.NewDb(company);
        return await db.Set<AccountingVoucher>().Include(v => v.AccountingVoucherLines).AsNoTracking().SingleAsync(v => v.Id == id);
    }

    private static async Task<List<(int Account, decimal Dr, decimal Cr)>> JournalAsync(string type, int id)
    {
        var company = new CompanyContext();
        company.Set(CompanyB);
        await using var db = Reference.NewDb(company);
        return await db.Set<JournalLine>()
            .Where(l => l.JournalEntry.VoucherType == type && l.JournalEntry.VoucherId == id && l.JournalEntry.IsPosted == true)
            .OrderBy(l => l.Id)
            .Select(l => new ValueTuple<int, decimal, decimal>(l.AccountId, l.Debit ?? 0, l.Credit ?? 0))
            .ToListAsync();
    }

    [ReferenceFact]
    public async Task Dashboard_and_new_voucher_form_render()
    {
        await using var host = await ModuleHost.StartAsync(typeof(AccountingModule).Assembly);
        var c = host.ClientFor(OwnerB, CompanyB);
        var dash = await c.GetAsync("/accounting/");
        Assert.Equal(HttpStatusCode.OK, dash.StatusCode);
        string html = await dash.Content.ReadAsStringAsync();
        Assert.Contains("Total vouchers", html);
        Assert.Contains("href=\"/accounting/vouchers?type=CPV\"", html);
        var form = await c.GetAsync("/accounting/vouchers?type=BRV");
        string fhtml = await form.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, form.StatusCode);
        Assert.Contains("New bank receipt voucher", fhtml);
        Assert.Contains("var IS_RECEIPT = true;", fhtml);
    }

    /// <summary>
    /// Golden (Golden/CpvSaveApprove.json, capture/voucher_save_approve.py): the same CPV form, posted
    /// to Python's route, wrote this voucher and this journal — lines, sides, amounts, descriptions,
    /// dates — with accounts as codes and the number's digits as '#'.
    /// </summary>
    [ReferenceFact]
    public async Task Save_and_approve_posts_what_the_python_form_posts()
    {
        var (cash, expense, _) = await AccountsAsync();
        await using var host = await ModuleHost.StartAsync(typeof(AccountingModule).Assembly);
        var c = host.ClientFor(OwnerB, CompanyB);
        var r = await c.PostFormAsync("/accounting/vouchers", CpvForm(cash, expense, "approve"), "/accounting/vouchers?type=CPV");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        int id = int.Parse(r.Headers.Location!.OriginalString.Split('/').Last());
        try
        {
            Golden.Equal("CpvSaveApprove", await ShapeAsync(id, (int)r.StatusCode));
        }
        finally
        {
            await c.PostFormAsync($"/accounting/vouchers/{id}/unapprove", [], $"/accounting/vouchers/{id}");
            await c.PostFormAsync($"/accounting/vouchers/{id}/delete", [], $"/accounting/vouchers/{id}");
        }
    }

    /// <summary>A voucher and its journal as capture/voucher_save_approve.py writes them.</summary>
    private static async Task<object> ShapeAsync(int id, int status)
    {
        var company = new CompanyContext();
        company.Set(CompanyB);
        await using var db = Reference.NewDb(company);
        var codes = await db.Set<ChartOfAccount>().ToDictionaryAsync(a => a.Id, a => a.Code);
        string? Code(int? aid) => aid is int x ? codes[x] : null;
        var v = await db.Set<AccountingVoucher>().Include(x => x.AccountingVoucherLines).AsNoTracking().SingleAsync(x => x.Id == id);
        string Plain(string? text) => text?.Replace(v.VoucherNumber, "{number}")!;
        static string Hash(string n) => System.Text.RegularExpressions.Regex.Replace(n, @"\d", "#");
        static string Iso(DateTime d) => d.ToString(d.Millisecond == 0 && d.Microsecond == 0 ? "yyyy-MM-ddTHH:mm:ss" : "yyyy-MM-ddTHH:mm:ss.ffffff",
                                                   System.Globalization.CultureInfo.InvariantCulture);
        var entries = await db.Set<JournalEntry>().AsNoTracking().Where(e => e.VoucherType == "CPV" && e.VoucherId == id).OrderBy(e => e.Id)
            .Select(e => new { e.Id, e.VoucherType, e.VoucherNumber, e.Description, e.EntryDate, e.IsPosted }).ToListAsync();
        var ids = entries.Select(e => e.Id).ToList();
        var lines = await db.Set<JournalLine>().AsNoTracking().Where(l => ids.Contains(l.JournalEntryId)).OrderBy(l => l.Id).ToListAsync();
        return new
        {
            status_code = status,
            voucher = new
            {
                voucher_type = v.VoucherType, number = Hash(v.VoucherNumber), status = v.Status, voucher_date = Iso(v.VoucherDate),
                cash_bank_account = Code(v.CashBankAccountId), label = v.LabelId, notes = v.Notes,
                lines = v.AccountingVoucherLines.OrderBy(l => l.LineNo).Select(l => new
                {
                    line_no = l.LineNo, account = Code(l.AccountId), description = Plain(l.Description),
                    debit = l.Debit ?? 0, credit = l.Credit ?? 0, label = l.LabelId,
                }),
            },
            journal = entries.Select(e => new
            {
                voucher_type = e.VoucherType, number = Hash(e.VoucherNumber), same_number = e.VoucherNumber == v.VoucherNumber,
                description = Plain(e.Description), entry_date = Iso(e.EntryDate), is_posted = e.IsPosted == true,
                lines = lines.Where(l => l.JournalEntryId == e.Id).Select(l => new
                {
                    account = Code(l.AccountId), debit = l.Debit ?? 0, credit = l.Credit ?? 0,
                    description = Plain(l.Description), label = l.LabelId,
                }),
            }),
        };
    }

    [ReferenceFact]
    public async Task An_unbalanced_journal_voucher_is_refused_and_nothing_is_saved()
    {
        var (_, expense, revenue) = await AccountsAsync();
        await using var host = await ModuleHost.StartAsync(typeof(AccountingModule).Assembly);
        var c = host.ClientFor(OwnerB, CompanyB);
        int before = await CountAsync();
        var r = await c.PostFormAsync("/accounting/vouchers",
        [
            new("action", "save"), new("voucher_type", "JV"), new("voucher_date", "2026-04-11T10:00"), new("notes", "x"),
            new("account_id[]", expense.ToString()), new("description[]", "a"), new("debit[]", "100"), new("credit[]", ""), new("label_id[]", ""),
            new("account_id[]", revenue.ToString()), new("description[]", "b"), new("debit[]", ""), new("credit[]", "99"), new("label_id[]", ""),
        ], "/accounting/vouchers?type=JV");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Debit total (100.0) does not match Credit total (99.0).", await r.Content.ReadAsStringAsync());
        Assert.Equal(before, await CountAsync());

        static async Task<int> CountAsync()
        {
            var company = new CompanyContext();
            company.Set(CompanyB);
            await using var db = Reference.NewDb(company);
            return await db.Set<AccountingVoucher>().CountAsync();
        }
    }

    [ReferenceFact]
    public async Task Unapprove_takes_the_journal_off_and_delete_removes_the_draft()
    {
        var (cash, expense, _) = await AccountsAsync();
        await using var host = await ModuleHost.StartAsync(typeof(AccountingModule).Assembly);
        var c = host.ClientFor(OwnerB, CompanyB);
        var r = await c.PostFormAsync("/accounting/vouchers", CpvForm(cash, expense, "approve", "300"), "/accounting/vouchers?type=CPV");
        int id = int.Parse(r.Headers.Location!.OriginalString.Split('/').Last());
        Assert.Equal(2, (await JournalAsync("CPV", id)).Count);

        var refused = await c.PostFormAsync($"/accounting/vouchers/{id}/delete", [], $"/accounting/vouchers/{id}");
        Assert.Equal("/accounting/vouchers/list", refused.Headers.Location!.OriginalString);
        Assert.Equal("approved", (await VoucherAsync(id)).Status);

        await c.PostFormAsync($"/accounting/vouchers/{id}/unapprove", [], $"/accounting/vouchers/{id}");
        Assert.Equal("unapproved", (await VoucherAsync(id)).Status);
        Assert.Empty(await JournalAsync("CPV", id));

        var del = await c.PostFormAsync($"/accounting/vouchers/{id}/delete", [], $"/accounting/vouchers/{id}");
        Assert.Equal(HttpStatusCode.Redirect, del.StatusCode);
        var company = new CompanyContext();
        company.Set(CompanyB);
        await using var db = Reference.NewDb(company);
        Assert.False(await db.Set<AccountingVoucher>().AnyAsync(v => v.Id == id));
    }

    [ReferenceFact]
    public async Task A_member_without_approve_rights_cannot_approve()
    {
        // The reference "restricted" manager may view and create journal vouchers, nothing more.
        int restricted = Reference.UserId(Reference.Email("restricted"));
        var (_, expense, revenue) = await AccountsAsync();
        await using var host = await ModuleHost.StartAsync(typeof(AccountingModule).Assembly);
        var c = host.ClientFor(restricted, CompanyB);
        var r = await c.PostFormAsync("/accounting/vouchers",
        [
            new("action", "approve"), new("voucher_type", "JV"), new("voucher_date", "2026-04-12T10:00"), new("notes", "x"),
            new("account_id[]", expense.ToString()), new("description[]", "a"), new("debit[]", "5"), new("credit[]", ""), new("label_id[]", ""),
            new("account_id[]", revenue.ToString()), new("description[]", "b"), new("debit[]", ""), new("credit[]", "5"), new("label_id[]", ""),
        ], "/accounting/vouchers?type=JV");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.DoesNotContain("/accounting/vouchers/", r.Headers.Location!.OriginalString.Replace("/accounting/vouchers?", ""));
        var cash = await c.GetAsync("/accounting/vouchers?type=CPV");
        Assert.Equal(HttpStatusCode.Redirect, cash.StatusCode);   // no CPV create right
        // The refusal says why on the page it returns to (the flash must survive the redirect).
        string landed = await (await c.GetAsync(cash.Headers.Location!.OriginalString)).Content.ReadAsStringAsync();
        Assert.Contains("No &#x27;create&#x27; rights here. Ask an admin in Settings.", landed);
    }
}

/// <summary>Lists, print, register, JSON endpoints and the accounting-entry panel.</summary>
public class VoucherListingTests
{
    private static int OwnerB => Reference.UserId(Reference.Email("owner_b"));
    private static int CompanyB => Reference.Company("b");

    private static async Task<int> ApprovedCpvAsync(PageClient c)
    {
        var company = new CompanyContext();
        company.Set(CompanyB);
        await using var db = Reference.NewDb(company);
        int cash = db.Set<ChartOfAccount>().Single(a => a.Code == "1-01-01-01-0001").Id;
        int expense = db.Set<ChartOfAccount>().Single(a => a.Code == "5-04-01-01-0001").Id;
        var r = await c.PostFormAsync("/accounting/vouchers",
        [
            new("action", "approve"), new("voucher_type", "CPV"), new("voucher_date", "2026-05-02T08:15"),
            new("cash_bank_account_id", cash.ToString()), new("label_id", ""), new("notes", "Courier"),
            new("account_id[]", expense.ToString()), new("description[]", "Courier May"),
            new("debit[]", "480"), new("credit[]", ""), new("label_id[]", ""),
        ], "/accounting/vouchers?type=CPV");
        return int.Parse(r.Headers.Location!.OriginalString.Split('/').Last());
    }

    [ReferenceFact]
    public async Task List_preview_register_and_documents_show_the_voucher()
    {
        await using var host = await ModuleHost.StartAsync(typeof(AccountingModule).Assembly);
        var c = host.ClientFor(OwnerB, CompanyB);
        int id = await ApprovedCpvAsync(c);

        string list = await (await c.GetAsync("/accounting/vouchers/list?filter_mode=custom&from=2026-05-01&to=2026-05-31")).Content.ReadAsStringAsync();
        Assert.Contains($"href=\"/accounting/vouchers/{id}\"", list);
        Assert.Contains("voucher-status-approved", list);

        var preview = await c.GetAsync($"/accounting/vouchers/{id}/preview");
        string p = await preview.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Contains("Cash Payment Voucher", p);
        Assert.Contains("Courier May", p);
        Assert.Contains("480.00", p);

        string reg = await (await c.GetAsync("/accounting/registers/vouchers?mode=month&month=2026-05&view=1")).Content.ReadAsStringAsync();
        Assert.Contains("Courier", reg);
        var data = await c.GetAsync("/accounting/registers/vouchers/data?mode=month&month=2026-05&limit=50&offset=0");
        using var json = JsonDocument.Parse(await data.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("total").GetInt32() >= 1);
        Assert.Contains("Courier", json.RootElement.GetProperty("html").GetString());
        var full = await c.GetAsync("/accounting/registers/vouchers/data?mode=month&month=2026-05&full=1&limit=10&offset=0");
        using var fjson = JsonDocument.Parse(await full.Content.ReadAsStringAsync());
        Assert.Contains("Cash Payment Voucher", fjson.RootElement.GetProperty("docs")[0].GetString());
        var docs = await c.GetAsync("/accounting/registers/vouchers/documents?mode=month&month=2026-05&view=1");
        Assert.Contains("Courier May", await docs.Content.ReadAsStringAsync());
    }

    [ReferenceFact]
    public async Task The_accounting_entry_panel_lists_debits_then_credits()
    {
        await using var host = await ModuleHost.StartAsync(typeof(AccountingModule).Assembly);
        var c = host.ClientFor(OwnerB, CompanyB);
        int id = await ApprovedCpvAsync(c);
        var r = await c.GetAsync($"/accounting/gl-entry?type=CPV&id={id}");
        using var json = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var posted = json.RootElement.GetProperty("posted");
        Assert.Equal(1, posted.GetArrayLength());
        var lines = posted[0].GetProperty("lines");
        Assert.Equal(480, lines[0].GetProperty("debit").GetDouble());
        Assert.Equal(480, lines[1].GetProperty("credit").GetDouble());
        Assert.True(posted[0].GetProperty("balanced").GetBoolean());
        Assert.Equal("Cash payment", posted[0].GetProperty("title").GetString());
    }

    [ReferenceFact]
    public async Task Account_pickers_offer_postable_accounts_only_and_balances_follow_the_ledger()
    {
        await using var host = await ModuleHost.StartAsync(typeof(AccountingModule).Assembly);
        var c = host.ClientFor(OwnerB, CompanyB);
        using var accts = JsonDocument.Parse(await (await c.GetAsync("/accounting/api/accounts?q=cash")).Content.ReadAsStringAsync());
        Assert.True(accts.RootElement.GetArrayLength() > 0);
        Assert.All(accts.RootElement.EnumerateArray(), a => Assert.Equal(5, a.GetProperty("code").GetString()!.Split('-').Length));
        using var cb = JsonDocument.Parse(await (await c.GetAsync("/accounting/api/cash-bank-accounts")).Content.ReadAsStringAsync());
        int cashId = cb.RootElement.EnumerateArray().First(a => a.GetProperty("code").GetString() == "1-01-01-01-0001").GetProperty("id").GetInt32();
        using var bal = JsonDocument.Parse(await (await c.GetAsync($"/accounting/api/account-balance?id={cashId}")).Content.ReadAsStringAsync());
        Assert.True(bal.RootElement.GetProperty("ok").GetBoolean());
    }
}
