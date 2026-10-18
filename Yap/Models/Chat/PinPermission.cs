namespace Yap.Models.Chat;

/// <summary>Who may pin and unpin messages in a room (AD-37). The admin always may. A direct message has no setting: both people may.</summary>
public enum PinPermission
{
    AdminOnly,
    Everyone,
    Selected    // the usernames in Channel.Pinners
}
