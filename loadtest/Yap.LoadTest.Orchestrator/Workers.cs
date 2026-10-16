using System.Diagnostics;
using System.Text.Json;
using Yap.LoadTest.Shared;

namespace Yap.LoadTest.Orchestrator;

/// <summary>Starts worker processes and reads their report lines. One worker holds up to --per-worker users.</summary>
public sealed class Workers(string workerDll, Aggregate aggregate) : IAsyncDisposable
{
    private readonly List<Process> _processes = [];
    private int _next;

    public int Count => _processes.Count;

    public void Start(Uri server, string usersFile, int from, int count, Preset preset, TimeSpan ramp, string? links, double? visible)
    {
        var id = _next++;
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var arg in new[] { workerDll, "--server", server.ToString(), "--users", usersFile, "--from", from.ToString(), "--count", count.ToString(),
                     "--ramp", ramp.TotalSeconds.ToString("0"), "--preset", preset.Name, "--worker", id.ToString() })
            psi.ArgumentList.Add(arg);
        if (links is not null) { psi.ArgumentList.Add("--links"); psi.ArgumentList.Add(links); }
        if (visible is { } v) { psi.ArgumentList.Add("--visible"); psi.ArgumentList.Add(v.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start the worker");
        _processes.Add(process);
        aggregate.Note($"worker {id}: users {from}..{from + count - 1} starting over {ramp.TotalSeconds:0} s");
        _ = ReadAsync(process, id);
    }

    private async Task ReadAsync(Process process, int id)
    {
        var stderr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
                if (line.Length > 0) aggregate.Note($"worker {id}: {line}");
        });
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith('{')) { aggregate.Note($"worker {id}: {line}"); continue; }
            try { if (JsonSerializer.Deserialize<WorkerReport>(line, Json.Options) is { } report) aggregate.Add(report); }
            catch (JsonException) { aggregate.Note($"worker {id}: bad line"); }
        }
        await stderr;
        aggregate.Note($"worker {id}: exited ({process.ExitCode})");
    }

    /// <summary>"stop" on stdin lets every virtual user disconnect; a worker that is still there after 15 s is killed.</summary>
    public async Task StopAsync()
    {
        foreach (var p in _processes.Where(p => !p.HasExited))
        {
            try { await p.StandardInput.WriteLineAsync("stop"); await p.StandardInput.FlushAsync(); } catch { /* already gone */ }
        }
        var deadline = Task.Delay(TimeSpan.FromSeconds(15));
        await Task.WhenAny(Task.WhenAll(_processes.Select(p => p.WaitForExitAsync())), deadline);
        foreach (var p in _processes.Where(p => !p.HasExited)) { try { p.Kill(entireProcessTree: true); } catch { } }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        foreach (var p in _processes) p.Dispose();
    }
}
