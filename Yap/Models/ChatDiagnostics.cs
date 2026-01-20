namespace Yap.Models;

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

    // Circuit stats (populated by CircuitTracker)
    public int ActiveCircuits { get; set; }
    public int DisconnectedCircuits { get; set; }
    public int TotalCircuitsCreated { get; set; }

    // Computed
    public int TotalEventSubscribers => EventSubscribers.Values.Sum();
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}
