namespace Yap.Models;

/// <summary>
/// How a user treats one whole class of channels (all DMs, or all rooms) for notifications.
/// </summary>
/// <remarks>
/// The numbering is load-bearing: AllowAll is 0 so the DM column takes the right value for every
/// existing row with no backfill. Rooms default to MuteAll instead, which is set explicitly in
/// <c>ChatDbContext</c> — do not renumber without a data migration.
/// </remarks>
public enum NotificationMode
{
    /// <summary>Every channel of this class notifies.</summary>
    AllowAll = 0,

    /// <summary>No channel of this class notifies.</summary>
    MuteAll = 1,

    /// <summary>Per-channel overrides decide; see <see cref="ChannelNotificationSetting"/>.</summary>
    Individual = 2
}
