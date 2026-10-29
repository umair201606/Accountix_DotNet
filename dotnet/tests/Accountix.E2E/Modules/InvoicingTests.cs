using System.Text.Json;
using Accountix.Core.Entities;
using Accountix.E2E.Harness;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Accountix.E2E.Modules;

/// <summary>
/// The invoice editors (Invoicing) in the browser, signed in as the super admin in
/// company 1 "Default" (demo customers, suppliers and products). AGENTS.md's Inventory
/// list for the Purchase Invoice: form, add/clear items, pill toggles, calculations,
/// global inputs (tests/e2e/test_inventory_pipeline.py), the discount scope × method
/// rules (test_discount_modes.py) on both editors, and a sales invoice saved as a draft,
/// reopened and deleted (test_invoice_form_flow.py / test_invoice_form_ux.py).
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class InvoicingTests(FullApp app) : IClassFixture<FullApp>
{
    private const string PurchaseInvoice = "/inventory/purchase-invoice/";
    private const string SalesInvoice = "/inventory/invoices/";
    private const string Sku = "SOL-MONO-450";   // seeded demo product, company 1

    private Task<UiSession> AdminAsync(Viewport vp, [System.Runtime.CompilerServices.CallerMemberName] string test = "") =>
        app.SessionAsync(vp, E2EEnv.SuperAdmin, E2EEnv.DefaultCompany, test: test);

    /// <summary>A new invoice opens on the source gate: take the blank path (select blank, Continue).</summary>
    private static async Task DismissGateAsync(IPage p)
    {
        var gate = p.Locator("#gateBlank");
        if (await gate.CountAsync() > 0 && await gate.IsVisibleAsync())
            await gate.ClickAsync();
        var go = p.Locator("#gateContinue");
        if (await go.CountAsync() > 0 && await go.IsVisibleAsync())
            await go.ClickAsync();
        await Expect(p.Locator("#itemsBody tr").First).ToBeAttachedAsync();
    }

    // ── Purchase invoice (AGENTS.md: form, add/clear items, pill toggles, calculations, global inputs) ──

    [E2EFact("Invoicing")]
    public async Task Purchase_invoice_form_loads_with_supplier_items_and_actions()
    {
        await using var s = await AdminAsync(Viewport.Desktop);
        await s.GoAsync(PurchaseInvoice);
        var p = s.Page;
        await DismissGateAsync(p);
        foreach (string id in new[] { "#supplierSearch", "#itemsBody", "#addLineBtn", "#clearAllBtn", "#saveBtn" })
            await Expect(p.Locator(id)).ToBeVisibleAsync();
        await Expect(p.Locator("#gleFab")).ToHaveCountAsync(1);   // the Accounting-entry button
        await s.AssertFitsAsync();
        await s.AssertCleanAsync();
    }

    [E2EFact("Invoicing")]
    public async Task Purchase_invoice_add_line_adds_a_row()
    {
        await using var s = await AdminAsync(Viewport.Desktop);
        await s.GoAsync(PurchaseInvoice);
        var p = s.Page;
        await DismissGateAsync(p);
        int before = await p.Locator("#itemsBody tr").CountAsync();
        await p.Locator("#addLineBtn").ClickAsync();
        await Expect(p.Locator("#itemsBody tr")).ToHaveCountAsync(before + 1);
        await s.AssertCleanAsync();
    }

    [E2EFact("Invoicing")]
    public async Task Purchase_invoice_clear_lines_asks_then_leaves_one_blank_row()
    {
        await using var s = await AdminAsync(Viewport.Desktop);
        await s.GoAsync(PurchaseInvoice);
        var p = s.Page;
        await DismissGateAsync(p);
        await p.Locator("#addLineBtn").ClickAsync();
        await p.Locator("#addLineBtn").ClickAsync();
        await p.Locator("#itemsBody [data-col='quantity']").First.FillAsync("3");
        await p.Locator("#clearAllBtn").ClickAsync();
        await Expect(p.Locator("#confirmOkBtn")).ToBeVisibleAsync();
        await p.Locator("#confirmOkBtn").ClickAsync();
        await Expect(p.Locator("#itemsBody tr")).ToHaveCountAsync(1);
        // A fresh row: the default quantity again, no product.
        Assert.NotEqual("3", await p.Locator("#itemsBody [data-col='quantity']").First.InputValueAsync());
        Assert.Equal("", await p.Locator("#itemsBody tr").First.GetAttributeAsync("data-product-id") ?? "");
        Assert.Empty(s.Dialogs);   // the confirm is in the page, not the browser's
        await s.AssertCleanAsync();
    }

    [E2EFact("Invoicing")]
    public async Task Purchase_invoice_scope_pills_toggle_in_the_settings_panel()
    {
        await using var s = await AdminAsync(Viewport.Desktop);
        await s.GoAsync(PurchaseInvoice);
        var p = s.Page;
        await DismissGateAsync(p);
        // The scope pills live in the Additional Settings slide-over, which starts closed.
        await p.Locator(".bsm-settings").First.ClickAsync();
        var pills = p.Locator("#discountMode .pill-b");
        await Expect(pills.First).ToBeVisibleAsync();
        int n = await pills.CountAsync();
        Assert.True(n >= 2, $"{n} discount scope pills");
        for (int i = n - 1; i >= 0; i--)
        {
            var pill = pills.Nth(i);
            if (!await pill.IsEnabledAsync())
                continue;
            await pill.ClickAsync();
            await Expect(pill).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bactive\b"));
            // Exactly one pill of the group is active.
            Assert.Equal(1, await p.Locator("#discountMode .pill-b.active").CountAsync());
        }
        await s.AssertCleanAsync();
    }

    [E2EFact("Invoicing")]
    public async Task Purchase_invoice_summary_calculates_from_the_lines()
    {
        await using var s = await AdminAsync(Viewport.Desktop);
        await s.GoAsync(PurchaseInvoice);
        var p = s.Page;
        await DismissGateAsync(p);
        await p.Locator("#itemsBody [data-col='quantity']").First.FillAsync("10");
        await p.Locator("#itemsBody [data-col='unit_price']").First.FillAsync("100");
        await Expect(p.Locator("#summarySubtotal")).ToBeVisibleAsync();
        await Expect(p.Locator("#summarySubtotal")).ToHaveTextAsync(new System.Text.RegularExpressions.Regex(@"^\D*1,000(\.00)?$"));
        await Expect(p.Locator("#summaryNetPayable")).ToHaveTextAsync(new System.Text.RegularExpressions.Regex(@"1,000(\.00)?"));
        await s.AssertCleanAsync();
    }

    [E2EFact("Invoicing")]
    public async Task Purchase_invoice_global_discount_reaches_the_net_payable()
    {
        await using var s = await AdminAsync(Viewport.Desktop);
        await s.GoAsync(PurchaseInvoice);
        var p = s.Page;
        await DismissGateAsync(p);
        await p.Locator("#itemsBody [data-col='quantity']").First.FillAsync("10");
        await p.Locator("#itemsBody [data-col='unit_price']").First.FillAsync("100");
        // The combined discount by percentage (the default); its box sits in the summary.
        await p.EvaluateAsync("() => { const g = document.getElementById('discountMode'); const b = g && g.querySelector('.pill-b[data-value=\"general\"]'); if (b && !b.disabled) b.click(); document.getElementById('settingsDiscountMethod').value = 'pct'; applyDiscountUI(); }");
        var pct = p.Locator("#globalDiscPct");
        await Expect(pct).ToBeVisibleAsync();
        await pct.FillAsync("5");
        await pct.DispatchEventAsync("input");
        await Expect(p.Locator("#summaryNetPayable")).ToHaveTextAsync(new System.Text.RegularExpressions.Regex(@"950(\.00)?"));
        await s.AssertCleanAsync();
    }

    [E2ETheory("Invoicing")]
    [InlineData("375")]
    [InlineData("320")]
    public async Task Purchase_invoice_form_works_on_a_phone(string viewport)
    {
        await using var s = await AdminAsync(Viewport.Named(viewport));
        await s.GoAsync(PurchaseInvoice);
        var p = s.Page;
        await DismissGateAsync(p);
        await Expect(p.Locator("#supplierSearch")).ToBeVisibleAsync();
        await Expect(p.Locator(".tb-w")).ToBeVisibleAsync();
        await p.Locator("#addLineBtn").ClickAsync();
        await s.AssertFitsAsync();
        await s.AssertCleanAsync();
    }

    // ── discount scope × method (test_discount_modes.py), both editors ──

    private const string Setup = """
        ([scope, method]) => {
          const grp = document.getElementById('discountMode');
          const btn = grp && grp.querySelector('.pill-b[data-value="' + scope + '"]');
          if (btn && !btn.disabled) btn.click();
          document.getElementById('settingsDiscountMethod').value = method;
          applyDiscountUI();
          document.getElementById('itemsBody').innerHTML = '';
          rowCount = 0;
          addRow({quantity: 2, unit_price: 1000});
          document.querySelectorAll('#itemsBody tr').forEach(tr => recalcRow(tr));
          recalc();
        }
        """;

    private const string Read = """
        () => {
          const vis = el => !!(el && el.offsetParent !== null);
          const row = document.querySelector('#itemsBody tr');
          return { pctBox: vis(document.getElementById('globalDiscPct')), valBox: vis(document.getElementById('globalDiscVal')),
                   discount: document.getElementById('summaryDiscount').textContent,
                   net: row.querySelector('[data-col="net_amount"]').value };
        }
        """;

    private static double Num(string text) =>
        double.Parse(new string(text.Where(c => char.IsDigit(c) || c == '.').ToArray()), System.Globalization.CultureInfo.InvariantCulture);

    [E2ETheory("Invoicing")]
    [InlineData(SalesInvoice)]
    [InlineData(PurchaseInvoice)]
    public async Task Combined_discount_by_percentage_or_amount_offers_one_box_and_uses_it(string editor)
    {
        await using var s = await AdminAsync(Viewport.Desktop, "discount_" + (editor == SalesInvoice ? "si" : "pi"));
        await s.GoAsync(editor);
        var p = s.Page;
        await DismissGateAsync(p);

        await p.EvaluateAsync(Setup, new[] { "general", "pct" });
        var r = await p.EvaluateAsync<JsonElement>(Read);
        Assert.True(r.GetProperty("pctBox").GetBoolean());
        Assert.False(r.GetProperty("valBox").GetBoolean(), "the amount box must not be offered as well");
        await p.EvaluateAsync("() => { document.getElementById('globalDiscPct').value = 10; recalc(); }");
        Assert.Equal(200.0, Num((await p.EvaluateAsync<JsonElement>(Read)).GetProperty("discount").GetString()!));   // 10% of 2,000

        await p.EvaluateAsync(Setup, new[] { "general", "amount" });
        r = await p.EvaluateAsync<JsonElement>(Read);
        Assert.True(r.GetProperty("valBox").GetBoolean());
        Assert.False(r.GetProperty("pctBox").GetBoolean());
        // A stale percentage in the hidden box cannot hijack an amount discount.
        await p.EvaluateAsync("() => { document.getElementById('globalDiscPct').value = 10; document.getElementById('globalDiscVal').value = 250; recalc(); }");
        Assert.Equal(250.0, Num((await p.EvaluateAsync<JsonElement>(Read)).GetProperty("discount").GetString()!));
        await s.AssertCleanAsync();
    }

    // ── sales invoice: a draft through the real UI ──

    [E2EFact("Invoicing")]
    public async Task Sales_invoice_draft_is_saved_with_ctrl_s_reopened_and_deleted()
    {
        await using var s = await AdminAsync(Viewport.Desktop);
        s.AcceptDialogs = true;
        await s.GoAsync(SalesInvoice);
        var p = s.Page;
        await DismissGateAsync(p);
        // Nothing posted yet: the Accounting-entry panel says so.
        await p.Locator("#gleFab").ClickAsync();
        await Expect(p.Locator("#glePanel")).ToBeVisibleAsync();
        await Expect(p.Locator("#gleBody")).ToContainTextAsync("Nothing posted yet");
        await p.Keyboard.PressAsync("Escape");
        await Expect(p.Locator("#glePanel")).ToBeHiddenAsync();

        await SeedLineAsync(p);
        await Expect(p.Locator("#summarySubtotal")).ToHaveTextAsync(new System.Text.RegularExpressions.Regex(@"360(\.00)?"));

        // Ctrl+S saves the draft (JSON POST with the antiforgery header) and keeps the form open.
        await p.Keyboard.PressAsync("Control+s");
        await p.WaitForFunctionAsync("() => document.getElementById('invoiceId').value !== ''");
        await p.WaitForFunctionAsync("() => window.formDirty === false");
        s.AssertNoProblems("after Ctrl+S");
        int id = int.Parse(await p.Locator("#invoiceId").InputValueAsync());
        using (var db = Db.For(E2EEnv.DefaultCompany))
        {
            var inv = db.Set<InvInvoice>().Single(i => i.Id == id);
            Assert.NotEqual("approved", inv.VoucherStatus);
        }

        // Reopened, it carries the line.
        await s.GoAsync($"{SalesInvoice}{id}");
        Assert.Equal("2", (await p.Locator("#itemsBody [data-col='quantity']").First.InputValueAsync()).TrimEnd('0').TrimEnd('.'));
        await s.AssertFitsAsync();

        // Delete the draft (the editor's JSON delete, with the antiforgery header).
        Assert.Equal(200, await PostAsync(p, $"/inventory/invoices/delete/{id}"));
        using (var db = Db.For(E2EEnv.DefaultCompany))
            Assert.False(db.Set<InvInvoice>().Any(i => i.Id == id));
        await s.AssertCleanAsync();
    }

    /// <summary>A billable line through the UI: the first "Solar" customer, the product by its code, qty 2 at 180.</summary>
    private static async Task SeedLineAsync(IPage p)
    {
        await p.Locator("#customerSearch").PressSequentiallyAsync("Solar");
        await p.Locator("#customerDropdown .ac-it").First.ClickAsync();
        await p.Locator("#itemsBody [data-col='code']").First.FillAsync(Sku);
        await p.WaitForFunctionAsync("() => document.querySelector('#itemsBody tr').dataset.productId !== ''");
        await p.Locator("#itemsBody [data-col='quantity']").First.FillAsync("2");
        await p.Locator("#itemsBody [data-col='unit_price']").First.FillAsync("180");
    }

    private static Task<int> PostAsync(IPage p, string url) =>
        p.EvaluateAsync<int>("async (url) => (await fetch(url, { method: 'POST', headers: { Accept: 'application/json' } })).status", url);

    /// <summary>
    /// Two people on one draft (tests/e2e/test_edit_conflicts_ui.py): a tab holding an older copy
    /// is told, in the form, that someone else changed it, and its save overwrites nothing; the
    /// same tab saving twice in a row is never refused (static/js/edit_version.js).
    /// </summary>
    [E2EFact("Invoicing")]
    public async Task A_stale_tab_is_told_and_overwrites_nothing()
    {
        await using var s = await AdminAsync(Viewport.Desktop);
        var a = s.Page;
        await s.GoAsync(SalesInvoice);
        await DismissGateAsync(a);
        await SeedLineAsync(a);
        await SaveDraftAsync(a);
        await Qty(a).FillAsync("2");
        await SaveDraftAsync(a);   // the same tab saving again is fine
        string id = await a.Locator("#invoiceId").InputValueAsync();

        var b = await s.Context.NewPageAsync();   // a colleague opens the same draft
        await b.GotoAsync($"{SalesInvoice}{id}");
        await b.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Qty(b).FillAsync("7");
        await SaveDraftAsync(b);
        await b.CloseAsync();

        s.ExpectedConsoleErrors.Add("status of 409");
        await Qty(a).FillAsync("3");   // A still holds the copy before B's save
        await a.Keyboard.PressAsync("Control+s");
        await a.WaitForSelectorAsync("#toastContainer >> text=after you opened it", new() { Timeout = 10_000 });
        Assert.True(await a.EvaluateAsync<bool>("() => window.formDirty === true"), "A's edit must stay unsaved");
        // Leaving with an unsaved edit asks first (beforeunload); leave anyway.
        s.AcceptDialogs = true;
        await a.ReloadAsync();
        await a.WaitForLoadStateAsync(LoadState.NetworkIdle);
        Assert.Contains(await Qty(a).InputValueAsync(), new[] { "7", "7.0", "7.00", "7.0000" });
        Assert.Equal(200, await PostAsync(a, $"/inventory/invoices/delete/{id}"));
        await s.AssertCleanAsync();
    }

    private static ILocator Qty(IPage p) => p.Locator("#itemsBody [data-col='quantity']").First;

    private static async Task SaveDraftAsync(IPage p)
    {
        await p.Keyboard.PressAsync("Control+s");
        await p.WaitForFunctionAsync("() => document.getElementById('invoiceId').value !== ''");
        await p.WaitForFunctionAsync("() => window.formDirty === false");
    }

    /// <summary>The Accounting-entry button is on every posting form (tests/e2e/test_gl_entry.py).</summary>
    [E2ETheory("Invoicing", "Inventory")]
    [InlineData("/inventory/purchase-invoice/")]
    [InlineData("/inventory/invoices/")]
    [InlineData("/accounting/vouchers")]
    [InlineData("/inventory/vouchers/consumption")]
    [InlineData("/inventory/vouchers/scrap")]
    [InlineData("/inventory/vouchers/adjustment")]
    public async Task The_accounting_entry_button_is_on_every_posting_form(string path)
    {
        await using var s = await AdminAsync(Viewport.Desktop, "gle");
        await s.GoAsync(path);
        await Expect(s.Page.Locator("#gleFab")).ToHaveCountAsync(1);
        await s.AssertCleanAsync();
    }

    /// <summary>
    /// Save &amp; approve posts the sale (stock moves, so it needs Inventory's costing); the
    /// Accounting-entry panel then lists the journal, balanced, debits before credits,
    /// each account linking to the general ledger (tests/e2e/test_gl_entry.py UI part).
    /// </summary>
    [E2EFact("Invoicing", "Inventory")]
    public async Task Sales_invoice_save_and_approve_posts_and_the_panel_shows_the_journal()
    {
        await using var s = await AdminAsync(Viewport.Desktop);
        await s.GoAsync(SalesInvoice);
        var p = s.Page;
        await DismissGateAsync(p);
        await SeedLineAsync(p);
        await p.Locator("#saveApproveBtn").ClickAsync();
        await p.Locator("#confirmOkBtn").ClickAsync();
        await p.WaitForSelectorAsync(".ap-b");
        await s.SettleAsync();
        await p.WaitForFunctionAsync("() => document.getElementById('gleFab').dataset.state === 'posted'");
        await p.Locator("#gleFab").ClickAsync();
        await p.WaitForSelectorAsync("#gleBody .gle-tbl");
        string text = await p.Locator("#gleBody").InnerTextAsync();
        Assert.Contains("Balanced", text);
        Assert.Contains("Sales invoice", text);
        Assert.True(await p.Locator("#gleBody .gle-dr").CountAsync() >= 1);
        Assert.True(await p.Locator("#gleBody .gle-cr").CountAsync() >= 1);
        string? href = await p.Locator("#gleBody .gle-acc a").First.GetAttributeAsync("href");
        Assert.StartsWith("/finance/ledger?", href);
        Assert.Contains("account_ids=", href);
        int id = int.Parse(await p.Locator("#invoiceId").InputValueAsync());
        Assert.Equal(200, await PostAsync(p, $"/inventory/invoices/unapprove/{id}"));
        Assert.Equal(200, await PostAsync(p, $"/inventory/invoices/delete/{id}"));
        await s.AssertCleanAsync();
    }
}
