using Spectre.Console;
using Spectre.Console.Rendering;
using Yap.LoadTest.Shared;

namespace Yap.LoadTest.Orchestrator;

/// <summary>The live view: the users, the kinds, the pace, the messages, the latency, the server, the last log lines.</summary>
public static class Dashboard
{
    public static IRenderable Render(Aggregate.View v, Session.Snapshot s, Uri server, string? adminLink, TimeSpan running)
    {
        var grid = new Grid().AddColumn(new GridColumn().PadRight(2)).AddColumn();
        Row(grid, "[bold]Yap load test[/]", $"{Esc(server.ToString())}   running {Clock(running)}   settings held {Clock(DateTime.UtcNow - s.ChangedAt)}   [bold]q[/] quit and write the summary");
        if (adminLink is not null) Row(grid, "admin", $"{Esc(adminLink)}   [dim]opens the admin page; the Diagnostics tab shows the server load[/]");
        grid.AddEmptyRow();

        var status = v.Status is { } st ? $"   [yellow]{Esc(st)}[/]" : "";
        var typing = s.Typing.Length > 0 ? $"   [yellow]new count {s.Typing}_  Enter sets it, Esc cancels[/]" : "";
        Row(grid, "users", $"[bold]{v.Users}[/] of {s.Target}   connected [bold green]{v.Connected}[/]{status}{typing}   [dim]+ - or arrows 50, PgUp PgDn 250, or type a number and Enter[/]");
        Row(grid, "kinds", string.Join("   ", Traffic.Each.Select(k => Switch(k, s.Kinds))) + "   [dim]press the letter[/]");
        Row(grid, "pace", $"[bold]{s.Pace.ToString().ToLowerInvariant()}[/]: a visible user acts about every {s.Pace.ThinkSeconds():0} s, {s.Pace.BurstChance():P0} of the sends start a burst   [dim]< slower, > faster[/]");
        grid.AddEmptyRow();

        var seg = v.Segment;
        Row(grid, "messages", $"sent [bold]{v.SentPerSec:0.0}[/]/s   received {v.ReceivedPerSec:0}/s   [dim]this segment:[/] {seg.Sent} sent, {seg.Uploads} uploads, {seg.Reactions} reactions, {Colour(seg.ErrorCount)} errors{Reasons(seg)}, {Colour(seg.Reconnects)} reconnects");
        Row(grid, "latency", $"send to appear p50 {Ms(v.WindowSta.Percentile(0.5))} / p95 {Warn(v.WindowSta.Percentile(0.95), 100, 500, "0")} ms   fan-out p95 {Ms(v.WindowFan.Percentile(0.95))} ms   send POST p95 {Ms(v.WindowHttp.Percentile(0.95))} ms   heartbeat p50 {Ms(seg.Rtt.Percentile(0.5))} ms   [dim]last 10 s[/]");
        Row(grid, "server", ServerLine(v));
        grid.AddEmptyRow();

        var first = true;
        foreach (var line in v.Log) { Row(grid, first ? "[dim]log[/]" : "", $"[dim]{Esc(line)}[/]"); first = false; }
        return grid;
    }

    private static string ServerLine(Aggregate.View v)
    {
        if (v.ServerError is { } err) return $"[red]unreachable: {Esc(err)}[/]";
        if (v.Server is not { } s) return "[dim]waiting for the first sample[/]";
        var age = (DateTime.UtcNow - s.At).TotalSeconds;
        var stale = age > 5 ? $"   [yellow]sample {age:0} s old[/]" : "";
        return $"cpu {Warn(s.CpuPercent, 70, 90, "0")} %   memory {s.WorkingSetMB} MB   connections {s.Connections}   requests {s.RequestsPerSec}/s, p95 {Warn(s.RequestP95Ms, 200, 1000, "0")} ms   " +
               $"thread pool queue {Warn(s.ThreadPoolQueue, 1, 100, "0")}   writer queue {Warn(s.WriterDepth, 100, 1000, "0")}   host load {s.MachineLoad1?.ToString("0.00") ?? "-"}{stale}";
    }

    // Every value is one line, cropped at the window's edge: a wrapped line would make the frame taller than the window.
    private static void Row(Grid grid, string label, string value) => grid.AddRow(new Markup(label), new Markup(value).Overflow(Overflow.Ellipsis));
    private static string Switch(Kinds k, Kinds on) => $"[bold]{k.ToString().ToLowerInvariant()[0]}[/] {k.ToString().ToLowerInvariant()} {(on.HasFlag(k) ? "[green]on[/]" : "[grey]off[/]")}";
    private static string Reasons(Totals t) => t.Errors.Count == 0 ? "" : $" ({Esc(string.Join(", ", t.Errors.OrderByDescending(e => e.Value).Take(3).Select(e => $"{e.Key} x{e.Value}")))})";
    private static string Clock(TimeSpan t) => t.ToString(@"hh\:mm\:ss");
    private static string Esc(string s) => Markup.Escape(s);
    private static string Ms(long v) => v < 0 ? "-" : v.ToString();
    private static string Colour(long n) => n == 0 ? "0" : $"[red]{n}[/]";
    private static string Warn(double v, double amber, double red, string fmt) => v < 0 ? "-" : v >= red ? $"[red]{v.ToString(fmt)}[/]" : v >= amber ? $"[yellow]{v.ToString(fmt)}[/]" : v.ToString(fmt);

    /// <summary>One line for a terminal without a live display (an ssh session without a tty, or a log file).</summary>
    public static string Line(Aggregate.View v, Session.Snapshot s)
    {
        var srv = v.Server;
        return $"{DateTime.Now:HH:mm:ss} users {v.Users,5}/{s.Target,-5} conn {v.Connected,5} | sent/s {v.SentPerSec,6:0.0} recv/s {v.ReceivedPerSec,7:0} | " +
               $"sta p50/p95 {Ms(v.WindowSta.Percentile(0.5)),5}/{Ms(v.WindowSta.Percentile(0.95)),5} fan p95 {Ms(v.WindowFan.Percentile(0.95)),5} | err {v.Segment.ErrorCount,4} | " +
               $"srv cpu {srv?.CpuPercent,5:0.0} mem {srv?.WorkingSetMB,5} q {srv?.ThreadPoolQueue,4} writer {srv?.WriterDepth,5} | {Traffic.Format(s.Kinds)} {s.Pace.ToString().ToLowerInvariant()}";
    }
}
