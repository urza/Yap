using Yap.LoadTest.Shared;

namespace Yap.LoadTest.Orchestrator;

/// <summary>Totals of one span of time: a segment (one stretch of unchanged settings), or the whole run.</summary>
public sealed class Totals
{
    public long Sent, Received, Events, Uploads, Reactions, Reconnects;
    public int UsersMax;
    public readonly Dictionary<string, long> Errors = [];
    public HistogramData SendToAppear = HistogramData.Empty, FanOut = HistogramData.Empty, Http = HistogramData.Empty, Rtt = HistogramData.Empty;
    public readonly List<LoadSample> Server = [];

    public void Add(WorkerReport r)
    {
        Sent += r.Sent; Received += r.Received; Events += r.Events; Uploads += r.Uploads; Reactions += r.Reactions; Reconnects += r.Reconnects;
        foreach (var (k, v) in r.Errors) Errors[k] = Errors.GetValueOrDefault(k) + v;
        SendToAppear = SendToAppear.Plus(r.SendToAppear); FanOut = FanOut.Plus(r.FanOut); Http = Http.Plus(r.Http); Rtt = Rtt.Plus(r.Rtt);
    }

    public long ErrorCount => Errors.Values.Sum();
}

/// <summary>
/// Merges the workers' one-second reports and the server's one-second samples. Gauges (users, connected) come
/// from each worker's latest line; counters add up per segment and for the run; a 10 s window feeds the dashboard.
/// </summary>
public sealed class Aggregate
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, WorkerReport> _latest = [];
    private readonly Queue<WorkerReport> _window = new();
    public readonly Totals Run = new();
    public Totals Segment { get; private set; } = new();
    public readonly List<string> Log = [];
    public LoadSample? Server { get; private set; }
    public string? ServerError { get; set; }
    public volatile string? Status;   // what the run loop is busy with, shown on the users line ("creating accounts 120/250")
    private DateTime _lastServerAt = DateTime.MinValue;

    public void NewSegment() { lock (_gate) Segment = new Totals(); }

    /// <summary>Once a second: the running users of the moment, for the segment's and the run's maximum.</summary>
    public void Tick()
    {
        lock (_gate)
        {
            var users = _latest.Values.Sum(w => w.Users);
            Run.UsersMax = Math.Max(Run.UsersMax, users); Segment.UsersMax = Math.Max(Segment.UsersMax, users);
        }
    }

    public void Add(WorkerReport r)
    {
        lock (_gate)
        {
            _latest[r.Worker] = r;
            Run.Add(r); Segment.Add(r);
            _window.Enqueue(r);
            var cutoff = DateTime.UtcNow.AddSeconds(-10);
            while (_window.Count > 0 && _window.Peek().At < cutoff) _window.Dequeue();
        }
    }

    public void AddServer(IEnumerable<LoadSample> samples)
    {
        lock (_gate)
        {
            foreach (var s in samples.Where(s => s.At > _lastServerAt).OrderBy(s => s.At))
            {
                Run.Server.Add(s); Segment.Server.Add(s);
                Server = s; _lastServerAt = s.At;
            }
            ServerError = null;
        }
    }

    public void Note(string line) { lock (_gate) { Log.Add($"{DateTime.Now:HH:mm:ss} {line}"); if (Log.Count > 200) Log.RemoveAt(0); } }

    public sealed record View(int Users, int Connected, double SentPerSec, double ReceivedPerSec,
        HistogramData WindowSta, HistogramData WindowFan, HistogramData WindowHttp, Totals Segment, Totals Run, LoadSample? Server, string? ServerError, string? Status, IReadOnlyList<string> Log);

    public View Snapshot()
    {
        lock (_gate)
        {
            var span = Math.Max(1, _window.Count == 0 ? 1 : (int)Math.Ceiling((DateTime.UtcNow - _window.Peek().At).TotalSeconds));
            var sta = HistogramData.Empty; var fan = HistogramData.Empty; var http = HistogramData.Empty;
            long sent = 0, received = 0;
            foreach (var r in _window) { sta = sta.Plus(r.SendToAppear); fan = fan.Plus(r.FanOut); http = http.Plus(r.Http); sent += r.Sent; received += r.Received; }
            return new View(_latest.Values.Sum(w => w.Users), _latest.Values.Sum(w => w.Connected), (double)sent / span, (double)received / span,
                sta, fan, http, Segment, Run, Server, ServerError, Status, Log.TakeLast(8).ToList());
        }
    }
}
