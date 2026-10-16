using System.Globalization;

// The box the server runs on, once a second, from /proc: CPU, memory, disk, network, and the Yap process (CPU,
// RSS, threads, open files). Independent of the app, so it still records when the app is down or restarting.
// A container's process is visible in the host's /proc, so this runs on the VPS itself, next to Docker.
//
//   dotnet Yap.LoadTest.HostMonitor.dll [--match Yap.dll | --pid N] [--out host.csv] [--interval 1] [--quiet]

var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (var i = 0; i < args.Length; i++) if (args[i].StartsWith("--")) opts[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
if (!OperatingSystem.IsLinux()) { Console.Error.WriteLine("Linux only: it reads /proc"); return 2; }
var match = opts.GetValueOrDefault("match", "Yap.dll");
var pid = opts.TryGetValue("pid", out var p) ? int.Parse(p) : (int?)null;
var interval = TimeSpan.FromSeconds(double.Parse(opts.GetValueOrDefault("interval", "1"), CultureInfo.InvariantCulture));
var quiet = opts.ContainsKey("quiet");
var outPath = opts.GetValueOrDefault("out", $"host-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
var inv = CultureInfo.InvariantCulture;
var ticks = (double)Environment.SystemPageSize;   // reused below for RSS pages -> MB
var hz = 100.0;   // CLK_TCK on every mainstream Linux build

using var csv = new StreamWriter(outPath, false) { AutoFlush = true };
csv.WriteLine("time,cpu_pct,iowait_pct,load1,mem_total_mb,mem_available_mb,swap_used_mb,disk_read_mb_s,disk_write_mb_s,net_rx_mb_s,net_tx_mb_s,tcp_established,proc_pid,proc_cpu_pct,proc_rss_mb,proc_threads,proc_fds");
Console.Error.WriteLine($"writing {outPath}; Ctrl+C to stop");

var cpuPrev = Cpu(); var diskPrev = Disk(); var netPrev = Net(); var procPrev = 0L; var procPid = pid; var at = DateTime.UtcNow;
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
using var timer = new PeriodicTimer(interval);
try
{
    while (await timer.WaitForNextTickAsync(cts.Token))
    {
        var now = DateTime.UtcNow; var dt = (now - at).TotalSeconds; at = now;
        var cpu = Cpu(); var disk = Disk(); var net = Net();
        var total = cpu.Total - cpuPrev.Total;
        var cpuPct = total > 0 ? 100.0 * (total - (cpu.Idle - cpuPrev.Idle) - (cpu.IoWait - cpuPrev.IoWait)) / total : 0;
        var ioPct = total > 0 ? 100.0 * (cpu.IoWait - cpuPrev.IoWait) / total : 0;
        var (memTotal, memAvail, swapUsed) = Mem();
        var load1 = File.ReadAllText("/proc/loadavg").Split(' ')[0];

        procPid ??= FindPid(match);
        string procCpu = "", procRss = "", procThreads = "", procFds = "";
        if (procPid is { } id)
        {
            try
            {
                var stat = File.ReadAllText($"/proc/{id}/stat");
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');   // after "(comm) ", so a space in the name cannot shift the fields
                var jiffies = long.Parse(fields[11]) + long.Parse(fields[12]);  // utime + stime
                procCpu = (procPrev == 0 ? 0 : 100.0 * (jiffies - procPrev) / hz / dt).ToString("0.0", inv);
                procPrev = jiffies;
                procThreads = fields[17];
                procRss = (long.Parse(File.ReadAllText($"/proc/{id}/statm").Split(' ')[1]) * ticks / 1048576).ToString("0", inv);
                procFds = Directory.GetFiles($"/proc/{id}/fd").Length.ToString();
            }
            catch { procPid = null; procPrev = 0; }   // gone; look again next tick
        }
        var line = string.Join(',', now.ToString("HH:mm:ss"), cpuPct.ToString("0.0", inv), ioPct.ToString("0.0", inv), load1, memTotal, memAvail, swapUsed,
            ((disk.Read - diskPrev.Read) / dt / 1048576).ToString("0.00", inv), ((disk.Write - diskPrev.Write) / dt / 1048576).ToString("0.00", inv),
            ((net.Rx - netPrev.Rx) / dt / 1048576).ToString("0.00", inv), ((net.Tx - netPrev.Tx) / dt / 1048576).ToString("0.00", inv), TcpEstablished(),
            procPid?.ToString() ?? "", procCpu, procRss, procThreads, procFds);
        csv.WriteLine(line);
        if (!quiet) Console.WriteLine($"{now:HH:mm:ss} cpu {cpuPct,5:0.0}% io {ioPct,4:0.0}% load {load1,5} free {memAvail,6} MB | net rx/tx {(net.Rx - netPrev.Rx) / dt / 1048576,6:0.00}/{(net.Tx - netPrev.Tx) / dt / 1048576,6:0.00} MB/s | yap pid {procPid?.ToString() ?? "—",6} cpu {procCpu,5}% rss {procRss,5} MB thr {procThreads,3} fds {procFds,5}");
        (cpuPrev, diskPrev, netPrev) = (cpu, disk, net);
    }
}
catch (OperationCanceledException) { }
return 0;

static (long Total, long Idle, long IoWait) Cpu()
{
    var f = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(long.Parse).ToArray();
    return (f.Take(8).Sum(), f[3], f[4]);
}

static (long Read, long Write) Disk()
{
    long read = 0, write = 0;
    foreach (var line in File.ReadLines("/proc/diskstats"))
    {
        var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length < 14 || f[2].StartsWith("loop") || f[2].StartsWith("ram") || char.IsDigit(f[2][^1]) && !f[2].StartsWith("nvme") && !f[2].StartsWith("mmcblk")) continue;   // whole devices only: sda, vda, nvme0n1
        read += long.Parse(f[5]) * 512; write += long.Parse(f[9]) * 512;
    }
    return (read, write);
}

static (long Rx, long Tx) Net()
{
    long rx = 0, tx = 0;
    foreach (var line in File.ReadLines("/proc/net/dev").Skip(2))
    {
        var parts = line.Split(':');
        if (parts[0].Trim() == "lo") continue;
        var f = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        rx += long.Parse(f[0]); tx += long.Parse(f[8]);
    }
    return (rx, tx);
}

static (long TotalMB, long AvailableMB, long SwapUsedMB) Mem()
{
    var kv = File.ReadLines("/proc/meminfo").Select(l => l.Split(':')).ToDictionary(p => p[0], p => long.Parse(p[1].Trim().Split(' ')[0]));
    return (kv["MemTotal"] >> 10, kv["MemAvailable"] >> 10, (kv["SwapTotal"] - kv["SwapFree"]) >> 10);
}

static int TcpEstablished()
{
    var n = 0;
    foreach (var file in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
    {
        if (!File.Exists(file)) continue;
        foreach (var line in File.ReadLines(file).Skip(1)) { var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries); if (f.Length > 3 && f[3] == "01") n++; }
    }
    return n;
}

static int? FindPid(string match)
{
    foreach (var dir in Directory.EnumerateDirectories("/proc"))
    {
        if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
        try { if (File.ReadAllText($"{dir}/cmdline").Replace('\0', ' ').Contains(match, StringComparison.OrdinalIgnoreCase) && pid != Environment.ProcessId) return pid; }
        catch { }
    }
    return null;
}
