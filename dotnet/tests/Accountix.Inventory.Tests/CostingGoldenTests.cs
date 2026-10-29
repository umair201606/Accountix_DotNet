using System.Globalization;
using System.Text.RegularExpressions;
using Accountix.Core.Entities;
using Accountix.Core.Shared;
using Accountix.Inventory.Services;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Inventory.Tests;

/// <summary>
/// Four scripted sequences of receipts, issues (back-dated ones included), reversals, setting changes
/// and a method-switch revaluation, run by the .NET engine on a fresh product of company B in a
/// transaction that is rolled back. The stock_ledger, stock_layers, layer_consumptions and
/// stock_cost_adjustments rows, the CADJ journals with their lines, the product's figures and every
/// refusal's words must be what the Python app's shared/costing.py wrote for the same sequence
/// (Golden/costing_*.json, captured by Golden/capture/costing.py before Python was removed).
/// </summary>
public class CostingGoldenTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // The op shapes of the capture script (Golden.SameInputs compares them key by key).
    private static Dictionary<string, object?> In(string vt, int vid, string qty, string cost, int days, string? total = null) =>
        new() { ["op"] = "in", ["vt"] = vt, ["vid"] = vid, ["vn"] = $"{vt}-{vid:D5}", ["qty"] = qty, ["cost"] = cost, ["days"] = days, ["total"] = total };

    private static Dictionary<string, object?> Out(string vt, int vid, string qty, int days, string? unit = null, int? prefer = null) =>
        new() { ["op"] = "out", ["vt"] = vt, ["vid"] = vid, ["vn"] = $"{vt}-{vid:D5}", ["qty"] = qty, ["days"] = days, ["unit"] = unit, ["prefer"] = prefer };

    private static Dictionary<string, object?> Rev(string vt, int vid, bool allow = false) =>
        new() { ["op"] = "rev", ["vt"] = vt, ["vid"] = vid, ["allow"] = allow };

    private static Dictionary<string, object?> Set(string method, bool neg) => new() { ["op"] = "set", ["method"] = method, ["neg"] = neg };

    private static Dictionary<string, object?> Revalue(string method) => new() { ["op"] = "revalue", ["method"] = method };

    private static string F(decimal? d) => (d ?? 0m).ToString("F4", Inv);

    /// <summary>Runs the ops through the engine and returns the capture's lines.</summary>
    private static async Task<List<string>> RunAsync(List<Dictionary<string, object?>> ops, string method, bool neg)
    {
        int @base = Random.Shared.Next(2_000_000, 2_900_000) * 100;
        var today = DateTime.Today;
        await using var w = await CostingWorld.StartAsync(method, neg);
        var results = new List<string>();
        foreach (var op in ops)
        {
            await w.Tx.CreateSavepointAsync("op");
            try
            {
                string kind = (string)op["op"]!;
                int vid = op.TryGetValue("vid", out var v) && v is int i ? @base + i : 0;
                DateTime? date = op.TryGetValue("days", out var d) && d is int days ? today.AddDays(-days) : null;
                switch (kind)
                {
                    case "in":
                        await w.Costing.ReceiveAsync(w.Product, (string)op["vt"]!, vid, (string)op["vn"]!, decimal.Parse((string)op["qty"]!, Inv),
                            decimal.Parse((string)op["cost"]!, Inv), "", w.User, date,
                            op["total"] is string t ? decimal.Parse(t, Inv) : null);
                        break;
                    case "out":
                        await w.Costing.IssueAsync(w.Product, (string)op["vt"]!, vid, (string)op["vn"]!, decimal.Parse((string)op["qty"]!, Inv), "",
                            w.User, op["unit"] is string u ? decimal.Parse(u, Inv) : null, date,
                            op["prefer"] is int pr ? ("PI", @base + pr) : null);
                        break;
                    case "rev":
                        await w.Costing.ReverseVoucherStockAsync((string)op["vt"]!, vid, (bool)op["allow"]!, w.User);
                        break;
                    case "set":
                        await w.SetAsync((string)op["method"]!, (bool)op["neg"]!);
                        break;
                    case "revalue":
                        await w.Costing.RevalueForMethodChangeAsync((string)op["method"]!, w.User);
                        await w.SetAsync((string)op["method"]!);
                        break;
                }
                await w.Db.SaveChangesAsync();
                await w.Tx.ReleaseSavepointAsync("op");
                results.Add("ok");
            }
            catch (BusinessRuleException e)
            {
                await w.Tx.RollbackToSavepointAsync("op");
                w.Db.ChangeTracker.Clear();
                string name = e switch
                {
                    NegativeStockException => "NegativeStockError",
                    ConsumedLayerException => "ConsumedLayerError",
                    StockQuantityException => "ValueError",
                    _ => e.GetType().Name,
                };
                results.Add($"{name}: {e.Message.Replace($"product {w.Product}", "product P")}");
            }
        }
        w.Db.ChangeTracker.Clear();

        var db = w.Db;
        int pid = w.Product;
        var rows = await db.Set<StockLedger>().AsNoTracking().Where(r => r.ProductId == pid).OrderBy(r => r.Id).ToListAsync();
        var rowIdx = rows.Select((r, n) => (r.Id, n)).ToDictionary(x => x.Id, x => x.n);
        var layers = await db.Set<StockLayer>().AsNoTracking().Where(l => l.ProductId == pid).OrderBy(l => l.Id).ToListAsync();
        var layerIdx = layers.Select((l, n) => (l.Id, n)).ToDictionary(x => x.Id, x => x.n);
        var cons = await db.Set<LayerConsumption>().AsNoTracking().Where(c => c.ProductId == pid).OrderBy(c => c.Id).ToListAsync();
        var adjs = await db.Set<StockCostAdjustment>().AsNoTracking().Where(a => a.ProductId == pid).OrderBy(a => a.Id).ToListAsync();
        var jes = await db.Set<JournalEntry>().AsNoTracking().Include(e => e.JournalLines)
            .Where(e => e.VoucherType.StartsWith("CADJ-") && e.VoucherId > @base && e.VoucherId < @base + 1000)
            .OrderBy(e => e.Id).ToListAsync();
        var jeIdx = jes.Select((e, n) => (e.Id, n)).ToDictionary(x => x.Id, x => x.n);
        string own = $"1-01-04-01-{pid + 100:D4}";
        var codes = await db.Set<ChartOfAccount>().AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Code == own ? "1-01-04-01-<P>" : a.Code);
        var product = await db.Set<InvProduct>().AsNoTracking().SingleAsync(p => p.Id == pid);

        var lines = results.Select((r, n) => $"result {n} {r}").ToList();
        lines.AddRange(rows.Select(r => "row " + string.Join("|", r.VoucherType, r.VoucherId - @base, r.VoucherNumber, r.TransactionType,
            F(r.Quantity), F(r.UnitCost), F(r.TotalCost), F(r.RunningQty), F(r.RunningCost), F(r.RunningAvg), r.ValuationMethod,
            r.TxnDate?.ToString("yyyy-MM-dd", Inv), r.Notes ?? "")));
        lines.AddRange(layers.Select(l => "layer " + string.Join("|",
            l.SourceLedgerId is int s ? rowIdx.GetValueOrDefault(s, -1).ToString(Inv) : "None",
            F(l.UnitCost), F(l.QtyOriginal), F(l.QtyRemaining), F(l.ValueRemaining), l.Method, l.IsRevaluation == true, l.Notes ?? "")));
        lines.AddRange(cons.Select(c => "consume " + string.Join("|", layerIdx.GetValueOrDefault(c.LayerId, -1),
            rowIdx.GetValueOrDefault(c.OutLedgerId, -1), F(c.Qty), F(c.UnitCost), F(c.TotalCost))));
        lines.AddRange(adjs.Select(a => "adjust " + string.Join("|",
            a.LedgerId is int l ? rowIdx.GetValueOrDefault(l, -1).ToString(Inv) : "None", a.VoucherType, a.VoucherId - @base, a.VoucherNumber,
            F(a.OldCost), F(a.NewCost), F(a.Delta), a.Trigger, a.EntryDate?.ToString("yyyy-MM-dd", Inv),
            a.JournalEntryId is int j ? jeIdx.GetValueOrDefault(j, -1).ToString(Inv) : "None")));
        foreach (var e in jes)
        {
            lines.Add("journal " + string.Join("|", e.VoucherType, e.VoucherId - @base, e.VoucherNumber, e.Description,
                e.EntryDate.ToString("yyyy-MM-dd", Inv), e.IsPosted == true));
            lines.AddRange(e.JournalLines.OrderBy(l => l.Id).Select(l =>
                $"  line {codes[l.AccountId]}|{F(l.Debit)}|{F(l.Credit)}|{l.Description}|{(l.LabelId is null ? "None" : "label")}"));
        }
        lines.Add($"product {F(product.CurrentStock)}|{F(product.CostPrice)}");

        // As the capture: dates as days before today, voucher ids from the run's random base as V+n.
        var anchor = DateOnly.FromDateTime(today);
        return lines.Select(l => Regex.Replace(Golden.RelDates(l, anchor), @"\b\d+\b", m =>
            long.Parse(m.Value, Inv) is var n && n >= @base && n < @base + 1000 ? $"V+{n - @base}" : m.Value)).ToList();
    }

    private static async Task MatchAsync(string name, string method, bool neg, List<Dictionary<string, object?>> ops)
    {
        Golden.SameInputs(name, new { method, neg, ops });
        Golden.Match(name, await RunAsync(ops, method, neg));
    }

    [ReferenceFact]
    public Task Fifo_backdated_receipts_sales_and_reversals_cost_what_python_costed() => MatchAsync("costing_fifo_backdated", "fifo", false,
    [
        In("PI", 1, "10", "10", 30), In("PI", 2, "10", "20", 25),
        Out("SI", 1, "10", 20), Out("SI", 2, "10", 10),
        Rev("SI", 1),                                   // the later sale re-costs 200 -> 100
        In("PI", 3, "5", "8", 40),                      // back-dated before everything
        Out("CONS", 1, "3", 5), Out("SCRAP", 1, "1.5", 4),
        Rev("PI", 2, allow: true),                      // correct the 20 purchase: shorts, no journal yet
        In("PI", 2, "10", "22", 25),                    // re-approve corrected on its own date
        Out("SI", 3, "50", 2),                          // refused: not on hand
        Out("SI", 4, "2", 35),                          // back-dated sale, refused: strips a later issue
        Rev("PI", 3),                                   // refused: consumed
    ]);

    [ReferenceFact]
    public Task Weighted_average_reaveraging_with_backdated_documents_costs_what_python_costed() => MatchAsync("costing_wa_reaveraging",
        "weighted_average", false,
    [
        In("PI", 1, "7", "10.37", 60), Out("SI", 1, "3", 59), In("PI", 2, "11", "12.4999", 50),
        Out("SI", 2, "2.5", 45), Out("CONS", 1, "4", 44), In("PI", 3, "3", "9.99", 55),   // back-dated: re-averages 3 issues
        In("PI", 4, "288", "15349.6528", 40, "4420700.00"),                                // landed total carried exactly
        Out("SI", 3, "100", 30), Rev("SI", 2), Rev("PI", 1),                               // refused: stranded value
        Out("SI", 4, "1.2345", 20), Rev("PI", 3, allow: true), In("PI", 3, "3", "11.11", 55),
    ]);

    [ReferenceFact]
    public Task Negative_stock_shorts_and_covering_receipts_cost_what_python_costed() => MatchAsync("costing_negative_stock",
        "weighted_average", true,
    [
        In("PI", 1, "5", "10", 20), Out("SI", 1, "8", 18),        // 3 short
        In("PI", 2, "10", "12", 15),                              // covers the short first
        Out("SI", 2, "9", 10), Out("SI", 3, "4", 8),             // short again
        Rev("PI", 2, allow: true), In("PI", 4, "6", "11", 6),
        Out("SI", 5, "2", 19),                                    // back-dated over-issue, allowed
        Rev("SI", 1),
        Set("fifo", false), Out("SI", 6, "100", 1),               // refused once negative stock is off
    ]);

    [ReferenceFact]
    public Task Method_switch_revaluation_and_explicit_basis_cost_what_python_costed() => MatchAsync("costing_method_switch", "fifo", false,
    [
        In("PI", 1, "10", "10", 30), In("PI", 2, "10", "20", 25), Out("SI", 1, "5", 24),
        Revalue("weighted_average"),                              // FIFO -> WA collapses at book value
        In("PI", 3, "4", "30", 20), Out("SI", 2, "6", 18),
        Revalue("fifo"), In("PI", 5, "2", "7", 15),
        Out("PRV", 1, "2", 10, unit: "7.0000", prefer: 5),        // a purchase return at its receipt's cost, from that receipt
        Out("SI", 3, "3", 9),
        In("PI", 6, "1", "1", 26),                                // back-dated: frozen PRV basis, re-costed sales
        In("PI", 7, "0", "1", 5),                                 // refused: not positive
    ]);
}
