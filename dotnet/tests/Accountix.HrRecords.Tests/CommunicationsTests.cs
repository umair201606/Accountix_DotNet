using System.Net;
using System.Text.RegularExpressions;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.HrRecords.Tests;

/// <summary>Communications (/communications) and the notification bell's API (/auth/api/notifications).</summary>
public class CommunicationsTests
{
    private static async Task<World> OfficeAsync()
    {
        var w = await World.CreateAsync("Comms",
            ("admin", "Ayesha Admin", Role.Admin), ("mgr1", "Bilal Manager", Role.Manager), ("mgr2", "Hina Manager", Role.Manager),
            ("emp1", "Kamran Employee", Role.Employee), ("emp2", "Sana Employee", Role.Employee),
            ("off", "Omar Switched Off", Role.Employee), ("blk", "Rabia Blocked", Role.Manager),
            ("acct", "Zara Account Manager", Role.Manager));
        await using var db = World.Db(null);
        var roles = await db.Roles.ToDictionaryAsync(r => r.Name, r => r.Id);
        // Switched-off account; blocked membership; a manager by account whose role HERE is employee.
        (await db.Users.FirstAsync(u => u.Id == w["off"])).IsActive = false;
        (await db.Memberships.FirstAsync(m => m.CompanyId == w.Company && m.UserId == w["blk"])).Status = CompanyMembership.Blocked;
        (await db.Memberships.FirstAsync(m => m.CompanyId == w.Company && m.UserId == w["acct"])).RoleId = roles[Role.Employee];
        await db.SaveChangesAsync();
        return w;
    }

    private static List<KeyValuePair<string, string>> Send(string title, string target, string? role = null) =>
    [
        new("title", title), new("message", $"{title} body"), new("type", "warning"), new("target", target), new("role", role ?? ""),
    ];

    private static async Task<List<int>> RecipientsAsync(World w, string title)
    {
        await using var db = w.Db();
        return await db.Set<NotificationRecipient>().Where(r => r.Notification.Title == title)
            .Select(r => r.UserId).OrderBy(x => x).ToListAsync();
    }

    /// <summary>
    /// "By role" means the role held in THIS company; blocked members and switched-off accounts are out
    /// (the recipient lists below are the ones Python's send wrote on the same office, checked 29 Oct).
    /// </summary>
    [ReferenceFact]
    public async Task A_notification_by_role_reaches_the_role_held_here()
    {
        var w = await OfficeAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrRecordsModule).Assembly);
        var c = w.Client(host, "admin");
        foreach (var (target, role) in new[] { ("role", "manager"), ("role", "employee"), ("all", (string?)null), ("role", "admin"), ("nobody", null) })
        {
            string tag = $"{target}-{role}-{Guid.NewGuid():N}"[..20];
            var r = await c.PostFormAsync("/communications/send-notification", Send("NET " + tag, target, role), "/communications/");
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            var net = await RecipientsAsync(w, "NET " + tag);
            var expected = (target, role) switch
            {
                ("role", "manager") => new[] { w["mgr1"], w["mgr2"] },
                ("role", "employee") => new[] { w["emp1"], w["emp2"], w["acct"] },
                ("all", _) => new[] { w["mgr1"], w["mgr2"], w["emp1"], w["emp2"], w["acct"] },
                _ => [],
            };
            Assert.Equal(expected.Order().ToList(), net);
        }
        // The rows are the ones Python writes.
        await using var db = w.Db();
        var n = await db.Set<Notification>().OrderByDescending(x => x.Id).FirstAsync(x => x.Title.StartsWith("NET "));
        Assert.Equal(("warning", "communications", w["admin"], (int?)null), (n.NotificationType, n.Module, n.CreatedBy!.Value, n.ReferenceId));
        Assert.All(await db.Set<NotificationRecipient>().Where(x => x.NotificationId == n.Id).ToListAsync(),
                   x => Assert.Equal((false, (DateTime?)null, (int?)w.Company), (x.IsRead!.Value, x.ReadAt, x.CompanyId)));
    }

    [ReferenceFact]
    public async Task Pages_flash_and_refusals_match_python()
    {
        var w = await OfficeAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrRecordsModule).Assembly);
        var admin = w.Client(host, "admin");
        var r = await admin.PostFormAsync("/communications/send-notification", Send("", "all"), "/communications/");
        Assert.Equal("/communications/", r.Headers.Location!.OriginalString);
        string page = await admin.GetStringAsync("/communications/");
        Assert.Contains("Title and message required.", page);

        r = await admin.PostFormAsync("/communications/send-notification", Send("Hello all", "all"), "/communications/");
        page = await admin.GetStringAsync("/communications/");
        Assert.Contains("Notification sent to 5 users.", page);
        Assert.Contains("<td>Hello all</td>", page);
        Assert.Contains("<span class=\"badge badge-warning\">warning</span>", page);
        Assert.Contains("<td>5</td>", page);

        r = await admin.PostFormAsync("/communications/send-email",
            [new("recipient", "x@example.com"), new("subject", "Payslips"), new("body", " hi ")], "/communications/");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        page = await admin.GetStringAsync("/communications/");
        Assert.Contains("Email logged (SMTP not configured for live send).", page);
        Assert.Contains(">not sent</span>", page);
        await using (var db = w.Db())
        {
            var log = await db.Set<EmailLog>().SingleAsync();
            Assert.Equal(("not_sent", "manual", "hi", (string?)null), (log.Status, log.Module, log.Body, log.ErrorMessage));
        }

        // Everyone else: their own notifications; the sends are refused as Python refuses them.
        var emp = w.Client(host, "emp1");
        page = await emp.GetStringAsync("/communications/");
        Assert.Contains("My Notifications", page);
        Assert.Contains("Hello all", page);
        Assert.Contains("background:var(--accent-light);", page);   // unread
        string token = await emp.TokenFromAsync("/communications/");
        foreach (string url in new[] { "/communications/send-notification", "/communications/send-email" })
        {
            var refused = await emp.PostFormWithTokenAsync(url, Send("x", "all"), token);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("{\"error\":\"Access denied\"}\n", await refused.Content.ReadAsStringAsync());
        }
        // A blocked member is out of the module altogether.
        var blocked = await w.Client(host, "blk").GetAsync("/communications/");
        Assert.NotEqual(HttpStatusCode.OK, blocked.StatusCode);
    }

    /// <summary>emp1's recipient ids → "n&lt;i&gt;" (the notification "Bell &lt;i&gt; …"), created_at digits → 0, as capture/bell.py wrote Python's.</summary>
    private static async Task<string> NormaliseBellAsync(World w, string json)
    {
        await using var db = w.Db();
        int uid = w["emp1"];
        var map = (await db.Set<NotificationRecipient>().Where(r => r.UserId == uid).Select(r => new { r.Id, r.Notification.Title }).ToListAsync())
            .ToDictionary(n => n.Id.ToString(), n => "n" + Regex.Match(n.Title, "Bell (\\d+) ").Groups[1].Value);
        json = Regex.Replace(json, "\"id\":(\\d+)", m => $"\"id\":\"{map[m.Groups[1].Value]}\"");
        return Regex.Replace(json, "\"created_at\":\"([^\"]*)\"", m => $"\"created_at\":\"{Regex.Replace(m.Groups[1].Value, "\\d", "0")}\"");
    }

    /// <summary>
    /// The bell's JSON is Python's byte for byte (Golden/bell.json, capture/bell.py: the same 23 sends through the
    /// form): as sent, after reading the newest two, after read-all; reads answer {"success": true}.
    /// </summary>
    [ReferenceFact]
    public async Task The_bell_answers_byte_for_byte_what_python_answers_and_marks_read_alike()
    {
        var golden = Golden.Load("bell");
        var w = await OfficeAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrRecordsModule).Assembly);
        var admin = w.Client(host, "admin");
        string at = await admin.TokenFromAsync("/communications/");
        for (int i = 0; i < 23; i++)
            await admin.PostFormWithTokenAsync("/communications/send-notification",
                [new("title", $"Bell {i} ✓ <b>"), new("message", $"Line \"{i}\"\nnext — Ünï"), new("type", i % 2 == 0 ? "info" : "danger"),
                 new("target", "all")], at);
        var emp = w.Client(host, "emp1");

        var r = await emp.GetAsync("/auth/api/notifications");
        Assert.Equal("application/json", r.Content.Headers.ContentType!.MediaType);
        string net = await r.Content.ReadAsStringAsync();
        Golden.SameText(golden.GetProperty("api_sent").GetString()!, await NormaliseBellAsync(w, net), "the bell's JSON as sent");
        Assert.Equal(20, World.Json(net).GetArrayLength());

        // Read the newest two (no token: csrf-exempt as in Python); the list loses them.
        var answers = new List<string>();
        foreach (int id in new[] { World.Json(net)[0].GetProperty("id").GetInt32(), World.Json(net)[1].GetProperty("id").GetInt32() })
            answers.Add(await (await emp.PostAsync($"/auth/api/notifications/{id}/read", null)).Content.ReadAsStringAsync());
        Golden.SameText(golden.GetProperty("api_after_reading_two").GetString()!,
                        await NormaliseBellAsync(w, await emp.GetStringAsync("/auth/api/notifications")), "the bell's JSON after reading two");
        // Someone else's notification: nothing changes, still "success".
        int others = World.Json(await w.Client(host, "emp2").GetStringAsync("/auth/api/notifications"))[0].GetProperty("id").GetInt32();
        Assert.Equal("{\"success\":true}\n", await (await emp.PostAsync($"/auth/api/notifications/{others}/read", null)).Content.ReadAsStringAsync());
        await using (var db = w.Db())
            Assert.False((await db.Set<NotificationRecipient>().SingleAsync(x => x.Id == others)).IsRead);

        // A write started by another site is refused (csrf_guard).
        var foreign = new HttpRequestMessage(HttpMethod.Post, "/auth/api/notifications/read-all");
        foreign.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await emp.SendAsync(foreign)).StatusCode);

        answers.Add(await (await emp.PostAsync("/auth/api/notifications/read-all", null)).Content.ReadAsStringAsync());
        Assert.Equal(golden.GetProperty("read_answers").EnumerateArray().Select(a => a.GetString()!), answers);
        string empty = await emp.GetStringAsync("/auth/api/notifications");
        Assert.Equal("[]\n", empty);
        Golden.SameText(golden.GetProperty("api_after_read_all").GetString()!, empty, "the bell's JSON after read-all");
        await using (var db = w.Db())
        {
            var mine = await db.Set<NotificationRecipient>().Where(x => x.UserId == w["emp1"]).ToListAsync();
            Assert.All(mine, x => Assert.True(x.IsRead == true && x.ReadAt is not null));
            Assert.True(await db.Set<NotificationRecipient>().AnyAsync(x => x.UserId == w["emp2"] && x.IsRead == false));
        }
    }

    [ReferenceFact]
    public async Task The_bell_costs_at_most_three_queries_of_its_own()
    {
        var w = await OfficeAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrRecordsModule).Assembly);
        var admin = w.Client(host, "admin");
        for (int i = 0; i < 30; i++)
            await admin.PostFormAsync("/communications/send-notification", Send($"Q{i}", "all"), "/communications/");
        var emp = w.Client(host, "emp1");
        // What signing in and resolving the person costs on any page: a page of this project that reads nothing itself.
        int baseline = (await host.MeasureAsync(emp, "/reports/", runs: 1)).Queries;
        var bell = await host.MeasureAsync(emp, "/auth/api/notifications", runs: 1);
        Assert.True(bell.Queries - baseline <= 3, $"bell: {bell.Queries} queries, sign-in baseline {baseline}\n{bell}");
        foreach (string url in new[] { "/communications/" })
            (await host.MeasureAsync(admin, url, runs: 1)).Within(5000, 12);

        string token = await emp.TokenFromAsync("/communications/");
        host.Queries.Reset();
        await emp.PostAsync("/auth/api/notifications/read-all", null);
        int readAll = host.Queries.Count;
        Assert.True(readAll - baseline <= 3, $"read-all: {readAll} queries, baseline {baseline}");
        _ = token;
    }

    [ReferenceFact]
    public async Task Six_sends_at_once_make_six_notifications_with_each_person_once()
    {
        var w = await OfficeAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrRecordsModule).Assembly);
        var clients = host.ClientsFor(w["admin"], w.Company, 6);
        var tokens = await Task.WhenAll(clients.Select(c => c.TokenFromAsync("/communications/")));
        string tag = Guid.NewGuid().ToString("N")[..6];
        var rs = await Race.RunAsync(6, i => clients[i].PostFormWithTokenAsync("/communications/send-notification",
            Send($"Race {tag} {i}", "all"), tokens[i]));
        Race.NoServerErrors(rs);
        await using var db = w.Db();
        var sent = await db.Set<Notification>().Where(n => n.Title.StartsWith($"Race {tag}"))
            .Select(n => new { n.Id, People = n.NotificationRecipients.Select(r => r.UserId).ToList() }).ToListAsync();
        Assert.Equal(6, sent.Count);
        Assert.All(sent, n => Assert.Equal(5, n.People.Distinct().Count()));
        Assert.All(sent, n => Assert.Equal(5, n.People.Count));
    }
}
