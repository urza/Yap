namespace Yap.Models.Admin;

/// <summary>
/// Diagnostic information about the chat system state.
/// </summary>
public class ChatDiagnostics
{
    public int UserSessions { get; set; }
    public int UniqueUsers { get; set; }
    public int Channels { get; set; }
    public int RoomChannels { get; set; }
    public int DmChannels { get; set; }
    public int TotalMessages { get; set; }
    public Dictionary<string, int> EventSubscribers { get; set; } = new();
    public string? AdminUser { get; set; }

    // Hub connections (from PresenceService)
    public int ActiveConnections { get; set; }
    public int DisconnectedConnections { get; set; }
    public int TotalConnections { get; set; }

    // Computed
    public int TotalEventSubscribers => EventSubscribers.Values.Sum();
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}
