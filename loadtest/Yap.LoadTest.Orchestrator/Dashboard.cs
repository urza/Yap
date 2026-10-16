using Spectre.Console;
using Spectre.Console.Rendering;
using Yap.LoadTest.Shared;

namespace Yap.LoadTest.Orchestrator;

/// <summary>The live view: the run, the workers, the window percentiles, the server, the log. Rebuilt once a second.</summary>
public static class Dashboard
{
    public static IRenderable Render(Aggregate.View v, string preset, string stepLabel, int stepIndex, int stepCount, TimeSpan inStep, TimeSpan hold, Uri server)
    {
        var header = new Panel(new Markup(
                $"[bold]{Markup.Escape(preset)}[/] against [blue]{Markup.Escape(server.ToString())}[/]   step [bold]{stepIndex}/{stepCount}[/]: {Markup.Escape(stepLabel)}   " +
                $"in step {inStep:mm\\:ss} of {hold:mm\\:ss}   users [bold]{v.Users}[/] connected [bold green]{v.Connected}[/]   " +
                $"[dim]keys: n = next step now, q = stop and summarise[/]"))
            .Expand().BorderColor(Color.Grey);

        var workers = new Table().Border(TableBorder.Rounded).Title("[bold]Workers[/]").Expand();
        foreach (var h in new[] { "id", "users", "conn", "sent/s", "recv/s", "events/s", "upl", "react", "sta p95", "fan p95", "http p95", "rtt", "reconn", "errors" }) workers.AddColumn(h);
        foreach (var w in v.Workers)
            workers.AddRow(w.Worker.ToString(), w.Users.ToString(), w.Connected.ToString(), w.Sent.ToString(), w.Received.ToString(), w.Events.ToString(),
                w.Uploads.ToString(), w.Reactions.ToString(), Ms(w.SendToAppear.Percentile(0.95)), Ms(w.FanOut.Percentile(0.95)), Ms(w.Http.Percentile(0.95)),
                Ms(w.Rtt.Percentile(0.5)), w.Reconnects.ToString(), w.Errors.Count == 0 ? "" : $"[red]{Markup.Escape(string.Join(" ", w.Errors.Select(e => $"{e.Key}×{e.Value}")))}[/]");

        var latency = new Table().Border(TableBorder.Rounded).Title("[bold]Latency (last 10 s / this step / run)[/]").Expand();
        latency.AddColumn(""); latency.AddColumn("p50"); latency.AddColumn("p95"); latency.AddColumn("p99"); latency.AddColumn("max"); latency.AddColumn("n");
        Row(latency, "send→appear 10 s", v.WindowSta); Row(latency, "send→appear step", v.Step.SendToAppear); Row(latency, "send→appear run", v.Run.SendToAppear);
        Row(latency, "fan-out 10 s", v.WindowFan); Row(latency, "fan-out step", v.Step.FanOut);
        Row(latency, "http POST 10 s", v.WindowHttp); Row(latency, "heartbeat rtt step", v.Step.Rtt);

        var totals = new Table().Border(TableBorder.Rounded).Title("[bold]Totals[/]").Expand();
        totals.AddColumn(""); totals.AddColumn("step"); totals.AddColumn("run");
        totals.AddRow("messages sent", v.Step.Sent.ToString(), v.Run.Sent.ToString());
        totals.AddRow("sent/s now", v.SentPerSec.ToString("0.0"), "");
        totals.AddRow("received/s now", v.ReceivedPerSec.ToString("0.0"), "");
        totals.AddRow("uploads", v.Step.Uploads.ToString(), v.Run.Uploads.ToString());
        totals.AddRow("reactions", v.Step.Reactions.ToString(), v.Run.Reactions.ToString());
        totals.AddRow("reconnects", Colour(v.Step.Reconnects), Colour(v.Run.Reconnects));
        totals.AddRow("errors", Colour(v.Step.ErrorCount), Colour(v.Run.ErrorCount));

        var s = v.Server;
        var serverTable = new Table().Border(TableBorder.Rounded).Title("[bold]Server[/] [dim](/api/admin/load)[/]").Expand();
        serverTable.AddColumn(""); serverTable.AddColumn("");
        if (v.ServerError is { } err) serverTable.AddRow("[red]error[/]", Markup.Escape(err));
        else if (s is null) serverTable.AddRow("waiting", "");
        else
        {
            serverTable.AddRow("cpu %", Warn(s.CpuPercent, 70, 90, "0"));
            serverTable.AddRow("working set / heap MB", $"{s.WorkingSetMB} / {s.HeapMB}");
            serverTable.AddRow("gc 0/1/2, pause ms", $"{s.Gen0}/{s.Gen1}/{s.Gen2}, {s.GcPauseMs}");
            serverTable.AddRow("thread pool threads / queue", $"{s.ThreadPoolThreads} / {Warn(s.ThreadPoolQueue, 1, 100, "0")}");
            serverTable.AddRow("hub connections", s.Connections.ToString());
            serverTable.AddRow("messages/s, hub sends/s", $"{s.MessagesPerSec}, {s.HubSendsPerSec}");
            serverTable.AddRow("requests/s, p95, max ms", $"{s.RequestsPerSec}, {Warn(s.RequestP95Ms, 200, 1000, "0")}, {s.RequestMaxMs}");
            serverTable.AddRow("writer depth", Warn(s.WriterDepth, 100, 1000, "0"));
            serverTable.AddRow("presence sweep ms", Warn(s.SweepMs, 50, 500, "0.0"));
            serverTable.AddRow("host load1 / mem free MB", $"{s.MachineLoad1?.ToString("0.00") ?? "—"} / {s.MachineMemAvailableMB?.ToString() ?? "—"}");
            serverTable.AddRow("sample age s", Warn((DateTime.UtcNow - s.At).TotalSeconds, 5, 15, "0"));
        }

        var log = new Panel(new Markup(string.Join("\n", v.Log.Select(Markup.Escape)))).Header("log").Expand().BorderColor(Color.Grey);

        return new Rows(header, workers, new Columns(latency, new Rows(totals, serverTable)), log);
    }

    private static void Row(Table t, string label, HistogramData h) =>
        t.AddRow(label, Ms(h.Percentile(0.5)), Ms(h.Percentile(0.95)), Ms(h.Percentile(0.99)), h.Count == 0 ? "—" : h.Max.ToString(), h.Count.ToString());
    private static string Ms(long v) => v < 0 ? "—" : v.ToString();
    private static string Colour(long n) => n == 0 ? "0" : $"[red]{n}[/]";
    private static string Warn(double v, double amber, double red, string fmt) => v >= red ? $"[red]{v.ToString(fmt)}[/]" : v >= amber ? $"[yellow]{v.ToString(fmt)}[/]" : v.ToString(fmt);

    /// <summary>One line for a terminal without a live display (an ssh session without a tty, or a log file).</summary>
    public static string Line(Aggregate.View v, string stepLabel)
    {
        var s = v.Server;
        return $"{DateTime.Now:HH:mm:ss} {stepLabel,-12} users {v.Users,5} conn {v.Connected,5} | sent/s {v.SentPerSec,6:0.0} recv/s {v.ReceivedPerSec,7:0.0} | " +
               $"sta p50/p95 {Ms(v.WindowSta.Percentile(0.5)),5}/{Ms(v.WindowSta.Percentile(0.95)),5} fan p95 {Ms(v.WindowFan.Percentile(0.95)),5} | " +
               $"err {v.Step.ErrorCount,4} | srv cpu {s?.CpuPercent,5:0.0} ws {s?.WorkingSetMB,5} q {s?.ThreadPoolQueue,4} depth {s?.WriterDepth,5} sends/s {s?.HubSendsPerSec,6}";
    }
}
