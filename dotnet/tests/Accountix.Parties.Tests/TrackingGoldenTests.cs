using System.Globalization;
using System.Text.Json;
using Accountix.Core.Entities;
using Accountix.Parties.Services;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;
using static Accountix.Parties.Services.TrackingRules;

namespace Accountix.Parties.Tests;

/// <summary>
/// The tracking engine against fixed expectations captured from the Python app's shared/payment_tracking.py
/// (Golden/*.json; the Python that produced each is in Golden/capture/): the same book read the same way, the same
/// match writing the same assignments, the same refusals in the same words.
/// </summary>
public class TrackingGoldenTests
{
    private static string D(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>The day the golden book is built around (and read as of).</summary>
    private static readonly DateTime T = new(2026, 6, 30);

    /// <summary>
    /// A messier party, in a fresh company: three invoices (one partly returned) and an unapproved one, three receipts
    /// (one partly split by hand, one partly force-closed), all at fixed dates around <see cref="T"/>.
    /// </summary>
    private sealed record Book(int Company, int CustomerId, string LedgerCode, int[] Invoices, int[] Lines)
    {
        public string Inv(int id) => "I" + Array.IndexOf(Invoices, id);
        public string Line(int id) => "R" + Array.IndexOf(Lines, id);
        public string Text(string s) => s.Replace(LedgerCode, "{customer}");
    }

    private static async Task<Book> BookAsync()
    {
        const string tag = "GOLD";
        int company = await Scenario.FreshCompanyAsync();
        await using var db = Scenario.Db(company);
        var (cid, _, acct, code) = await Scenario.CustomerAsync(db, tag, terms: "Net 15");
        int i1 = await Scenario.InvoiceAsync(db, cid, $"BK1-{tag}", 1000m, T.AddDays(-100), null);
        int i2 = await Scenario.InvoiceAsync(db, cid, $"BK2-{tag}", 2500.55m, T.AddDays(-50), T.AddDays(-20));
        int i3 = await Scenario.InvoiceAsync(db, cid, $"BK3-{tag}", 4000m, T.AddDays(-10), T.AddDays(20));
        await Scenario.InvoiceAsync(db, cid, $"BK4-{tag}", 900m, T.AddDays(-5), null, status: "unapproved");
        await Scenario.ReturnAsync(i2, cid, 300.25m, $"BKCN-{tag}", company, T.AddDays(-3));
        var r1 = await Scenario.ReceiptAsync(db, acct, 1800m, $"BKR1-{tag}", T.AddDays(-40));
        var r2 = await Scenario.ReceiptAsync(db, acct, 3000.10m, $"BKR2-{tag}", T.AddDays(-5), "BRV");
        var r3 = await Scenario.ReceiptAsync(db, acct, 500m, $"BKR3-{tag}", T.AddDays(-1), "JV");
        var pt = Scenario.Engine(company).Pt;
        await pt.AllocateAsync(r3.LineId, [new(i3, 200m)], Scenario.OwnerB, "by hand");
        await pt.ForceCloseAsync(r2.LineId, 100m, "bank charge", Scenario.OwnerB);
        return new Book(company, cid, code, [i1, i2, i3], [r1.LineId, r2.LineId, r3.LineId]);
    }

    /// <summary>A match result and what it left behind, ids replaced by their place in the book.</summary>
    private static object MatchShape(Book b, (int Lines, int Receipts, int Payments, int Invoices, decimal Amount) result,
                                     IEnumerable<(int Line, string DocType, int DocId, decimal Amount, string? Note)> allocations,
                                     IEnumerable<(int Id, decimal Paid, string? Status)> invoices) => new
    {
        result = new { lines = result.Lines, receipts = result.Receipts, payments = result.Payments, invoices = result.Invoices, amount = D(result.Amount) },
        allocations = allocations.Select(a => $"{b.Line(a.Line)}→{a.DocType}{(a.DocType == NotApplicable ? "" : b.Inv(a.DocId))} {D(a.Amount)} {a.Note}"),
        invoices = invoices.Select(i => $"{b.Inv(i.Id)} paid {D(i.Paid)} {i.Status}"),
    };

    private static async Task<object> NetMatchAsync(Book b)
    {
        var r = await Scenario.Engine(b.Company).Pt.AutoMatchAsync("customer", b.CustomerId, Scenario.OwnerB);
        await using var db = Scenario.Db(b.Company);
        var allocs = await db.Set<PaymentAllocation>().AsNoTracking().Where(a => b.Lines.Contains(a.VoucherLineId))
            .OrderBy(a => a.VoucherLineId).ThenBy(a => a.DocType).ThenBy(a => a.DocId).ToListAsync();
        var invs = await db.Set<InvInvoice>().AsNoTracking().Where(i => b.Invoices.Contains(i.Id)).OrderBy(i => i.Id).ToListAsync();
        return MatchShape(b, (r.Lines, r.Receipts, r.Payments, r.Invoices, r.Amount),
            allocs.Select(a => (a.VoucherLineId, a.DocType, a.DocId, a.Amount, a.Note)),
            invs.Select(i => (i.Id, i.PaidAmount ?? 0, i.PaymentStatus)));
    }

    [ReferenceFact]
    public async Task Match_oldest_first_writes_pythons_assignments()
    {
        var book = await BookAsync();
        var shape = await NetMatchAsync(book);
        string text = Golden.ToNode(shape).ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Assert.Contains("Matched oldest first", text);
        Assert.Contains("R1→NA 100.00 bank charge", text);
        Golden.Match("MatchOldestFirst", shape);
    }

    private static object OutstandingShape(Book b,
        IEnumerable<(string Number, decimal Settled, decimal Credited, decimal Outstanding, string State, int Days, string Bucket, string Due, string Date, double Progress)> inv,
        IEnumerable<(int Line, string Flow, decimal Amount, decimal Assigned, decimal Forced, decimal Unassigned, string State, string Ledger, string Date, string Reason, string Cash)> feed,
        IEnumerable<(string Kind, string Voucher, decimal Amount, decimal BalanceAfter, bool Counts)> hist,
        IEnumerable<(int Id, decimal AssignedHere, decimal Assignable)> open,
        (int Count, int OpenCount, decimal Outstanding, decimal Overdue, int OverdueCount) summary) => new
    {
        inv = inv.Select(r => new
        {
            number = r.Number, settled = D(r.Settled), credited = D(r.Credited), outstanding = D(r.Outstanding), pay_state = r.State,
            overdue_days = r.Days, age_bucket = r.Bucket, due = r.Due, date = r.Date, progress = r.Progress.ToString("F6", CultureInfo.InvariantCulture),
        }),
        feed = feed.Select(r => new
        {
            line = b.Line(r.Line), flow = r.Flow, amount = D(r.Amount), assigned = D(r.Assigned), forced = D(r.Forced), unassigned = D(r.Unassigned),
            state = r.State, ledger = b.Text(r.Ledger), date = r.Date, reason = r.Reason, cash = r.Cash,
        }),
        hist = hist.Select(h => new { kind = h.Kind, voucher = h.Voucher, amount = D(h.Amount), balance_after = D(h.BalanceAfter), counts = h.Counts }),
        open = open.Select(o => new { invoice = b.Inv(o.Id), assigned_here = D(o.AssignedHere), assignable = D(o.Assignable) }),
        summary = new
        {
            count = summary.Count, open_count = summary.OpenCount, outstanding = D(summary.Outstanding), overdue = D(summary.Overdue),
            overdue_count = summary.OverdueCount,
        },
    };

    [ReferenceFact]
    public async Task Outstanding_figures_feed_rows_and_history_equal_pythons()
    {
        var b = await BookAsync();
        var pt = Scenario.Engine(b.Company).Pt;
        await pt.AllocateAsync(b.Lines[0], [new(b.Invoices[0], 1000m), new(b.Invoices[1], 650.4m)], Scenario.OwnerB);
        var asOf = DateOnly.FromDateTime(T);

        var rows = await pt.InvoiceRowsAsync(Sales, new InvoiceFilter { PartyId = b.CustomerId }, asOf);
        var summary = OutstandingSummary.Of(rows);
        var net = OutstandingShape(b,
            rows.Select(r => (r.Number, r.Settled, r.Credited, r.Outstanding, r.PayState, r.OverdueDays, r.AgeBucket, r.DueStr, r.DateStr, r.Progress)),
            (await pt.FeedRowsAsync(new FeedFilter { PartyType = "customer", PartyId = b.CustomerId }))
                .Select(r => (r.LineId, r.Flow, r.Amount, r.Assigned, r.Forced, r.Unassigned, r.State, r.LedgerCode, r.DateStr, r.ForcedReason, r.CashAccount)),
            (await pt.HistoryForDocAsync(Sales, b.Invoices[1])).Select(h => (h.Kind, h.VoucherNumber, h.Amount, h.BalanceAfter, h.Counts)),
            (await pt.OpenInvoicesAsync("customer", b.CustomerId, b.Lines[0], asOf)).Select(i => (i.Id, i.AssignedHere!.Value, i.Assignable!.Value)),
            (summary.Count, summary.OpenCount, summary.Outstanding, summary.Overdue, summary.OverdueCount));

        Golden.Match("OutstandingFigures", net);
    }

    private static List<string> RefusalShape(Scenario s, IEnumerable<string> messages) => messages.Select(m => m.Replace(s.Tag, "TAG")).ToList();

    [ReferenceFact]
    public async Task Refusals_are_worded_as_pythons()
    {
        var s = await Scenario.CreateAsync();
        await Scenario.Engine().Pt.ForceCloseAsync(s.LineId, 4000m, "advance", Scenario.OwnerB);
        var pt = Scenario.Engine().Pt;
        var net = new List<string>();
        foreach (var entries in new List<PaymentTracking.AllocationEntry>[] { [new(s.InvA, 3500m)], [new(s.InvA, 1500m)], [new(s.InvA, -1m)], [] })
            net.Add(await Try(() => pt.AllocateAsync(s.LineId, entries, Scenario.OwnerB)));
        net.Add(await Try(() => pt.ForceCloseAsync(s.LineId, 9000m, "x", Scenario.OwnerB)));
        net.Add(await Try(() => pt.ForceCloseAsync(s.LineId, 10m, " ", Scenario.OwnerB)));
        Assert.DoesNotContain("ok", net);
        Golden.Match("Refusals", RefusalShape(s, net));

        static async Task<string> Try(Func<Task> f)
        {
            try
            {
                await f();
                return "ok";
            }
            catch (AllocationException e)
            {
                return e.Message;
            }
        }
    }

    [ReferenceFact]
    public async Task The_contract_hook_refreshes_the_cache_of_every_invoice_the_voucher_settled()
    {
        var s = await Scenario.CreateAsync();
        await Scenario.Engine().Pt.AllocateAsync(s.LineId, [new(s.InvA, 3000m), new(s.InvB, 1000m)], Scenario.OwnerB);
        await using (var db = Scenario.Db())
        {
            var v = await db.Set<AccountingVoucher>().SingleAsync(v => v.Id == s.VoucherId);
            v.Status = "unapproved";
            await db.SaveChangesAsync();
        }
        await ((Accountix.Core.Shared.IPaymentTracking)Scenario.Engine().Pt).ReconcileVoucherAsync(s.VoucherId);
        await using var check = Scenario.Db();
        var cache = string.Join(",", (await check.Set<InvInvoice>().AsNoTracking()
            .Where(i => i.Id == s.InvA || i.Id == s.InvB).OrderBy(i => i.Id).ToListAsync()).Select(i => $"{i.PaymentStatus}:{D(i.PaidAmount ?? 0)}"));
        Assert.Equal("unpaid:0.00,unpaid:0.00", cache);
    }
}
