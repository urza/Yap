using System.Text;
using System.Text.Json;
using Yap.LoadTest.Orchestrator;
using Yap.LoadTest.Shared;

// Prepares the server, then runs until you press q. The keys change the load while it runs (see Session.Key):
// + and - step the user count, digits and Enter set it, t r l i d switch the kinds of traffic, < and > the pace.
//
//   dotnet Yap.LoadTest.Orchestrator.dll --server http://host:5221
//     [--users 0] [--kinds all] [--pace normal] [--previews off] [--rooms 1] [--visible 0.3] [--rate 10] [--per-worker 250]
//     [--admin loadadmin] [--admin-code "blue fox 42"] [--prefix lt_u] [--links links.txt]
//     [--state state] [--out results] [--for 30m] [--plain] [--worker path/to/Yap.LoadTest.Worker.dll]

var a = new Args(args);
if (a.Has("help") || !a.Has("server"))
{
    Console.WriteLine("usage: --server URL [--users 0] [--kinds all] [--pace normal] [--previews off] [--rooms 1] [--visible 0.3] [--rate 10] [--per-worker 250]");
    Console.WriteLine("       [--admin NAME] [--admin-code CODE] [--prefix lt_u] [--links FILE] [--state DIR] [--out DIR] [--for 30m] [--plain] [--worker DLL]");
    Console.WriteLine("keys:  + -  50 users   PgUp PgDn  250 users   digits, Enter  an exact count   t r l i d  text, reactions, links, images, DMs on or off   < >  pace   q  quit");
    Console.WriteLine("kinds: all, none, or a list such as text,links   pace: calm, normal, busy, frantic   previews: off (the default: no fetches of the shared links) or on (previews and media caching, as in production)");
    return 2;
}

var server = new Uri(a.Require("server"));
var perWorker = a.Int("per-worker", 250);
var rate = a.Double("rate", 10);
var rooms = a.Int("rooms", 1);
var visible = a.Double("visible", 0.3);
var prefix = a.Get("prefix") ?? "lt_u";
var stateDir = a.Get("state") ?? "state";
var links = a.Get("links");
// Off by default: with previews on, one IP asks YouTube and the other platforms for the same few links over and over,
// they refuse, and the box spends cores on yt-dlp and ffmpeg (the 2026-10-18 run lost half of them). On measures that cost on purpose.
var previews = (a.Get("previews") ?? "off").ToLowerInvariant() switch { "on" => true, "off" => false, var v => throw new ArgumentException($"--previews takes on or off, not '{v}'") };
var runFor = a.Get("for") is { } f ? Duration(f) : (TimeSpan?)null;
var workerDll = a.Get("worker") ?? Path.Combine(AppContext.BaseDirectory, "Yap.LoadTest.Worker.dll");
if (!File.Exists(workerDll)) { Console.Error.WriteLine($"worker not found: {workerDll} (pass --worker)"); return 2; }
var session = new Session(a.Int("users", 0), Traffic.ParseKinds(a.Get("kinds") ?? "all"), Enum.Parse<Pace>(a.Get("pace") ?? "normal", ignoreCase: true));

try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { }   // Windows PowerShell's default code page cannot show every character the log carries
var plain = a.Has("plain") || !Screen.Supported;   // Screen.Supported also turns the console's ANSI mode on, on Windows

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

var api = new YapApi(server);
var statePath = RunState.PathFor(server, stateDir);
var state = RunState.Load(statePath);
var aggregate = new Aggregate();
using var results = new Results(a.Get("out") ?? "results");
Action<string> log = line => { aggregate.Note(line); results.Log(line); if (plain) Console.WriteLine(line); };
log($"results: {results.Dir}");

// ---- the server: admin, settings, rooms. The accounts come later, per worker, when the user count asks for them. ----
var setup = new Setup(api, state, log);
string admin;
string? adminLink;
try
{
    admin = await setup.EnsureAdminAsync(a.Get("admin") ?? "loadadmin", a.Get("admin-code"), ct);
    state.Save(statePath);
    await setup.ApplySettingsAsync(admin, previews, ct);
    await setup.EnsureRoomsAsync(admin, rooms, ct);
    adminLink = await setup.AdminLinkAsync(admin, ct);
}
catch (Exception e) when (e is not OperationCanceledException)
{
    Console.Error.WriteLine($"setup failed: {e.Message}");
    return 1;
}

// ---- the run ------------------------------------------------------------------------------------------
var usersFile = Path.Combine(stateDir, "users.json");
await using var workers = new Workers(workerDll, aggregate);
var startedAt = DateTime.UtcNow;
var done = false;

if (!Console.IsInputRedirected)
    new Thread(() => { while (true) { try { session.Key(Console.ReadKey(true)); } catch (InvalidOperationException) { return; } } }) { IsBackground = true, Name = "keys" }.Start();

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
    using var screen = plain ? null : new Screen();
    var drawsPerRow = plain ? 1 : 4;
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / drawsPerRow));   // a fixed cadence: a slow iteration does not push every later row
    var draws = 0;
    while (!done)   // not the token: the dashboard stays up while the users disconnect after Ctrl-C or q
    {
        var s = session.Current;
        var v = aggregate.Snapshot();
        if (draws++ % drawsPerRow == 0)
        {
            aggregate.Tick();
            results.Row(s, v);
            if (plain && draws % (5 * drawsPerRow) == 0) Console.WriteLine(Dashboard.Line(v, s));
        }
        try { screen?.Draw(Dashboard.Render(v, s, server, adminLink, DateTime.UtcNow - startedAt)); }
        catch (Exception e) { log($"dashboard: {e.Message}"); }   // a frame that fails to render must not end the run
        await timer.WaitForNextTickAsync();
    }
});

// Each change of the settings closes a segment and opens the next one; the summary has one row per segment.
Session.Snapshot? current = null;
var segmentStarted = startedAt;
var endAt = runFor is { } d ? startedAt + d : DateTime.MaxValue;
while (!ct.IsCancellationRequested && !session.Quit && DateTime.UtcNow < endAt)
{
    var s = session.Current;
    if (current is not null && s.Version == current.Version) { await Task.Delay(250); continue; }
    if (current is not null) results.SegmentDone(current, segmentStarted, DateTime.UtcNow, aggregate.Segment);
    aggregate.NewSegment();
    segmentStarted = DateTime.UtcNow;
    current = s;
    log($"settings: {s.Label}");
    try { await ApplyAsync(s); }
    catch (OperationCanceledException) { }
    catch (Exception e) { log($"could not apply the settings: {e.Message} Change the user count to try again."); }
}
if (current is not null) results.SegmentDone(current, segmentStarted, DateTime.UtcNow, aggregate.Segment);
if (runFor is not null && DateTime.UtcNow >= endAt) log("the time is up");

aggregate.Status = "stopping: the users disconnect";
await workers.StopAsync();
done = true;
cts.Cancel();
await Task.WhenAll(display, poll);

var summary = results.Summary(server, startedAt);
File.WriteAllText(Path.Combine(results.Dir, "summary.md"), summary);
Console.WriteLine();
Console.WriteLine(summary);
Console.WriteLine($"files: {results.Dir}");
if (adminLink is not null) Console.WriteLine($"admin page: {adminLink}");   // the dashboard is gone now; the link is the way to the load history
return 0;

// Makes the workers match the settings. Worker k owns accounts k * perWorker .. (k + 1) * perWorker - 1; it is
// started, with its accounts created first, when the target first reaches into its block, and it stays for the run.
async Task ApplyAsync(Session.Snapshot s)
{
    var needed = (int)Math.Ceiling(s.Target / (double)perWorker);
    for (var id = 0; id < needed; id++)
    {
        if (workers.Has(id)) continue;
        var block = (id + 1) * perWorker;
        try { await setup.EnsureUsersAsync(prefix, block, ct, (n, of) => aggregate.Status = $"creating accounts {n}/{of}"); }
        finally { aggregate.Status = null; state.Save(statePath); }
        var accounts = state.Users.Where(u => u.Username.StartsWith(prefix)).OrderBy(u => u.Username, StringComparer.Ordinal).ToList();
        File.WriteAllText(usersFile, JsonSerializer.Serialize(accounts, Json.Options));
        workers.Start(id, server, usersFile, id * perWorker, perWorker, rate, rooms, visible, prefix, links);
        if (session.Current.Version != s.Version) return;   // the person changed the settings meanwhile; the loop applies the new ones
    }
    for (var id = 0; id < workers.Count; id++)
    {
        await workers.SendAsync(id, $"kinds {Traffic.Format(s.Kinds)}");
        await workers.SendAsync(id, $"pace {s.Pace}");
        await workers.SendAsync(id, $"users {Math.Clamp(s.Target - id * perWorker, 0, perWorker)}");
    }
}

static TimeSpan Duration(string s) => s[^1] switch
{
    's' => TimeSpan.FromSeconds(double.Parse(s[..^1])),
    'm' => TimeSpan.FromMinutes(double.Parse(s[..^1])),
    'h' => TimeSpan.FromHours(double.Parse(s[..^1])),
    _ => TimeSpan.FromSeconds(double.Parse(s)),
};
