using System.Collections.Concurrent;
using Yap.LoadTest.Shared;

namespace Yap.LoadTest.Worker;

/// <summary>The process-wide counters every virtual user bumps. Drained once a second into a <see cref="WorkerReport"/>.</summary>
public sealed class Stats(int worker)
{
    private int _users, _connected, _sent, _received, _events, _uploads, _reactions, _reconnects;
    private readonly ConcurrentDictionary<string, int> _errors = new();
    public readonly Histogram SendToAppear = new(), FanOut = new(), Http = new(), Rtt = new();

    public void UserStarted() => Interlocked.Increment(ref _users);
    public void Connected(bool up) => Interlocked.Add(ref _connected, up ? 1 : -1);
    public void Sent() => Interlocked.Increment(ref _sent);
    public void Received() => Interlocked.Increment(ref _received);
    public void Event() => Interlocked.Increment(ref _events);
    public void Upload() => Interlocked.Increment(ref _uploads);
    public void Reaction() => Interlocked.Increment(ref _reactions);
    public void Reconnect() => Interlocked.Increment(ref _reconnects);
    public void Error(string reason) => _errors.AddOrUpdate(reason, 1, (_, n) => n + 1);

    public WorkerReport Drain()
    {
        var errors = new Dictionary<string, int>();
        foreach (var key in _errors.Keys) if (_errors.TryRemove(key, out var n) && n > 0) errors[key] = n;
        return new WorkerReport(worker, DateTime.UtcNow, Volatile.Read(ref _users), Volatile.Read(ref _connected),
            Interlocked.Exchange(ref _sent, 0), Interlocked.Exchange(ref _received, 0), Interlocked.Exchange(ref _events, 0),
            Interlocked.Exchange(ref _uploads, 0), Interlocked.Exchange(ref _reactions, 0), Interlocked.Exchange(ref _reconnects, 0),
            errors, SendToAppear.Drain(), FanOut.Drain(), Http.Drain(), Rtt.Drain());
    }
}
