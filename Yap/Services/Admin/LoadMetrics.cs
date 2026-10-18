using System.Diagnostics;
using Yap.Services.Chat;
using Yap.Services.Persistence;
using Yap.Services.Presence;

namespace Yap.Services.Admin;

/// <summary>One second of the server under load: the process, the runtime, the hub and the queues.</summary>
public sealed record LoadSample(
    DateTime At,
    double CpuPercent,          // of all cores: 100 means every core busy
    long WorkingSetMB,
    long HeapMB,
    int Gen0, int Gen1, int Gen2,   // collections since start
    double GcPauseMs,           // GC pause time inside this second
    int ThreadPoolThreads,
    long ThreadPoolQueue,       // work items waiting for a thread: the thread-pool starvation signal
    int Connections,            // hub connections the presence service tracks as connected
    int MessagesPerSec,
    int HubSendsPerSec,         // SendCoreAsync calls, not recipients
    int RequestsPerSec,
    double RequestP95Ms,
    long RequestMaxMs,
    int WriterDepth,            // database writes queued and not yet on disk
    double SweepMs,             // the last presence sweep
    double? MachineLoad1,       // /proc/loadavg, Linux only
    long? MachineMemAvailableMB // /proc/meminfo, Linux only
);

/// <summary>
/// Samples <see cref="LoadCounters"/> and the runtime once a second into a ring buffer of one hour. The admin
/// page and the load-test orchestrator read it through GET /api/admin/load. It exists for stress tests: the
/// 30 s diagnostics snapshots are too coarse to see a knee in the curve.
/// </summary>
public sealed class LoadMetrics(LoadCounters counters, ChatService chat, PresenceService presence, PersistenceWriter writer) : BackgroundService
{
    public const int Capacity = 3600;   // 1 h at 1 s; about 200 bytes a sample, so under 1 MB
    private readonly Queue<LoadSample> _samples = new(Capacity);
    private readonly Lock _gate = new();
    private readonly Process _process = Process.GetCurrentProcess();

    public IReadOnlyList<LoadSample> Last(int seconds)
    {
        lock (_gate) return _samples.TakeLast(Math.Clamp(seconds, 1, Capacity)).ToList();
    }

    /// <summary>
    /// The last <paramref name="seconds"/> as buckets of <paramref name="step"/> seconds, so an hour of samples
    /// comes back as a few hundred points. A bucket averages the gauges and rates, keeps the worst request and
    /// the last GC counts, and carries the time of its last sample.
    /// </summary>
    public IReadOnlyList<LoadSample> Last(int seconds, int step)
    {
        var samples = Last(seconds);
        if (step <= 1) return samples;
        return samples.Chunk(step).Select(Bucket).ToList();
    }

    private static LoadSample Bucket(LoadSample[] chunk)
    {
        var last = chunk[^1];
        double Avg(Func<LoadSample, double> of) => Math.Round(chunk.Average(of), 1);
        double? AvgOrNull(Func<LoadSample, double?> of) => chunk.All(s => of(s) is null) ? null : Math.Round(chunk.Average(s => of(s) ?? 0), 1);
        return last with
        {
            CpuPercent = Avg(s => s.CpuPercent),
            WorkingSetMB = (long)Avg(s => s.WorkingSetMB),
            HeapMB = (long)Avg(s => s.HeapMB),
            GcPauseMs = Avg(s => s.GcPauseMs),
            ThreadPoolThreads = (int)Avg(s => s.ThreadPoolThreads),
            ThreadPoolQueue = (long)Avg(s => s.ThreadPoolQueue),
            Connections = (int)Avg(s => s.Connections),
            MessagesPerSec = (int)Math.Round(chunk.Average(s => s.MessagesPerSec)),
            HubSendsPerSec = (int)Math.Round(chunk.Average(s => s.HubSendsPerSec)),
            RequestsPerSec = (int)Math.Round(chunk.Average(s => s.RequestsPerSec)),
            RequestP95Ms = chunk.Max(s => s.RequestP95Ms),
            RequestMaxMs = chunk.Max(s => s.RequestMaxMs),
            WriterDepth = (int)Avg(s => s.WriterDepth),
            SweepMs = Avg(s => s.SweepMs),
            MachineLoad1 = AvgOrNull(s => s.MachineLoad1),
            MachineMemAvailableMB = (long?)AvgOrNull(s => s.MachineMemAvailableMB),
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Action<Models.Chat.ChatMessage> onMessage = _ => counters.Message();
        chat.OnMessageReceived += onMessage;
        var lastCpu = _process.TotalProcessorTime;
        var lastPause = GC.GetTotalPauseDuration();
        var lastAt = DateTime.UtcNow;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = DateTime.UtcNow;
                _process.Refresh();
                var cpu = _process.TotalProcessorTime;
                var pause = GC.GetTotalPauseDuration();
                var elapsed = (now - lastAt).TotalSeconds;
                var cpuPercent = elapsed > 0 ? (cpu - lastCpu).TotalSeconds / elapsed / Environment.ProcessorCount * 100 : 0;
                var (messages, sends, requests, p95, max) = counters.Drain();
                var (connected, _, _) = presence.Stats();
                var (load, mem) = Machine();

                var sample = new LoadSample(now, Math.Round(cpuPercent, 1), _process.WorkingSet64 >> 20, GC.GetTotalMemory(false) >> 20,
                    GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), Math.Round((pause - lastPause).TotalMilliseconds, 1),
                    ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount, connected, messages, sends, requests, p95, max,
                    writer.Depth, counters.LastSweepMs, load, mem);
                lock (_gate)
                {
                    if (_samples.Count == Capacity) _samples.Dequeue();
                    _samples.Enqueue(sample);
                }
                (lastCpu, lastPause, lastAt) = (cpu, pause, now);
            }
        }
        catch (OperationCanceledException) { }
        finally { chat.OnMessageReceived -= onMessage; }
    }

    // The host's view, so a run can tell "the app is slow" from "the box is out of memory". Both files are
    // cheap to read; a failure (not Linux, a locked-down container) just leaves the two fields empty.
    private static (double? Load1, long? MemAvailableMB) Machine()
    {
        if (!OperatingSystem.IsLinux()) return (null, null);
        try
        {
            var load = double.Parse(File.ReadAllText("/proc/loadavg").Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
            var memLine = File.ReadLines("/proc/meminfo").FirstOrDefault(l => l.StartsWith("MemAvailable:"));
            long? mem = memLine is null ? null : long.Parse(memLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) >> 10;
            return (load, mem);
        }
        catch { return (null, null); }
    }
}
