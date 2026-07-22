using System.Collections.Concurrent;

namespace Yap.Services;

/// <summary>
/// Singleton service that tracks circuit lifecycle and per-circuit latency telemetry for diagnostics.
/// Latency data comes from three sources: the client-side RTT probe (ChatLayout + chat.js),
/// slow inbound-event timing (ChatCircuitHandler), and the client-side send→appear timer (chat.js).
/// </summary>
public class CircuitTracker
{
    private readonly ConcurrentDictionary<string, CircuitInfo> _circuits = new();
    private int _totalCreated = 0;

    // Telemetry lives in init-only extras so the original positional construction stays untouched.
    public record CircuitInfo(string CircuitId, DateTime CreatedAt, bool IsConnected, DateTime? DisconnectedAt)
    {
        public string? Username { get; init; }
        public string? ClientIp { get; init; }
        public bool? IsWebSocket { get; init; }          // false = SSE/long-polling fallback — prime per-client latency suspect
        public double? LastRttMs { get; init; }
        public double? AvgRttMs { get; init; }           // EWMA — smooths jitter without keeping a sample list
        public double? MaxRttMs { get; init; }
        public int RttSamples { get; init; }
        public DateTime? RttUpdatedAt { get; init; }
        public int SlowEventCount { get; init; }         // inbound events over ChatCircuitHandler's slow threshold
        public double? MaxEventMs { get; init; }
        public DateTime? LastSlowEventAt { get; init; }
        public double? LastSendToAppearMs { get; init; } // client-measured: send click → own message in the DOM
        public DateTime? SendTimingAt { get; init; }
    }

    public void OnCircuitOpened(string circuitId)
    {
        Interlocked.Increment(ref _totalCreated);
        _circuits[circuitId] = new CircuitInfo(circuitId, DateTime.UtcNow, true, null);
    }

    public void OnConnectionUp(string circuitId) =>
        Update(circuitId, info => info with { IsConnected = true, DisconnectedAt = null });

    public void OnConnectionDown(string circuitId) =>
        Update(circuitId, info => info with { IsConnected = false, DisconnectedAt = DateTime.UtcNow });

    public void OnCircuitClosed(string circuitId)
    {
        _circuits.TryRemove(circuitId, out _);
    }

    /// <summary>
    /// Attaches user identity + transport to a circuit. Null args leave existing values untouched —
    /// the username may not be hydrated yet at circuit-open, and transport is unknowable outside
    /// a request context (e.g. on some reconnect callbacks).
    /// </summary>
    public void SetUser(string circuitId, string? username, string? clientIp, bool? isWebSocket) =>
        Update(circuitId, info => info with
        {
            Username = username ?? info.Username,
            ClientIp = clientIp ?? info.ClientIp,
            IsWebSocket = isWebSocket ?? info.IsWebSocket
        });

    public void ReportRtt(string circuitId, double rttMs) =>
        Update(circuitId, info => info with
        {
            LastRttMs = rttMs,
            AvgRttMs = info.AvgRttMs is double avg ? 0.75 * avg + 0.25 * rttMs : rttMs,
            MaxRttMs = Math.Max(info.MaxRttMs ?? 0, rttMs),
            RttSamples = info.RttSamples + 1,
            RttUpdatedAt = DateTime.UtcNow
        });

    public void ReportSlowEvent(string circuitId, double elapsedMs) =>
        Update(circuitId, info => info with
        {
            SlowEventCount = info.SlowEventCount + 1,
            MaxEventMs = Math.Max(info.MaxEventMs ?? 0, elapsedMs),
            LastSlowEventAt = DateTime.UtcNow
        });

    public void ReportSendTiming(string circuitId, double ms) =>
        Update(circuitId, info => info with { LastSendToAppearMs = ms, SendTimingAt = DateTime.UtcNow });

    // Read-modify-write without a lock: all frequent writers for one circuit run on that circuit's
    // own dispatcher (sequential), so the classic lost-update race can't bite in practice — and a
    // dropped telemetry sample would be harmless anyway.
    private void Update(string circuitId, Func<CircuitInfo, CircuitInfo> mutate)
    {
        if (_circuits.TryGetValue(circuitId, out var info))
            _circuits[circuitId] = mutate(info);
    }

    public (int Active, int Disconnected, int TotalCreated) GetStats()
    {
        var active = _circuits.Values.Count(c => c.IsConnected);
        var disconnected = _circuits.Values.Count(c => !c.IsConnected);
        return (active, disconnected, _totalCreated);
    }

    public List<CircuitInfo> GetAllCircuits() => _circuits.Values.ToList();
}
