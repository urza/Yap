namespace Yap.Models;

/// <summary>
/// User presence status for display in the chat.
/// </summary>
public enum UserStatus
{
    /// <summary>
    /// User is actively online (green indicator).
    /// </summary>
    Online,

    /// <summary>
    /// User is away - set manually or automatically after inactivity (orange indicator).
    /// </summary>
    Away,

    /// <summary>
    /// User appears offline to others but can still send/receive messages.
    /// </summary>
    Invisible
}
