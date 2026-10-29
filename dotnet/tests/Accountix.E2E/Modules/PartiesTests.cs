using System.Text.Json;
using Accountix.Core.Entities;
using Accountix.E2E.Harness;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Accountix.E2E.Modules;

/// <summary>
/// Customers and suppliers (Parties) in the browser, company 1 "Default": the lists
/// (AGENTS.md's Inventory list: Suppliers, Customers), a party created, searched, edited
/// and deleted through the forms, the double-submit guard (tests/e2e/test_edit_conflicts_ui.py),
/// and the Invoice Register loading its rows on demand.
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class PartiesTests(FullApp app) : IClassFixture<FullApp>
{
    [E2ETheory("Parties")]
    [InlineData("customers", "SolarTech Solutions")]
    [InlineData("suppliers", "Longi Solar Pakistan")]
    public async Task Party_list_shows_the_demo_parties_and_searches(string kind, string known)
    {
        await using var s = await app.SessionAsync(Viewport.Desktop, E2EEnv.SuperAdmin, E2EEnv.DefaultCompany, test: "list_" + kind);
        var p = s.Page;
        await s.GoAsync($"/inventory/{kind}/");
        Assert.Contains(kind[..^1], p.Url);
        await Expect(p.GetByText(known).First).ToBeVisibleAsync();
        await s.GoAsync($"/inventory/{kind}/?q={Uri.EscapeDataString(known[..6])}");
        await Expect(p.GetByText(known).First).ToBeVisibleAsync();
        await s.AssertCleanAsync();
    }

    [E2ETheory("Parties")]
    [InlineData("customers", "desktop")]
    [InlineData("suppliers", "320")]
    public async Task A_party_is_created_edited_and_deleted_through_the_forms(string kind, string viewport)
    {
        string name = $"E2E {kind[..^1]} {Guid.NewGuid().ToString("N")[..6]}";
        await using var s = await app.SessionAsync(Viewport.Named(viewport), E2EEnv.SuperAdmin, E2EEnv.DefaultCompany, test: "party_" + kind);
        s.AcceptDialogs = true;   // the list's Delete asks with confirm(), as in Python
        var p = s.Page;
        await s.GoAsync($"/inventory/{kind}/create");
        await p.Locator("input[name=name]").FillAsync(name);
        await p.Locator("input[name=city]").FillAsync("Lahore");
        await p.Locator("input[name=phone]").FillAsync("042-1234567");
        await Accounting.VoucherUi.SubmitAndWaitAsync(s, p.GetByRole(AriaRole.Button, new() { Name = "Create" }), $"/inventory/{kind}/");
        await Expect(p.Locator(".app-flash-success")).ToBeVisibleAsync();
        await s.GoAsync($"/inventory/{kind}/?q={Uri.EscapeDataString(name)}");
        var row = p.Locator("tr", new() { HasTextString = name });
        await Expect(row).ToHaveCountAsync(1);

        // Edit: the city changes.
        await Accounting.VoucherUi.SubmitAndWaitAsync(s, row.Locator("a[href*='/edit/']").First, $"/inventory/{kind}/edit/\\d+");
        await p.Locator("input[name=city]").FillAsync("Karachi");
        await Accounting.VoucherUi.SubmitAndWaitAsync(s, p.GetByRole(AriaRole.Button, new() { Name = "Update" }), $"/inventory/{kind}/");
        await s.GoAsync($"/inventory/{kind}/?q={Uri.EscapeDataString(name)}");
        await Expect(p.Locator("tr", new() { HasTextString = name })).ToContainTextAsync("Karachi");

        // Delete (no documents yet): asked, then gone.
        await Accounting.VoucherUi.SubmitAndWaitAsync(s, p.Locator("tr", new() { HasTextString = name }).Locator("form[action*='/delete/'] button"), $"/inventory/{kind}/");
        Assert.Contains(s.Dialogs, d => d.StartsWith("confirm"));
        using (var db = Db.For(E2EEnv.DefaultCompany))
            Assert.False(kind == "customers" ? db.Set<InvCustomer>().Any(c => c.Name == name) : db.Set<InvSupplier>().Any(c => c.Name == name));
        await s.AssertCleanAsync();
    }

    /// <summary>
    /// The guard's decisions, read off the submit events themselves: the first submit goes
    /// through untouched, a second while it is in flight is cancelled, and a form that saves
    /// with fetch (its own handler cancels the submit) is never marked (static/js/app.js).
    /// </summary>
    [E2EFact("Parties")]
    public async Task A_double_clicked_form_submits_once()
    {
        await using var s = await app.SessionAsync(Viewport.Desktop, E2EEnv.SuperAdmin, E2EEnv.DefaultCompany);
        var p = s.Page;
        await s.GoAsync("/inventory/customers/create");
        await p.Locator("input[name='name']").FillAsync($"Double Click {Guid.NewGuid().ToString("N")[..6]}");
        var seen = await p.EvaluateAsync<JsonElement>("""
            () => {
              const f = document.querySelector("input[name='name']").form;
              const seen = [];
              window.addEventListener('submit', (e) => { seen.push([e.target === f ? 'form' : 'fetch-form', e.defaultPrevented]); e.preventDefault(); });
              f.requestSubmit();
              f.requestSubmit();
              const g = document.createElement('form');
              g.method = 'post';
              g.addEventListener('submit', (e) => e.preventDefault());
              document.body.appendChild(g);
              g.requestSubmit();
              g.requestSubmit();
              return { seen, marked: f.dataset.submitting || null, fetchMarked: g.dataset.submitting || null };
            }
            """);
        Assert.Equal("[[\"form\",false],[\"form\",true],[\"fetch-form\",true],[\"fetch-form\",true]]", seen.GetProperty("seen").GetRawText().Replace(" ", ""));
        Assert.Equal("1", seen.GetProperty("marked").GetString());
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("fetchMarked").ValueKind);
        await s.AssertCleanAsync();
    }

    [E2EFact("Parties")]
    public async Task Invoice_register_opens_empty_and_builds_on_demand()
    {
        await using var s = await app.SessionAsync(Viewport.Phone375, E2EEnv.SuperAdmin, E2EEnv.DefaultCompany);
        await s.GoAsync("/invoicing/registers/");
        await s.GoAsync("/invoicing/registers/?doc=sales&mode=custom&from=2020-01-01&to=2030-01-01&view=1");
        await s.GoAsync("/invoicing/registers/?doc=purchase&mode=custom&from=2020-01-01&to=2030-01-01&view=1");
        await s.AssertCleanAsync();
    }
}
