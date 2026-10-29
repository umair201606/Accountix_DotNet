using Accountix.Core.Entities;
using Accountix.Core.Shared;
using Accountix.Parties.Services;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;
using static Accountix.Parties.Services.TrackingRules;

namespace Accountix.Parties.Tests;

/// <summary>
/// tests/e2e/test_payment_tracking.py, the engine half, on the reference database (company B): every test makes
/// its own customer, two invoices and a 5,000 receipt.
/// </summary>
public class TrackingEngineTests
{
    private static PaymentTracking Pt() => Scenario.Engine().Pt;
    private static AllocationEntryList E(params (int Doc, decimal Amount)[] xs) => new(xs);

    public sealed class AllocationEntryList((int Doc, decimal Amount)[] xs) : List<PaymentTracking.AllocationEntry>(
        xs.Select(x => new PaymentTracking.AllocationEntry(x.Doc, x.Amount)));

    private static async Task<InvoiceRow> Inv(int id) => (await Pt().InvoiceRowAsync(Sales, id))!;
    private static async Task<FeedRow> Line(int id) => (await Pt().FeedRowAsync(id))!;

    [ReferenceFact]
    public async Task Receipt_appears_in_the_feed_as_unassigned()
    {
        var s = await Scenario.CreateAsync();
        var row = await Line(s.LineId);
        Assert.Equal("receipt", row.Flow);
        Assert.Equal(Sales, row.DocType);
        Assert.Equal(s.CustomerId, row.PartyId);
        Assert.Equal(5000m, row.Amount);
        Assert.Equal(5000m, row.Unassigned);
        Assert.Equal("unassigned", row.State);
    }

    [ReferenceFact]
    public async Task The_invoices_own_ar_line_is_not_offered_as_cash()
    {
        var s = await Scenario.CreateAsync();
        await using var db = Scenario.Db();
        var v = new AccountingVoucher
        {
            VoucherType = "CRV", VoucherNumber = $"TRK-CRV-DR-{s.Tag}", VoucherDate = DateTime.UtcNow, Status = "approved",
            CreatedBy = Scenario.OwnerB, ApprovedBy = Scenario.OwnerB, CreatedAt = DateTime.UtcNow,
        };
        db.Add(v);
        await db.SaveChangesAsync();
        var l = new AccountingVoucherLine { VoucherId = v.Id, LineNo = 1, AccountId = s.AccountId, Debit = 1000, Credit = 0 };
        db.Add(l);
        await db.SaveChangesAsync();
        Assert.Null(await Pt().FeedRowAsync(l.Id));
    }

    [Fact]
    public void Feed_covers_the_five_settlement_voucher_types()
    {
        Assert.Equal(["CRV", "BRV", "JV"], ReceiptVouchers);
        Assert.Equal(["CPV", "BPV", "JV"], PaymentVouchers);
        Assert.Equal(["customer", "supplier"], DocForParty.Keys.Order());
    }

    [ReferenceFact]
    public async Task Feed_is_customers_and_suppliers_only_and_ledger_ids_never_collide()
    {
        var s = await Scenario.CreateAsync();
        var index = await Pt().PartyAccountIndexAsync();
        Assert.All(index.Values, p => Assert.Contains(p.Kind, new[] { "customer", "supplier" }));
        Assert.Equal(("customer", s.CustomerId), (index[s.AccountId].Kind, index[s.AccountId].Id));
        var ledger = index.Values.Where(p => IsLedgerParty(p.Id)).Select(p => p.Id).ToHashSet();
        var real = index.Values.Where(p => !IsLedgerParty(p.Id)).Select(p => p.Id).ToHashSet();
        Assert.NotEmpty(ledger);
        Assert.Empty(ledger.Intersect(real));
        Assert.All(ledger, i => Assert.True(i < 0));
        Assert.All(real, i => Assert.True(i > 0));
    }

    private static async Task<(int VoucherId, int LineId, int AccountId)> LedgerVoucherAsync(string code, string vtype, decimal debit, decimal credit)
    {
        await using var db = Scenario.Db();
        var acct = await db.Set<ChartOfAccount>().SingleAsync(a => a.Code == code);
        var v = new AccountingVoucher
        {
            VoucherType = vtype, VoucherNumber = $"TRK-LEDGER-{Scenario.NewTag()}", VoucherDate = DateTime.UtcNow, Status = "approved",
            CreatedBy = Scenario.OwnerB, ApprovedBy = Scenario.OwnerB, CreatedAt = DateTime.UtcNow,
        };
        db.Add(v);
        await db.SaveChangesAsync();
        var l = new AccountingVoucherLine { VoucherId = v.Id, LineNo = 1, AccountId = acct.Id, Debit = debit, Credit = credit };
        db.Add(l);
        await db.SaveChangesAsync();
        return (v.Id, l.Id, acct.Id);
    }

    [ReferenceFact]
    public async Task Whole_ledger_payers_reach_the_feed_by_direction_and_are_closed_not_assigned()
    {
        var pay = await LedgerVoucherAsync(SupplierParent, "BPV", 100000, 0);
        var row = await Line(pay.LineId);
        Assert.Equal("payment", row.Flow);
        Assert.True(IsLedgerParty(row.PartyId));
        Assert.Equal(LedgerPartyId(pay.AccountId), row.PartyId);
        Assert.Equal(100000m, row.Amount);

        var rec = await LedgerVoucherAsync(CustomerParent, "BRV", 0, 4200);
        Assert.Equal("receipt", (await Line(rec.LineId)).Flow);

        var credited = await LedgerVoucherAsync(SupplierParent, "BPV", 0, 500);
        Assert.Null(await Pt().FeedRowAsync(credited.LineId));

        var small = await LedgerVoucherAsync(SupplierParent, "BPV", 900, 0);
        var srow = await Line(small.LineId);
        Assert.Empty(await Pt().OpenInvoicesAsync(srow.PartyType, srow.PartyId));
        await Pt().ForceCloseAsync(small.LineId, 900m, "no invoice — ledger settlement", Scenario.OwnerB);
        Assert.Equal("forced", (await Line(small.LineId)).State);
    }

    [ReferenceFact]
    public async Task Feed_search_matches_the_ledger_the_money_moved_through()
    {
        var s = await Scenario.CreateAsync();
        async Task<bool> Found(string q) => (await Pt().FeedRowsAsync(new FeedFilter { Search = q })).Any(r => r.LineId == s.LineId);
        Assert.True(await Found(s.AccountCode));
        Assert.True(await Found(s.CustomerName));
        Assert.False(await Found("zzz-no-such-ledger"));
    }

    [ReferenceFact]
    public async Task One_receipt_splits_across_two_invoices()
    {
        var s = await Scenario.CreateAsync();
        await Pt().AllocateAsync(s.LineId, E((s.InvA, 3000), (s.InvB, 1500)), Scenario.OwnerB);
        var a = await Inv(s.InvA);
        var b = await Inv(s.InvB);
        Assert.Equal((3000m, "paid", 0m), (a.Settled, a.PayState, a.Outstanding));
        Assert.Equal((1500m, "partial", 2500m), (b.Settled, b.PayState, b.Outstanding));
        var row = await Line(s.LineId);
        Assert.Equal((4500m, 500m, "partial"), (row.Assigned, row.Unassigned, row.State));
        await using var db = Scenario.Db();
        var cached = await db.Set<InvInvoice>().AsNoTracking().SingleAsync(i => i.Id == s.InvA);
        Assert.Equal(("paid", 3000m), (cached.PaymentStatus, cached.PaidAmount!.Value));
    }

    [ReferenceFact]
    public async Task Reassigning_the_same_line_replaces_rather_than_stacks()
    {
        var s = await Scenario.CreateAsync();
        await Pt().AllocateAsync(s.LineId, E((s.InvA, 1000)), Scenario.OwnerB);
        await Pt().AllocateAsync(s.LineId, E((s.InvA, 2000)), Scenario.OwnerB);
        await using var db = Scenario.Db();
        Assert.Equal(1, await db.Set<PaymentAllocation>().CountAsync(a => a.VoucherLineId == s.LineId && a.DocId == s.InvA));
        Assert.Equal(2000m, (await Inv(s.InvA)).Settled);
    }

    [ReferenceFact]
    public async Task A_split_cannot_exceed_the_money_or_an_invoice_and_is_refused_whole()
    {
        var s = await Scenario.CreateAsync();
        var e = await Assert.ThrowsAsync<AllocationException>(() => Pt().AllocateAsync(s.LineId, E((s.InvA, 3000), (s.InvB, 4000)), Scenario.OwnerB));
        Assert.Equal("This receipt is 5,000.00; assigning 7,000.00 would exceed it.", e.Message);
        Assert.Equal(0m, (await Line(s.LineId)).Assigned);
        var over = await Assert.ThrowsAsync<AllocationException>(() => Pt().AllocateAsync(s.LineId, E((s.InvA, 3500)), Scenario.OwnerB));
        Assert.Equal($"{s.NumA} only has 3,000.00 outstanding — 3,500.00 cannot be assigned to it.", over.Message);
        var neg = await Assert.ThrowsAsync<AllocationException>(() => Pt().AllocateAsync(s.LineId, E((s.InvA, -1)), Scenario.OwnerB));
        Assert.Equal("An assigned amount cannot be negative.", neg.Message);
        var none = await Assert.ThrowsAsync<AllocationException>(() => Pt().AllocateAsync(s.LineId, [], Scenario.OwnerB));
        Assert.Equal("Select at least one invoice to assign this to.", none.Message);
        var missing = await Assert.ThrowsAsync<AllocationException>(() => Pt().AllocateAsync(s.LineId, E((999_999_999, 1)), Scenario.OwnerB));
        Assert.Equal("Invoice #999999999 was not found.", missing.Message);
    }

    [ReferenceFact]
    public async Task Unassign_returns_the_money_to_the_feed()
    {
        var s = await Scenario.CreateAsync();
        await Pt().AllocateAsync(s.LineId, E((s.InvA, 3000)), Scenario.OwnerB);
        await using var db = Scenario.Db();
        int alloc = await db.Set<PaymentAllocation>().Where(a => a.VoucherLineId == s.LineId).Select(a => a.Id).FirstAsync();
        var result = await Pt().UnallocateAsync(alloc);
        Assert.Equal((Sales, s.InvA, s.LineId), result);
        Assert.Equal(5000m, (await Line(s.LineId)).Unassigned);
        var back = await Inv(s.InvA);
        Assert.Equal(("unpaid", 0m), (back.PayState, back.Settled));
        var gone = await Assert.ThrowsAsync<AllocationException>(() => Pt().UnallocateAsync(alloc));
        Assert.Equal("That assignment no longer exists.", gone.Message);
    }

    [ReferenceFact]
    public async Task Force_close_settles_money_with_no_invoice_and_needs_a_reason()
    {
        var s = await Scenario.CreateAsync();
        var noReason = await Assert.ThrowsAsync<AllocationException>(() => Pt().ForceCloseAsync(s.LineId, null, "   ", Scenario.OwnerB));
        Assert.Equal("Give a reason for closing this without an invoice.", noReason.Message);
        var tooMuch = await Assert.ThrowsAsync<AllocationException>(() => Pt().ForceCloseAsync(s.LineId, 6000m, "too much", Scenario.OwnerB));
        Assert.Equal("Only 5,000.00 of this receipt is still unassigned; 6,000.00 cannot be closed.", tooMuch.Message);

        var row = (await Pt().ForceCloseAsync(s.LineId, null, "customer advance", Scenario.OwnerB))!;
        Assert.Equal(("forced", 5000m, 0m, 0m, "customer advance"), (row.State, row.Forced, row.ToInvoices, row.Unassigned, row.ForcedReason));
        foreach (int doc in new[] { s.InvA, s.InvB })
            Assert.Equal(("unpaid", 0m), ((await Inv(doc)).PayState, (await Inv(doc)).Settled));
        var history = await Pt().HistoryForLineAsync(s.LineId);
        Assert.Single(history);
        Assert.True(history[0].Forced);
        Assert.Contains("Not applicable", history[0].Number);
        Assert.Equal("customer advance", history[0].Note);

        var reopened = (await Pt().ReleaseForceAsync(s.LineId))!;
        Assert.Equal(("unassigned", 0m, 5000m), (reopened.State, reopened.Forced, reopened.Unassigned));
        var notForced = await Assert.ThrowsAsync<AllocationException>(() => Pt().ReleaseForceAsync(s.LineId));
        Assert.Equal("This payment has not been force-closed.", notForced.Message);
    }

    [ReferenceFact]
    public async Task Partial_assignment_plus_forced_remainder_reads_as_forced()
    {
        var s = await Scenario.CreateAsync();
        await Pt().AllocateAsync(s.LineId, E((s.InvA, 3000)), Scenario.OwnerB);
        var row = (await Pt().ForceCloseAsync(s.LineId, null, "rounding difference", Scenario.OwnerB))!;
        Assert.Equal(("forced", 3000m, 2000m, 0m), (row.State, row.ToInvoices, row.Forced, row.Unassigned));
        Assert.Equal("paid", (await Inv(s.InvA)).PayState);
    }

    [ReferenceFact]
    public async Task Forced_money_is_not_available_for_assignment_and_a_partly_forced_line_stays_queued()
    {
        var s = await Scenario.CreateAsync();
        var partly = (await Pt().ForceCloseAsync(s.LineId, 1000m, "bank charge", Scenario.OwnerB))!;
        Assert.Equal(("partial", 4000m), (partly.State, partly.Unassigned));
        Assert.Contains(s.LineId, (await Pt().FeedRowsAsync(new FeedFilter { States = DefaultAssignStates })).Select(r => r.LineId));
        await Pt().ForceCloseAsync(s.LineId, 3000m, "advance", Scenario.OwnerB);   // tops up the same row: 4,000 forced
        var e = await Assert.ThrowsAsync<AllocationException>(() => Pt().AllocateAsync(s.LineId, E((s.InvA, 3000)), Scenario.OwnerB));
        Assert.Equal("This receipt is 5,000.00 (4,000.00 of it is already closed as not applicable); assigning 7,000.00 would exceed it.", e.Message);
        await Pt().AllocateAsync(s.LineId, E((s.InvA, 1000)), Scenario.OwnerB);
        Assert.Equal(0m, (await Line(s.LineId)).Unassigned);
    }

    [ReferenceFact]
    public async Task Feed_filters_by_every_assignment_state()
    {
        var s = await Scenario.CreateAsync();
        async Task<bool> In(params string[] states) =>
            (await Pt().FeedRowsAsync(new FeedFilter { States = states, PartyType = "customer", PartyId = s.CustomerId })).Any(r => r.LineId == s.LineId);
        Assert.True(await In("unassigned"));
        await Pt().AllocateAsync(s.LineId, E((s.InvA, 1000)), Scenario.OwnerB);
        Assert.True(await In("partial"));
        Assert.False(await In("unassigned"));
        await Pt().ForceCloseAsync(s.LineId, null, "advance", Scenario.OwnerB);
        Assert.True(await In("forced"));
        Assert.False(await In("partial"));
        Assert.False(await In("assigned"));
        await Pt().ReleaseForceAsync(s.LineId);
        await Pt().AllocateAsync(s.LineId, E((s.InvA, 3000), (s.InvB, 2000)), Scenario.OwnerB);
        Assert.True(await In("assigned"));
        Assert.True(await In("unassigned", "partial", "assigned", "forced"));
    }

    [ReferenceFact]
    public async Task Pay_state_overdue_and_ageing_filters()
    {
        var s = await Scenario.CreateAsync();
        var a0 = await Inv(s.InvA);
        Assert.Equal("unpaid", a0.PayState);
        Assert.True(a0.OverdueDays >= 55);
        Assert.NotEqual("not_due", a0.AgeBucket);
        await Pt().AllocateAsync(s.LineId, E((s.InvA, 3000), (s.InvB, 1500)), Scenario.OwnerB);
        async Task<HashSet<int>> Ids(InvoiceFilter f) => (await Pt().InvoiceRowsAsync(Sales, f with { PartyId = s.CustomerId })).Select(r => r.Id).ToHashSet();
        Assert.Contains(s.InvA, await Ids(new InvoiceFilter { PayStates = ["paid"] }));
        Assert.DoesNotContain(s.InvB, await Ids(new InvoiceFilter { PayStates = ["paid"] }));
        Assert.Contains(s.InvB, await Ids(new InvoiceFilter { PayStates = ["partial"] }));
        var overdue = await Ids(new InvoiceFilter { OverdueOnly = true });
        Assert.DoesNotContain(s.InvA, overdue);   // settled, so not overdue although its due date is past
        Assert.DoesNotContain(s.InvB, overdue);   // not due yet
    }

    [ReferenceFact]
    public async Task Outstanding_summary_tracks_assignment()
    {
        var s = await Scenario.CreateAsync();
        decimal before = OutstandingSummary.Of(await Pt().InvoiceRowsAsync(Sales)).Outstanding;
        await Pt().AllocateAsync(s.LineId, E((s.InvA, 3000)), Scenario.OwnerB);
        decimal after = OutstandingSummary.Of(await Pt().InvoiceRowsAsync(Sales)).Outstanding;
        Assert.Equal(before - 3000m, after);
    }

    [ReferenceFact]
    public async Task History_links_invoice_and_receipt_both_ways()
    {
        var s = await Scenario.CreateAsync();
        await Pt().AllocateAsync(s.LineId, E((s.InvA, 3000), (s.InvB, 1500)), Scenario.OwnerB, "split across two");
        var byDoc = await Pt().HistoryForDocAsync(Sales, s.InvA);
        Assert.Single(byDoc);
        Assert.Equal((3000m, s.VoucherNumber, 0m), (byDoc[0].Amount, byDoc[0].VoucherNumber, byDoc[0].BalanceAfter));
        var byLine = await Pt().HistoryForLineAsync(s.LineId);
        Assert.Equal(new[] { s.InvA, s.InvB }.Order(), byLine.Select(h => h.DocId).Order());
        Assert.Equal(4500m, byLine.Sum(h => h.Amount));
        Assert.All(byLine, h => Assert.Equal("split across two", h.Note));
    }

    [ReferenceFact]
    public async Task Auto_split_proposes_oldest_first_and_is_a_complete_split()
    {
        var s = await Scenario.CreateAsync();
        var proposal = await Pt().AutoSplitAsync(s.LineId);
        Assert.Equal(s.InvA, proposal[0].DocId);
        Assert.Equal(3000m, proposal[0].Amount);
        Assert.Equal(5000m, proposal.Sum(p => p.Amount));
        Assert.Equal(0m, (await Line(s.LineId)).Assigned);   // a proposal only

        await Pt().AllocateAsync(s.LineId, E((s.InvB, 1000)), Scenario.OwnerB);
        var again = (await Pt().AutoSplitAsync(s.LineId)).ToDictionary(p => p.DocId, p => p.Amount);
        Assert.Equal(new Dictionary<int, decimal> { [s.InvA] = 3000m, [s.InvB] = 2000m }, again);
        await Pt().AllocateAsync(s.LineId, again.Select(kv => new PaymentTracking.AllocationEntry(kv.Key, kv.Value)).ToList(), Scenario.OwnerB);
        Assert.Equal("assigned", (await Line(s.LineId)).State);
    }

    [ReferenceFact]
    public async Task Shrinking_the_voucher_flags_the_line_for_reassignment()
    {
        var s = await Scenario.CreateAsync();
        await Pt().AllocateAsync(s.LineId, E((s.InvA, 3000)), Scenario.OwnerB);
        await using (var db = Scenario.Db())
        {
            var l = await db.Set<AccountingVoucherLine>().SingleAsync(x => x.Id == s.LineId);
            l.Credit = 1000;
            await db.SaveChangesAsync();
        }
        var row = await Line(s.LineId);
        Assert.Equal(("review", 1000m, 3000m, 2000m), (row.State, row.Amount, row.Assigned, row.OverAssigned));
        Assert.Contains(s.LineId, (await Pt().FeedRowsAsync(new FeedFilter { States = ["review"] })).Select(r => r.LineId));
    }

    [ReferenceFact]
    public async Task Unapproving_the_voucher_reverts_the_invoice_and_the_hook_refreshes_its_cache()
    {
        var s = await Scenario.CreateAsync();
        await Pt().AllocateAsync(s.LineId, E((s.InvA, 3000)), Scenario.OwnerB);
        Assert.Equal("paid", (await Inv(s.InvA)).PayState);
        await using var db = Scenario.Db();
        var v = await db.Set<AccountingVoucher>().SingleAsync(x => x.Id == s.VoucherId);
        v.Status = "unapproved";
        await db.SaveChangesAsync();
        var back = await Inv(s.InvA);
        Assert.Equal(("unpaid", 0m, 3000m), (back.PayState, back.Settled, back.Outstanding));
        var stale = await Pt().StaleAllocationsAsync();
        var mine = Assert.Single(stale, r => r.VoucherId == s.VoucherId);
        Assert.Equal((s.VoucherNumber, s.NumA, s.CustomerName, 3000m), (mine.VoucherNumber, mine.DocNumber, mine.PartyName, mine.Amount));
        Assert.Equal("The voucher no longer carries this customer as an approved settlement.", mine.Reason);

        Assert.Equal(1, await Pt().ReconcileVoucherCountAsync(s.VoucherId));
        await using var check = Scenario.Db();
        Assert.Equal("unpaid", (await check.Set<InvInvoice>().AsNoTracking().SingleAsync(i => i.Id == s.InvA)).PaymentStatus);
        Assert.Equal(0, await Pt().ReconcileVoucherCountAsync(s.VoucherId));   // nothing left to change
    }

    [ReferenceFact]
    public async Task Repointing_the_line_or_unapproving_the_invoice_breaks_the_assignment()
    {
        var s = await Scenario.CreateAsync();
        await Pt().AllocateAsync(s.LineId, E((s.InvA, 3000), (s.InvB, 1000)), Scenario.OwnerB);
        await using (var db = Scenario.Db())
        {
            var b = await db.Set<InvInvoice>().SingleAsync(i => i.Id == s.InvB);
            b.VoucherStatus = "unapproved";
            await db.SaveChangesAsync();
        }
        var row = await Line(s.LineId);
        Assert.Equal(2000m, row.Unassigned);   // the money on B is loose again
        var stale = (await Pt().StaleAllocationsAsync()).Where(r => r.VoucherId == s.VoucherId).ToList();
        Assert.Single(stale);
        Assert.Equal("The invoice is no longer approved, or was deleted.", stale[0].Reason);

        string before = await Scenario.LedgerFingerprintAsync();
        Assert.Equal(1, await Pt().ClearStaleAsync([stale[0].Id]));
        Assert.Equal(before, await Scenario.LedgerFingerprintAsync());
        await using var check = Scenario.Db();
        Assert.Equal(new[] { s.InvA }, await check.Set<PaymentAllocation>().Where(a => a.VoucherLineId == s.LineId).Select(a => a.DocId).ToListAsync());
        Assert.Equal("paid", (await Inv(s.InvA)).PayState);
    }

    [ReferenceFact]
    public async Task Raising_an_invoice_total_makes_it_partially_paid()
    {
        var s = await Scenario.CreateAsync();
        await Pt().AllocateAsync(s.LineId, E((s.InvA, 3000)), Scenario.OwnerB);
        await using (var db = Scenario.Db())
        {
            var a = await db.Set<InvInvoice>().SingleAsync(i => i.Id == s.InvA);
            a.TotalAmount = 5000;
            await db.SaveChangesAsync();
        }
        var (edb, pt) = Scenario.Engine();
        await using (edb)
            await pt.SyncInvoiceAsync(Sales, s.InvA);
        var row = await Inv(s.InvA);
        Assert.Equal(("partial", 2000m), (row.PayState, row.Outstanding));
        await using var check = Scenario.Db();
        Assert.Equal("partial", (await check.Set<InvInvoice>().AsNoTracking().SingleAsync(i => i.Id == s.InvA)).PaymentStatus);
    }

    [ReferenceFact]
    public async Task Assignment_and_force_close_leave_the_general_ledger_untouched()
    {
        var s = await Scenario.CreateAsync();
        string before = await Scenario.LedgerFingerprintAsync();
        await Pt().AllocateAsync(s.LineId, E((s.InvA, 3000), (s.InvB, 1500)), Scenario.OwnerB);
        Assert.Equal(before, await Scenario.LedgerFingerprintAsync());
        await Pt().ForceCloseAsync(s.LineId, null, "advance", Scenario.OwnerB);
        await Pt().ReleaseForceAsync(s.LineId);
        await using var db = Scenario.Db();
        int alloc = await db.Set<PaymentAllocation>().Where(a => a.VoucherLineId == s.LineId).Select(a => a.Id).FirstAsync();
        await Pt().UnallocateAsync(alloc);
        Assert.Equal(before, await Scenario.LedgerFingerprintAsync());
        // A structural guard: the allocation row carries no posting columns.
        var cols = db.Model.FindEntityType(typeof(PaymentAllocation))!.GetProperties().Select(p => p.GetColumnName()).ToHashSet();
        Assert.Empty(cols.Intersect(["debit", "credit", "account_id", "journal_id", "ledger_entry_id", "voucher_type"]));
    }

    [ReferenceFact]
    public async Task A_return_settles_part_of_its_invoice()
    {
        var s = await Scenario.CreateAsync();
        int rid = await Scenario.ReturnAsync(s.InvA, s.CustomerId, 500, $"TRK-CN-{s.Tag}");
        var row = await Inv(s.InvA);
        Assert.Equal((500m, 2500m, "partial"), (row.Credited, row.Outstanding, row.PayState));
        var open = (await Pt().OpenInvoicesAsync("customer", s.CustomerId)).Single(i => i.Id == s.InvA);
        Assert.Equal(2500m, open.Assignable);
        var e = await Assert.ThrowsAsync<AllocationException>(() => Pt().AllocateAsync(s.LineId, E((s.InvA, 3000)), Scenario.OwnerB));
        Assert.Contains("2,500", e.Message);
        await Pt().AllocateAsync(s.LineId, E((s.InvA, 2500)), Scenario.OwnerB);
        Assert.Equal("paid", (await Inv(s.InvA)).PayState);
        var story = await Pt().HistoryForDocAsync(Sales, s.InvA);
        Assert.Equal(["return", "money"], story.Select(h => h.Kind));
        Assert.Equal(rid, story[0].Id);
        Assert.Equal("Credit note", story[0].VoucherType);
        Assert.Equal(0m, story[^1].BalanceAfter);
    }

    [ReferenceFact]
    public async Task A_fully_returned_invoice_is_settled_and_not_offered()
    {
        var s = await Scenario.CreateAsync();
        await Scenario.ReturnAsync(s.InvA, s.CustomerId, 3000, $"TRK-CN-{s.Tag}");
        Assert.Equal("paid", (await Inv(s.InvA)).PayState);
        Assert.DoesNotContain(s.InvA, (await Pt().OpenInvoicesAsync("customer", s.CustomerId)).Select(i => i.Id));
        var all = await Pt().InvoiceRowsAsync(Sales);
        Assert.Equal(all.Sum(r => r.Outstanding), OutstandingSummary.Of(all).Outstanding);
    }

    [ReferenceFact]
    public async Task Auto_match_settles_the_oldest_invoices_first_posts_nothing_and_is_idempotent()
    {
        var s = await Scenario.CreateAsync();
        string before = await Scenario.LedgerFingerprintAsync();
        var result = await Pt().AutoMatchAsync("customer", s.CustomerId, Scenario.OwnerB);
        Assert.Equal(new MatchResult(1, 1, 0, 2, 5000m), result);
        Assert.Equal("paid", (await Inv(s.InvA)).PayState);
        var b = await Inv(s.InvB);
        Assert.Equal((2000m, "partial"), (b.Settled, b.PayState));
        Assert.Equal("assigned", (await Line(s.LineId)).State);
        Assert.Equal(0m, (await Pt().AutoMatchAsync("customer", s.CustomerId, Scenario.OwnerB)).Amount);
        Assert.Equal(before, await Scenario.LedgerFingerprintAsync());
        await using var db = Scenario.Db();
        Assert.All(await db.Set<PaymentAllocation>().Where(a => a.VoucherLineId == s.LineId).ToListAsync(),
                   a => Assert.Equal("Matched oldest first", a.Note));
    }

    [ReferenceFact]
    public async Task Auto_match_leaves_existing_splits_and_closed_money_alone()
    {
        var s = await Scenario.CreateAsync();
        await Pt().AllocateAsync(s.LineId, E((s.InvB, 1000)), Scenario.OwnerB);
        await Pt().ForceCloseAsync(s.LineId, 500m, "rounding", Scenario.OwnerB);
        await Pt().AutoMatchAsync("customer", s.CustomerId, Scenario.OwnerB);
        var row = await Line(s.LineId);
        Assert.Equal((500m, 0m), (row.Forced, row.Unassigned));
        Assert.Equal(3000m, (await Inv(s.InvA)).Settled);
        Assert.Equal(1500m, (await Inv(s.InvB)).Settled);
    }

    [ReferenceFact]
    public async Task Feed_and_register_filtering_in_memory_match_the_query()
    {
        var s = await Scenario.CreateAsync();
        var (rows, _) = await Pt().FeedSnapshotAsync();
        foreach (var f in new[]
                 {
                     new FeedFilter { States = ["unassigned"] }, new FeedFilter { Flow = "receipt" },
                     new FeedFilter { PartyType = "customer", PartyId = s.CustomerId }, new FeedFilter { Search = "TRK-CRV" },
                     new FeedFilter { Search = "nothing-like-this" },
                 })
            Assert.Equal((await Pt().FeedRowsAsync(f)).Select(r => r.LineId), PaymentTracking.FilterFeed(rows, f).Select(r => r.LineId));
        var inv = await Pt().InvoiceRowsAsync(Sales);
        foreach (var f in new[]
                 {
                     new InvoiceFilter { PayStates = ["unpaid"] }, new InvoiceFilter { OverdueOnly = true },
                     new InvoiceFilter { Search = s.NumA }, new InvoiceFilter { PartyId = s.CustomerId },
                 })
            Assert.Equal((await Pt().InvoiceRowsAsync(Sales, f)).Select(r => r.Id), PaymentTracking.FilterInvoices(inv, f).Select(r => r.Id));
    }

    [ReferenceFact]
    public async Task The_contract_drops_a_deleted_vouchers_assignments_and_refreshes_its_invoices()
    {
        var s = await Scenario.CreateAsync();
        await Pt().AllocateAsync(s.LineId, E((s.InvA, 3000)), Scenario.OwnerB);
        var (db, pt) = Scenario.Engine();
        await using (db)
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await ((IPaymentTracking)pt).DropForVoucherAsync(s.VoucherId);
            await tx.CommitAsync();
        }
        await using var check = Scenario.Db();
        Assert.False(await check.Set<PaymentAllocation>().AnyAsync(a => a.VoucherId == s.VoucherId));
        var inv = await check.Set<InvInvoice>().AsNoTracking().SingleAsync(i => i.Id == s.InvA);
        Assert.Equal(("unpaid", 0m), (inv.PaymentStatus, inv.PaidAmount!.Value));

        await Pt().AllocateAsync(s.LineId, E((s.InvB, 1000)), Scenario.OwnerB);
        Assert.Equal(1, await ((IPaymentTracking)Pt()).DropForDocAsync(Sales, s.InvB));
    }
}
