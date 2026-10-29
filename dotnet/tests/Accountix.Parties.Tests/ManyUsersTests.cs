using System.Net;
using System.Text.Json;
using Accountix.Core.Entities;
using Accountix.Parties.Services;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Parties.Tests;

/// <summary>
/// Several people assigning money in one company at the same instant (tests/concurrency/test_concurrent_users.py
/// test_receipts_assigned_together_never_overpay_an_invoice, and the rest of the tracking writes): the receipt
/// line is locked, then the invoices, as Python does; auto-match is queued per company.
/// </summary>
public class ManyUsersTests
{
    private const int N = 6;

    private static Task<ModuleHost> HostAsync() =>
        ModuleHost.StartAsync(typeof(PartiesModule).Assembly, typeof(Accountix.Accounting.AccountingModule).Assembly);

    private sealed record Party(int CustomerId, int AccountId, string Tag);

    private static async Task<Party> PartyAsync()
    {
        string tag = Scenario.NewTag();
        await using var db = Scenario.Db();
        var (cid, _, acct, _) = await Scenario.CustomerAsync(db, tag);
        return new Party(cid, acct, tag);
    }

    private static async Task<int> InvoiceAsync(Party p, decimal total, int daysAgo)
    {
        await using var db = Scenario.Db();
        return await Scenario.InvoiceAsync(db, p.CustomerId, $"RACE-{p.Tag}-{Guid.NewGuid():N}"[..30], total, DateTime.UtcNow.Date.AddDays(-daysAgo), null);
    }

    private static async Task<int> ReceiptAsync(Party p, decimal amount, int daysAgo = 0)
    {
        await using var db = Scenario.Db();
        return (await Scenario.ReceiptAsync(db, p.AccountId, amount, $"RCV-{p.Tag}-{Guid.NewGuid():N}"[..30], DateTime.UtcNow.Date.AddDays(-daysAgo))).LineId;
    }

    private static async Task<decimal> OnInvoiceAsync(int invoiceId)
    {
        await using var db = Scenario.Db();
        return await db.Set<PaymentAllocation>().Where(a => a.DocType == "SI" && a.DocId == invoiceId).SumAsync(a => a.Amount);
    }

    private static async Task<decimal> OnLineAsync(int lineId)
    {
        await using var db = Scenario.Db();
        return await db.Set<PaymentAllocation>().Where(a => a.VoucherLineId == lineId).SumAsync(a => a.Amount);
    }

    [ReferenceFact]
    public async Task Six_receipts_assigned_to_one_invoice_at_once_never_overpay_it()
    {
        var p = await PartyAsync();
        int inv = await InvoiceAsync(p, 1500m, 3);
        var lines = new List<int>();
        for (int i = 0; i < N; i++)
            lines.Add(await ReceiptAsync(p, 1000m));
        await using var host = await HostAsync();
        var clients = host.ClientsFor(Scenario.OwnerB, Scenario.CompanyB, N);
        var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync("/invoicing/tracking/")));
        var rs = await Race.RunAsync(N, i => clients[i].PostJsonWithTokenAsync($"/invoicing/tracking/api/assign/{lines[i]}",
            new { entries = new[] { new { doc_id = inv, amount = 1000 } } }, tokens[i]));

        Race.NoServerErrors(rs);
        Assert.Equal(1, rs.Count(r => r.StatusCode == HttpStatusCode.OK));
        foreach (var r in rs.Where(r => r.StatusCode != HttpStatusCode.OK))
            Assert.Contains("only has 500.00 outstanding", await r.Content.ReadAsStringAsync());
        Assert.Equal(1000m, await OnInvoiceAsync(inv));
        var row = await Scenario.Engine().Pt.InvoiceRowAsync("SI", inv);
        Assert.Equal(("partial", 500m), (row!.PayState, row.Outstanding));
    }

    [ReferenceFact]
    public async Task Six_matches_at_once_assign_each_amount_once()
    {
        var p = await PartyAsync();
        int[] invs = [await InvoiceAsync(p, 1000m, 30), await InvoiceAsync(p, 2000m, 20), await InvoiceAsync(p, 3000m, 10)];
        int[] lines = [await ReceiptAsync(p, 2500m, 5), await ReceiptAsync(p, 2500m, 4), await ReceiptAsync(p, 2500m, 3)];
        await using var host = await HostAsync();
        var clients = host.ClientsFor(Scenario.OwnerB, Scenario.CompanyB, N);
        var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync("/invoicing/tracking/")));
        var rs = await Race.RunAsync(N, i => clients[i].PostJsonWithTokenAsync("/invoicing/tracking/api/auto-match",
            new { party = $"customer:{p.CustomerId}" }, tokens[i]));

        Race.NoServerErrors(rs);
        var bodies = await Task.WhenAll(rs.Select(async r => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement));
        Assert.Equal(6000.0, bodies.Sum(b => b.GetProperty("amount").GetDouble()));
        Assert.Equal(1, bodies.Count(b => b.GetProperty("lines").GetInt32() > 0));
        Assert.Equal([1000m, 2000m, 3000m], await Task.WhenAll(invs.Select(OnInvoiceAsync)));
        Assert.Equal([2500m, 2500m, 1000m], await Task.WhenAll(lines.Select(OnLineAsync)));
    }

    [ReferenceFact]
    public async Task Six_people_detaching_one_assignment_detach_it_once()
    {
        var s = await Scenario.CreateAsync();
        await Scenario.Engine().Pt.AllocateAsync(s.LineId, [new(s.InvA, 3000m), new(s.InvB, 1000m)], Scenario.OwnerB);
        await using var db = Scenario.Db();
        int alloc = await db.Set<PaymentAllocation>().Where(a => a.DocId == s.InvA && a.VoucherLineId == s.LineId).Select(a => a.Id).SingleAsync();
        await using var host = await HostAsync();
        var clients = host.ClientsFor(Scenario.OwnerB, Scenario.CompanyB, N);
        var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync("/invoicing/tracking/")));
        // Half detach the A part; the other half re-split the same receipt at the same moment.
        var rs = await Race.RunAsync(N, i => i % 2 == 0
            ? clients[i].PostJsonWithTokenAsync($"/invoicing/tracking/api/unassign/{alloc}", new { }, tokens[i])
            : clients[i].PostJsonWithTokenAsync($"/invoicing/tracking/api/assign/{s.LineId}",
                new { entries = new[] { new { doc_id = s.InvB, amount = 4000 } } }, tokens[i]));

        Race.NoServerErrors(rs);
        var unassigns = rs.Where((_, i) => i % 2 == 0).ToList();
        Assert.Equal(1, unassigns.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(unassigns.Where(r => r.StatusCode != HttpStatusCode.OK),
                   r => Assert.Contains("That assignment no longer exists.", r.Content.ReadAsStringAsync().Result));
        Assert.True(await OnLineAsync(s.LineId) <= 5000m);
        Assert.Equal(0m, await OnInvoiceAsync(s.InvA));
        await using var check = Scenario.Db();
        Assert.Equal("unpaid", (await check.Set<InvInvoice>().AsNoTracking().SingleAsync(i => i.Id == s.InvA)).PaymentStatus);
    }
}
