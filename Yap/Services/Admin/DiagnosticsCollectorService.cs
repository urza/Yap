using Yap.Models;
using Yap.Models.Admin;
using Yap.Services.Chat;
using Yap.Services.Presence;

namespace Yap.Services.Admin;

/// <summary>
/// Captures diagnostics snapshots every 30 seconds into a circular buffer (max 7 days).
/// </summary>
public class DiagnosticsCollectorService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private const int MaxEntries = 20160; // 7 days at 30s intervals

    private readonly ChatService _chatService;
    private readonly PresenceService _presence;
    private readonly ILogger<DiagnosticsCollectorService> _logger;

    private readonly LinkedList<DiagnosticsSnapshot> _snapshots = new();
    private readonly object _lock = new();

    public DiagnosticsCollectorService(
        ChatService chatService,
        PresenceService presence,
        ILogger<DiagnosticsCollectorService> logger)
    {
        _chatService = chatService;
        _presence = presence;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Capture initial snapshot immediately
        CaptureSnapshot();

        using var timer = new PeriodicTimer(Interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                CaptureSnapshot();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void CaptureSnapshot()
    {
        try
        {
            var diag = _chatService.GetDiagnostics();
            var (active, disconnected, totalCreated) = _presence.Stats();

            var snapshot = new DiagnosticsSnapshot(
                Timestamp: DateTime.UtcNow,
                UserSessions: diag.UserSessions,
                UniqueUsers: diag.UniqueUsers,
                Channels: diag.Channels,
                DmChannels: diag.DmChannels,
                TotalMessages: diag.TotalMessages,
                ActiveConnections: active,
                DisconnectedConnections: disconnected,
                TotalConnections: totalCreated,
                TotalEventSubscribers: diag.TotalEventSubscribers);

            lock (_lock)
            {
                _snapshots.AddLast(snapshot);
                while (_snapshots.Count > MaxEntries)
                    _snapshots.RemoveFirst();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to capture diagnostics snapshot");
        }
    }

    /// <summary>
    /// Returns all snapshots in chronological order.
    /// </summary>
    public List<DiagnosticsSnapshot> GetSnapshots()
    {
        lock (_lock)
        {
            return _snapshots.ToList();
        }
    }

    /// <summary>
    /// Returns the most recent snapshot, or null if none captured yet.
    /// </summary>
    public DiagnosticsSnapshot? GetLatest()
    {
        lock (_lock)
        {
            return _snapshots.Last?.Value;
        }
    }
}
