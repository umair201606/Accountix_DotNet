using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Accountix.Core.Entities;
using Accountix.HrRecords.Controllers;
using Accountix.TestSupport;
using Accountix.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accountix.HrRecords.Tests;

/// <summary>
/// The pages say what Python's say: on the same data, each page's content
/// (from its page header to the end of &lt;main&gt;) carries the same element ids,
/// the same classes and the same visible text as Python's pages did
/// (Golden/pages.json, capture/pages.py: the same data through Python's forms).
/// Plus the sidebar entries and the rights declaration every module action needs.
/// </summary>
public class PageParityTests
{
    private static string Content(string html)
    {
        int start = html.IndexOf("<div class=\"page-header\">", StringComparison.Ordinal);
        int end = html.IndexOf("</main>", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "no page header / main");
        return html[start..end];
    }

    private static SortedSet<string> Ids(string html) =>
        new(Regex.Matches(html, "\\sid=\"([^\"]+)\"").Select(m => m.Groups[1].Value), StringComparer.Ordinal);

    private static SortedSet<string> Classes(string html) =>
        new(Regex.Matches(html, "\\sclass=\"([^\"]*)\"").SelectMany(m => m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
            StringComparer.Ordinal);

    /// <summary>The page without the report's employee picker, and the picker's options.</summary>
    private static (string Others, HashSet<string> People) People(string html)
    {
        var m = Regex.Match(html, "<select id=\"report-emp\"[^>]*>(.*?)</select>", RegexOptions.Singleline);
        if (!m.Success) return (html, []);
        var people = Regex.Matches(m.Groups[1].Value, "<option[^>]*>(.*?)</option>").Select(o => o.Value.Trim()).ToHashSet();
        return (html.Remove(m.Groups[1].Index, m.Groups[1].Length), people);
    }

    private static string Text(string html)
    {
        string t = Regex.Replace(html, "<script.*?</script>", " ", RegexOptions.Singleline);
        t = Regex.Replace(t, "<[^>]+>", " ");
        t = WebUtility.HtmlDecode(t);
        return Regex.Replace(t, "\\s+", " ").Trim();
    }

    /// <summary>
    /// What changes from day to day, alike on Python's page (its capture day) and .NET's (today): the
    /// "29-Oct-2026 09:00" stamps of things made today, today's ISO date (company or UTC), and the
    /// years of the year pickers, as offsets from this year.
    /// </summary>
    private static string Dated(string text, DateOnly today, DateOnly utcToday)
    {
        text = Regex.Replace(text, "\\b\\d{2}-[A-Z][a-z]{2}-\\d{4}( \\d{2}:\\d{2})?", "DATE");
        foreach (var d in new[] { today, utcToday, today.AddDays(-1) })
            text = text.Replace(d.ToString("yyyy-MM-dd"), "TODAY");
        return Regex.Replace(text, "(?<![\\d-])20\\d\\d(?![\\d-])", m =>
        {
            int off = int.Parse(m.Value) - today.Year;
            return off is >= -4 and <= 1 ? $"Y{off:+0;-0;+0}" : m.Value;
        });
    }

    /// <summary>This world's people as capture/pages.py wrote Python's: picker values USER_key, codes CODE_key, e-mails EMAIL_key.</summary>
    private static string People(string html, World w, IReadOnlyDictionary<string, (string Code, string Email)> people)
    {
        var keys = Golden.Keys(w);
        html = Regex.Replace(html, "(<select id=\"report-emp\"[^>]*>)(.*?)(</select>)", m => m.Groups[1].Value
            + Regex.Replace(m.Groups[2].Value, "value=\"(\\d+)\"", v => $"value=\"USER_{keys[int.Parse(v.Groups[1].Value)]}\"") + m.Groups[3].Value,
            RegexOptions.Singleline);
        foreach (var (key, (code, email)) in people)
            html = html.Replace(code, "CODE_" + key).Replace(email, "EMAIL_" + key);
        return html;
    }

    [ReferenceFact]
    public async Task Each_page_has_pythons_ids_classes_and_text()
    {
        var w = await World.CreateAsync("Parity",
            ("admin", "Ayesha Admin", Role.Admin), ("mgr", "Bilal Manager", Role.Manager),
            ("emp", "Kamran Employee", Role.Employee), ("emp2", "Sana Employee", Role.Employee));
        await w.EmploymentAsync("emp", manager: "mgr", department: "Finance");
        await using var host = await ModuleHost.StartAsync(typeof(HrRecordsModule).Assembly);
        var admin = w.Client(host, "admin");
        var mgr = w.Client(host, "mgr");
        var emp = w.Client(host, "emp");
        // Data through the .NET forms.
        string t = await admin.TokenFromAsync("/communications/");
        await admin.PostFormWithTokenAsync("/communications/send-notification",
            [new("title", "Holiday on Monday"), new("message", "The office is closed on Monday for the public holiday, & reopens Tuesday."),
             new("type", "success"), new("target", "all")], t);
        await admin.PostFormWithTokenAsync("/communications/send-email", [new("recipient", "a@b.com"), new("subject", "Payslips <October>")], t);
        string tm = await mgr.TokenFromAsync("/workplace/");
        await mgr.PostFormWithTokenAsync("/workplace/announcements/create", [new("title", "Townhall"), new("content", "Quarterly townhall in the main hall."), new("priority", "high")], tm);
        await mgr.PostFormWithTokenAsync("/workplace/events/create", [new("title", "Townhall"), new("event_date", "2031-03-04"), new("start_time", "09:30"), new("location", "Main hall")], tm);
        string te = await emp.TokenFromAsync("/workplace/kanban");
        int board = World.Json(await (await emp.PostFormWithTokenAsync("/workplace/kanban/create-board", [new("title", "Launch")], te)).Content.ReadAsStringAsync()).GetProperty("id").GetInt32();
        var form = new MultipartFormDataContent { { new StringContent(te), "__RequestVerificationToken" }, { new StringContent("CNIC scan"), "title" }, { new StringContent("2031-01-01"), "expiry_date" } };
        form.Add(new ByteArrayContent(new byte[2048]), "file", "cnic.pdf");
        await emp.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/digital-files/upload") { Content = form });
        await emp.GetStringAsync("/digital-files/");     // take the flash

        var golden = Golden.Load("pages");
        var pyToday = DateOnly.Parse(golden.GetProperty("today").GetString()!);
        var pyUtc = DateOnly.Parse(golden.GetProperty("utc_today").GetString()!);
        var today = BigOffice.Today;
        var utcToday = DateOnly.FromDateTime(DateTime.UtcNow);
        Dictionary<string, (string Code, string Email)> people;
        await using (var db = World.Db(null))
        {
            var ids = w.Ids.Values.ToList();
            var users = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id);
            people = w.Ids.ToDictionary(kv => kv.Key, kv => (users[kv.Value].EmployeeCode, users[kv.Value].Email));
        }
        var problems = new List<string>();
        int seen = 0;
        foreach (var g in golden.GetProperty("pages").EnumerateArray())
        {
            string who = g.GetProperty("who").GetString()!, template = g.GetProperty("url").GetString()!;
            string url = template.Replace("EMP", w["emp"].ToString()).Replace("BOARD", board.ToString());
            var r = await w.Client(host, who).GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            seen++;
            string net = People(Content(await r.Content.ReadAsStringAsync()), w, people);
            string pyc = g.GetProperty("content").GetString()!;
            if (!Ids(pyc).SetEquals(Ids(net)))
                problems.Add($"{who} {template} ids:\n py  {string.Join(",", Ids(pyc))}\n net {string.Join(",", Ids(net))}");
            if (!Classes(pyc).SetEquals(Classes(net)))
                problems.Add($"{who} {template} classes:\n py  {string.Join(",", Classes(pyc))}\n net {string.Join(",", Classes(net))}");
            // Python lists the report's employees in its query's (physical) order; .NET by id: compare them as a set.
            var (pyText, pyPeople) = People(pyc);
            var (netText, netPeople) = People(net);
            string pt = Dated(Text(pyText), pyToday, pyUtc), nt = Dated(Text(netText), today, utcToday);
            if (pt != nt)
                problems.Add($"{who} {template} text:\n py  {pt}\n net {nt}");
            if (!pyPeople.SetEquals(netPeople))
                problems.Add($"{who} {template} employees: py {string.Join(",", pyPeople)} net {string.Join(",", netPeople)}");
        }
        Assert.Equal(15, seen);
        Assert.True(problems.Count == 0, string.Join("\n\n", problems));
    }

    [ReferenceFact]
    public async Task The_hr_sidebar_carries_this_projects_links_by_role()
    {
        var w = await World.CreateAsync("Nav", ("admin", "A", Role.Admin), ("mgr", "M", Role.Manager), ("emp", "E", Role.Employee));
        await using var host = await ModuleHost.StartAsync(typeof(HrRecordsModule).Assembly);
        string Nav(string html) => html.Split("<nav class=\"sidebar-nav\">")[1].Split("</nav>")[0];
        string a = Nav(await w.Client(host, "admin").GetStringAsync("/digital-files/admin"));
        foreach (string link in new[] { "/digital-files/", "/workplace/", "/reports/", "/communications/", "/digital-files/admin" })
            Assert.Contains($"href=\"{link}\"", a);
        Assert.Contains("<a href=\"/digital-files/admin\" class=\"nav-item active\">", a);
        Assert.Contains("<a href=\"/digital-files/\" class=\"nav-item \">", a);       // My Files is not lit on All Files
        string m = Nav(await w.Client(host, "mgr").GetStringAsync("/workplace/events"));
        Assert.Contains("href=\"/reports/\"", m);
        Assert.DoesNotContain("href=\"/communications/\"", m);
        Assert.Contains("<a href=\"/workplace/\" class=\"nav-item active\">", m);
        string e = Nav(await w.Client(host, "emp").GetStringAsync("/digital-files/"));
        Assert.DoesNotContain("href=\"/reports/\"", e);
        Assert.DoesNotContain("href=\"/digital-files/admin\"", e);
        Assert.Contains("<a href=\"/digital-files/\" class=\"nav-item active\">", e);
    }

    [Fact]
    public void Every_module_action_declares_a_right_or_any_member()
    {
        var missing = typeof(HrRecordsModule).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract && t.GetCustomAttribute<ModuleAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName && m.GetCustomAttribute<NonActionAttribute>() is null)
                .Where(m => !m.IsDefined(typeof(RightAttribute)) && !m.IsDefined(typeof(AnyMemberAttribute)))
                .Select(m => $"{t.Name}.{m.Name}"))
            .ToList();
        Assert.True(missing.Count == 0, string.Join(", ", missing));
        // The bell is the one controller outside the module gate (module_guard.ALWAYS_OPEN).
        Assert.Null(typeof(NotificationsApiController).GetCustomAttribute<ModuleAttribute>());
    }
}
