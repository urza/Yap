namespace Yap.Services.Admin;

/// <summary>
/// The counters the hot paths bump: one message sent, one hub send, one HTTP request with its duration, one
/// presence sweep with its duration. <see cref="LoadMetrics"/> reads and resets them once a second. This class
/// has no dependencies on purpose, so the presence service and the middleware can take it without a cycle.
/// </summary>
public sealed class LoadCounters
{
    private int _messages, _hubSends, _requests;
    private readonly List<long> _requestMs = [];
    private readonly Lock _gate = new();

    /// <summary>Milliseconds of the last presence sweep. A sweep is rare (10 s), so the last value is the sample.</summary>
    public double LastSweepMs { get; private set; }

    public void Message() => Interlocked.Increment(ref _messages);
    public void HubSend() => Interlocked.Increment(ref _hubSends);
    public void Sweep(double ms) => LastSweepMs = ms;

    public void Request(long ms)
    {
        Interlocked.Increment(ref _requests);
        lock (_gate) _requestMs.Add(ms);
    }

    /// <summary>Takes one second of counts and starts the next second at zero.</summary>
    public (int Messages, int HubSends, int Requests, double RequestP95Ms, long RequestMaxMs) Drain()
    {
        var messages = Interlocked.Exchange(ref _messages, 0);
        var sends = Interlocked.Exchange(ref _hubSends, 0);
        var requests = Interlocked.Exchange(ref _requests, 0);
        long[] durations;
        lock (_gate) { durations = [.. _requestMs]; _requestMs.Clear(); }
        if (durations.Length == 0) return (messages, sends, requests, 0, 0);
        Array.Sort(durations);
        var p95 = durations[Math.Min(durations.Length - 1, (int)Math.Ceiling(durations.Length * 0.95) - 1)];
        return (messages, sends, requests, p95, durations[^1]);
    }
}
