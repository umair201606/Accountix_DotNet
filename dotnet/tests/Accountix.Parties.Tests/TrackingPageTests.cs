using System.Net;
using System.Text.Json;
using Accountix.Core.Entities;
using Accountix.Parties.Services;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;
using static Accountix.Parties.Services.TrackingRules;

namespace Accountix.Parties.Tests;

/// <summary>The Invoice Tracking pages and their JSON API through the .NET app (test_payment_tracking.py, HTTP half).</summary>
public class TrackingPageTests
{
    private static Task<ModuleHost> HostAsync() =>
        ModuleHost.StartAsync(typeof(PartiesModule).Assembly, typeof(Accountix.Accounting.AccountingModule).Assembly);

    private static PageClient Owner(ModuleHost host) => host.ClientFor(Scenario.OwnerB, Scenario.CompanyB);

    private static async Task<(HttpStatusCode Status, JsonElement Json)> PostJson(PageClient c, string url, object payload)
    {
        var r = await c.PostJsonAsync(url, payload, "/invoicing/tracking/");
        string body = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, JsonDocument.Parse(body).RootElement.Clone());
    }

    private static async Task<string> Html(PageClient c, string url)
    {
        var r = await c.GetAsync(url);
        string body = WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync());
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url}: {(int)r.StatusCode}\n{body[..Math.Min(body.Length, 3000)]}");
        return body;
    }

    [ReferenceFact]
    public async Task Tracking_pages_render_and_the_filter_chips_round_trip()
    {
        var s = await Scenario.CreateAsync();
        await using var host = await HostAsync();
        var c = Owner(host);
        string feed = await Html(c, "/invoicing/tracking/");
        Assert.Contains("id=\"feedFilters\"", feed);
        Assert.Contains("Match oldest first", feed);
        await Html(c, "/invoicing/tracking/sales");
        await Html(c, "/invoicing/tracking/purchases");
        await Html(c, "/invoicing/tracking/?state=");
        foreach (string state in new[] { "unassigned", "partial", "assigned", "forced" })
            await Html(c, $"/invoicing/tracking/?state={state}");
        await Html(c, "/invoicing/tracking/sales?pay=paid&pay=partial&overdue=1");
        await Html(c, "/invoicing/tracking/?page=99");
        string mine = await Html(c, $"/invoicing/tracking/?party=customer:{s.CustomerId}");
        Assert.Contains(s.VoucherNumber, mine);
        Assert.Contains($"For {s.CustomerName}:", mine);
    }

    [ReferenceFact]
    public async Task Assign_workspace_and_invoice_history_render()
    {
        var s = await Scenario.CreateAsync();
        await using var host = await HostAsync();
        var c = Owner(host);
        string ws = await Html(c, $"/invoicing/tracking/assign/{s.LineId}");
        Assert.Contains(s.NumA, ws);
        Assert.Contains("id=\"allocTable\"", ws);
        string hist = await Html(c, $"/invoicing/tracking/invoice/SI/{s.InvA}");
        Assert.Contains(s.NumA, hist);
        Assert.Contains("Apply 3,000", hist);   // the waiting receipt, offered as the invoice's whole balance
        var missing = await c.GetAsync("/invoicing/tracking/invoice/XX/1");
        Assert.Equal(HttpStatusCode.Redirect, missing.StatusCode);
        Assert.Equal("/invoicing/tracking/", missing.Headers.Location!.OriginalString);
        var notALine = await c.GetAsync("/invoicing/tracking/assign/999999999");
        Assert.Equal("/invoicing/tracking/", notALine.Headers.Location!.OriginalString);
    }

    [ReferenceFact]
    public async Task Force_close_api_round_trip_and_the_forced_row_renders()
    {
        var s = await Scenario.CreateAsync();
        await using var host = await HostAsync();
        var c = Owner(host);
        var bad = await PostJson(c, $"/invoicing/tracking/api/force-close/{s.LineId}", new { reason = "" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        Assert.False(bad.Json.GetProperty("ok").GetBoolean());

        var (status, json) = await PostJson(c, $"/invoicing/tracking/api/force-close/{s.LineId}", new { amount = 500, reason = "rounding" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("partial", json.GetProperty("row").GetProperty("state").GetString());
        Assert.Equal(500.0, json.GetProperty("row").GetProperty("forced").GetDouble());
        Assert.Equal("Closed as not applicable", json.GetProperty("message").GetString());
        string ws = await Html(c, $"/invoicing/tracking/assign/{s.LineId}");
        Assert.Contains("rounding", ws);
        Assert.DoesNotContain("/invoicing/tracking/invoice/NA/0", ws);

        var all = await PostJson(c, $"/invoicing/tracking/api/force-close/{s.LineId}", new { reason = "customer advance" });
        Assert.Equal("forced", all.Json.GetProperty("row").GetProperty("state").GetString());
        string feed = await Html(c, $"/invoicing/tracking/?state=forced&party=customer:{s.CustomerId}");
        Assert.Contains("customer advance", feed);
        Assert.Contains("No invoice", feed);

        var reopen = await PostJson(c, $"/invoicing/tracking/api/release-force/{s.LineId}", new { });
        Assert.Equal("unassigned", reopen.Json.GetProperty("row").GetProperty("state").GetString());
    }

    [ReferenceFact]
    public async Task Assign_api_saves_a_split_and_rejects_a_bad_one()
    {
        var s = await Scenario.CreateAsync();
        await using var host = await HostAsync();
        var c = Owner(host);
        var bad = await PostJson(c, $"/invoicing/tracking/api/assign/{s.LineId}", new { entries = new[] { new { doc_id = s.InvA, amount = 99999 } } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        Assert.Equal($"{s.NumA} only has 3,000.00 outstanding — 99,999.00 cannot be assigned to it.", bad.Json.GetProperty("error").GetString());

        var ok = await PostJson(c, $"/invoicing/tracking/api/assign/{s.LineId}",
            new { entries = new object[] { new { doc_id = s.InvA, amount = 3000 }, new { doc_id = s.InvB, amount = "1500.50" } }, note = " by hand " });
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        Assert.Equal("Assignment saved", ok.Json.GetProperty("message").GetString());
        Assert.Equal(4500.5, ok.Json.GetProperty("row").GetProperty("assigned").GetDouble());
        Assert.Equal(2, ok.Json.GetProperty("splits").GetArrayLength());
        Assert.Equal("by hand", ok.Json.GetProperty("splits")[0].GetProperty("note").GetString());

        var item = await c.GetAsync($"/invoicing/tracking/api/feed-item/{s.LineId}");
        var itemJson = JsonDocument.Parse(await item.Content.ReadAsStringAsync()).RootElement;
        Assert.True(itemJson.GetProperty("ok").GetBoolean());
        Assert.Equal(2, itemJson.GetProperty("invoices").GetArrayLength());
        var split = JsonDocument.Parse(await (await c.GetAsync($"/invoicing/tracking/api/auto-split/{s.LineId}")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(5000.0, split.GetProperty("split").EnumerateArray().Sum(x => x.GetProperty("amount").GetDouble()));
        var open = JsonDocument.Parse(await (await c.GetAsync($"/invoicing/tracking/api/open-invoices?party_type=customer&party_id={s.CustomerId}")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, open.GetProperty("invoices").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/invoicing/tracking/api/open-invoices?party_type=x&party_id=1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/invoicing/tracking/api/feed-item/999999999")).StatusCode);

        await using var db = Scenario.Db();
        int alloc = await db.Set<PaymentAllocation>().Where(a => a.VoucherLineId == s.LineId && a.DocId == s.InvB).Select(a => a.Id).SingleAsync();
        var un = await PostJson(c, $"/invoicing/tracking/api/unassign/{alloc}", new { });
        Assert.Equal(("Assignment removed", "SI", s.InvB), (un.Json.GetProperty("message").GetString(), un.Json.GetProperty("doc_type").GetString(),
                                                            un.Json.GetProperty("doc_id").GetInt32()));
        var gone = await PostJson(c, $"/invoicing/tracking/api/unassign/{alloc}", new { });
        Assert.Equal(HttpStatusCode.BadRequest, gone.Status);
    }

    [ReferenceFact]
    public async Task Apply_here_and_match_oldest_first_over_http()
    {
        var s = await Scenario.CreateAsync();
        await using var host = await HostAsync();
        var c = Owner(host);
        var apply = await PostJson(c, $"/invoicing/tracking/api/apply/{s.LineId}/SI/{s.InvB}", new { });
        Assert.Equal(HttpStatusCode.OK, apply.Status);
        Assert.Equal($"Applied 4,000.00 to {s.NumB}", apply.Json.GetProperty("message").GetString());
        var match = await PostJson(c, "/invoicing/tracking/api/auto-match", new { party = $"customer:{s.CustomerId}" });
        Assert.True(match.Json.GetProperty("ok").GetBoolean());
        Assert.Equal("Matched 1 receipt to 1 invoice, oldest first.", match.Json.GetProperty("message").GetString());
        var pt = Scenario.Engine().Pt;
        Assert.Equal(4000m, (await pt.InvoiceRowAsync(Sales, s.InvB))!.Settled);
        Assert.Equal(1000m, (await pt.InvoiceRowAsync(Sales, s.InvA))!.Settled);
        Assert.Equal("assigned", (await pt.FeedRowAsync(s.LineId))!.State);
        var nothing = await PostJson(c, "/invoicing/tracking/api/auto-match", new { party = $"customer:{s.CustomerId}" });
        Assert.Equal("Nothing to match: no open receipt or payment has an open invoice to settle.", nothing.Json.GetProperty("message").GetString());
        var full = await PostJson(c, $"/invoicing/tracking/api/apply/{s.LineId}/SI/{s.InvA}", new { });
        Assert.Equal(HttpStatusCode.BadRequest, full.Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostJson(c, $"/invoicing/tracking/api/apply/{s.LineId}/XX/{s.InvA}", new { })).Status);
    }

    [ReferenceFact]
    public async Task The_needs_attention_panel_renders_and_clears()
    {
        var s = await Scenario.CreateAsync();
        await using var host = await HostAsync();
        var c = Owner(host);
        await PostJson(c, "/invoicing/tracking/api/clear-stale", new { });   // what earlier tests left broken
        Assert.DoesNotContain("needs attention", await Html(c, "/invoicing/tracking/"));

        await Scenario.Engine().Pt.AllocateAsync(s.LineId, [new(s.InvA, 3000m)], Scenario.OwnerB);
        await using (var db = Scenario.Db())
        {
            var a = await db.Set<InvInvoice>().SingleAsync(i => i.Id == s.InvA);
            a.VoucherStatus = "unapproved";
            await db.SaveChangesAsync();
        }
        string body = await Html(c, "/invoicing/tracking/");
        Assert.Contains("needs attention", body);
        Assert.Contains(s.NumA, body);
        Assert.Contains("no longer approved", body);
        var cleared = await PostJson(c, "/invoicing/tracking/api/clear-stale", new { });
        Assert.Equal(1, cleared.Json.GetProperty("cleared").GetInt32());
        Assert.Equal("Cleared 1 broken assignment", cleared.Json.GetProperty("message").GetString());
        Assert.DoesNotContain("needs attention", await Html(c, "/invoicing/tracking/"));
        var none = await PostJson(c, "/invoicing/tracking/api/clear-stale", new { });
        Assert.Equal("There was nothing to clear", none.Json.GetProperty("message").GetString());
    }

    [ReferenceFact]
    public async Task Registers_open_on_what_is_still_owed_sort_and_show_returns()
    {
        var s = await Scenario.CreateAsync();
        await Scenario.Engine().Pt.AllocateAsync(s.LineId, [new(s.InvA, 3000m)], Scenario.OwnerB);
        await using var host = await HostAsync();
        var c = Owner(host);
        string open = await Html(c, $"/invoicing/tracking/sales?party_id={s.CustomerId}");
        Assert.Contains(s.NumB, open);
        Assert.DoesNotContain(s.NumA, open);
        string every = await Html(c, $"/invoicing/tracking/sales?pay=&party_id={s.CustomerId}");
        Assert.Contains(s.NumA, every);
        Assert.Contains(s.NumB, every);
        foreach (string sort in new[] { "oldest", "late", "owed" })
            await Html(c, $"/invoicing/tracking/sales?sort={sort}&pay=");

        await Scenario.ReturnAsync(s.InvB, s.CustomerId, 700, $"TRK-CN-HTTP-{s.Tag}");
        string hist = await Html(c, $"/invoicing/tracking/invoice/SI/{s.InvB}");
        Assert.Contains("Credit note", hist);
        Assert.Contains($"TRK-CN-HTTP-{s.Tag}", hist);
        Assert.Contains("returned", await Html(c, $"/invoicing/tracking/sales?pay=&party_id={s.CustomerId}"));
    }

    [ReferenceFact]
    public async Task Without_payment_tracking_rights_the_pages_and_the_api_refuse()
    {
        var s = await Scenario.CreateAsync();
        await using var host = await HostAsync();
        var c = host.ClientFor(Reference.UserId(Reference.Email("restricted")), Scenario.CompanyB);
        var page = await c.GetAsync("/invoicing/tracking/");
        Assert.NotEqual(HttpStatusCode.OK, page.StatusCode);
        var api = await c.GetAsync($"/invoicing/tracking/api/feed-item/{s.LineId}");
        Assert.Equal(HttpStatusCode.Forbidden, api.StatusCode);
    }

    [ReferenceFact]
    public async Task List_pages_run_a_fixed_number_of_queries()
    {
        await Scenario.CreateAsync();
        await using var host = await HostAsync();
        var c = Owner(host);
        foreach (string url in new[]
                 {
                     "/invoicing/tracking/", "/invoicing/tracking/sales?pay=", "/invoicing/tracking/purchases?pay=",
                     "/inventory/customers/", "/inventory/suppliers/", "/invoicing/registers/?view=1&mode=year",
                     "/invoicing/registers/data?mode=year",
                 })
            (await host.MeasureAsync(c, url, runs: 1)).Within(maxMilliseconds: 5000, maxQueries: 40);
    }

    [ReferenceFact]
    public async Task Script_blocks_carry_no_html_encoded_text()
    {
        var s = await Scenario.CreateAsync();
        await using var host = await HostAsync();
        var c = Owner(host);
        foreach (string url in new[]
                 {
                     "/invoicing/tracking/", $"/invoicing/tracking/assign/{s.LineId}", "/invoicing/tracking/sales",
                     $"/invoicing/tracking/invoice/SI/{s.InvA}", "/invoicing/registers/?view=1&mode=year", "/inventory/customers/batch",
                     "/inventory/suppliers/batch",
                 })
        {
            string raw = await (await c.GetAsync(url)).Content.ReadAsStringAsync();
            var scripts = System.Text.RegularExpressions.Regex.Matches(raw, "<script>(.*?)</script>", System.Text.RegularExpressions.RegexOptions.Singleline);
            Assert.NotEmpty(scripts);
            foreach (System.Text.RegularExpressions.Match m in scripts)
                foreach (string bad in new[] { "&quot;", "&amp;", "&#x27;", "&lt;", "&gt;" })
                    Assert.False(m.Groups[1].Value.Contains(bad), $"{url}: {bad} inside a script block");
        }
    }

    [ReferenceFact]
    public async Task Unapproving_and_deleting_a_voucher_in_accounting_refreshes_the_invoices_it_settled()
    {
        var s = await Scenario.CreateAsync();
        await Scenario.Engine().Pt.AllocateAsync(s.LineId, [new(s.InvA, 3000m), new(s.InvB, 500m)], Scenario.OwnerB);
        await using var host = await HostAsync();
        var c = Owner(host);
        async Task<string> Cache()
        {
            await using var db = Scenario.Db();
            return string.Join(",", (await db.Set<InvInvoice>().AsNoTracking().Where(i => i.Id == s.InvA || i.Id == s.InvB).OrderBy(i => i.Id)
                .ToListAsync()).Select(i => $"{i.PaymentStatus}:{i.PaidAmount:0.00}"));
        }
        Assert.Equal("paid:3000.00,partial:500.00", await Cache());
        var un = await c.PostFormAsync($"/accounting/vouchers/{s.VoucherId}/unapprove", [], $"/accounting/vouchers/{s.VoucherId}");
        Assert.Equal(HttpStatusCode.Redirect, un.StatusCode);
        Assert.Equal("unpaid:0.00,unpaid:0.00", await Cache());     // IPaymentTracking.ReconcileVoucherAsync
        var del = await c.PostFormAsync($"/accounting/vouchers/{s.VoucherId}/delete", [], $"/accounting/vouchers/{s.VoucherId}");
        Assert.Equal(HttpStatusCode.Redirect, del.StatusCode);
        await using var check = Scenario.Db();
        Assert.False(await check.Set<PaymentAllocation>().AnyAsync(a => a.VoucherId == s.VoucherId));   // DropForVoucherAsync
        Assert.False(await check.Set<AccountingVoucher>().AnyAsync(v => v.Id == s.VoucherId));
    }
}
