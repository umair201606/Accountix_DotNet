namespace Accountix.HrRecords.Services;

/// <summary>
/// hr_app/routes/communication.py: company-wide notifications and the
/// manual e-mail log. The rows are what Python writes (one notifications
/// row, one notification_recipients row per person, module
/// "communications"; an e-mail logged "not_sent", since no mail server is
/// configured).
/// </summary>
public sealed class Communications(AccountixDbContext db, Staff staff, CompanyClock clock)
{
    public const string Module = "communications";

    /// <summary>
    /// Who a send reaches: "all" = every active employee here (admins are not
    /// staff); "role" = the active employees holding that role in THIS
    /// company (the membership's, not the account's); anything else, nobody.
    /// </summary>
    public async Task<List<int>> RecipientsAsync(int companyId, string target, string? roleName, CancellationToken ct = default)
    {
        IQueryable<Person> q;
        if (target == "all")
            q = await staff.ActiveEmployeesAsync(companyId, ct: ct);
        else if (target == "role")
        {
            if (roleName is null || !(await staff.RoleIdsAsync(ct)).TryGetValue(roleName, out int roleId))
                return [];
            q = await staff.ActiveEmployeesAsync(companyId, roleId, ct: ct);
        }
        else
            return [];
        return await q.Select(p => p.Id).ToListAsync(ct);
    }

    /// <summary>send_notification: the notification and its recipients, in one transaction. Returns how many people it reached.</summary>
    public async Task<int> SendAsync(int companyId, int senderId, string title, string message, string type,
                                     string target, string? roleName, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var n = new Notification
        {
            Title = title, Message = message, NotificationType = type, Module = Module,
            CreatedBy = senderId, CreatedAt = clock.UtcNow,
        };
        db.Add(n);
        await db.SaveChangesAsync(ct);
        var users = await RecipientsAsync(companyId, target, roleName, ct);
        foreach (int uid in users)
            db.Add(new NotificationRecipient { NotificationId = n.Id, UserId = uid, IsRead = false });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return users.Count;
    }

    /// <summary>send_email: logged only ("not_sent"): no mail server is configured, so nothing leaves the app.</summary>
    public async Task LogEmailAsync(string recipient, string subject, string body, CancellationToken ct = default)
    {
        db.Add(new EmailLog
        {
            Recipient = recipient, Subject = subject, Body = body, Module = "manual", Status = "not_sent",
            SentAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }
}
