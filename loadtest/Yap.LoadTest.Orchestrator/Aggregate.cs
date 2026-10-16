using Yap.LoadTest.Shared;

namespace Yap.LoadTest.Orchestrator;

/// <summary>Totals of one span of time: a step, or the whole run.</summary>
public sealed class Totals
{
    public long Sent, Received, Events, Uploads, Reactions, Reconnects;
    public readonly Dictionary<string, long> Errors = [];
    public HistogramData SendToAppear = HistogramData.Empty, FanOut = HistogramData.Empty, Http = HistogramData.Empty, Rtt = HistogramData.Empty;
    public readonly List<LoadSample> Server = [];
    public int Seconds;

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
/// from each worker's latest line; counters add up per step and for the run; a 10 s window feeds the dashboard.
/// </summary>
public sealed class Aggregate
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, WorkerReport> _latest = [];
    private readonly Queue<WorkerReport> _window = new();
    public readonly Totals Run = new();
    public Totals Step { get; private set; } = new();
    public readonly List<string> Log = [];
    public LoadSample? Server { get; private set; }
    public string? ServerError { get; set; }
    private DateTime _lastServerAt = DateTime.MinValue;

    public void NewStep() { lock (_gate) Step = new Totals(); }
    public void Tick() { lock (_gate) { Run.Seconds++; Step.Seconds++; } }

    public void Add(WorkerReport r)
    {
        lock (_gate)
        {
            _latest[r.Worker] = r;
            Run.Add(r); Step.Add(r);
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
                Run.Server.Add(s); Step.Server.Add(s);
                Server = s; _lastServerAt = s.At;
            }
            ServerError = null;
        }
    }

    public void Note(string line) { lock (_gate) { Log.Add($"{DateTime.Now:HH:mm:ss} {line}"); if (Log.Count > 200) Log.RemoveAt(0); } }

    public sealed record View(IReadOnlyList<WorkerReport> Workers, int Users, int Connected, double SentPerSec, double ReceivedPerSec,
        HistogramData WindowSta, HistogramData WindowFan, HistogramData WindowHttp, Totals Step, Totals Run, LoadSample? Server, string? ServerError, IReadOnlyList<string> Log);

    public View Snapshot()
    {
        lock (_gate)
        {
            var workers = _latest.Values.OrderBy(w => w.Worker).ToList();
            var span = Math.Max(1, _window.Count == 0 ? 1 : (int)Math.Ceiling((DateTime.UtcNow - _window.Peek().At).TotalSeconds));
            var sta = HistogramData.Empty; var fan = HistogramData.Empty; var http = HistogramData.Empty;
            long sent = 0, received = 0;
            foreach (var r in _window) { sta = sta.Plus(r.SendToAppear); fan = fan.Plus(r.FanOut); http = http.Plus(r.Http); sent += r.Sent; received += r.Received; }
            return new View(workers, workers.Sum(w => w.Users), workers.Sum(w => w.Connected), (double)sent / span, (double)received / span,
                sta, fan, http, Step, Run, Server, ServerError, Log.TakeLast(8).ToList());
        }
    }
}
