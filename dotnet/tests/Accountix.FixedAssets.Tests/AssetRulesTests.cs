using System.Net;
using Accountix.Core.Entities;
using Accountix.FixedAssets.Services;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.FixedAssets.Tests;

/// <summary>
/// Fixed assets through the .NET routes: what an auditor would try
/// (tests/e2e/test_fixed_asset_rules.py). Pure fixed-asset steps run in company B
/// and end with Books Integrity (Executive's port of shared/integrity.py); steps that
/// move stock run in company A against the stock stand-in.
/// </summary>
public class AssetRulesTests
{
    private static int B => Fa.CompanyB;

    internal static List<KeyValuePair<string, string>> AssetForm(int category, string name, params (string Key, string Value)[] over)
    {
        var d = new Dictionary<string, string>
        {
            ["name"] = name, ["category_id"] = category.ToString(), ["purchase_date"] = "2024-01-01",
            ["purchase_cost"] = "1200000", ["useful_life"] = "5", ["salvage_value"] = "0", ["depreciation_method"] = "straight_line",
        };
        foreach (var (k, v) in over) d[k] = v;
        return d.ToList();
    }

    internal static Task<HttpResponseMessage> CreateAsync(PageClient c, int category, string name, params (string, string)[] over) =>
        Fa.PostAsync(c, "/fixed-assets/assets/create", AssetForm(category, name, over), "/fixed-assets/assets/create");

    private static Task<string> EditAsync(PageClient c, FixedAsset a, int category, params (string Key, string Value)[] over)
    {
        var d = new Dictionary<string, string>
        {
            ["name"] = a.Name, ["category_id"] = category.ToString(), ["purchase_date"] = "2024-01-01", ["purchase_cost"] = "1200000",
            ["useful_life"] = a.UsefulLife.ToString(), ["salvage_value"] = "0", ["depreciation_method"] = "straight_line",
        };
        foreach (var (k, v) in over) d[k] = v;
        return Fa.PostAndFollowAsync(c, $"/fixed-assets/assets/{a.Id}/edit", d.ToList(), $"/fixed-assets/assets/{a.Id}/edit");
    }

    private static async Task<AssetCalc> CalcAsync(int company, int assetId)
    {
        await using var db = Fa.Db(company);
        var a = await db.Set<FixedAsset>().AsNoTracking().SingleAsync(x => x.Id == assetId);
        var live = await db.Set<AssetDepreciation>().Where(d => d.AssetId == assetId
                && (d.JournalEntryId == null || db.Set<JournalEntry>().Any(e => e.Id == d.JournalEntryId && e.IsPosted == true)))
            .ToListAsync();
        return new AssetCalc(a, live.Count == 0 ? LiveCharges.None
            : new LiveCharges(live.Sum(x => x.Amount), live.Max(x => x.EntryDate), live.Min(x => x.EntryDate), live.Count));
    }

    [ReferenceFact]
    public async Task Create_refuses_nonsense_terms()
    {
        int cat = await Fa.CategoryAsync(B);
        await using var host = await Fa.HostAsync();
        var c = host.ClientFor(Fa.OwnerB, B);
        string tag = Fa.Tag();
        Assert.Contains("less than the cost", await Fa.FollowAsync(c, await CreateAsync(c, cat, $"Bad A {tag}", ("salvage_value", "1200000"))));
        Assert.Contains("more than zero", await Fa.FollowAsync(c, await CreateAsync(c, cat, $"Bad B {tag}", ("purchase_cost", "0"))));
        Assert.Contains("between 1 and 100", await Fa.FollowAsync(c, await CreateAsync(c, cat, $"Bad C {tag}", ("useful_life", "0"))));
        Assert.Contains("Choose a category.", await Fa.FollowAsync(c, await CreateAsync(c, cat, $"Bad D {tag}", ("category_id", ""))));
        Assert.Contains("Purchase date is required.", await Fa.FollowAsync(c, await CreateAsync(c, cat, $"Bad E {tag}", ("purchase_date", ""))));
        var noName = await CreateAsync(c, cat, "  ");
        Assert.Equal(HttpStatusCode.OK, noName.StatusCode);
        Assert.Contains("Asset name is required.", await noName.Content.ReadAsStringAsync());
        foreach (string n in new[] { "A", "B", "C", "D", "E" })
            Assert.Null(await Fa.FindAsync(B, $"Bad {n} {tag}"));
    }

    [ReferenceFact]
    public async Task A_depreciation_run_posts_the_year_and_the_page_plans_the_rest()
    {
        int cat = await Fa.CategoryAsync(B);
        await using var host = await Fa.HostAsync();
        var c = host.ClientFor(Fa.OwnerB, B);
        string name = $"Delivery Van {Fa.Tag()}";
        var created = await CreateAsync(c, cat, name);
        Assert.Equal("/fixed-assets/assets/", Fa.Location(created));
        var a = await Fa.AssetAsync(B, name);
        Assert.StartsWith("FA-", a.AssetCode);
        Assert.Equal(1, await Fa.PostedEntriesAsync(B, "FA-ACQ", a.Id));

        string page = await (await c.GetAsync("/fixed-assets/depreciation/?as_of=2025-01-01")).Content.ReadAsStringAsync();
        Assert.Contains(name, page);
        Assert.Contains("240,000", page);
        var r = await Fa.PostAsync(c, "/fixed-assets/depreciation/post", Fa.Form(("asset_ids[]", a.Id.ToString()), ("entry_date", "2025-01-01")),
                                   "/fixed-assets/depreciation/?as_of=2025-01-01");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(240_000m, await Fa.PostedAsync(B, a.Id));
        string view = await (await c.GetAsync($"/fixed-assets/assets/{a.Id}")).Content.ReadAsStringAsync();
        Assert.Contains("Planned depreciation", view);
        Assert.Contains("id=\"gleFab\"", view);
        Assert.Contains($"FA-DISP:{a.Id}", view);
        Assert.Equal("[]", await Fa.IntegrityAsync(B));

        // A change of life is prospective: 960,000 left over the 8 years left.
        await EditAsync(c, a, cat, ("useful_life", "9"));
        Assert.Equal(960_000.0 / 8, (await CalcAsync(B, a.Id)).DueDepreciation(new DateOnly(2026, 1, 1)), 0.01);

        // Edit guards.
        a = await Fa.AssetAsync(B, name);
        Assert.Contains("cannot be below", await EditAsync(c, a, cat, ("purchase_cost", "200000")));
        Assert.Contains("cannot be later", await EditAsync(c, a, cat, ("purchase_date", "2025-06-01")));
        await EditAsync(c, a, cat, ("status", "disposed"));
        Assert.Equal("active", (await Fa.AssetAsync(B, name)).Status);
        // Restating the cost moves the acquisition journal with it.
        await EditAsync(c, a, cat, ("purchase_cost", "1300000"));
        a = await Fa.AssetAsync(B, name);
        Assert.Equal(1_300_000m, a.PurchaseCost);
        Assert.Equal(1, await Fa.PostedEntriesAsync(B, "FA-ACQ", a.Id));
        Assert.Equal(1_300_000m, await Fa.AssetBalanceAsync(B, a.FixedAssetAccountId!.Value, a.Id));
        Assert.Equal("[]", await Fa.IntegrityAsync(B));

        // A disposal cannot predate posted depreciation.
        Assert.Contains("Depreciation is posted after that date",
            await Fa.PostAndFollowAsync(c, $"/fixed-assets/assets/{a.Id}/dispose", Fa.Form(("disposal_date", "2024-06-30"), ("proceeds", "0")),
                                        $"/fixed-assets/assets/{a.Id}"));
        Assert.Equal("active", (await Fa.AssetAsync(B, name)).Status);
        var sold = await Fa.PostAsync(c, $"/fixed-assets/assets/{a.Id}/dispose", Fa.Form(("disposal_date", "2025-03-31"), ("proceeds", "900000")),
                                      $"/fixed-assets/assets/{a.Id}");
        Assert.Equal("/fixed-assets/assets/", Fa.Location(sold));
        a = await Fa.AssetAsync(B, name);
        Assert.Equal("disposed", a.Status);
        Assert.Equal(0m, await Fa.AssetBalanceAsync(B, a.FixedAssetAccountId!.Value, a.Id));
        Assert.Equal(0m, await Fa.AssetBalanceAsync(B, a.AccumDepAccountId!.Value, a.Id));
        // Out of service: manual charges and cost edits are refused.
        Assert.Contains("in service", await Fa.PostAndFollowAsync(c, $"/fixed-assets/assets/{a.Id}/depreciate",
            Fa.Form(("entry_date", "2025-04-30"), ("amount", "1000")), $"/fixed-assets/assets/{a.Id}"));
        await EditAsync(c, a, cat, ("purchase_cost", "5"));
        Assert.Equal(1_300_000m, (await Fa.AssetAsync(B, name)).PurchaseCost);
        Assert.Equal("[]", await Fa.IntegrityAsync(B));

        // Undo the disposal: cost and depreciation come back together.
        await Fa.PostAsync(c, $"/fixed-assets/assets/{a.Id}/undo-disposal", [], $"/fixed-assets/assets/{a.Id}");
        a = await Fa.AssetAsync(B, name);
        Assert.Equal("active", a.Status);
        Assert.Equal(1_300_000m, await Fa.AssetBalanceAsync(B, a.FixedAssetAccountId!.Value, a.Id));
        Assert.Equal(-await Fa.PostedAsync(B, a.Id), await Fa.AssetBalanceAsync(B, a.AccumDepAccountId!.Value, a.Id));
        Assert.Equal("[]", await Fa.IntegrityAsync(B));
    }

    [ReferenceFact]
    public async Task Manual_charges_reversals_and_impairment()
    {
        int cat = await Fa.CategoryAsync(B);
        await using var host = await Fa.HostAsync();
        var c = host.ClientFor(Fa.OwnerB, B);
        string name = $"Lathe {Fa.Tag()}";
        await CreateAsync(c, cat, name, ("purchase_cost", "1000000"), ("salvage_value", "100000"));
        var a = await Fa.AssetAsync(B, name);
        string view = $"/fixed-assets/assets/{a.Id}";

        Assert.Contains("must be dated on or after", await Fa.PostAndFollowAsync(c, $"{view}/depreciate",
            Fa.Form(("entry_date", "2023-12-31"), ("amount", "100")), view));
        Assert.Contains("must be positive", await Fa.PostAndFollowAsync(c, $"{view}/depreciate",
            Fa.Form(("entry_date", "2024-06-30"), ("amount", "0")), view));
        Assert.Contains("Only 900,000.00 remains depreciable before the salvage value of 100,000.00.",
            await Fa.PostAndFollowAsync(c, $"{view}/depreciate", Fa.Form(("entry_date", "2024-06-30"), ("amount", "950000")), view));
        Assert.Contains("Depreciation of 25,000.00 recorded", await Fa.PostAndFollowAsync(c, $"{view}/depreciate",
            Fa.Form(("entry_date", "2024-06-30"), ("amount", "25000"), ("kind", "depreciation"), ("notes", "catch-up")), view));
        Assert.Contains("Depreciation of 50,000.00 recorded", await Fa.PostAndFollowAsync(c, $"{view}/depreciate",
            Fa.Form(("entry_date", "2024-07-31"), ("amount", "50000"), ("kind", "impairment"), ("notes", "flood")), view));
        Assert.Equal(75_000m, await Fa.PostedAsync(B, a.Id));

        await using (var db = Fa.Db(B))
        {
            var rows = await db.Set<AssetDepreciation>().Where(d => d.AssetId == a.Id).OrderBy(d => d.Id).ToListAsync();
            Assert.Equal(["catch-up", "Impairment: flood"], rows.Select(r => r.Notes));
            var imp = await db.Set<JournalLine>().Include(l => l.Account).Where(l => l.JournalEntryId == rows[1].JournalEntryId && l.Debit > 0).SingleAsync();
            Assert.Equal("Impairment Loss on Fixed Assets", imp.Account.Name);
            Assert.Equal(75_000m, rows[1].AccumulatedAfter);
            Assert.Equal(925_000m, rows[1].NetBookValueAfter);
            var entry = await db.Set<JournalEntry>().SingleAsync(e => e.Id == rows[1].JournalEntryId);
            Assert.Equal($"FA-DEP-{a.AssetCode}-{rows[1].Id}", entry.VoucherNumber);
            Assert.Equal($"Impairment: flood - {name}", entry.Description);

            // Reverse the latest: the period it covered becomes chargeable again, the GL follows.
            var latest = rows[1];
            var r = await Fa.PostAsync(c, $"/fixed-assets/depreciation/entry/{latest.Id}/reverse", [], view);
            Assert.Contains("Depreciation of 50,000.00 reversed; book value is now 975,000.00.", await Fa.FollowAsync(c, r));
            Assert.Contains("already reversed", await Fa.PostAndFollowAsync(c, $"/fixed-assets/depreciation/entry/{latest.Id}/reverse", [], view));
        }
        Assert.Equal(25_000m, await Fa.PostedAsync(B, a.Id));
        a = await Fa.AssetAsync(B, name);
        Assert.Equal(25_000m, a.AccumulatedDepreciation);
        Assert.Equal(975_000m, a.CurrentBookValue);
        Assert.Equal(-25_000m, await Fa.AssetBalanceAsync(B, a.AccumDepAccountId!.Value, a.Id));
        Assert.Contains("class=\"text-muted\"", await (await c.GetAsync(view)).Content.ReadAsStringAsync());
    }

    [ReferenceFact]
    public async Task Draft_capitalisation_is_not_in_service_until_approved()
    {
        FakeStock.Instance.UnitCost = 250_000m;
        int A = Fa.CompanyA;
        int cat = await Fa.CategoryAsync(A);
        int product = await ProductAsync(A);
        await using var host = await Fa.HostAsync();
        var c = host.ClientFor(Fa.OwnerA, A);
        string name = $"Standby Generators {Fa.Tag()}";
        var r = await Fa.PostAsync(c, "/fixed-assets/transfers/capitalise", Fa.Form(
            ("source_product_id", product.ToString()), ("quantity", "2"), ("name", name), ("category_id", cat.ToString()),
            ("useful_life", "10"), ("depreciation_method", "straight_line"), ("salvage_value", "0"), ("transfer_date", "2024-07-01"),
            ("status", "unapproved")), "/fixed-assets/transfers/capitalise");
        Assert.Equal("/fixed-assets/transfers/", Fa.Location(r));
        var a = await Fa.AssetAsync(A, name);
        Assert.Equal("draft", a.Status);
        Assert.False(a.IsActive);
        Assert.Equal(0m, a.PurchaseCost);
        Assert.DoesNotContain(name, await (await c.GetAsync("/fixed-assets/depreciation/?as_of=2025-07-01")).Content.ReadAsStringAsync());
        AssetTransfer t;
        await using (var db = Fa.Db(A))
            t = await db.Set<AssetTransfer>().SingleAsync(x => x.AssetId == a.Id);
        Assert.Equal(2m, t.Quantity);
        Assert.StartsWith("FA-CAP-", t.VoucherNumber);
        // Raised from Fixed Assets, so the Inventory list shows it too.
        Assert.Contains(t.VoucherNumber, await (await c.GetAsync("/inventory/transfer-to-fa/")).Content.ReadAsStringAsync());

        // Approving later uses the stored quantity and the costing engine's cost.
        var ok = await Fa.PostAsync(c, $"/fixed-assets/transfers/{t.Id}/approve-capitalisation", [], "/fixed-assets/transfers/");
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        a = await Fa.AssetAsync(A, name);
        Assert.Equal("active", a.Status);
        Assert.Equal(500_000m, a.PurchaseCost);
        Assert.Equal(new DateOnly(2024, 7, 1), a.PurchaseDate);
        Assert.Contains(FakeStock.Instance.Calls, x => x.StartsWith($"out FA-CAP:{t.Id} p{product} 2"));
        Assert.Equal(500_000m, await Fa.AssetBalanceAsync(A, a.FixedAssetAccountId!.Value, a.Id));
        Assert.Equal(1, await Fa.PostedEntriesAsync(A, "FA-CAP", t.Id));
        Assert.Contains("Not a draft capitalisation.",
            await Fa.PostAndFollowAsync(c, $"/fixed-assets/transfers/{t.Id}/approve-capitalisation", [], "/fixed-assets/transfers/"));

        // State changes are POST only.
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await c.GetAsync($"/fixed-assets/transfers/{t.Id}/unapprove-capitalisation")).StatusCode);
        await Fa.PostAsync(c, $"/fixed-assets/transfers/{t.Id}/unapprove-capitalisation", [], "/fixed-assets/transfers/");
        a = await Fa.AssetAsync(A, name);
        Assert.Equal("draft", a.Status);
        Assert.Equal(0, await Fa.PostedEntriesAsync(A, "FA-CAP", t.Id));
        Assert.Contains($"reverse FA-CAP:{t.Id}", FakeStock.Instance.Calls);
        await Fa.PostAsync(c, $"/fixed-assets/transfers/{t.Id}/delete", [], "/fixed-assets/transfers/");
        Assert.Null(await Fa.FindAsync(A, name));
        await using (var db = Fa.Db(A))
            Assert.False(await db.Set<AssetTransfer>().AnyAsync(x => x.Id == t.Id));
    }

    [ReferenceFact]
    public async Task Inventory_side_draft_has_no_typed_cost_and_is_approved_there()
    {
        FakeStock.Instance.UnitCost = 250_000m;
        int A = Fa.CompanyA;
        int cat = await Fa.CategoryAsync(A);
        int product = await ProductAsync(A);
        await using var host = await Fa.HostAsync();
        var c = host.ClientFor(Fa.OwnerA, A);
        string name = $"Workshop Generator {Fa.Tag()}";
        var r = await Fa.PostAsync(c, "/inventory/transfer-to-fa/create", Fa.Form(
            ("product_id", product.ToString()), ("name", name), ("category_id", cat.ToString()), ("purchase_date", "2024-08-01"),
            ("quantity", "1"), ("useful_life", "8"), ("purchase_cost", "999999"), ("salvage_value", "0"), ("status", "unapproved")),
            "/inventory/transfer-to-fa/create");
        Assert.Equal("/inventory/transfer-to-fa/", Fa.Location(r));
        var a = await Fa.AssetAsync(A, name);
        Assert.Equal("draft", a.Status);
        Assert.Equal(0m, a.PurchaseCost);
        Assert.Equal(8, a.UsefulLife);
        Assert.Contains(name, await (await c.GetAsync("/inventory/transfer-to-fa/")).Content.ReadAsStringAsync());
        AssetTransfer t;
        await using (var db = Fa.Db(A))
            t = await db.Set<AssetTransfer>().SingleAsync(x => x.AssetId == a.Id);
        Assert.StartsWith("INV-FA-", t.VoucherNumber);
        Assert.Equal($"Transfer from inventory: {name}", t.Description);
        var edit = await c.GetAsync($"/inventory/transfer-to-fa/{t.Id}/edit");
        Assert.Contains("id=\"product-select\"", await edit.Content.ReadAsStringAsync());

        // Approve from the inventory form: the product select is disabled, the voucher's own product is used.
        await Fa.PostAsync(c, $"/inventory/transfer-to-fa/{t.Id}/edit", Fa.Form(("status", "approved"), ("quantity", "1"), ("description", "")),
                           $"/inventory/transfer-to-fa/{t.Id}/edit");
        a = await Fa.AssetAsync(A, name);
        Assert.Equal("active", a.Status);
        Assert.Equal(250_000m, a.PurchaseCost);
        Assert.Contains("depreciation charges first.", await NoChargesThenUnapproveAsync());
        Assert.Equal("draft", (await Fa.AssetAsync(A, name)).Status);
        // It carried a (reversed) charge, so deleting the draft keeps the asset for the audit trail, cancelled.
        await Fa.PostAsync(c, $"/inventory/transfer-to-fa/{t.Id}/delete", [], "/inventory/transfer-to-fa/");
        Assert.Equal("cancelled", (await Fa.AssetAsync(A, name)).Status);
        await using (var db = Fa.Db(A))
            Assert.False(await db.Set<AssetTransfer>().AnyAsync(x => x.Id == t.Id));

        async Task<string> NoChargesThenUnapproveAsync()
        {
            // A live charge blocks the unapproval; once reversed it goes through.
            string view = $"/fixed-assets/assets/{a.Id}";
            string charged = await Fa.PostAndFollowAsync(c, $"{view}/depreciate", Fa.Form(("entry_date", "2024-09-30"), ("amount", "1000")), view);
            Assert.Contains("Depreciation of 1,000.00 recorded", charged);
            string refused = await Fa.PostAndFollowAsync(c, $"/inventory/transfer-to-fa/{t.Id}/unapprove", [], "/inventory/transfer-to-fa/");
            await using var db = Fa.Db(A);
            int dep = await db.Set<AssetDepreciation>().Where(d => d.AssetId == a.Id).Select(d => d.Id).SingleAsync();
            await Fa.PostAsync(c, $"/fixed-assets/depreciation/entry/{dep}/reverse", [], view);
            await Fa.PostAsync(c, $"/inventory/transfer-to-fa/{t.Id}/unapprove", [], "/inventory/transfer-to-fa/");
            return refused;
        }
    }

    [ReferenceFact]
    public async Task Asset_to_stock_needs_a_stock_item_and_posts_at_book_value()
    {
        int A = Fa.CompanyA;
        int cat = await Fa.CategoryAsync(A);
        int product = await ProductAsync(A);
        await using var host = await Fa.HostAsync();
        var c = host.ClientFor(Fa.OwnerA, A);
        string name = $"Old Compressor {Fa.Tag()}";
        await CreateAsync(c, cat, name, ("purchase_cost", "300000"));
        var a = await Fa.AssetAsync(A, name);
        string form = $"/fixed-assets/transfers/create?asset_id={a.Id}";
        var refused = await Fa.PostAsync(c, form, Fa.Form(("transfer_date", "2024-02-01"), ("status", "approved"), ("description", "")), form);
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Contains("Pick the stock item", await refused.Content.ReadAsStringAsync());
        Assert.Equal("active", (await Fa.AssetAsync(A, name)).Status);

        var ok = await Fa.PostAsync(c, form, Fa.Form(("product_id", product.ToString()), ("transfer_date", "2025-01-01"),
                                                     ("status", "approved"), ("description", "")), form);
        Assert.Equal("/fixed-assets/transfers/", Fa.Location(ok));
        a = await Fa.AssetAsync(A, name);
        Assert.Equal("transferred", a.Status);
        AssetTransfer t;
        await using (var db = Fa.Db(A))
            t = await db.Set<AssetTransfer>().SingleAsync(x => x.AssetId == a.Id);
        // A year of catch-up depreciation (60,000) first, then one unit at the 240,000 left.
        Assert.Equal(60_000m, await Fa.PostedAsync(A, a.Id));
        Assert.Equal(240_000m, t.TransferAmount);
        Assert.Contains(FakeStock.Instance.Calls, x => x.StartsWith($"in FA-TRF:{t.Id} p{product} 1@240000"));
        Assert.Equal(0m, await Fa.AssetBalanceAsync(A, a.FixedAssetAccountId!.Value, a.Id));
        Assert.Equal(0m, await Fa.AssetBalanceAsync(A, a.AccumDepAccountId!.Value, a.Id));
        Assert.Contains("Cannot edit an approved transfer.", await Fa.FollowAsync(c, await c.GetAsync($"/fixed-assets/transfers/{t.Id}/edit")));

        await Fa.PostAsync(c, $"/fixed-assets/transfers/{t.Id}/unapprove", [], "/fixed-assets/transfers/");
        a = await Fa.AssetAsync(A, name);
        Assert.Equal("active", a.Status);
        Assert.Equal(300_000m, await Fa.AssetBalanceAsync(A, a.FixedAssetAccountId!.Value, a.Id));
        Assert.Contains($"reverse FA-TRF:{t.Id}", FakeStock.Instance.Calls);
        // A draft asset → stock voucher opens in its form; deleting it leaves the asset alone.
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/fixed-assets/transfers/{t.Id}/edit")).StatusCode);
        await Fa.PostAsync(c, $"/fixed-assets/transfers/{t.Id}/delete", [], "/fixed-assets/transfers/");
        Assert.NotNull(await Fa.FindAsync(A, name));
    }

    [ReferenceFact]
    public async Task The_movement_schedule_rolls_forward_and_exports()
    {
        int cat = await Fa.CategoryAsync(B, "Machinery & Equipment");
        await using var host = await Fa.HostAsync();
        var c = host.ClientFor(Fa.OwnerB, B);
        var from = new DateOnly(2024, 1, 1);
        var to = new DateOnly(2024, 12, 31);
        async Task<MovementRow> MachineryAsync()
        {
            await using var scope = Fa.Db(B);
            var m = await new Movement(scope, new AssetBooks(scope, null!, null!)).BuildAsync(from, to);
            var t = m.Total;
            Assert.Equal(t.CostOpen + t.Additions - t.CostDisposals, t.CostClose, 0.005);
            Assert.Equal(t.DepOpen + t.Charge - t.DepDisposals, t.DepClose, 0.01);
            return m.Rows.SingleOrDefault(r => r.Category == "Machinery & Equipment") ?? new MovementRow();
        }
        var before = await MachineryAsync();
        string tag = Fa.Tag();
        await CreateAsync(c, cat, $"Held {tag}", ("purchase_date", "2023-01-01"), ("purchase_cost", "1000000"), ("salvage_value", "100000"));
        await CreateAsync(c, cat, $"Added {tag}", ("purchase_date", "2024-03-01"), ("purchase_cost", "1000000"));
        await CreateAsync(c, cat, $"Sold {tag}", ("purchase_date", "2022-01-01"), ("purchase_cost", "1000000"));
        var held = await Fa.AssetAsync(B, $"Held {tag}");
        var added = await Fa.AssetAsync(B, $"Added {tag}");
        var sold = await Fa.AssetAsync(B, $"Sold {tag}");
        foreach (var (id, date) in new[] { (held.Id, "2024-01-01"), (held.Id, "2025-01-01"), (added.Id, "2024-09-01"), (sold.Id, "2024-06-01") })
            await Fa.PostAsync(c, "/fixed-assets/depreciation/post", Fa.Form(("asset_ids[]", id.ToString()), ("entry_date", date)),
                               "/fixed-assets/depreciation/");
        await Fa.PostAsync(c, $"/fixed-assets/assets/{sold.Id}/dispose", Fa.Form(("disposal_date", "2024-06-01"), ("proceeds", "0")),
                           $"/fixed-assets/assets/{sold.Id}");
        Assert.Equal("disposed", (await Fa.AssetAsync(B, $"Sold {tag}")).Status);
        var after = await MachineryAsync();
        Assert.Equal(2_000_000, after.CostOpen - before.CostOpen, 0.005);        // held + sold
        Assert.Equal(1_000_000, after.Additions - before.Additions, 0.005);
        Assert.Equal(1_000_000, after.CostDisposals - before.CostDisposals, 0.005);
        Assert.Equal(2_000_000, after.CostClose - before.CostClose, 0.005);      // held + added
        double heldDep = (double)await Fa.PostedAsync(B, held.Id), addedDep = (double)await Fa.PostedAsync(B, added.Id);
        Assert.Equal(heldDep - 180_000 + addedDep, after.DepClose - before.DepClose, 0.01);

        var page = await c.GetAsync("/fixed-assets/reports/movement?from=2024-01-01&to=2024-12-31");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        string html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Fixed Asset Movement Schedule", html);
        Assert.Contains("01 Jan 2024 to 31 Dec 2024", html);
        var x = await c.GetAsync("/fixed-assets/reports/movement?from=2024-01-01&to=2024-12-31&format=excel");
        Assert.Equal(HttpStatusCode.OK, x.StatusCode);
        Assert.Equal("PK"u8.ToArray(), await x.Content.ReadAsByteArrayAsync());
        var (fmt, table) = FakeExport.Instance.Last!.Value;
        Assert.Equal("excel", fmt);
        Assert.Equal("Fixed Asset Movement Schedule", table.Title);
        Assert.Equal("01 Jan 2024 to 31 Dec 2024", table.Period);
        Assert.Equal("2024-12-31", table.FilePeriod);
        Assert.Equal("grand", table.Rows[^1].Style);
        Assert.Equal(11, table.Columns.Count);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/fixed-assets/reports/movement?format=docx")).StatusCode);
    }

    /// <summary>A stock item in the company (the stand-in issues it at FakeStock.UnitCost), made when missing.</summary>
    internal static async Task<int> ProductAsync(int company, string sku = "FA-TEST-GEN", string name = "Generator 10kVA")
    {
        await using var db = Fa.Db(company);
        var p = await db.Set<InvProduct>().FirstOrDefaultAsync(x => x.Sku == sku);
        if (p is null)
        {
            p = new InvProduct
            {
                Sku = sku, Name = name, UnitPrice = 0m, CostPrice = 0m, CurrentStock = 0m, ReorderLevel = 0,
                Unit = "pcs", HsCode = "", Weight = 0m, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.Add(p);
            await db.SaveChangesAsync();
        }
        return p.Id;
    }
}
