using System.Text.Json;
using Accountix.HrRecords.Services;

namespace Accountix.HrRecords.Controllers;

/// <summary>
/// hr_app/routes/workplace.py (blueprint "workplace"): announcements, the
/// team calendar and task boards. Company-wide posts take an admin or a
/// manager by the role held in THIS company; a board's tasks are managed by
/// its owner, a manager or an admin, and moved also by the person assigned.
/// </summary>
[Area("HrRecords")]
[Module(Modules.Hr)]
[Route("workplace")]
public sealed class WorkplaceController(AccountixDbContext db, Staff staff, CompanyClock clock) : RecordsController
{
    private static readonly string[] TaskStatuses = ["todo", "in_progress", "done"];

    public sealed record AnnouncementRow(string Title, string Content, string? Priority, string Author, DateTime? CreatedAt);

    public sealed record EventRow(DateOnly EventDate, string Title, string? EventType, TimeOnly? StartTime, TimeOnly? EndTime,
                                  string? Location, string Creator);

    public sealed record IndexModel(IReadOnlyList<AnnouncementRow> Announcements, IReadOnlyList<EventRow> Events,
                                    IReadOnlyList<(int Id, string Title)> Boards, bool CanPost);

    public sealed record EventsModel(IReadOnlyList<EventRow> Events, bool CanPost);

    public sealed record KanbanModel(IReadOnlyList<(int Id, string Title)> Boards, IReadOnlySet<int> Manageable, int SelectedBoard);

    /// <summary>_can_post: announcements and calendar events.</summary>
    private bool CanPost => IsAdmin || IsManager;

    /// <summary>_can_manage_board: its owner, a manager or an admin.</summary>
    private bool CanManage(int boardCreatedBy) => boardCreatedBy == Me || IsAdmin || IsManager;

    private IQueryable<AnnouncementRow> Announcements() =>
        db.Set<Announcement>().AsNoTracking()
            .OrderByDescending(a => a.Pinned).ThenByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id)
            .Select(a => new AnnouncementRow(a.Title, a.Content, a.Priority, a.Author.FullName, a.CreatedAt));

    private static IQueryable<EventRow> EventRows(IQueryable<TeamEvent> q) =>
        q.Select(e => new EventRow(e.EventDate, e.Title, e.EventType, e.StartTime, e.EndTime, e.Location, e.CreatedByNavigation.FullName));

    [HttpGet("")]
    [AnyMember]   // HR is governed by role in the route
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var announcements = await Announcements().Take(5).ToListAsync(ct);   // the page shows the first five
        var today = clock.Today;
        var events = await EventRows(db.Set<TeamEvent>().AsNoTracking().Where(e => e.EventDate >= today)
            .OrderBy(e => e.EventDate).ThenBy(e => e.Id).Take(10)).ToListAsync(ct);
        var boards = await db.Set<KanbanBoard>().AsNoTracking().OrderByDescending(b => b.CreatedAt).ThenByDescending(b => b.Id)
            .Select(b => new { b.Id, b.Title }).ToListAsync(ct);
        return View(new IndexModel(announcements, events, boards.Select(b => (b.Id, b.Title)).ToList(), CanPost));
    }

    [HttpGet("announcements")]
    [AnyMember]
    public async Task<IActionResult> AnnouncementsPage(CancellationToken ct) =>
        View("Announcements", await Announcements().ToListAsync(ct));

    [HttpPost("announcements/create")]
    [AnyMember]   // a manager or an admin, checked in the route
    public async Task<IActionResult> CreateAnnouncement(CancellationToken ct)
    {
        if (!CanPost)
            return FlashTo("Only a manager or an admin can publish announcements.", "danger", "/workplace/");
        string title = FormStr("title").Trim();
        string content = FormStr("content").Trim();
        string priority = FormStr("priority", "normal");
        if (priority is not ("normal" or "high" or "urgent"))
            priority = "normal";
        if (title.Length == 0 || content.Length == 0)
            return FlashTo("Title and content are required.", "danger", "/workplace/");
        var now = clock.UtcNow;
        db.Add(new Announcement
        {
            Title = title, Content = content, Priority = priority, AuthorId = Me, Pinned = false,
            CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        return FlashTo("Announcement published.", "success", "/workplace/");
    }

    [HttpGet("events")]
    [AnyMember]
    public async Task<IActionResult> Events(CancellationToken ct)
    {
        var events = await EventRows(db.Set<TeamEvent>().AsNoTracking().OrderBy(e => e.EventDate).ThenBy(e => e.Id)).ToListAsync(ct);
        return View(new EventsModel(events, CanPost));
    }

    [HttpPost("events/create")]
    [AnyMember]   // a manager or an admin, checked in the route
    public async Task<IActionResult> CreateEvent(CancellationToken ct)
    {
        if (!CanPost)
            return FlashTo("Only a manager or an admin can add events to the team calendar.", "danger", "/workplace/events");
        string title = FormStr("title").Trim();
        string? eventDate = FormOpt("event_date");
        string? startTime = FormOpt("start_time");
        string? endTime = FormOpt("end_time");
        string location = FormStr("location").Trim();
        string eventType = FormStr("event_type", "meeting");
        if (title.Length == 0 || string.IsNullOrEmpty(eventDate))
            return FlashTo("Title and date are required.", "danger", "/workplace/");
        if (eventType is not ("meeting" or "workshop" or "social" or "other"))
            eventType = "other";
        TimeOnly start = default, end = default;
        if (!Py.TryParseDate(eventDate, out var date)
            || (!string.IsNullOrEmpty(startTime) && !Py.TryParseTime(startTime, out start))
            || (!string.IsNullOrEmpty(endTime) && !Py.TryParseTime(endTime, out end)))
            return FlashTo("Enter the date as YYYY-MM-DD and times as HH:MM.", "danger", "/workplace/events");
        db.Add(new TeamEvent
        {
            Title = title, EventDate = date, Location = location, EventType = eventType, CreatedBy = Me,
            StartTime = string.IsNullOrEmpty(startTime) ? null : start,
            EndTime = string.IsNullOrEmpty(endTime) ? null : end,
            CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return FlashTo("Event created.", "success", "/workplace/");
    }

    [HttpGet("kanban")]
    [AnyMember]
    public async Task<IActionResult> Kanban(CancellationToken ct)
    {
        var boards = await db.Set<KanbanBoard>().AsNoTracking().OrderBy(b => b.Id)
            .Select(b => new { b.Id, b.Title, b.CreatedBy }).ToListAsync(ct);
        var manageable = boards.Where(b => CanManage(b.CreatedBy)).Select(b => b.Id).ToHashSet();
        return View(new KanbanModel(boards.Select(b => (b.Id, b.Title)).ToList(), manageable,
                                    Py.JinjaInt(Request.Query["board"].FirstOrDefault() ?? "0")));
    }

    [HttpPost("kanban/create-board")]
    [AnyMember]   // anyone in the company may start a board
    public async Task<IActionResult> CreateBoard(CancellationToken ct)
    {
        string title = FormStr("title").Trim();
        if (title.Length == 0)
            return PyError("Title required", 400);
        var board = new KanbanBoard { Title = title, CreatedBy = Me, CreatedAt = clock.UtcNow };
        db.Add(board);
        await db.SaveChangesAsync(ct);
        return PyJson(new Dictionary<string, object?> { ["success"] = true, ["id"] = board.Id, ["title"] = board.Title });
    }

    [HttpPost("kanban/{bid:int}/add-task")]
    [AnyMember]   // the board's owner, a manager or an admin, checked in the route
    public async Task<IActionResult> AddTask(int bid, CancellationToken ct)
    {
        var board = await db.Set<KanbanBoard>().AsNoTracking().Where(b => b.Id == bid)
            .Select(b => new { b.CreatedBy }).FirstOrDefaultAsync(ct);
        if (board is null) return NotFoundPage();
        if (!CanManage(board.CreatedBy))
            return PyError("Only the board's owner, a manager or an admin can add tasks.", 403);
        string title = FormStr("title").Trim();
        int? assigneeId = FormInt("assignee_id");
        if (title.Length == 0)
            return PyError("Title required", 400);
        if (assigneeId is int aid && aid != 0 && await staff.MemberAsync(aid, Access, ct) is null)
            // A task could be handed to anyone's id, from any company.
            return PyError("Assign the task to a member of this company.", 400);
        var now = clock.UtcNow;
        var task = new KanbanTask
        {
            BoardId = bid, Title = title, AssigneeId = assigneeId, Status = "todo", Priority = "medium", Position = 0,
            CreatedAt = now, UpdatedAt = now,
        };
        db.Add(task);
        await db.SaveChangesAsync(ct);
        return PyJson(new Dictionary<string, object?> { ["success"] = true, ["id"] = task.Id });
    }

    [HttpGet("kanban/{bid:int}/tasks")]
    [AnyMember]
    public async Task<IActionResult> BoardTasks(int bid, CancellationToken ct)
    {
        var board = await db.Set<KanbanBoard>().AsNoTracking().Where(b => b.Id == bid)
            .Select(b => new { b.CreatedBy }).FirstOrDefaultAsync(ct);
        if (board is null) return NotFoundPage();
        var tasks = await db.Set<KanbanTask>().AsNoTracking().Where(t => t.BoardId == bid)
            .OrderBy(t => t.Position).ThenBy(t => t.Id)
            .Select(t => new
            {
                t.Id, t.Title, t.Status, t.Priority, t.AssigneeId, t.DueDate,
                Assignee = t.Assignee != null ? t.Assignee.FullName : null,
            })
            .ToListAsync(ct);
        bool canDelete = CanManage(board.CreatedBy);
        return PyJson(tasks.Select(t => new Dictionary<string, object?>
        {
            ["id"] = t.Id, ["title"] = t.Title, ["status"] = t.Status, ["priority"] = t.Priority,
            ["assignee"] = t.Assignee,
            ["due_date"] = Py.Iso(t.DueDate),
            ["can_move"] = canDelete || t.AssigneeId == Me,
            ["can_delete"] = canDelete,
        }).ToList());
    }

    [HttpPost("kanban/update-task/{tid:int}")]
    [AnyMember]   // the assignee, the board's owner, a manager or an admin, checked in the route
    public async Task<IActionResult> UpdateTask(int tid, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var task = await db.LockAsync<KanbanTask>(tid, ct);
        if (task is null) return NotFoundPage();
        int owner = await db.Set<KanbanBoard>().Where(b => b.Id == task.BoardId).Select(b => b.CreatedBy).FirstAsync(ct);
        if (!(CanManage(owner) || task.AssigneeId == Me))
            return PyError("Only the task's assignee, the board's owner, a manager or an admin can move it.", 403);
        var data = await ReadJsonObjectAsync(ct);
        bool changed = false;
        if (data is not null && data.Value.TryGetProperty("status", out var status))
        {
            string? s = status.ValueKind == JsonValueKind.String ? status.GetString() : null;
            if (s is null || !TaskStatuses.Contains(s))
                return PyError("Unknown status", 400);
            changed |= task.Status != s;
            task.Status = s;
        }
        if (data is not null && data.Value.TryGetProperty("position", out var position))
        {
            if (PythonInt(position) is not int p)
                return PyError("Position must be a number", 400);
            changed |= task.Position != p;
            task.Position = p;
        }
        if (changed)
            task.UpdatedAt = clock.UtcNow;      // onupdate=datetime.utcnow: only when a column changes
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return PySuccess();
    }

    [HttpPost("kanban/delete-task/{tid:int}")]
    [AnyMember]   // the board's owner, a manager or an admin, checked in the route
    public async Task<IActionResult> DeleteTask(int tid, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var task = await db.LockAsync<KanbanTask>(tid, ct);
        if (task is null) return NotFoundPage();
        int owner = await db.Set<KanbanBoard>().Where(b => b.Id == task.BoardId).Select(b => b.CreatedBy).FirstAsync(ct);
        if (!CanManage(owner))
            return PyError("Only the board's owner, a manager or an admin can delete tasks.", 403);
        db.Remove(task);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return PySuccess();
    }

    /// <summary>request.get_json(silent=True) or {}: the body as an object, null when absent, not sent as JSON or unreadable.</summary>
    private async Task<JsonElement?> ReadJsonObjectAsync(CancellationToken ct)
    {
        if (!Request.HasJsonContentType()) return null;
        try
        {
            using var doc = await JsonDocument.ParseAsync(Request.Body, cancellationToken: ct);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>int(value) of a JSON value: a whole number, a float (truncated), a numeric string, a bool; null otherwise.</summary>
    private static int? PythonInt(JsonElement v)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.Number:
                if (v.TryGetInt32(out int i)) return i;
                return v.TryGetDouble(out double d) && !double.IsNaN(d) && Math.Abs(d) < int.MaxValue ? (int)Math.Truncate(d) : null;
            case JsonValueKind.String:
                return Py.Int(v.GetString());
            case JsonValueKind.True:
                return 1;
            case JsonValueKind.False:
                return 0;
            default:
                return null;
        }
    }
}
