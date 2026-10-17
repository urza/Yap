using System.Text.Json;
using Yap.LoadTest.Shared;
using Yap.LoadTest.Worker;

// One process, a slice of the accounts, one JSON report line per second on stdout. The orchestrator starts several
// and drives each one over stdin: "users 120" sets how many of the slice run, "kinds text,links" and "pace busy"
// change what they do, "stop" ends the run with a clean disconnect. The end of stdin means the orchestrator is
// gone, and the worker stops too, so no worker outlives it.
//
//   --server http://host:5221 --users users.json --from 0 --count 250 --worker 0
//   [--rate 10] [--rooms 1] [--visible 0.3] [--prefix lt_u] [--links links.txt]

var a = new Args(args);
var server = new Uri(a.Require("server"));
var worker = a.Int("worker", 0);
var from = a.Int("from", 0);
var users = JsonSerializer.Deserialize<List<SavedUser>>(File.ReadAllText(a.Require("users")), Json.Options) ?? [];
var slice = users.Skip(from).Take(a.Int("count", users.Count)).ToList();
var links = a.Get("links") is { } linksPath && File.Exists(linksPath)
    ? File.ReadAllLines(linksPath).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList()
    : Content.DefaultLinks.ToList();

var controls = new Controls();
var stats = new Stats(worker);
var ctx = new Context(worker, new YapApi(server), controls, stats, a.Int("rooms", 1), a.Double("visible", 0.3), a.Get("prefix") ?? "lt_u", links);
var crowd = new Crowd(ctx, slice, from, a.Double("rate", 10));
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

_ = Task.Run(() =>
{
    while (Console.ReadLine() is { } line)
    {
        var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) continue;
        try
        {
            switch (parts[0])
            {
                case "stop": cts.Cancel(); return;
                case "users": crowd.SetTarget(int.Parse(parts[1])); break;
                case "kinds": controls.Kinds = Traffic.ParseKinds(parts[1]); break;
                case "pace": controls.Pace = Enum.Parse<Pace>(parts[1], ignoreCase: true); break;
                default: Console.Error.WriteLine($"unknown command: {line}"); break;
            }
        }
        catch (Exception e) { Console.Error.WriteLine($"bad command '{line}': {e.Message}"); }
    }
    cts.Cancel();
});

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

await crowd.RunAsync(cts.Token);
await reporter;
Console.Out.WriteLine(JsonSerializer.Serialize(stats.Drain(), Json.Options));   // the disconnects after the stop

/// <summary>A test account the orchestrator created: the name and the permanent yap_auth token.</summary>
public sealed record SavedUser(string Username, string Token);
