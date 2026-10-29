using System.Net;
using System.Text.RegularExpressions;
using Accountix.Core.Entities;
using Accountix.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Accountix.HrRecords.Tests;

/// <summary>The workplace pages (/workplace): announcements, the team calendar, task boards.</summary>
public class WorkplaceTests
{
    private static Task<World> OfficeAsync() => World.CreateAsync("Workplace",
        ("admin", "Ayesha Admin", Role.Admin), ("mgr", "Bilal Manager", Role.Manager),
        ("emp1", "Kamran Employee", Role.Employee), ("emp2", "Sana Employee", Role.Employee));

    private static string Flashed(string page, string text)
    {
        Assert.Contains(text, page);
        return page;
    }

    [ReferenceFact]
    public async Task Announcements_and_events_are_posted_by_managers_and_admins_only()
    {
        var w = await OfficeAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrRecordsModule).Assembly);
        var mgr = w.Client(host, "mgr");
        var emp = w.Client(host, "emp1");

        // An employee sees no form and is refused in Python's words.
        string empHome = await emp.GetStringAsync("/workplace/");
        Assert.DoesNotContain("id=\"ann-form\"", empHome);
        var r = await emp.PostFormAsync("/workplace/announcements/create", [new("title", "Hi"), new("content", "x")], "/workplace/");
        Assert.Equal("/workplace/", r.Headers.Location!.OriginalString);
        Flashed(await emp.GetStringAsync("/workplace/"), "Only a manager or an admin can publish announcements.");
        r = await emp.PostFormAsync("/workplace/events/create", [new("title", "Party"), new("event_date", "2030-01-01")], "/workplace/events");
        Assert.Equal("/workplace/events", r.Headers.Location!.OriginalString);
        Flashed(await emp.GetStringAsync("/workplace/events"), "Only a manager or an admin can add events to the team calendar.");

        Assert.Contains("id=\"ann-form\"", await mgr.GetStringAsync("/workplace/"));
        for (int i = 0; i < 7; i++)
            await mgr.PostFormAsync("/workplace/announcements/create",
                [new("title", $"Notice {i}"), new("content", new string('x', 160) + i), new("priority", i == 6 ? "urgent" : i == 5 ? "bogus" : "high")],
                "/workplace/");
        await using (var db = w.Db())
        {
            var a = await db.Set<Announcement>().OrderBy(x => x.Id).ToListAsync();
            Assert.Equal(7, a.Count);
            Assert.Equal(("normal", false, w["mgr"]), (a[5].Priority!, a[5].Pinned!.Value, a[5].AuthorId));
            Assert.NotNull(a[0].CreatedAt);
        }
        string home = await mgr.GetStringAsync("/workplace/");
        Assert.Equal(5, Regex.Matches(home, "<strong style=\"font-size:13px;\">Notice").Count);   // the latest five
        Assert.Contains("<span class=\"badge badge-danger\">urgent</span>", home);
        Assert.Contains(new string('x', 150) + "...", home);
        string all = await emp.GetStringAsync("/workplace/announcements");
        Assert.Equal(7, Regex.Matches(all, "<strong style=\"font-size:14px;\">Notice").Count);
        Assert.Contains("By Bilal Manager on ", all);

        r = await mgr.PostFormAsync("/workplace/events/create", [new("title", ""), new("event_date", "2030-01-01")], "/workplace/events");
        Assert.Equal("/workplace/", r.Headers.Location!.OriginalString);
        Flashed(await mgr.GetStringAsync("/workplace/"), "Title and date are required.");
        r = await mgr.PostFormAsync("/workplace/events/create", [new("title", "Bad"), new("event_date", "2030-01-01"), new("start_time", "25:00")], "/workplace/events");
        Assert.Equal("/workplace/events", r.Headers.Location!.OriginalString);
        Flashed(await mgr.GetStringAsync("/workplace/events"), "Enter the date as YYYY-MM-DD and times as HH:MM.");
        await mgr.PostFormAsync("/workplace/events/create",
            [new("title", "Townhall"), new("event_date", "2030-1-9"), new("start_time", "9:05"), new("end_time", "10:30"),
             new("location", " Lahore "), new("event_type", "party")], "/workplace/events");
        await using (var db = w.Db())
        {
            var e = await db.Set<TeamEvent>().SingleAsync();
            Assert.Equal((new DateOnly(2030, 1, 9), new TimeOnly(9, 5), new TimeOnly(10, 30), "Lahore", "other", w["mgr"]),
                         (e.EventDate, e.StartTime!.Value, e.EndTime!.Value, e.Location!, e.EventType!, e.CreatedBy));
        }
        string events = await emp.GetStringAsync("/workplace/events");
        Assert.Contains("<td>2030-01-09</td>", events);
        Assert.Contains("<td>09:05 - 10:30</td>", events);
        Assert.DoesNotContain("id=\"show-event-form\"", events);
        Assert.Contains("2030-01-09  &middot; Lahore", await emp.GetStringAsync("/workplace/"));
    }

    /// <summary>
    /// Task boards: each viewer's task list is Python's byte for byte (Golden/kanban.json, capture/kanban.py: the
    /// same board, tasks and move through Python's routes); who may add, move and delete.
    /// </summary>
    [ReferenceFact]
    public async Task Task_boards_answer_what_python_answers_and_guard_who_may_change_them()
    {
        var w = await OfficeAsync();
        await using var host = await ModuleHost.StartAsync(typeof(HrRecordsModule).Assembly);
        var emp1 = w.Client(host, "emp1");
        var emp2 = w.Client(host, "emp2");
        string t1 = await emp1.TokenFromAsync("/workplace/kanban");
        string t2 = await emp2.TokenFromAsync("/workplace/kanban");

        var r = await emp1.PostFormWithTokenAsync("/workplace/kanban/create-board", [new("title", "  ")], t1);
        Assert.Equal((HttpStatusCode.BadRequest, "{\"error\":\"Title required\"}\n"), (r.StatusCode, await r.Content.ReadAsStringAsync()));
        r = await emp1.PostFormWithTokenAsync("/workplace/kanban/create-board", [new("title", "Launch ✓")], t1);
        string created = await r.Content.ReadAsStringAsync();
        int board = World.Json(created).GetProperty("id").GetInt32();
        Assert.Equal($"{{\"id\":{board},\"success\":true,\"title\":\"Launch \\u2713\"}}\n", created);
        var golden = Golden.Load("kanban");
        Golden.SameText(golden.GetProperty("create_board").GetString()!, created.Replace($"\"id\":{board}", "\"id\":\"board\""), "create-board");

        // Tasks: the owner adds; someone else may not; nobody hands a task to an outsider.
        r = await emp2.PostFormWithTokenAsync($"/workplace/kanban/{board}/add-task", [new("title", "Sneaky")], t2);
        Assert.Equal((HttpStatusCode.Forbidden, "{\"error\":\"Only the board's owner, a manager or an admin can add tasks.\"}\n"),
                     (r.StatusCode, await r.Content.ReadAsStringAsync()));
        r = await emp1.PostFormWithTokenAsync($"/workplace/kanban/{board}/add-task",
            [new("title", "Outsider"), new("assignee_id", Reference.UserId(Reference.Email("owner_b")).ToString())], t1);
        Assert.Equal("{\"error\":\"Assign the task to a member of this company.\"}\n", await r.Content.ReadAsStringAsync());
        int a = World.Json(await (await emp1.PostFormWithTokenAsync($"/workplace/kanban/{board}/add-task",
            [new("title", "Write <copy>"), new("assignee_id", w["emp2"].ToString())], t1)).Content.ReadAsStringAsync()).GetProperty("id").GetInt32();
        int b = World.Json(await (await emp1.PostFormWithTokenAsync($"/workplace/kanban/{board}/add-task",
            [new("title", "Design")], t1)).Content.ReadAsStringAsync()).GetProperty("id").GetInt32();
        await using (var db = w.Db())
            await db.Set<KanbanTask>().Where(x => x.Id == b).ExecuteUpdateAsync(s => s.SetProperty(x => x.DueDate, new DateOnly(2030, 2, 3)));

        // The assignee moves their task (JSON body), but cannot delete it.
        r = await emp2.PostJsonWithTokenAsync($"/workplace/kanban/update-task/{a}", new { status = "done", position = 2.9 }, t2);
        Golden.SameText(golden.GetProperty("move").GetString()!, await r.Content.ReadAsStringAsync(), "update-task");
        r = await emp2.PostJsonWithTokenAsync($"/workplace/kanban/update-task/{a}", new { status = "blocked" }, t2);
        Assert.Equal((HttpStatusCode.BadRequest, "{\"error\":\"Unknown status\"}\n"), (r.StatusCode, await r.Content.ReadAsStringAsync()));
        r = await emp2.PostJsonWithTokenAsync($"/workplace/kanban/update-task/{a}", new { position = "x" }, t2);
        Assert.Equal("{\"error\":\"Position must be a number\"}\n", await r.Content.ReadAsStringAsync());
        r = await emp2.PostJsonWithTokenAsync($"/workplace/kanban/update-task/{b}", new { status = "done" }, t2);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        r = await emp2.PostFormWithTokenAsync($"/workplace/kanban/delete-task/{a}", [], t2);
        Assert.Equal("{\"error\":\"Only the board's owner, a manager or an admin can delete tasks.\"}\n", await r.Content.ReadAsStringAsync());
        await using (var db = w.Db())
        {
            var t = await db.Set<KanbanTask>().SingleAsync(x => x.Id == a);
            Assert.Equal(("done", (int?)2, "medium"), (t.Status!, t.Position, t.Priority!));
        }

        // Each viewer's list is byte for byte Python's (can_move / can_delete differ by viewer); task ids as a / b.
        foreach (var g in golden.GetProperty("tasks").EnumerateArray())
        {
            string who = g.GetProperty("who").GetString()!;
            string net = await w.Client(host, who).GetStringAsync($"/workplace/kanban/{board}/tasks");
            net = Regex.Replace(net, "\"id\":(\\d+)", m => $"\"id\":\"{(int.Parse(m.Groups[1].Value) == a ? "a" : int.Parse(m.Groups[1].Value) == b ? "b" : "?")}\"");
            Golden.SameText(g.GetProperty("body").GetString()!, net, $"tasks as {who}");
        }
        var view2 = World.Json(await emp2.GetStringAsync($"/workplace/kanban/{board}/tasks"));
        Assert.Equal(2, view2.GetArrayLength());
        Assert.Equal("Design", view2[0].GetProperty("title").GetString());     // position 0 before 2
        Assert.Equal("2030-02-03", view2[0].GetProperty("due_date").GetString());
        Assert.True(view2[1].GetProperty("can_move").GetBoolean());
        Assert.False(view2[1].GetProperty("can_delete").GetBoolean());

        // The page: the board's buttons, the add button only for those who manage it.
        Assert.Contains("id=\"add-task-btn\"", await emp1.GetStringAsync($"/workplace/kanban?board={board}"));
        string other = await emp2.GetStringAsync($"/workplace/kanban?board={board}");    // UTF-8, as Jinja writes it
        Assert.DoesNotContain("id=\"add-task-btn\"", other);
        Assert.Contains($"data-board=\"{board}\"", other);
        Assert.Contains($"<a href=\"?board={board}\" class=\"btn btn-primary btn-sm\">Launch ✓</a>", other);
        Assert.DoesNotContain("id=\"kanban-board\"", await emp2.GetStringAsync("/workplace/kanban?board=abc"));

        r = await emp1.PostFormWithTokenAsync($"/workplace/kanban/delete-task/{a}", [], t1);
        Assert.Equal("{\"success\":true}\n", await r.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await emp1.PostFormWithTokenAsync($"/workplace/kanban/delete-task/{a}", [], t1)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await emp1.GetAsync("/workplace/kanban/987654321/tasks")).StatusCode);
    }
}
