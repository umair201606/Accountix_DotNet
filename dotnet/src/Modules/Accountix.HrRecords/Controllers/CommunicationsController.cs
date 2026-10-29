using Accountix.HrRecords.Services;

namespace Accountix.HrRecords.Controllers;

/// <summary>hr_app/routes/communication.py (blueprint "communications"): notifications sent by admins, the e-mail log.</summary>
[Area("HrRecords")]
[Module(Modules.Hr)]
[Route("communications")]
public sealed class CommunicationsController(AccountixDbContext db, Communications comms) : RecordsController
{
    public sealed record MyRow(bool IsRead, string Title, string Message, DateTime? CreatedAt);

    public sealed record SentRow(string Title, string Message, string? Type, DateTime? CreatedAt, int Recipients);

    public sealed record EmailRow(string Recipient, string Subject, string? Status, DateTime? SentAt);

    public sealed record AdminModel(IReadOnlyList<SentRow> Notifications, IReadOnlyList<EmailRow> EmailLogs);

    [HttpGet("")]
    [AnyMember]   // HR is governed by role in the route: admins see what was sent, everyone else their own
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        if (!IsAdmin)
        {
            var mine = await db.Set<NotificationRecipient>().AsNoTracking()
                .Where(r => r.UserId == Me)
                .OrderByDescending(r => r.Id).Take(50)
                .Select(r => new MyRow(r.IsRead == true, r.Notification.Title, r.Notification.Message, r.Notification.CreatedAt))
                .ToListAsync(ct);
            return View("Index", mine);
        }
        // Recipients counted in the same query (Python asked once per row).
        var sent = await db.Set<Notification>().AsNoTracking()
            .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id).Take(50)
            .Select(n => new SentRow(n.Title, n.Message, n.NotificationType, n.CreatedAt, n.NotificationRecipients.Count()))
            .ToListAsync(ct);
        var logs = await db.Set<EmailLog>().AsNoTracking()
            .OrderByDescending(e => e.SentAt).ThenByDescending(e => e.Id).Take(20)
            .Select(e => new EmailRow(e.Recipient, e.Subject, e.Status, e.SentAt))
            .ToListAsync(ct);
        return View("Admin", new AdminModel(sent, logs));
    }

    [HttpPost("send-notification")]
    [AnyMember]   // admins only, checked in the route as Python does
    public async Task<IActionResult> SendNotification(CancellationToken ct)
    {
        if (!IsAdmin)
            return PyError("Access denied", 403);
        string title = FormStr("title").Trim();
        string message = FormStr("message").Trim();
        string type = FormStr("type", "info");
        string target = FormStr("target", "all");
        if (title.Length == 0 || message.Length == 0)
            return FlashTo("Title and message required.", "danger", "/communications/");
        int count = await comms.SendAsync(Cid, Me, title, message, type, target, FormOpt("role"), ct);
        return FlashTo($"Notification sent to {count} users.", "success", "/communications/");
    }

    [HttpPost("send-email")]
    [AnyMember]   // admins only, checked in the route
    public async Task<IActionResult> SendEmail(CancellationToken ct)
    {
        if (!IsAdmin)
            return PyError("Access denied", 403);
        string recipient = FormStr("recipient").Trim();
        string subject = FormStr("subject").Trim();
        string body = FormStr("body").Trim();
        if (recipient.Length == 0 || subject.Length == 0)
            return FlashTo("Recipient and subject required.", "danger", "/communications/");
        await comms.LogEmailAsync(recipient, subject, body, ct);
        return FlashTo("Email logged (SMTP not configured for live send).", "info", "/communications/");
    }
}
