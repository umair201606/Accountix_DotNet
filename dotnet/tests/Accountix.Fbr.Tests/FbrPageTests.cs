using System.Net;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Fbr.Tests;

/// <summary>The pages and JSON answers of /fbr, the refusals, and that FBR is reached only through the fake.</summary>
public class FbrPageTests
{
    private static Task<ModuleHost> HostAsync() => ModuleHost.StartAsync(typeof(FbrModule).Assembly, typeof(FakeFbrModule).Assembly);

    [ReferenceFact]
    public async Task Without_settings_the_dashboard_says_so_and_send_is_refused()
    {
        await FbrData.ConfigureAsync(active: false);
        try
        {
            await using var host = await HostAsync();
            var c = host.ClientFor(FbrData.OwnerB, FbrData.CompanyB);
            var html = await (await c.GetAsync("/fbr/")).Content.ReadAsStringAsync();
            Assert.Contains("FBR Digital Invoicing is not configured yet.", html);
            Assert.Contains("FBR module is not configured. Ask an administrator to configure FBR settings first.", html);
            Assert.Contains("href=\"/fbr/settings/\" class=\"btn btn-accent\"", html);
            Assert.DoesNotContain("<table>", html);
            Assert.Contains("id=\"jsonModal\"", html);

            int id = await FbrData.SimpleInvoiceAsync("off");
            FakeFbr.Reset();
            var r = await c.PostFormAsync($"/fbr/submit/{id}", [], "/fbr/");
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Equal("{\"error\":\"FBR not configured\",\"ok\":false}\n", await r.Content.ReadAsStringAsync());
            Assert.Empty(FakeFbr.Calls);
        }
        finally
        {
            await FbrData.ConfigureAsync();
        }
    }

    [ReferenceFact]
    public async Task The_dashboard_lists_submissions_newest_first_and_filters_by_status()
    {
        await FbrData.ConfigureAsync();
        int ok = await FbrData.SimpleInvoiceAsync("list-ok");
        int bad = await FbrData.SimpleInvoiceAsync("list-bad");
        await using var host = await HostAsync();
        var c = host.ClientFor(FbrData.OwnerB, FbrData.CompanyB);
        FakeFbr.Reset();
        FakeFbr.Then(FakeFbr.Json(200, """{"fbrInvoiceNumber": "7000007DI-LIST-1", "submissionId": "S"}"""));
        FakeFbr.Then(FakeFbr.Json(400, """{"error": "Buyer NTN <invalid> & \"wrong\""}"""));
        Assert.Equal(HttpStatusCode.OK, (await c.PostFormAsync($"/fbr/submit/{ok}", [], "/fbr/")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostFormAsync($"/fbr/submit/{bad}", [], "/fbr/")).StatusCode);
        int okRec = (await FbrData.RecordsAsync(ok)).Single().Id, badRec = (await FbrData.RecordsAsync(bad)).Single().Id;

        string html = await (await c.GetAsync("/fbr/")).Content.ReadAsStringAsync();
        Assert.Contains("<span class=\"badge badge-success\" style=\"font-size:11px;\">", html);
        Assert.Contains("SANDBOX", html);
        Assert.Contains("ACTIVE", html);
        Assert.Contains($"<a href=\"/inventory/invoices/{ok}\"", html);
        Assert.Contains("7000007DI-LIST-1", html);
        Assert.True(html.IndexOf($"<td>{badRec}</td>", StringComparison.Ordinal) < html.IndexOf($"<td>{okRec}</td>", StringComparison.Ordinal));
        Assert.Contains($"onclick=\"retrySubmit({badRec})\"", html);
        Assert.DoesNotContain($"onclick=\"retrySubmit({okRec})\"", html);
        Assert.Contains("Buyer NTN &lt;invalid&gt; &amp; &quot;wrong&quot;", html);            // escaped
        Assert.Contains("<span class=\"badge badge-danger\">Failed</span>", html);
        Assert.Contains("class=\"nav-item active\"", html);

        string failed = await (await c.GetAsync("/fbr/?status=failed")).Content.ReadAsStringAsync();
        Assert.Contains("<option value=\"failed\" selected=\"selected\">Failed</option>", failed);
        Assert.Contains($"<td>{badRec}</td>", failed);
        Assert.DoesNotContain($"<td>{okRec}</td>", failed);
        string none = await (await c.GetAsync("/fbr/?status=processing")).Content.ReadAsStringAsync();
        Assert.Contains("No FBR submissions yet. Submit an invoice from the sales invoice page.", none);
    }

    [ReferenceFact]
    public async Task Send_sends_the_payload_with_the_token_and_keeps_the_record()
    {
        await FbrData.ConfigureAsync(token: "tok-abc", environment: "live");
        try
        {
            int id = await FbrData.SimpleInvoiceAsync("send");
            await using var host = await HostAsync();
            var c = host.ClientFor(FbrData.OwnerB, FbrData.CompanyB);
            FakeFbr.Reset(FakeFbr.Json(200, """{"fbrInvoiceNumber": "7000007DI1", "submissionId": "SUB-1"}"""));
            var r = await c.PostFormAsync($"/fbr/submit/{id}", [], "/fbr/");
            string body = await r.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var call = Assert.Single(FakeFbr.Calls);
            Assert.Equal("https://di.fbr.gov.pk/di/api/sales/send", call.Url);
            Assert.Equal("Bearer tok-abc", call.Authorization);
            Assert.Equal("application/json", call.ContentType);
            Assert.StartsWith("{\"fbrInvoice\": {\"sellerNTN\": \"1234567-8\"", call.Body);
            var rec = (await FbrData.RecordsAsync(id)).Single();
            Assert.Equal(("success", "7000007DI1", "SUB-1", 1, "S"), (rec.Status, rec.FbrInvoiceNumber, rec.FbrSubmissionId, rec.AttemptCount, rec.InvoiceType));
            Assert.Equal($"{{\"fbr_invoice_number\":\"7000007DI1\",\"message\":\"Invoice submitted to FBR successfully.\",\"ok\":true,\"record_id\":{rec.Id}}}\n", body);

            // The record's JSON, as the dashboard's JSON button shows it.
            var json = await (await c.GetAsync($"/fbr/record/{rec.Id}")).Content.ReadAsStringAsync();
            Assert.Contains("\"request_json\":\"{\\n  \\\"fbrInvoice\\\": {\\n", json);
            Assert.Contains("\"submitted_by\":\"", json);
            // An unapproved invoice and a missing one are refused without calling FBR.
            FakeFbr.Reset();
            await FbrData.SetAsync(id, i => i.VoucherStatus = "unapproved");
            await FbrData.DeleteRecordsAsync(id);
            var draft = await c.PostFormAsync($"/fbr/submit/{id}", [], "/fbr/");
            Assert.Contains("Only approved invoices can be submitted to FBR", await draft.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.NotFound, (await c.PostFormAsync("/fbr/submit/987654321", [], "/fbr/")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/fbr/record/987654321")).StatusCode);
            Assert.Empty(FakeFbr.Calls);
        }
        finally
        {
            await FbrData.ConfigureAsync();
        }
    }

    [ReferenceFact]
    public async Task A_network_failure_or_a_timeout_is_recorded_as_a_failed_attempt()
    {
        await FbrData.ConfigureAsync();
        int id = await FbrData.SimpleInvoiceAsync("net");
        await using var host = await HostAsync();
        var c = host.ClientFor(FbrData.OwnerB, FbrData.CompanyB);
        FakeFbr.Reset(new FakeFbr.Reply(0, "", Fail: true));
        var r = await c.PostFormAsync($"/fbr/submit/{id}", [], "/fbr/");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var rec = (await FbrData.RecordsAsync(id)).Single();
        Assert.Equal("failed", rec.Status);
        Assert.StartsWith("HTTPSConnectionPool(host='sandbox.fbr.gov.pk', port=443): Max retries exceeded with url: /di/api/sales/send", rec.ErrorMessage);
        Assert.Contains(rec.ErrorMessage!, await r.Content.ReadAsStringAsync());
    }

    [ReferenceFact]
    public async Task Settings_are_for_admins_and_saved_per_company()
    {
        await using var host = await HostAsync();
        var admin = host.ClientFor(FbrData.OwnerB, FbrData.CompanyB);
        await FbrData.ConfigureAsync();
        string page = await (await admin.GetAsync("/fbr/settings/")).Content.ReadAsStringAsync();
        Assert.Contains("<option value=\"sandbox\" selected=\"selected\">Sandbox (Testing)</option>", page);
        Assert.Contains("<input type=\"checkbox\" name=\"is_active\" checked=\"checked\">", page);
        Assert.Contains("value=\"1234567-8\"", page);
        Assert.Contains("id=\"testConnectionBtn\"", page);
        Assert.Contains("href=\"/fbr/settings/\" class=\"nav-item active\"", page);

        var r = await admin.PostFormAsync("/fbr/settings/", [new("environment", "live"), new("access_token", "new-token"), new("seller_ntn", "999")], "/fbr/settings/");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/fbr/settings/", r.Headers.Location!.OriginalString);
        await using (var db = FbrData.Db())
        {
            var cfg = await db.Set<FbrConfig>().OrderBy(x => x.Id).SingleAsync();
            Assert.Equal(("live", "new-token", "999", false, ""), (cfg.Environment, cfg.AccessToken, cfg.SellerNtn, cfg.IsActive, cfg.SellerName));
        }
        FakeFbr.Reset(new FakeFbr.Reply(200, "OK", "text/plain"));
        var tc = await admin.PostFormAsync("/fbr/settings/test-connection", [], "/fbr/settings/");
        Assert.Equal("{\"message\":\"Connection successful: OK\",\"ok\":true}\n", await tc.Content.ReadAsStringAsync());
        Assert.Equal("https://di.fbr.gov.pk/di/api/heartbeat", Assert.Single(FakeFbr.Calls).Url);
        await FbrData.ConfigureAsync();
    }

    /// <summary>
    /// A member who is not an admin, with the module and only the view right
    /// on the dashboard: may look, may not Send (approve), and the settings
    /// are an admin's (flash + hub, and "Admin only" for the test).
    /// </summary>
    [ReferenceFact]
    public async Task A_member_without_rights_or_admin_is_refused_in_pythons_words()
    {
        int uid = FbrData.EmployeeInB, cid = FbrData.CompanyB;
        string? flags;
        await using (var db = FbrData.Db())
        {
            var m = await db.Memberships.SingleAsync(x => x.UserId == uid && x.CompanyId == cid);
            flags = m.ModuleFlags;
            m.SetModuleFlags(new Dictionary<string, bool> { ["fbr"] = true });
            foreach (var p in await db.UserPermissions.Where(p => p.UserId == uid && (p.Resource == "fbr_dashboard" || p.Resource == "fbr_settings")).ToListAsync())
            {
                p.CanView = true;
                p.CanEdit = p.Resource == "fbr_settings";
            }
            await db.SaveChangesAsync();
        }
        try
        {
            await FbrData.ConfigureAsync();
            int id = await FbrData.SimpleInvoiceAsync("rights");
            await using var host = await HostAsync();
            var c = host.ClientFor(uid, cid);
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/fbr/")).StatusCode);
            string dash = await (await c.GetAsync("/fbr/")).Content.ReadAsStringAsync();
            Assert.DoesNotContain("href=\"/fbr/settings/\" class=\"nav-item", dash);     // Settings link is for admins

            FakeFbr.Reset();
            var send = await c.PostFormAsync($"/fbr/submit/{id}", [], "/fbr/");
            Assert.Equal(HttpStatusCode.Redirect, send.StatusCode);
            var token = await c.TokenFromAsync("/fbr/");
            var req = new HttpRequestMessage(HttpMethod.Post, $"/fbr/submit/{id}");
            req.Headers.Add("X-CSRF-Token", token);
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");
            var json = await c.SendAsync(req);
            Assert.Equal(HttpStatusCode.Forbidden, json.StatusCode);
            Assert.Contains("You don't have 'approve' rights for this section.", await json.Content.ReadAsStringAsync());
            Assert.Empty(FakeFbr.Calls);

            var settings = await c.GetAsync("/fbr/settings/");
            Assert.Equal(HttpStatusCode.Redirect, settings.StatusCode);
            Assert.Equal("/dashboard/", settings.Headers.Location!.OriginalString);
            Assert.Contains("Only administrators can access FBR settings.", await (await c.GetAsync("/fbr/")).Content.ReadAsStringAsync());
            var test = await c.PostFormAsync("/fbr/settings/test-connection", [], "/fbr/");
            Assert.Equal(HttpStatusCode.Forbidden, test.StatusCode);
            Assert.Equal("{\"error\":\"Admin only\",\"ok\":false}\n", await test.Content.ReadAsStringAsync());
        }
        finally
        {
            await using var db = FbrData.Db();
            var m = await db.Memberships.SingleAsync(x => x.UserId == uid && x.CompanyId == cid);
            m.ModuleFlags = flags;
            foreach (var p in await db.UserPermissions.Where(p => p.UserId == uid && (p.Resource == "fbr_dashboard" || p.Resource == "fbr_settings")).ToListAsync())
                (p.CanView, p.CanEdit) = (false, false);
            await db.SaveChangesAsync();
        }
    }

    /// <summary>tests/e2e/test_fbr_retry.py: Retry sends the invoice as it is now, and never a reported or unapproved one.</summary>
    [ReferenceFact]
    public async Task Retry_sends_the_invoice_as_it_is_now_and_only_while_it_may_be_reported()
    {
        await FbrData.ConfigureAsync();
        int id = await FbrData.SimpleInvoiceAsync("retry-now");
        await using var host = await HostAsync();
        var c = host.ClientFor(FbrData.OwnerB, FbrData.CompanyB);
        FakeFbr.Reset(FakeFbr.Json(500, """{"error": "down"}"""));
        var first = await c.PostFormAsync($"/fbr/submit/{id}", [], "/fbr/");
        Assert.Equal(HttpStatusCode.BadRequest, first.StatusCode);
        Assert.Contains("\"error\":\"down\"", await first.Content.ReadAsStringAsync());
        int rec = (await FbrData.RecordsAsync(id)).Single().Id;

        // Corrected since: unapproved is refused, then the new figures are what FBR gets.
        await FbrData.SetAsync(id, i => i.VoucherStatus = "unapproved");
        FakeFbr.Reset(FakeFbr.Json(200, """{"fbrInvoiceNumber": "FBR-1", "submissionId": "S1"}"""));
        var draft = await c.PostFormAsync($"/fbr/retry/{rec}", [], "/fbr/");
        Assert.Contains("Only approved invoices can be submitted to FBR", await draft.Content.ReadAsStringAsync());
        Assert.Empty(FakeFbr.Calls);
        await FbrData.SetAsync(id, i => { i.VoucherStatus = "approved"; i.Subtotal = 4321.5m; });
        var ok = await c.PostFormAsync($"/fbr/retry/{rec}", [], "/fbr/");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("\"totalSaleValue\": 4311.5", Assert.Single(FakeFbr.Calls).Body);   // 4,321.50 less the 10.00 discount
        var row = (await FbrData.RecordsAsync(id)).Single();
        Assert.Equal(("success", 2, "FBR-1", ""), (row.Status, row.AttemptCount, row.FbrInvoiceNumber, row.ErrorMessage));
        Assert.Contains("\"totalSaleValue\": 4311.5", row.RequestJson);

        // Reported: a second retry is refused without calling FBR.
        FakeFbr.Reset();
        var again = await c.PostFormAsync($"/fbr/retry/{rec}", [], "/fbr/");
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains("already submitted successfully", await again.Content.ReadAsStringAsync());
        Assert.Empty(FakeFbr.Calls);
    }

    /// <summary>Razor encodes @expr everywhere, scripts included: the pages' scripts must come out as written.</summary>
    [ReferenceFact]
    public async Task The_page_scripts_are_not_html_encoded()
    {
        await FbrData.ConfigureAsync();
        int id = await FbrData.SimpleInvoiceAsync("script");
        await using var host = await HostAsync();
        var c = host.ClientFor(FbrData.OwnerB, FbrData.CompanyB);
        FakeFbr.Reset(FakeFbr.Json(400, """{"error": "it's <bad> & \"worse\""}"""));
        await c.PostFormAsync($"/fbr/submit/{id}", [], "/fbr/");
        foreach (string url in new[] { "/fbr/", "/fbr/settings/" })
        {
            string html = await (await c.GetAsync(url)).Content.ReadAsStringAsync();
            var scripts = System.Text.RegularExpressions.Regex.Matches(html, "<script>(.*?)</script>", System.Text.RegularExpressions.RegexOptions.Singleline);
            Assert.NotEmpty(scripts);
            foreach (System.Text.RegularExpressions.Match m in scripts)
            {
                Assert.DoesNotContain("&quot;", m.Groups[1].Value);
                Assert.DoesNotContain("&amp;", m.Groups[1].Value);
                Assert.DoesNotContain("&#x", m.Groups[1].Value);
            }
            Assert.Contains(url == "/fbr/" ? "fetch('/fbr/retry/0'.replace('0', recordId), { method: 'POST' })" : "fetch('/fbr/settings/test-connection', { method: 'POST' })", html);
        }
    }

    [ReferenceFact]
    public async Task Without_the_module_the_pages_are_refused()
    {
        await using var host = await HostAsync();
        var c = host.ClientFor(FbrData.Restricted, FbrData.CompanyB);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/fbr/")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/fbr/settings/")).StatusCode);
    }
}
