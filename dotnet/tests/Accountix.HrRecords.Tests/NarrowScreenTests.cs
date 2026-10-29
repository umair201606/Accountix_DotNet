using System.Net;
using System.Text.RegularExpressions;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.Playwright;

namespace Accountix.HrRecords.Tests;

/// <summary>
/// Every page of this project in a real browser at 320, 360 and 1366 px:
/// nothing scrolls sideways, no script error, and the data written into
/// &lt;script&gt; blocks is never HTML-encoded (E2E finding F1). The app runs on
/// Kestrel at a free local port; the browser is Playwright's Chromium for .NET.
/// </summary>
public class NarrowScreenTests
{
    private static async Task<(World W, Dictionary<string, (string Who, string Url)> Pages)> OfficeAsync()
    {
        var w = await World.CreateAsync("Narrow",
            ("admin", "Ayesha Admin With A Rather Long Name", Role.Admin), ("mgr", "Bilal Manager", Role.Manager),
            ("emp", "Kamran Employee", Role.Employee));
        await w.EmploymentAsync("emp", manager: "mgr", department: "Engineering");
        int board, file;
        await using (var db = w.Db())
        {
            db.Add(new Notification { Title = "Office closed on Friday for maintenance work", Message = string.Concat(Enumerable.Repeat("The office will be closed on Friday while the generators are serviced. ", 5)), NotificationType = "info", Module = "communications", CreatedBy = w["admin"], CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            db.Add(new NotificationRecipient { NotificationId = db.Set<Notification>().First().Id, UserId = w["emp"], IsRead = false });
            db.Add(new EmailLog { Recipient = "someone.with.a.long.address@example.com", Subject = "Payslips for October", Body = "", Module = "manual", Status = "not_sent", SentAt = DateTime.UtcNow });
            db.Add(new Announcement { Title = "Quarterly townhall", Content = string.Concat(Enumerable.Repeat("Everyone is invited to the quarterly townhall in the main hall. ", 7)), Priority = "urgent", AuthorId = w["mgr"], Pinned = false, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            db.Add(new TeamEvent { Title = "Townhall", EventDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3), StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0), Location = "Main hall, third floor", EventType = "meeting", CreatedBy = w["mgr"], CreatedAt = DateTime.UtcNow });
            var b = new KanbanBoard { Title = "Launch plan", CreatedBy = w["emp"], CreatedAt = DateTime.UtcNow };
            db.Add(b);
            var f = new DigitalFile { UserId = w["emp"], Title = "National identity card (front and back)", Filename = "x.pdf", OriginalName = "a_very_long_original_file_name_for_testing.pdf", FileSize = 123456, MimeType = "application/pdf", Notes = "", ExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10), IsVerified = false, UploadedAt = DateTime.UtcNow };
            db.Add(f);
            await db.SaveChangesAsync();
            db.Add(new KanbanTask { BoardId = b.Id, Title = "Write the copy for the landing page", Status = "todo", Priority = "medium", AssigneeId = w["emp"], Position = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            board = b.Id;
            file = f.Id;
        }
        _ = file;
        var pages = new Dictionary<string, (string, string)>
        {
            ["comms-admin"] = ("admin", "/communications/"),
            ["comms-mine"] = ("emp", "/communications/"),
            ["files-mine"] = ("emp", "/digital-files/"),
            ["files-admin"] = ("admin", "/digital-files/admin"),
            ["files-profile"] = ("admin", $"/digital-files/profile/{w["emp"]}"),
            ["workplace"] = ("mgr", "/workplace/"),
            ["announcements"] = ("emp", "/workplace/announcements"),
            ["events"] = ("mgr", "/workplace/events"),
            ["kanban"] = ("emp", $"/workplace/kanban?board={board}"),
            ["reports"] = ("admin", "/reports/"),
            ["reports-employee"] = ("emp", "/reports/"),
        };
        return (w, pages);
    }

    [ReferenceFact]
    public async Task Script_blocks_carry_no_html_encoded_data()
    {
        var (w, pages) = await OfficeAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrRecordsModule).Assembly);
        foreach (var (name, (who, url)) in pages)
        {
            var r = await w.Client(host, who).GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            string html = await r.Content.ReadAsStringAsync();
            foreach (Match m in Regex.Matches(html, "<script>(.*?)</script>", RegexOptions.Singleline))
            {
                // The pages' esc() helpers spell the entities out as JS strings ('&quot;'): those are code, not data.
                string code = Regex.Replace(m.Groups[1].Value, "'&(?:quot|amp|lt|gt|#39);'", "''");
                Assert.DoesNotContain("&quot;", code);
                Assert.DoesNotContain("&amp;", code);
                Assert.DoesNotContain("&#x", code);
            }
            _ = name;
        }
    }

    [ReferenceFact]
    public async Task Pages_fit_phones_from_320px_and_raise_no_script_errors()
    {
        var (w, pages) = await OfficeAsync();
        await using var app = await LiveApp.StartAsync();
        var (pw, browser) = await LiveApp.BrowserAsync();
        var problems = new List<string>();
        int seen = 0;
        try
        {
            foreach (string who in new[] { "admin", "mgr", "emp" })
                foreach (int width in new[] { 320, 360, 1366 })
                {
                    var ctx = await app.ContextAsync(browser, TestSignIn.CookieFor(w[who], w.Company), width);
                    var page = await ctx.NewPageAsync();
                    var errors = new List<string>();
                    page.PageError += (_, e) => errors.Add(e);
                    page.Console += (_, m) => { if (m.Type == "error") errors.Add(m.Text); };
                    foreach (var (name, (pageWho, url)) in pages)
                    {
                        if (pageWho != who) continue;
                        errors.Clear();
                        var r = await page.GotoAsync(app.BaseUrl + url, new() { WaitUntil = WaitUntilState.NetworkIdle });
                        int scroll = await page.EvaluateAsync<int>("document.documentElement.scrollWidth");
                        int inner = await page.EvaluateAsync<int>("window.innerWidth");
                        seen++;
                        if (r?.Status != 200 || scroll > inner || errors.Count > 0)
                            problems.Add($"{name} ({who}) at {width}px: status {r?.Status}, scroll {scroll} > {inner}?, errors [{string.Join("; ", errors)}], at {page.Url}");
                    }
                    await ctx.CloseAsync();
                }
        }
        finally
        {
            await browser.CloseAsync();
            pw.Dispose();
        }
        Assert.Equal(pages.Count * 3, seen);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
