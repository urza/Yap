namespace Yap.Models.Admin;

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
    int ActiveConnections,
    int DisconnectedConnections,
    int TotalConnections,
    int TotalEventSubscribers);
