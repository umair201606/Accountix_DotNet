using Microsoft.AspNetCore.Mvc.ViewComponents;

namespace Accountix.HrRecords.Components;

/// <summary>
/// The topbar bell (hr_app/app.py inject_notifications + the notif-dropdown
/// block of templates/layouts/app_shell.html): unread count and the five
/// newest unread notifications, rendered with Python's markup so
/// /static/js/app.js marks them read through /auth/api/notifications. Two
/// queries. Nothing renders without an open company (Python skips it too).
/// The app shell (Web.Shared, coordinator-owned) does not call it yet: one
/// line there, <c>@await Component.InvokeAsync("NotificationBell")</c> after
/// the module badge, when HrRecords is loaded.
/// </summary>
public sealed class NotificationBellViewComponent(AccountixDbContext db) : ViewComponent
{
    public sealed record Item(int Id, string Title, string Message);

    public sealed record Model(int UnreadCount, IReadOnlyList<Item> Recent);

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var access = HttpContext.Access();
        if (access?.CompanyId is null)
            return Content("");
        int me = access.User.Id;
        var unread = db.Set<NotificationRecipient>().AsNoTracking().Where(r => r.UserId == me && r.IsRead == false);
        int count = await unread.CountAsync(HttpContext.RequestAborted);
        var recent = count == 0 ? [] : await unread.OrderByDescending(r => r.Id).Take(5)
            .Select(r => new Item(r.Id, r.Notification.Title, r.Notification.Message))
            .ToListAsync(HttpContext.RequestAborted);
        return View(new Model(count, recent));
    }
}
