namespace Yap.LoadTest.Shared;

/// <summary>
/// One second of one worker, written as a JSON line on stdout. Counters are deltas for that second; the
/// orchestrator sums them. Users and Connected are gauges.
/// </summary>
public sealed record WorkerReport(
    int Worker,
    DateTime At,
    int Users,            // virtual users started so far
    int Connected,        // hub connections up right now
    int Sent,             // messages POSTed and accepted (2xx)
    int Received,         // hub `message` events
    int Events,           // every hub event, all names
    int Uploads,
    int Reactions,
    int Reconnects,
    Dictionary<string, int> Errors,   // by reason: "http 409", "hub", "upload", ...
    HistogramData SendToAppear,       // POST start to own `message` event
    HistogramData FanOut,             // another user's send time to receipt here (same machine clock)
    HistogramData Http,               // POST round trip
    HistogramData Rtt                 // heartbeat invoke round trip
);
