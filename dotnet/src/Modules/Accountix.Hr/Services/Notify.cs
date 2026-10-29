namespace Accountix.Hr.Services;

/// <summary>
/// In-app notifications, written as the Python routes write them
/// (_create_notification in leave.py, _notify in mss.py, the timesheet and
/// loan notices): one notifications row and one notification_recipients row
/// per person. Public for other modules (HrRecords' bell reads these rows).
/// Adds to the context; the caller commits with its own change.
/// </summary>
public sealed class Notify(AccountixDbContext db)
{
    /// <summary>One notification for one person. type: info | success | danger | warning; module: leaves, mss, timesheets, loans...</summary>
    public async Task<Notification> ToAsync(int userId, string title, string message, string type, string module,
                                            int? referenceId, int createdBy, DateTime utcNow, CancellationToken ct = default)
    {
        var n = await CreateAsync(title, message, type, module, referenceId, createdBy, utcNow, ct);
        db.Add(new NotificationRecipient { NotificationId = n.Id, UserId = userId, IsRead = false });
        return n;
    }

    /// <summary>The notification row alone (flushed, so it has its id); add recipients with <see cref="Recipient"/>.</summary>
    public async Task<Notification> CreateAsync(string title, string message, string type, string module,
                                                int? referenceId, int createdBy, DateTime utcNow, CancellationToken ct = default)
    {
        var n = new Notification
        {
            Title = title, Message = message, NotificationType = type, Module = module,
            ReferenceId = referenceId, CreatedBy = createdBy, CreatedAt = utcNow,
        };
        db.Add(n);
        await db.SaveChangesAsync(ct);
        return n;
    }

    public void Recipient(Notification n, int userId) =>
        db.Add(new NotificationRecipient { NotificationId = n.Id, UserId = userId, IsRead = false });
}
