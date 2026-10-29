using System.Text.RegularExpressions;
using Accountix.E2E.Harness;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Accountix.E2E.Accounting;

/// <summary>Driving the voucher form the way a person does (keyboard pickers, buttons, the in-page confirm).</summary>
public static partial class VoucherUi
{
    /// <summary>
    /// An account picker: focus, type to search (or Space for the full list), take the
    /// first suggestion with ArrowDown + Enter (tests/e2e/test_voucher_v3_and_sidebar.py _pick).
    /// </summary>
    public static async Task PickAsync(IPage page, ILocator input, string? search)
    {
        await input.FocusAsync();
        if (search is null)
            await page.Keyboard.PressAsync(" ");
        else
            await input.PressSequentiallyAsync(search);
        await Expect(page.Locator(".ac-dd.show .ac-it").First).ToBeVisibleAsync();
        await page.Keyboard.PressAsync("ArrowDown");
        await page.Keyboard.PressAsync("Enter");
        await Expect(page.Locator(".ac-dd.show")).ToHaveCountAsync(0);
    }

    public static ILocator Rows(IPage page) => page.Locator("#entriesBody tr");

    public static ILocator Row(IPage page, int i) => Rows(page).Nth(i);

    /// <summary>Pick a line's account by name and give it an amount (debit or credit for a JV, the amount for cash/bank).</summary>
    public static async Task FillLineAsync(IPage page, int row, string account, string? debit = null, string? credit = null,
                                           string? amount = null, string? description = null)
    {
        var tr = Row(page, row);
        await PickAsync(page, tr.Locator(".acct-input"), account);
        Assert.NotEqual("", await tr.Locator("[name='account_id[]']").InputValueAsync());
        if (description is not null)
            await tr.Locator(".desc-input").FillAsync(description);
        if (amount is not null)
            await tr.Locator(".amt-input").FillAsync(amount);
        if (debit is not null)
            await tr.Locator(".dr-input").FillAsync(debit);
        if (credit is not null)
            await tr.Locator(".cr-input").FillAsync(credit);
    }

    /// <summary>Clicks something that submits a form and waits for the next page to load.</summary>
    public static async Task SubmitAndWaitAsync(UiSession s, ILocator button, string urlPattern)
    {
        string before = s.Page.Url;
        await button.ClickAsync();
        await s.Page.WaitForURLAsync(new Regex(urlPattern), new PageWaitForURLOptions { WaitUntil = WaitUntilState.Load });
        await s.SettleAsync();
        s.AssertNoProblems($"after {before.Replace(s.Server.BaseUrl, "")}");
        await s.AssertFitsAsync();
    }

    /// <summary>The in-page confirm (#vConfirm): its message, then OK.</summary>
    public static async Task<string> ConfirmAsync(IPage page)
    {
        await Expect(page.Locator("#vConfirm")).ToBeVisibleAsync();
        string msg = await page.Locator("#vConfirmMsg").InnerTextAsync();
        await page.Locator("#vConfirmOk").ClickAsync();
        return msg;
    }

    /// <summary>
    /// The in-page confirm for a button that submits a form: its message, then OK, then the
    /// form's POST must be accepted (a redirect) and the next page load cleanly.
    /// </summary>
    public static async Task<string> ConfirmPostAsync(UiSession s)
    {
        var p = s.Page;
        await Expect(p.Locator("#vConfirm")).ToBeVisibleAsync();
        string msg = await p.Locator("#vConfirmMsg").InnerTextAsync();
        var answer = p.WaitForResponseAsync(r => r.Request.Method == "POST" && r.Request.ResourceType == "document");
        await p.Locator("#vConfirmOk").ClickAsync();
        var r = await answer;
        Assert.True(r.Status is >= 300 and < 400,
            $"{r.Request.Method} {r.Url.Replace(s.Server.BaseUrl, "")} answered {r.Status}, not a redirect" + (r.Status == 400 ? " (antiforgery token missing?)" : ""));
        await p.WaitForURLAsync(u => u == r.Headers.GetValueOrDefault("location") || !u.Contains("/unapprove") && !u.Contains("/delete") && !u.EndsWith("/approve"));
        await s.SettleAsync();
        s.AssertNoProblems("after " + r.Url.Replace(s.Server.BaseUrl, ""));
        await s.AssertFitsAsync();
        return msg;
    }

    /// <summary>The voucher id in a /accounting/vouchers/&lt;id&gt; URL.</summary>
    public static int IdFrom(string url) => int.Parse(VoucherIdInUrl().Match(url).Groups[1].Value);

    [GeneratedRegex(@"/accounting/vouchers/(\d+)")]
    private static partial Regex VoucherIdInUrl();

    /// <summary>Saves a voucher straight through the form route (data for tests that do not test saving).</summary>
    public static async Task<int> SaveDraftViaHttpAsync(AppFixture app, string type, string notes, decimal amount,
                                                        int cash, int account)
    {
        var c = await app.HttpAsync(E2EEnv.OwnerB, E2EEnv.CompanyB);
        var fields = new List<KeyValuePair<string, string>>
        {
            new("action", "save"), new("voucher_type", type), new("voucher_date", AccountingSeed.FormDate(AccountingSeed.CompanyNow)),
            new("cash_bank_account_id", cash.ToString()), new("label_id", ""), new("notes", notes),
            new("account_id[]", account.ToString()), new("description[]", notes),
            new(type is "CRV" or "BRV" ? "credit[]" : "debit[]", amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(type is "CRV" or "BRV" ? "debit[]" : "credit[]", ""), new("label_id[]", ""),
        };
        var r = await c.PostFormAsync("/accounting/vouchers", fields, $"/accounting/vouchers?type={type}");
        Assert.Equal(System.Net.HttpStatusCode.Redirect, r.StatusCode);
        return IdFrom(r.Headers.Location!.OriginalString);
    }
}
