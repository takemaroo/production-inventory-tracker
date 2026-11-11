using InventoryTracker.Api.Auth;

namespace InventoryTracker.Api.Domain;

/// <summary>
/// Records that one Manager has read one notification (US-23). Table <c>NotificationReads</c>.
/// </summary>
/// <remarks>
/// The primary key is the pair (notification, user), so the same Manager cannot mark the same notification read
/// twice. "Unread for a Manager" means no row exists for that Manager.
/// </remarks>
public class NotificationRead
{
    /// <summary>Foreign key to the notification; first half of the primary key.</summary>
    public int NotificationId { get; set; }

    /// <summary>The notification, when loaded.</summary>
    public Notification Notification { get; set; } = null!;

    /// <summary>Foreign key to the Manager who read it; second half of the primary key.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>The Manager, when loaded.</summary>
    public AppUser User { get; set; } = null!;

    /// <summary>When the Manager marked it read (UTC).</summary>
    public DateTime ReadAt { get; set; }
}
