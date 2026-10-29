using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace Accountix.Payroll.Tests;

public sealed class PageHarness : Harness
{
    public int RunId { get; private set; }
    public int SlipId { get; private set; }

    protected override Task<World> BuildAsync() => World.CreateAsync("Pages Co", [new World.Person("Hina Clerk", "2019-04-01")]);

    protected override async Task SeedAsync()
    {
        await W.ProfileAsync(W.Admin, 90_000, "2000-01-01", ("House Rent", "allowance", "percent_of_basic", 40m, true));
        await W.ProfileAsync(W.Employees[0], 55_000, "2019-04-01", ("Conveyance", "allowance", "fixed", 3000m, true));
        await using var db = W.Db();
        db.Add(new LoanAdvanceRequest { UserId = W.Employees[0], RequestType = "advance", Amount = 20_000, InstallmentMonths = 4, MonthlyInstallment = 5_000,
                                        RemainingAmount = 20_000, Purpose = "<b>rent</b> & \"deposit\"", Status = "approved", CreatedAt = DateTime.UtcNow });
        db.Add(new PfConfig { EmployeeContributionPct = 5, EmployerContributionPct = 5, ContributionBase = "basic", MaxLoanPercentage = 50,
                              InterestRate = 0, MinServiceMonthsForLoan = 0, IsActive = true, UpdatedAt = DateTime.UtcNow });
        db.Add(new PfWithdrawalRequest { UserId = W.Employees[0], Amount = 100, Reason = "O'Brien & <script>", Status = "pending", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    public async Task RunAsync()
    {
        if (RunId != 0) return;
        await PostAsync("/compensation/run-payroll", ("month", "6"), ("year", "2025"));
        var run = await W.RunAsync(6, 2025);
        RunId = run!.Id;
        await using var db = W.Db();
        SlipId = await db.Set<PayrollSlip>().Where(s => s.PayrollRunId == RunId && s.UserId == W.Employees[0]).Select(s => s.Id).SingleAsync();
    }
}

/// <summary>Every page of the module through the .NET app: renders, scripts intact, sidebar, files, JSON answers.</summary>
public sealed class PageTests(PageHarness h) : IClassFixture<PageHarness>
{
    private World W => h.W;

    private async Task<string[]> AdminPagesAsync()
    {
        await h.RunAsync();
        return
        [
            "/compensation/", "/compensation/tax-settings", "/compensation/create-profile", $"/compensation/edit-profile/{W.Employees[0]}",
            "/compensation/run-payroll", $"/compensation/run/{h.RunId}", "/compensation/loans", "/compensation/revisions",
            $"/compensation/slip/{h.SlipId}", "/pf/", "/pf/config",
        ];
    }

    [ReferenceFact]
    public async Task Every_admin_page_renders_with_intact_scripts_in_few_queries()
    {
        foreach (string url in await AdminPagesAsync())
        {
            h.Host.Queries.Reset();
            var r = await h.C.GetAsync(url);
            string html = await r.Content.ReadAsStringAsync();
            Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url}: {(int)r.StatusCode}");
            Assert.True(h.Host.Queries.Count <= 20, $"{url}: {h.Host.Queries.Count} queries\n{string.Join("\n", h.Host.Queries.Repeated(3))}");
            Assert.Contains("<title>", html);
            Assert.Contains("Accountix ERP</h2>", html);       // the HR topbar label
            foreach (Match m in Regex.Matches(html, @"<script(?![^>]*\bsrc=)[^>]*>(.*?)</script>", RegexOptions.Singleline))
            {
                Assert.DoesNotContain("&quot;", m.Groups[1].Value);
                Assert.DoesNotContain("&amp;", m.Groups[1].Value);
            }
            Assert.DoesNotContain("<script>alert", html);
        }
    }

    [ReferenceFact]
    public async Task The_sidebar_has_the_financial_group_and_the_admin_items()
    {
        string admin = await (await h.C.GetAsync("/compensation/")).Content.ReadAsStringAsync();
        Assert.Contains("data-group=\"financial\"", admin);
        Assert.Matches("<a href=\"/compensation/\" class=\"nav-item active\">", admin);
        foreach (string item in new[] { "/pf/config", "/compensation/tax-settings", "/compensation/loans" })
            Assert.Contains($"href=\"{item}\"", admin);
        int pfConfig = admin.IndexOf("href=\"/pf/config\"", StringComparison.Ordinal);
        int tax = admin.IndexOf("href=\"/compensation/tax-settings\"", StringComparison.Ordinal);
        int loans = admin.IndexOf("href=\"/compensation/loans\" class", StringComparison.Ordinal);
        Assert.True(pfConfig < tax && tax < loans, "PF Config, Tax & Payroll Settings, Staff Loans: Python's order");
        string loansPage = await (await h.C.GetAsync("/compensation/loans")).Content.ReadAsStringAsync();
        Assert.Contains("<a href=\"/compensation/loans\" class=\"nav-item active\">", loansPage);
        Assert.DoesNotContain("<a href=\"/compensation/\" class=\"nav-item active\">", loansPage);
        var emp = h.Host.ClientFor(W.Employees[0], W.Company);
        string mine = await (await emp.GetAsync("/compensation/")).Content.ReadAsStringAsync();
        Assert.Contains("href=\"/pf/\"", mine);
        Assert.DoesNotContain("href=\"/pf/config\"", mine);
    }

    [ReferenceFact]
    public async Task Employee_pages_show_only_their_own()
    {
        await h.RunAsync();
        var emp = h.Host.ClientFor(W.Employees[0], W.Company);
        string comp = await (await emp.GetAsync("/compensation/")).Content.ReadAsStringAsync();
        Assert.Contains("My Compensation", comp);
        Assert.Contains($"/compensation/slip/{h.SlipId}/pdf", comp);
        string pf = await (await emp.GetAsync("/pf/")).Content.ReadAsStringAsync();
        Assert.Contains("My Provident Fund", pf);
        Assert.Contains("O&#x27;Brien &amp; &lt;script&gt;", pf);    // encoded, never markup
        Assert.Equal(HttpStatusCode.OK, (await emp.GetAsync($"/compensation/slip/{h.SlipId}")).StatusCode);
        foreach (string url in new[] { "/compensation/tax-settings", "/compensation/loans", "/compensation/revisions", "/pf/config",
                                       $"/compensation/run/{h.RunId}", "/compensation/download-template" })
            Assert.Equal("/dashboard", (await emp.GetAsync(url)).Headers.Location?.OriginalString);
        Assert.Equal("/compensation/", (await emp.GetAsync("/compensation/create-profile")).Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Forbidden, (await emp.GetAsync($"/compensation/export-bank-ledger/{h.RunId}")).StatusCode);
    }

    [ReferenceFact]
    public async Task The_bulk_template_round_trips_through_the_upload()
    {
        var t = await h.C.GetAsync("/compensation/download-template");
        Assert.Equal("payroll_template.xlsx", t.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        byte[] filled;
        using (var wb = new XLWorkbook(new MemoryStream(await t.Content.ReadAsByteArrayAsync())))
        {
            var ws = wb.Worksheet("Payroll Data");
            Assert.Equal("Employee Code", ws.Cell(1, 1).GetString());
            Assert.Equal("Allowances (blank = salary package)", ws.Cell(1, 6).GetString());
            int row = Enumerable.Range(2, 5).First(r => ws.Cell(r, 1).GetString() == W.Codes[1]);
            Assert.Equal("Hina Clerk", ws.Cell(row, 2).GetString());
            ws.Cell(row, 3).Value = 5000;
            ws.Cell(row, 5).Value = "125.5";
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            filled = ms.ToArray();
        }
        var x = await UploadAsync(filled, "data.xlsx");
        var adj = x.GetProperty("adjustments").GetProperty(W.Employees[0].ToString());
        Assert.Equal("5000", adj.GetProperty("bonus").GetString());
        Assert.Equal("125.5", adj.GetProperty("custom_deduction").GetString());
        Assert.Equal("Bulk data loaded for 1 employees from data.xlsx.", x.GetProperty("message").GetString());

        var csv = Encoding.UTF8.GetBytes($"﻿Employee Code,Full Name,Bonus,Income Tax (blank = automatic)\n{W.Codes[0]},Admin,,999\nNOPE,x,1,\n");
        var y = await UploadAsync(csv, "data.csv");
        var a = y.GetProperty("adjustments");
        Assert.Single(a.EnumerateObject());
        Assert.Equal("999", a.GetProperty(W.Admin.ToString()).GetProperty("income_tax").GetString());

        var bad = await h.C.SendAsync(Multipart(Encoding.UTF8.GetBytes("x"), "data.xls", await h.TokenAsync()));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("Upload a .csv or .xlsx file.", await bad.Content.ReadAsStringAsync());
    }

    private static HttpRequestMessage Multipart(byte[] file, string name, string token)
    {
        var content = new MultipartFormDataContent { { new ByteArrayContent(file), "file", name } };
        var req = new HttpRequestMessage(HttpMethod.Post, "/compensation/upload-bulk-data") { Content = content };
        req.Headers.Add("X-CSRF-Token", token);
        return req;
    }

    private async Task<JsonElement> UploadAsync(byte[] file, string name)
    {
        var r = await h.C.SendAsync(Multipart(file, name, await h.TokenAsync()));
        string body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [ReferenceFact]
    public async Task Pf_json_answers()
    {
        int lid;
        await using (var db = W.Db())
        {
            var loan = new PfLoanRequest { UserId = W.Employees[0], Amount = 1_000_000, InstallmentMonths = 72, MonthlyInstallment = 1, RemainingAmount = 1_000_000,
                                           Purpose = "x", Status = "pending", CreatedAt = DateTime.UtcNow };
            db.Add(loan);
            await db.SaveChangesAsync();
            lid = loan.Id;
        }
        var c = JsonDocument.Parse(await (await h.C.GetAsync($"/pf/compliance-check/{lid}")).Content.ReadAsStringAsync()).RootElement;
        Assert.False(c.GetProperty("compliant").GetBoolean());
        Assert.Equal(2, c.GetProperty("issues").GetArrayLength());
        var req = new HttpRequestMessage(HttpMethod.Post, $"/pf/approve-loan/{lid}")
        {
            Content = new FormUrlEncodedContent([new("action", "reject"), new("__RequestVerificationToken", await h.TokenAsync())]),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var rj = await h.C.SendAsync(req);
        Assert.Equal("{\"success\":true,\"message\":\"Loan rejected.\"}", await rj.Content.ReadAsStringAsync());
        int role;
        await using (var db = W.Db())
            role = await db.Roles.Where(r => r.Name == "employee").Select(r => r.Id).FirstAsync();
        var bp = await h.PostAsync("/pf/button-permissions", ("role_id", role.ToString()), ("button_code", "BUTTON_PROCESS_SETTLEMENT"), ("granted", "true"));
        Assert.Equal("{\"success\":true}", await bp.Content.ReadAsStringAsync());
        await using (var db = W.Db())
            Assert.True((await db.Set<ButtonPermission>().SingleAsync(b => b.ButtonCode == "BUTTON_PROCESS_SETTLEMENT")).IsGranted);
    }
}
