using Yap.Models;

namespace Yap.Services;

/// <summary>
/// Captures diagnostics snapshots every 30 seconds into a circular buffer (max 7 days).
/// </summary>
public class DiagnosticsCollectorService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private const int MaxEntries = 20160; // 7 days at 30s intervals

    private readonly ChatService _chatService;
    private readonly CircuitTracker _circuitTracker;
    private readonly ILogger<DiagnosticsCollectorService> _logger;

    private readonly LinkedList<DiagnosticsSnapshot> _snapshots = new();
    private readonly object _lock = new();

    public DiagnosticsCollectorService(
        ChatService chatService,
        CircuitTracker circuitTracker,
        ILogger<DiagnosticsCollectorService> logger)
    {
        _chatService = chatService;
        _circuitTracker = circuitTracker;
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
            var (active, disconnected, totalCreated) = _circuitTracker.GetStats();

            var snapshot = new DiagnosticsSnapshot(
                Timestamp: DateTime.UtcNow,
                UserSessions: diag.UserSessions,
                UniqueUsers: diag.UniqueUsers,
                Channels: diag.Channels,
                RoomChannels: diag.RoomChannels,
                DmChannels: diag.DmChannels,
                TotalMessages: diag.TotalMessages,
                ActiveCircuits: active,
                DisconnectedCircuits: disconnected,
                TotalCircuitsCreated: totalCreated,
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
