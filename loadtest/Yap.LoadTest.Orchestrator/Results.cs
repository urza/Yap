using System.Globalization;
using System.Text;
using Yap.LoadTest.Shared;

namespace Yap.LoadTest.Orchestrator;

/// <summary>
/// The files of one run: a one-second timeline CSV and the log while it runs, and at the end a summary with one
/// row per segment, a stretch of unchanged settings.
/// </summary>
public sealed class Results : IDisposable
{
    public string Dir { get; }
    private readonly StreamWriter _timeline, _log;
    private readonly List<(Session.Snapshot Settings, DateTime Started, DateTime Ended, Totals Totals)> _segments = [];

    public Results(string root)
    {
        Dir = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(Dir);
        _timeline = new StreamWriter(Path.Combine(Dir, "timeline.csv"), false, new UTF8Encoding(false)) { AutoFlush = true };
        _timeline.WriteLine("time,users_target,users,connected,kinds,pace,sent_per_s,received_per_s,sta_p50,sta_p95,sta_p99,fan_p95,http_p95,errors_segment," +
                            "srv_cpu,srv_ws_mb,srv_heap_mb,srv_gc2,srv_pool_queue,srv_connections,srv_msg_s,srv_hub_sends_s,srv_req_s,srv_req_p95,srv_writer_depth,srv_sweep_ms,host_load1,host_mem_free_mb");
        _log = new StreamWriter(Path.Combine(Dir, "log.txt"), false, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public void Log(string line) => _log.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");

    public void Row(Session.Snapshot st, Aggregate.View v)
    {
        var s = v.Server;
        var inv = CultureInfo.InvariantCulture;
        _timeline.WriteLine(string.Join(',',
            DateTime.UtcNow.ToString("HH:mm:ss"), st.Target, v.Users, v.Connected, Traffic.Format(st.Kinds), st.Pace.ToString().ToLowerInvariant(),
            v.SentPerSec.ToString("0.0", inv), v.ReceivedPerSec.ToString("0.0", inv),
            Cell(v.WindowSta.Percentile(0.5)), Cell(v.WindowSta.Percentile(0.95)), Cell(v.WindowSta.Percentile(0.99)), Cell(v.WindowFan.Percentile(0.95)), Cell(v.WindowHttp.Percentile(0.95)), v.Segment.ErrorCount,
            s?.CpuPercent.ToString(inv), s?.WorkingSetMB, s?.HeapMB, s?.Gen2, s?.ThreadPoolQueue, s?.Connections, s?.MessagesPerSec, s?.HubSendsPerSec, s?.RequestsPerSec,
            s?.RequestP95Ms.ToString(inv), s?.WriterDepth, s?.SweepMs.ToString("0.0", inv), s?.MachineLoad1?.ToString(inv), s?.MachineMemAvailableMB));
    }

    public void SegmentDone(Session.Snapshot settings, DateTime started, DateTime ended, Totals totals) => _segments.Add((settings, started, ended, totals));

    /// <summary>The table the run is for: one row per segment, so the knee is visible at a glance.</summary>
    public string Summary(Uri server, DateTime startedAt)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Load test against {server}");
        sb.AppendLine();
        sb.AppendLine($"Started {startedAt.ToLocalTime():yyyy-MM-dd HH:mm}, ran {DateTime.UtcNow - startedAt:hh\\:mm\\:ss}. " +
                      "One row per stretch of unchanged settings that lasted 30 s or more with users running; the shorter stretches are the moments between two key presses. timeline.csv has every second.");
        sb.AppendLine();
        sb.AppendLine("| Started | Length | Users | Kinds | Pace | Msg sent | Msg/s | Send to appear p50/p95/p99 ms | Fan-out p95/p99 ms | HTTP p95 ms | RTT p95 ms | Errors | Reconnects | CPU avg/max % | Memory max MB | Pool queue max | Writer queue max |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        var withUsers = _segments.Where(s => s.Totals.UsersMax > 0).ToList();
        var listed = withUsers.Where(s => s.Ended - s.Started >= TimeSpan.FromSeconds(30)).ToList();
        if (listed.Count == 0) listed = withUsers;
        foreach (var (st, started, ended, t) in listed)
        {
            var srv = t.Server;
            var seconds = (ended - started).TotalSeconds;
            var cpu = srv.Count > 0 ? $"{srv.Average(s => s.CpuPercent):0} / {srv.Max(s => s.CpuPercent):0}" : "-";
            var users = t.UsersMax < st.Target ? $"{t.UsersMax} of {st.Target}" : st.Target.ToString();   // fewer than wanted: the ramp was cut short, or accounts failed
            sb.AppendLine($"| {started.ToLocalTime():HH:mm:ss} | {ended - started:hh\\:mm\\:ss} | {users} | {Traffic.Format(st.Kinds)} | {st.Pace.ToString().ToLowerInvariant()} | {t.Sent} | {(seconds > 0 ? t.Sent / seconds : 0):0.0} | " +
                          $"{P(t.SendToAppear, 0.5)} / {P(t.SendToAppear, 0.95)} / {P(t.SendToAppear, 0.99)} | {P(t.FanOut, 0.95)} / {P(t.FanOut, 0.99)} | {P(t.Http, 0.95)} | {P(t.Rtt, 0.95)} | " +
                          $"{t.ErrorCount} | {t.Reconnects} | {cpu} | {Max(srv, s => s.WorkingSetMB)} | {Max(srv, s => s.ThreadPoolQueue)} | {Max(srv, s => s.WriterDepth)} |");
        }
        var errors = _segments.SelectMany(s => s.Totals.Errors).GroupBy(e => e.Key).Select(g => $"{g.Key}: {g.Sum(e => e.Value)}").ToList();
        if (errors.Count > 0) { sb.AppendLine(); sb.AppendLine("Errors by reason: " + string.Join(", ", errors)); }
        sb.AppendLine();
        sb.AppendLine("Percentiles are the upper bound of a log-spaced bucket (1, 2, 3, 5, 8, 10, 15, 20, 30, 50, 75, 100, 150, 200, 300, 500, 750, 1000, ... ms), never optimistic.");
        return sb.ToString();
    }

    private static string Cell(long percentile) => percentile < 0 ? "" : percentile.ToString();   // an empty window is an empty cell, not -1
    private static string P(HistogramData h, double p) => h.Count == 0 ? "-" : h.Percentile(p).ToString();
    private static string Max(List<LoadSample> s, Func<LoadSample, long> f) => s.Count == 0 ? "-" : s.Max(f).ToString();

    public void Dispose() { _timeline.Dispose(); _log.Dispose(); }
}
