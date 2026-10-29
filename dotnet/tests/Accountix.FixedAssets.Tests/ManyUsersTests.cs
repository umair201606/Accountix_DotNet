using System.Net;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.FixedAssets.Tests;

/// <summary>
/// Several people pressing the same button at the same moment
/// (tests/concurrency/test_concurrent_users.py): each change happens once,
/// numbers stay distinct, and nobody gets a server error.
/// </summary>
public class ManyUsersTests
{
    private const int N = 6;
    private static int B => Fa.CompanyB;

    private static async Task<FixedAsset> NewAssetAsync(PageClient c, int company, string name)
    {
        int cat = await Fa.CategoryAsync(company);
        var r = await AssetRulesTests.CreateAsync(c, cat, name);
        Assert.Equal("/fixed-assets/assets/", Fa.Location(r));
        return await Fa.AssetAsync(company, name);
    }

    [ReferenceFact]
    public async Task Six_depreciation_runs_at_once_charge_each_month_once()
    {
        await using var host = await Fa.HostAsync();
        var a = await NewAssetAsync(host.ClientFor(Fa.OwnerB, B), B, $"Shared Van {Fa.Tag()}");
        var clients = host.ClientsFor(Fa.OwnerB, B, N);
        var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync("/fixed-assets/depreciation/")));
        var rs = await Race.RunAsync(N, i => clients[i].PostFormWithTokenAsync("/fixed-assets/depreciation/post",
            Fa.Form(("asset_ids[]", a.Id.ToString()), ("entry_date", "2025-01-01")), tokens[i]));
        Race.NoServerErrors(rs);
        Assert.Equal(240_000m, await Fa.PostedAsync(B, a.Id));       // 12 months, once
        await using var db = Fa.Db(B);
        Assert.Equal(1, await db.Set<AssetDepreciation>().CountAsync(d => d.AssetId == a.Id));
        var asset = await db.Set<FixedAsset>().SingleAsync(x => x.Id == a.Id);
        Assert.Equal(-240_000m, await Fa.AssetBalanceAsync(B, asset.AccumDepAccountId!.Value, asset.Id));
    }

    [ReferenceFact]
    public async Task Six_disposals_of_one_asset_dispose_it_once()
    {
        await using var host = await Fa.HostAsync();
        var a = await NewAssetAsync(host.ClientFor(Fa.OwnerB, B), B, $"Shared Crane {Fa.Tag()}");
        string view = $"/fixed-assets/assets/{a.Id}";
        var clients = host.ClientsFor(Fa.OwnerB, B, N);
        var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync(view)));
        var rs = await Race.RunAsync(N, i => clients[i].PostFormWithTokenAsync($"{view}/dispose",
            Fa.Form(("disposal_date", "2025-06-30"), ("proceeds", "500000")), tokens[i]));
        Race.NoServerErrors(rs);
        Assert.Equal(1, await Fa.PostedEntriesAsync(B, "FA-DISP", a.Id));
        await using var db = Fa.Db(B);
        Assert.Equal(1, await db.Set<AssetDepreciation>().CountAsync(d => d.AssetId == a.Id));   // the catch-up to the disposal date, once
        var asset = await db.Set<FixedAsset>().SingleAsync(x => x.Id == a.Id);
        Assert.Equal("disposed", asset.Status);
        Assert.Equal(0m, await Fa.AssetBalanceAsync(B, asset.FixedAssetAccountId!.Value, asset.Id));
        Assert.Equal(0m, await Fa.AssetBalanceAsync(B, asset.AccumDepAccountId!.Value, asset.Id));
    }

    [ReferenceFact]
    public async Task Six_approvals_of_one_capitalisation_capitalise_it_once()
    {
        FakeStock.Instance.UnitCost = 250_000m;
        int A = Fa.CompanyA;
        int cat = await Fa.CategoryAsync(A);
        int product = await AssetRulesTests.ProductAsync(A);
        await using var host = await Fa.HostAsync();
        var c0 = host.ClientFor(Fa.OwnerA, A);
        string name = $"Shared Generator {Fa.Tag()}";
        await Fa.PostAsync(c0, "/fixed-assets/transfers/capitalise", Fa.Form(("source_product_id", product.ToString()), ("quantity", "1"),
            ("name", name), ("category_id", cat.ToString()), ("salvage_value", "0"), ("transfer_date", "2024-05-01"), ("status", "unapproved")),
            "/fixed-assets/transfers/capitalise");
        var a = await Fa.AssetAsync(A, name);
        int tid;
        await using (var db = Fa.Db(A))
            tid = await db.Set<AssetTransfer>().Where(t => t.AssetId == a.Id).Select(t => t.Id).SingleAsync();
        var clients = host.ClientsFor(Fa.OwnerA, A, N);
        var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync("/fixed-assets/transfers/")));
        var rs = await Race.RunAsync(N, i => clients[i].PostFormWithTokenAsync($"/fixed-assets/transfers/{tid}/approve-capitalisation", [], tokens[i]));
        Race.NoServerErrors(rs);
        Assert.Equal(1, await Fa.PostedEntriesAsync(A, "FA-CAP", tid));
        Assert.Single(FakeStock.Instance.Calls, x => x.StartsWith($"out FA-CAP:{tid} "));
        var asset = await Fa.AssetAsync(A, name);
        Assert.Equal(("active", 250_000m), (asset.Status, asset.PurchaseCost));
        Assert.Equal(250_000m, await Fa.AssetBalanceAsync(A, asset.FixedAssetAccountId!.Value, asset.Id));
    }

    [ReferenceFact]
    public async Task Six_assets_created_at_once_get_six_codes()
    {
        int cat = await Fa.CategoryAsync(B);
        await using var host = await Fa.HostAsync();
        var clients = host.ClientsFor(Fa.OwnerB, B, N);
        var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync("/fixed-assets/assets/create")));
        string tag = Fa.Tag();
        var rs = await Race.RunAsync(N, i => clients[i].PostFormWithTokenAsync("/fixed-assets/assets/create",
            AssetRulesTests.AssetForm(cat, $"Race Code {tag} {i}"), tokens[i]));
        Race.NoServerErrors(rs);
        Assert.All(rs, r => Assert.Equal("/fixed-assets/assets/", Fa.Location(r)));
        await using var db = Fa.Db(B);
        var codes = await db.Set<FixedAsset>().Where(a => a.Name.StartsWith($"Race Code {tag} ")).Select(a => a.AssetCode).ToListAsync();
        Assert.Equal(N, codes.Count);
        Assert.Equal(N, codes.Distinct().Count());
        var seq = codes.Select(x => int.Parse(x[3..])).Order().ToList();
        Assert.Equal(N - 1, seq[^1] - seq[0]);           // queued per company: consecutive
    }
}
