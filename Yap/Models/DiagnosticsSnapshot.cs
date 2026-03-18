namespace Yap.Models;

/// <summary>
/// Lightweight snapshot of system diagnostics captured periodically.
/// Omits EventSubscribers dict and AdminUser string to keep buffer lean.
/// </summary>
public record DiagnosticsSnapshot(
    DateTime Timestamp,
    int UserSessions,
    int UniqueUsers,
    int Channels,
    int RoomChannels,
    int DmChannels,
    int TotalMessages,
    int ActiveCircuits,
    int DisconnectedCircuits,
    int TotalCircuitsCreated,
    int TotalEventSubscribers);
