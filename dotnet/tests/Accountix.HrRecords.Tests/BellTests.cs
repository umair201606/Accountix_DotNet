using System.Net;
using System.Text.RegularExpressions;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace Accountix.HrRecords.Tests;

/// <summary>
/// The notification bell in the app shell (NotificationBellViewComponent): the
/// unread count and the five newest unread, as Python's shell shows them
/// (Golden/bell.json "shell", capture/bell.py), and marking read through the
/// shell's own script (static/js/app.js) in Chromium.
/// </summary>
public class BellTests
{
    private static async Task<World> OfficeAsync(int notifications)
    {
        var w = await World.CreateAsync("Bell", ("admin", "Ayesha Admin", Role.Admin), ("emp", "Kamran Employee", Role.Employee),
                                        ("emp2", "Sana Employee", Role.Employee));
        await using var db = w.Db();
        for (int i = 0; i < notifications; i++)
        {
            var n = new Notification
            {
                Title = $"Notice {i} — Ünïcødé", Message = $"Message {i} <b>bold</b> & more", NotificationType = "info",
                Module = "communications", CreatedBy = w["admin"], CreatedAt = DateTime.UtcNow.AddMinutes(i),
            };
            db.Add(n);
            await db.SaveChangesAsync();
            db.Add(new NotificationRecipient { NotificationId = n.Id, UserId = w["emp"], IsRead = false });
            db.Add(new NotificationRecipient { NotificationId = n.Id, UserId = w["emp2"], IsRead = false });
        }
        await db.SaveChangesAsync();
        return w;
    }

    private static string Bell(string html)
    {
        int start = html.IndexOf("<div class=\"notif-dropdown\" id=\"notif-dropdown\">", StringComparison.Ordinal);
        Assert.True(start >= 0, "no bell on the page");
        int end = html.IndexOf("<div class=\"user-menu\">", start, StringComparison.Ordinal);
        return html[start..end];
    }

    private static List<string> DataIds(string bell) =>
        Regex.Matches(bell, "class=\"notif-item\" data-id=\"(\\w+)\"").Select(m => m.Groups[1].Value).ToList();

    /// <summary>Recipient ids → "n&lt;i&gt;" (the notification "Notice &lt;i&gt; …"), as the capture wrote them.</summary>
    private static async Task<string> NormaliseAsync(World w, string who, string bell)
    {
        await using var db = w.Db();
        int uid = w[who];
        var names = await db.Set<NotificationRecipient>().Where(r => r.UserId == uid)
            .Select(r => new { r.Id, r.Notification.Title }).ToListAsync();
        var map = names.ToDictionary(n => n.Id.ToString(), n => "n" + Regex.Match(n.Title, "Notice (\\d+) ").Groups[1].Value);
        return Regex.Replace(bell, "data-id=\"(\\d+)\"", m => $"data-id=\"{map[m.Groups[1].Value]}\"");
    }

    private static string Text(string html) =>
        Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html, "<svg.*?</svg>", " ", RegexOptions.Singleline), "<[^>]+>", " ")), "\\s+", " ").Trim();

    [ReferenceFact]
    public async Task The_shell_shows_the_unread_count_and_the_newest_five_as_python_does()
    {
        var w = await OfficeAsync(7);
        await using var host = await ModuleHost.StartAsync(typeof(HrRecordsModule).Assembly);
        var emp = w.Client(host, "emp");
        string net = Bell(await emp.GetStringAsync("/workplace/"));
        Assert.Contains("<span class=\"notif-badge\">7</span>", net);
        var ids = DataIds(net);
        var api = World.Json(await emp.GetStringAsync("/auth/api/notifications"));
        Assert.Equal(api.EnumerateArray().Take(5).Select(x => x.GetProperty("id").GetInt32().ToString()), ids);
        Assert.Contains("Notice 6 — Ünïcødé", net);                         // UTF-8, as Jinja writes it
        Assert.Contains("Message 6 &lt;b&gt;bold&lt;/b&gt; &amp; more", net);  // text, never markup

        string py = Golden.Load("bell").GetProperty("shell").GetString()!;
        string mine = await NormaliseAsync(w, "emp", net);
        Assert.Equal(["n6", "n5", "n4", "n3", "n2"], DataIds(py));
        Assert.Equal(DataIds(py), DataIds(mine));
        Golden.SameText(Text(py), Text(mine), "the bell's text");
        foreach (string id in new[] { "notif-dropdown", "notif-btn", "notif-menu", "mark-all-read", "notif-list" })
            Assert.Contains($"id=\"{id}\"", net);

        // A page of any kind carries it; the bell adds two queries.
        int withBell = (await host.MeasureAsync(emp, "/workplace/announcements", runs: 1)).Queries;
        Assert.True(withBell <= 10, $"{withBell} queries");

        // Mark all read the way the shell's script does it: the badge and the items go.
        Assert.Equal("{\"success\":true}\n", await (await emp.PostAsync("/auth/api/notifications/read-all", null)).Content.ReadAsStringAsync());
        string after = Bell(await emp.GetStringAsync("/workplace/"));
        Assert.DoesNotContain("notif-badge", after);
        Assert.DoesNotContain("notif-item", after);
        Assert.Contains("<div class=\"notif-empty\">No new notifications</div>", after);
        // Someone else's bell is untouched.
        Assert.Contains("<span class=\"notif-badge\">7</span>", Bell(await w.Client(host, "emp2").GetStringAsync("/workplace/")));
    }

    [ReferenceFact]
    public async Task Clicking_an_item_and_mark_all_read_in_the_browser_update_the_bell_and_the_records()
    {
        var w = await OfficeAsync(3);
        int newest;
        await using (var db = w.Db())
            newest = await db.Set<NotificationRecipient>().Where(r => r.UserId == w["emp"]).MaxAsync(r => r.Id);
        await using (var app = await LiveApp.StartAsync())
        {
            var (pw, browser) = await LiveApp.BrowserAsync();
            try
            {
                var ctx = await app.ContextAsync(browser, TestSignIn.CookieFor(w["emp"], w.Company), 360);
                var page = await ctx.NewPageAsync();
                var errors = new List<string>();
                page.PageError += (_, e) => errors.Add(e);
                await page.GotoAsync(app.BaseUrl + "/workplace/", new() { WaitUntil = WaitUntilState.NetworkIdle });
                Assert.Equal("3", await page.InnerTextAsync(".notif-badge"));
                await page.ClickAsync("#notif-btn");
                Assert.DoesNotContain("hidden", await page.GetAttributeAsync("#notif-menu", "class") ?? "");
                var one = await page.RunAndWaitForResponseAsync(() => page.ClickAsync(".notif-item >> nth=0"), r => r.Url.Contains("/read"));
                Assert.Equal(200, one.Status);
                Assert.Equal("2", await page.InnerTextAsync(".notif-badge"));
                await page.ClickAsync("#notif-btn");
                var all = await page.RunAndWaitForResponseAsync(() => page.ClickAsync("#mark-all-read"), r => r.Url.EndsWith("/read-all"));
                Assert.Equal(200, all.Status);
                await page.WaitForTimeoutAsync(200);
                Assert.Equal(0, await page.Locator(".notif-badge").CountAsync());
                await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
                Assert.Equal("No new notifications", (await page.InnerTextAsync("#notif-list")).Trim());
                Assert.Empty(errors);
                await ctx.CloseAsync();
            }
            finally
            {
                await browser.CloseAsync();
                pw.Dispose();
            }
        }
        await using var check = w.Db();
        var mine = await check.Set<NotificationRecipient>().Where(x => x.UserId == w["emp"]).ToListAsync();
        Assert.All(mine, x => Assert.True(x.IsRead == true && x.ReadAt is not null));
        Assert.True(mine.Single(x => x.Id == newest).ReadAt <= mine.Where(x => x.Id != newest).Min(x => x.ReadAt));   // the clicked one first
        Assert.All(await check.Set<NotificationRecipient>().Where(x => x.UserId == w["emp2"]).ToListAsync(), x => Assert.False(x.IsRead));
    }
}
