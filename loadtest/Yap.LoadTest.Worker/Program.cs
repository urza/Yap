using System.Text.Json;
using Yap.LoadTest.Shared;
using Yap.LoadTest.Worker;

// One process, N virtual users, one JSON report line per second on stdout. The orchestrator starts several of
// these and reads the lines; "stop" on stdin (or SIGTERM) ends the run with a clean disconnect.
//
//   --server http://host:5221 --users users.json --from 0 --count 250 --ramp 60 --preset big-room --worker 0
//   [--links links.txt] [--visible 0.3]

var a = new Args(args);
var server = new Uri(a.Require("server"));
var preset = Preset.Find(a.Get("preset") ?? "big-room") ?? throw new ArgumentException($"unknown preset: {a.Get("preset")}");
var worker = a.Int("worker", 0);
var users = JsonSerializer.Deserialize<List<SavedUser>>(File.ReadAllText(a.Require("users")), Json.Options) ?? [];
var slice = users.Skip(a.Int("from", 0)).Take(a.Int("count", users.Count)).ToList();
var ramp = TimeSpan.FromSeconds(a.Double("ramp", 60));
var links = a.Get("links") is { } linksPath && File.Exists(linksPath)
    ? File.ReadAllLines(linksPath).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList()
    : Content.DefaultLinks.ToList();
double? visible = a.Has("visible") ? a.Double("visible", preset.VisibleShare) : null;

var stats = new Stats(worker);
var ctx = new Context(worker, new YapApi(server), preset, stats, links, visible);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();
_ = Task.Run(() => { while (Console.ReadLine() is { } line) if (line.Trim() == "stop") { cts.Cancel(); break; } });

var reporter = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
    var stdout = Console.Out;
    try
    {
        while (await timer.WaitForNextTickAsync(cts.Token))
            await stdout.WriteLineAsync(JsonSerializer.Serialize(stats.Drain(), Json.Options));
    }
    catch (OperationCanceledException) { }
});

// Users start spread evenly over the ramp, so the server sees people arriving, not a thundering herd.
var tasks = new List<Task>();
var gap = slice.Count > 1 ? ramp / slice.Count : TimeSpan.Zero;
for (var i = 0; i < slice.Count && !cts.IsCancellationRequested; i++)
{
    var u = slice[i];
    tasks.Add(new VirtualUser(ctx, u.Username, u.Token, a.Int("from", 0) + i).RunAsync(cts.Token));
    try { await Task.Delay(gap, cts.Token); } catch (OperationCanceledException) { }
}
await Task.WhenAll(tasks);
await reporter;
Console.Out.WriteLine(JsonSerializer.Serialize(stats.Drain(), Json.Options));   // the last partial second

/// <summary>A test account the orchestrator created: the name and the permanent yap_auth token.</summary>
public sealed record SavedUser(string Username, string Token);
