using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class Notification
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string Title { get; set; } = null!;

    public string Message { get; set; } = null!;

    public string? NotificationType { get; set; }

    public string? Module { get; set; }

    public int? ReferenceId { get; set; }

    public int? CreatedBy { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual User? CreatedByNavigation { get; set; }

    public virtual ICollection<NotificationRecipient> NotificationRecipients { get; set; } = new List<NotificationRecipient>();
}
