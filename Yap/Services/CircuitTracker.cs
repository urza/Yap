using System.Collections.Concurrent;

namespace Yap.Services;

/// <summary>
/// Singleton service that tracks circuit lifecycle for diagnostics.
/// </summary>
public class CircuitTracker
{
    private readonly ConcurrentDictionary<string, CircuitInfo> _circuits = new();
    private int _totalCreated = 0;

    public record CircuitInfo(string CircuitId, DateTime CreatedAt, bool IsConnected, DateTime? DisconnectedAt);

    public void OnCircuitOpened(string circuitId)
    {
        Interlocked.Increment(ref _totalCreated);
        _circuits[circuitId] = new CircuitInfo(circuitId, DateTime.UtcNow, true, null);
    }

    public void OnConnectionUp(string circuitId)
    {
        if (_circuits.TryGetValue(circuitId, out var info))
        {
            _circuits[circuitId] = info with { IsConnected = true, DisconnectedAt = null };
        }
    }

    public void OnConnectionDown(string circuitId)
    {
        if (_circuits.TryGetValue(circuitId, out var info))
        {
            _circuits[circuitId] = info with { IsConnected = false, DisconnectedAt = DateTime.UtcNow };
        }
    }

    public void OnCircuitClosed(string circuitId)
    {
        _circuits.TryRemove(circuitId, out _);
    }

    public (int Active, int Disconnected, int TotalCreated) GetStats()
    {
        var active = _circuits.Values.Count(c => c.IsConnected);
        var disconnected = _circuits.Values.Count(c => !c.IsConnected);
        return (active, disconnected, _totalCreated);
    }

    public List<CircuitInfo> GetAllCircuits() => _circuits.Values.ToList();
}
