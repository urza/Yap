namespace Yap.Models;

/// <summary>
/// One user's mute override for one channel, used when the matching
/// <see cref="NotificationMode"/> is <see cref="NotificationMode.Individual"/>.
/// </summary>
/// <remarks>
/// Rows are written only when the user actually flips a channel, so a missing row means
/// "use the class default": <c>User.NotifNewDmsMuted</c> for DMs, muted for rooms. Rows survive
/// a switch to AllowAll/MuteAll and are ignored while that lasts, so returning to Individual
/// restores the user's earlier per-channel picks.
/// </remarks>
public class ChannelNotificationSetting
{
    public Guid UserId { get; set; }

    public Guid ChannelId { get; set; }

    /// <summary>True = this channel notifies nothing for this user.</summary>
    public bool Muted { get; set; }

    // Navigation property
    public User? User { get; set; }
}
