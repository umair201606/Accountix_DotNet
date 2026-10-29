using System.Globalization;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.FixedAssets.Tests;

/// <summary>
/// An asset booked through the .NET routes posts exactly the FA-ACQ / FA-DEP / FA-DISP / FA-CAP /
/// FA-TRF journals, depreciation rows, vouchers and cached balances the Python app posted for the
/// same steps, to the paisa (fixed expectations captured from the Python app: Golden/*.json, the
/// Python that produced them in Golden/capture/).
/// </summary>
public class JournalParityTests
{
    private static string Inv(FormattableString s) => FormattableString.Invariant(s);

    private static async Task<List<string>> DepRowsAsync(int company, int assetId, string code)
    {
        await using var db = Fa.Db(company);
        var rows = await db.Set<AssetDepreciation>().AsNoTracking().Where(d => d.AssetId == assetId).OrderBy(d => d.Id).ToListAsync();
        var output = new List<string>();
        foreach (var d in rows)
        {
            bool live = d.JournalEntryId is not int j || await db.Set<JournalEntry>().AnyAsync(e => e.Id == j && e.IsPosted == true);
            output.Add(Inv($"{d.EntryDate:yyyy-MM-dd}|{d.Amount:0.000000}|{d.AccumulatedAfter:0.000000}|{d.NetBookValueAfter:0.000000}|{d.Notes}|{live}"));
            output.AddRange(await Fa.JournalAsync(company, "FA-DEP", d.Id, assetId, code));
        }
        return output;
    }

    private static async Task<string> AssetRowAsync(int company, int assetId)
    {
        await using var db = Fa.Db(company);
        var a = await db.Set<FixedAsset>().AsNoTracking().SingleAsync(x => x.Id == assetId);
        string entity = $"-{assetId + 100:D4}";
        async Task<string> Code(int? id) => id is int i
            ? (await db.Set<ChartOfAccount>().Where(x => x.Id == i).Select(x => x.Code).SingleAsync()).Replace(entity, "-{A}") : "-";
        return Inv($"{a.Name}|{a.Status}|{a.IsActive}|{a.PurchaseDate:yyyy-MM-dd}|{a.PurchaseCost:0.000000}|{a.SalvageValue:0.000000}|") +
               Inv($"{a.AccumulatedDepreciation:0.000000}|{a.CurrentBookValue:0.000000}|{a.UsefulLife}|{a.DepreciationMethod}|") +
               $"{await Code(a.FixedAssetAccountId)}|{await Code(a.AccumDepAccountId)}|{await Code(a.DepExpenseAccountId)}|" +
               $"{await Code(a.AcquisitionCreditAccountId)}|{a.Location}|{a.Notes}|{a.Description}|{a.SerialNumber}";
    }

    private static async Task<(string Code, string Name)> CodeAndNameAsync(int company, int assetId)
    {
        await using var db = Fa.Db(company);
        var a = await db.Set<FixedAsset>().AsNoTracking().Where(x => x.Id == assetId).Select(x => new { x.AssetCode, x.Name }).SingleAsync();
        return (a.AssetCode, a.Name);
    }

    private static List<string> Named(IEnumerable<string> lines, params (string Text, string Role)[] roles) =>
        lines.Select(l => roles.Aggregate(l, (s, r) => s.Replace(r.Text, r.Role))).ToList();

    /// <summary>
    /// An asset's row, depreciation rows and FA-ACQ / FA-DEP / FA-DISP journals, normalised: its code → {CODE},
    /// its name → {NAME}, its own accounts → {A}, voucher ids → {V} (dates are the test's fixed dates).
    /// </summary>
    internal sealed record LifeAnswer(string asset, List<string> acq, List<string> depreciation, List<string> disposal);

    internal static async Task<LifeAnswer> LifeAnswerAsync(int company, int assetId)
    {
        var (code, name) = await CodeAndNameAsync(company, assetId);
        (string, string)[] roles = [(name, "{NAME}")];
        return new LifeAnswer(
            Named([await AssetRowAsync(company, assetId)], roles)[0],
            Named(await Fa.JournalAsync(company, "FA-ACQ", assetId, assetId, code), roles),
            Named(await DepRowsAsync(company, assetId, code), roles),
            Named(await Fa.JournalAsync(company, "FA-DISP", assetId, assetId, code), roles));
    }

    private static readonly string[] RunDates =
    [
        "2024-02-29", "2024-03-31", "2024-04-30", "2024-05-31", "2024-06-30", "2024-07-31", "2024-08-31",
        "2024-09-30", "2024-10-31", "2024-11-30", "2024-12-31", "2025-01-31", "2025-02-28", "2025-03-31",
    ];

    [ReferenceFact]
    public async Task An_asset_through_its_life_posts_what_the_python_app_posts()
    {
        int B = Fa.CompanyB;
        int cat = await Fa.CategoryAsync(B, "Vehicles");
        string name = $"Parity Truck {Fa.Tag()}";
        var form = new Dictionary<string, string>
        {
            ["name"] = name, ["category_id"] = cat.ToString(), ["purchase_date"] = "2024-01-15", ["purchase_cost"] = "1234567.89",
            ["useful_life"] = "7", ["salvage_value"] = "12345.67", ["depreciation_method"] = "declining_balance",
            ["location"] = "Lahore", ["serial_number"] = "TR-77", ["description"] = "Isuzu", ["notes"] = "n", ["label_id"] = "",
            ["fixed_asset_account_id"] = "", ["accum_dep_account_id"] = "", ["dep_expense_account_id"] = "", ["acquisition_credit_account_id"] = "",
        };

        await using var host = await Fa.HostAsync();
        var c = host.ClientFor(Fa.OwnerB, B);
        await Fa.PostAsync(c, "/fixed-assets/assets/create", form.ToList(), "/fixed-assets/assets/create");
        int netId;
        await using (var db = Fa.Db(B))
            netId = await db.Set<FixedAsset>().Where(a => a.Name == name).Select(a => a.Id).SingleAsync();
        string view = $"/fixed-assets/assets/{netId}";
        foreach (string d in RunDates)
            await Fa.PostAsync(c, "/fixed-assets/depreciation/post", Fa.Form(("asset_ids[]", netId.ToString()), ("entry_date", d)), "/fixed-assets/depreciation/");
        await Fa.PostAsync(c, $"{view}/depreciate", Fa.Form(("entry_date", "2025-04-30"), ("amount", "33333.33"), ("kind", "impairment"), ("notes", "storm")), view);
        await Fa.PostAsync(c, $"{view}/depreciate", Fa.Form(("entry_date", "2025-04-30"), ("amount", "1111.11"), ("kind", "depreciation"), ("notes", "")), view);
        int lastNet;
        await using (var db = Fa.Db(B))
            lastNet = await db.Set<AssetDepreciation>().Where(x => x.AssetId == netId).MaxAsync(x => x.Id);
        await Fa.PostAsync(c, $"/fixed-assets/depreciation/entry/{lastNet}/reverse", [], view);
        await Fa.PostAsync(c, "/fixed-assets/depreciation/post", Fa.Form(("asset_ids[]", netId.ToString()), ("entry_date", "2025-06-30")), "/fixed-assets/depreciation/");
        await Fa.PostAsync(c, $"{view}/dispose", Fa.Form(("disposal_date", "2025-07-31"), ("proceeds", "750000.55"), ("proceeds_account_id", "")), view);


        var answer = await LifeAnswerAsync(B, netId);
        Golden.Match("AssetLifeJournals", answer);
        Assert.Equal(3, answer.acq.Count);
        Assert.True(answer.depreciation.Count > 40);
        Assert.Contains(answer.disposal, l => l.StartsWith("L|5-04-01-01-0002|"));      // gain or loss on disposal
        Assert.Equal("[]", await Fa.IntegrityAsync(B));
    }

    /// <summary>
    /// The capitalisation and transfer-to-stock vouchers of an asset, its row, FA-CAP / FA-DEP / FA-TRF journals and
    /// depreciation rows, normalised: code → {CODE}, name → {NAME}, the stock item → {P} (id and its inventory
    /// account) / {PRODUCT} (name), the asset's own accounts → {A}, voucher ids → {V}.
    /// </summary>
    internal sealed record CapAnswer(string capitalisation, string transfer, string asset, List<string> cap_journal,
                                     List<string> transfer_journal, List<string> depreciation);

    internal static async Task<CapAnswer> CapAnswerAsync(int company, int assetId, int productId, string productName)
    {
        var (code, name) = await CodeAndNameAsync(company, assetId);
        // The stock item's own inventory account (its entity account under 1-01-04-01) → {P}.
        (string, string)[] roles = [(name, "{NAME}"), (productName, "{PRODUCT}"), ($"|1-01-04-01-{productId + 100:D4}|", "|1-01-04-01-{P}|")];
        List<AssetTransfer> ts;
        await using (var db = Fa.Db(company))
            ts = await db.Set<AssetTransfer>().AsNoTracking().Where(t => t.AssetId == assetId).OrderBy(t => t.Id).ToListAsync();
        var cap = ts.Single(t => t.Direction == "to_fixed_asset");
        var trf = ts.Single(t => t.Direction == "to_inventory");
        string P(int? id) => id is null ? "" : id == productId ? "{P}" : "other";
        string Shape(AssetTransfer t) => Inv($"{t.Direction}|{t.Status}|{P(t.ProductId)}|{P(t.SourceProductId)}|{t.Quantity:0.000000}|{t.TransferAmount:0.000000}|") +
                                         Inv($"{t.Description}|{t.TransferDate:yyyy-MM-dd}|{t.NewProductName}|{t.VoucherNumber[..t.VoucherNumber.LastIndexOf('-')]}");
        return new CapAnswer(
            Named([Shape(cap)], roles)[0],
            Named([Shape(trf)], roles)[0],
            Named([await AssetRowAsync(company, assetId)], roles)[0],
            Named(await Fa.JournalAsync(company, "FA-CAP", cap.Id, assetId, code), roles),
            Named(await Fa.JournalAsync(company, "FA-TRF", trf.Id, assetId, code), roles),
            Named(await DepRowsAsync(company, assetId, code), roles));
    }

    [ReferenceFact]
    public async Task Capitalisation_and_transfer_to_stock_post_what_the_python_app_posts()
    {
        int A = Fa.CompanyA;
        int cat = await Fa.CategoryAsync(A, "Machinery & Equipment");
        string tag = Fa.Tag();
        // A stock item; the stand-in issues it at 125,000.50 a unit (Python's costing engine received it at that cost).
        string productName = $"Parity Gen {tag}";
        int product = await AssetRulesTests.ProductAsync(A, $"PG-{tag}", productName);
        FakeStock.Instance.UnitCost = 125_000.50m;
        string name = $"Parity Press {tag}";
        var capForm = new Dictionary<string, string>
        {
            ["source_product_id"] = product.ToString(), ["quantity"] = "2", ["name"] = name, ["category_id"] = cat.ToString(),
            ["useful_life"] = "6", ["depreciation_method"] = "straight_line", ["salvage_value"] = "1000", ["location"] = "Plant",
            ["description"] = "Press line", ["transfer_date"] = "2024-03-10", ["status"] = "approved",
        };
        var trfForm = new Dictionary<string, string>
        {
            ["product_id"] = product.ToString(), ["new_product_name"] = "", ["description"] = "", ["transfer_date"] = "2025-02-15",
            ["status"] = "approved",
        };

        await using var host = await Fa.HostAsync();
        var c = host.ClientFor(Fa.OwnerA, A);
        await Fa.PostAsync(c, "/fixed-assets/transfers/capitalise", capForm.ToList(), "/fixed-assets/transfers/capitalise");
        int netId;
        await using (var db = Fa.Db(A))
            netId = await db.Set<FixedAsset>().Where(a => a.Name == name).Select(a => a.Id).SingleAsync();
        await Fa.PostAsync(c, $"/fixed-assets/transfers/create?asset_id={netId}", trfForm.ToList(), $"/fixed-assets/transfers/create?asset_id={netId}");


        var answer = await CapAnswerAsync(A, netId, product, productName);
        Golden.Match("CapitaliseAndTransferJournals", answer);
        Assert.Contains("|250001.0000|", string.Join("\n", answer.cap_journal));
        Assert.Contains(FakeStock.Instance.Calls, x => x.StartsWith("out FA-CAP:") && x.Contains($" p{product} 2"));
    }
}
