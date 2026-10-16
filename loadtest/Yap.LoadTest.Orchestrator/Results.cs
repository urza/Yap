using System.Globalization;
using System.Text;
using Yap.LoadTest.Shared;

namespace Yap.LoadTest.Orchestrator;

/// <summary>The files of one run: a one-second timeline CSV while it runs, and a summary table per step at the end.</summary>
public sealed class Results : IDisposable
{
    public string Dir { get; }
    private readonly StreamWriter _timeline;
    private readonly List<(string Label, Totals Totals)> _steps = [];

    public Results(string root, Preset preset)
    {
        Dir = Path.Combine(root, $"{DateTime.Now:yyyyMMdd-HHmmss}-{preset.Name}");
        Directory.CreateDirectory(Dir);
        _timeline = new StreamWriter(Path.Combine(Dir, "timeline.csv"), false, new UTF8Encoding(false)) { AutoFlush = true };
        _timeline.WriteLine("time,step,users,connected,sent_per_s,received_per_s,sta_p50,sta_p95,sta_p99,fan_p95,http_p95,errors_step," +
                            "srv_cpu,srv_ws_mb,srv_heap_mb,srv_gc2,srv_pool_queue,srv_connections,srv_msg_s,srv_hub_sends_s,srv_req_s,srv_req_p95,srv_writer_depth,srv_sweep_ms,host_load1,host_mem_free_mb");
    }

    public void Row(string step, Aggregate.View v)
    {
        var s = v.Server;
        var inv = CultureInfo.InvariantCulture;
        _timeline.WriteLine(string.Join(',',
            DateTime.UtcNow.ToString("HH:mm:ss"), step, v.Users, v.Connected, v.SentPerSec.ToString("0.0", inv), v.ReceivedPerSec.ToString("0.0", inv),
            Cell(v.WindowSta.Percentile(0.5)), Cell(v.WindowSta.Percentile(0.95)), Cell(v.WindowSta.Percentile(0.99)), Cell(v.WindowFan.Percentile(0.95)), Cell(v.WindowHttp.Percentile(0.95)), v.Step.ErrorCount,
            s?.CpuPercent.ToString(inv), s?.WorkingSetMB, s?.HeapMB, s?.Gen2, s?.ThreadPoolQueue, s?.Connections, s?.MessagesPerSec, s?.HubSendsPerSec, s?.RequestsPerSec,
            s?.RequestP95Ms.ToString(inv), s?.WriterDepth, s?.SweepMs.ToString("0.0", inv), s?.MachineLoad1?.ToString(inv), s?.MachineMemAvailableMB));
    }

    public void StepDone(string label, Totals totals) => _steps.Add((label, totals));

    /// <summary>The table the run is for: one row per step, so the knee is visible at a glance.</summary>
    public string Summary(Preset preset, Uri server)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Load test: {preset.Name} against {server}");
        sb.AppendLine();
        sb.AppendLine(preset.Description);
        sb.AppendLine();
        sb.AppendLine("| Step | Users | Seconds | Msg sent | Msg/s | Send→appear p50/p95/p99 ms | Fan-out p95/p99 ms | HTTP p95 ms | RTT p95 ms | Errors | Reconnects | CPU avg/max % | WS max MB | Pool queue max | Writer depth max | Sweep max ms |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var (label, t) in _steps)
        {
            var cpu = t.Server.Count > 0 ? $"{t.Server.Average(s => s.CpuPercent):0} / {t.Server.Max(s => s.CpuPercent):0}" : "—";
            var ws = t.Server.Count > 0 ? t.Server.Max(s => s.WorkingSetMB).ToString() : "—";
            var pool = t.Server.Count > 0 ? t.Server.Max(s => s.ThreadPoolQueue).ToString() : "—";
            var depth = t.Server.Count > 0 ? t.Server.Max(s => s.WriterDepth).ToString() : "—";
            var sweep = t.Server.Count > 0 ? t.Server.Max(s => s.SweepMs).ToString("0.0") : "—";
            sb.AppendLine($"| {label} | {label.Split(' ')[0]} | {t.Seconds} | {t.Sent} | {(t.Seconds > 0 ? (double)t.Sent / t.Seconds : 0):0.0} | " +
                          $"{P(t.SendToAppear, 0.5)} / {P(t.SendToAppear, 0.95)} / {P(t.SendToAppear, 0.99)} | {P(t.FanOut, 0.95)} / {P(t.FanOut, 0.99)} | {P(t.Http, 0.95)} | {P(t.Rtt, 0.95)} | " +
                          $"{t.ErrorCount} | {t.Reconnects} | {cpu} | {ws} | {pool} | {depth} | {sweep} |");
        }
        var errors = _steps.SelectMany(s => s.Totals.Errors).GroupBy(e => e.Key).Select(g => $"{g.Key}: {g.Sum(e => e.Value)}").ToList();
        if (errors.Count > 0) { sb.AppendLine(); sb.AppendLine("Errors by reason: " + string.Join(", ", errors)); }
        sb.AppendLine();
        sb.AppendLine("Percentiles are the upper bound of a log-spaced bucket (1, 2, 3, 5, 8, 10, 15, 20, 30, 50, 75, 100, 150, 200, 300, 500, 750, 1000, ... ms), never optimistic.");
        return sb.ToString();
    }

    private static string Cell(long percentile) => percentile < 0 ? "" : percentile.ToString();   // an empty window is an empty cell, not -1
    private static string P(HistogramData h, double p) => h.Count == 0 ? "—" : h.Percentile(p).ToString();

    public void Dispose() => _timeline.Dispose();
}
