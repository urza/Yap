using Spectre.Console;
using Yap.LoadTest.Orchestrator;
using Yap.LoadTest.Shared;

// Prepares the server, starts the workers step by step, shows the dashboard, writes the results.
//
//   dotnet Yap.LoadTest.Orchestrator.dll --server http://host:5221 --preset big-room --steps 50,200,500,1000 --hold 10m
//     [--ramp 60s] [--per-worker 250] [--admin loadadmin] [--admin-code "blue fox 42"] [--prefix lt_u]
//     [--links links.txt] [--visible 0.3] [--state state] [--out results] [--plain] [--worker path/to/Yap.LoadTest.Worker.dll]

var a = new Args(args);
if (a.Has("help") || !a.Has("server"))
{
    Console.WriteLine("usage: --server URL [--preset NAME] [--steps 50,200,500,1000] [--hold 10m] [--ramp 60s] [--per-worker 250] [--admin NAME] [--admin-code CODE] [--prefix lt_u] [--links FILE] [--visible 0.3] [--plain]");
    Console.WriteLine("presets:");
    foreach (var p in Preset.All) Console.WriteLine($"  {p.Name,-12} {p.Description}");
    return 2;
}

var server = new Uri(a.Require("server"));
var preset = Preset.Find(a.Get("preset") ?? "big-room") ?? throw new ArgumentException($"unknown preset; one of {string.Join(", ", Preset.All.Select(p => p.Name))}");
var steps = (a.Get("steps") ?? "50,200,500,1000").Split(',').Select(int.Parse).ToList();
var hold = Duration(a.Get("hold") ?? "10m");
var ramp = Duration(a.Get("ramp") ?? "60s");
var perWorker = a.Int("per-worker", 250);
var prefix = a.Get("prefix") ?? "lt_u";
var stateDir = a.Get("state") ?? "state";
var links = a.Get("links");
double? visible = a.Has("visible") ? a.Double("visible", preset.VisibleShare) : null;
var plain = a.Has("plain") || Console.IsOutputRedirected;
var workerDll = a.Get("worker") ?? Path.Combine(AppContext.BaseDirectory, "Yap.LoadTest.Worker.dll");
if (!File.Exists(workerDll)) { Console.Error.WriteLine($"worker not found: {workerDll} (pass --worker)"); return 2; }

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

var api = new YapApi(server);
var statePath = RunState.PathFor(server, stateDir);
var state = RunState.Load(statePath);
var aggregate = new Aggregate();
Action<string> log = line => { aggregate.Note(line); if (plain) Console.WriteLine(line); else AnsiConsole.MarkupLine($"[grey]{Markup.Escape(line)}[/]"); };

// ---- the server: admin, settings, users, rooms ------------------------------------------------------
string admin;
try
{
    var setup = new Setup(api, state, log);
    admin = await setup.EnsureAdminAsync(a.Get("admin") ?? "loadadmin", a.Get("admin-code"), ct);
    state.Save(statePath);
    await setup.ApplySettingsAsync(admin, preset, ct);
    await setup.EnsureUsersAsync(prefix, steps.Max(), ct);
    state.Save(statePath);
    await setup.EnsureRoomsAsync(admin, preset.Rooms, ct);
}
catch (Exception e) when (e is not OperationCanceledException)
{
    Console.Error.WriteLine($"setup failed: {e.Message}");
    return 1;
}
var usersFile = Path.Combine(stateDir, "users.json");
File.WriteAllText(usersFile, System.Text.Json.JsonSerializer.Serialize(state.Users, Json.Options));

// ---- the run ------------------------------------------------------------------------------------------
using var results = new Results(a.Get("out") ?? "results", preset);
await using var workers = new Workers(workerDll, aggregate);
log($"results: {results.Dir}");

var stepIndex = 0;
var stepLabel = "setup";
var stepStarted = DateTime.UtcNow;
var skip = false;   // 'n' key
var done = false;

var poll = Task.Run(async () =>
{
    while (!ct.IsCancellationRequested)
    {
        try { var load = await api.GetAsync<LoadResponse>("/api/admin/load?seconds=4", admin, ct); if (load is not null) aggregate.AddServer(load.Series); }
        catch (OperationCanceledException) { }
        catch (Exception e) { aggregate.ServerError = e.Message; }
        try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { }
    }
});

var display = Task.Run(async () =>
{
    async Task Tick()
    {
        aggregate.Tick();
        results.Row(stepLabel, aggregate.Snapshot());
        if (!Console.IsInputRedirected && Console.KeyAvailable)
        {
            var key = Console.ReadKey(true).KeyChar;
            if (key is 'n' or 'N') skip = true;
            if (key is 'q' or 'Q') cts.Cancel();
        }
        try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { }
    }
    if (plain)
    {
        var n = 0;
        while (!done && !ct.IsCancellationRequested) { await Tick(); if (++n % 5 == 0) Console.WriteLine(Dashboard.Line(aggregate.Snapshot(), stepLabel)); }
        return;
    }
    await AnsiConsole.Live(new Text("starting")).AutoClear(false).StartAsync(async live =>
    {
        while (!done && !ct.IsCancellationRequested)
        {
            live.UpdateTarget(Dashboard.Render(aggregate.Snapshot(), preset.Name, stepLabel, stepIndex, steps.Count, DateTime.UtcNow - stepStarted, hold, server));
            await Tick();
        }
    });
});

var started = 0;
for (var i = 0; i < steps.Count && !ct.IsCancellationRequested; i++)
{
    stepIndex = i + 1;
    stepLabel = $"{steps[i]} users";
    stepStarted = DateTime.UtcNow;
    aggregate.NewStep();
    for (var remaining = steps[i] - started; remaining > 0;)
    {
        var count = Math.Min(perWorker, remaining);
        workers.Start(server, usersFile, started, count, preset, ramp, links, visible);
        started += count; remaining -= count;
    }
    // Hold = ramp + the time at the plateau. 'n' ends the hold early; the step's totals still count what ran.
    var until = DateTime.UtcNow + ramp + hold;
    skip = false;
    while (DateTime.UtcNow < until && !skip && !ct.IsCancellationRequested) { try { await Task.Delay(500, ct); } catch (OperationCanceledException) { } }
    results.StepDone(stepLabel, aggregate.Step);
}

done = true;
stepLabel = "stopping";
await workers.StopAsync();
cts.Cancel();
await Task.WhenAll(display, poll);

var summary = results.Summary(preset, server);
File.WriteAllText(Path.Combine(results.Dir, "summary.md"), summary);
Console.WriteLine();
Console.WriteLine(summary);
Console.WriteLine($"files: {results.Dir}");
return 0;

static TimeSpan Duration(string s) => s[^1] switch
{
    's' => TimeSpan.FromSeconds(double.Parse(s[..^1])),
    'm' => TimeSpan.FromMinutes(double.Parse(s[..^1])),
    'h' => TimeSpan.FromHours(double.Parse(s[..^1])),
    _ => TimeSpan.FromSeconds(double.Parse(s)),
};
